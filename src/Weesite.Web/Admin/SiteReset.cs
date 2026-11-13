using System.Globalization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using Weesite.Content;
using Weesite.Content.Sources;
using Weesite.Search;

namespace Weesite.Web.Admin;

/// <summary>Deletes the site's files: content, media, settings and stored vectors.</summary>
internal interface ISiteFiles
{
    /// <summary>Deletes every file the reset covers.</summary>
    /// <param name="cancellationToken">Stops the deletion.</param>
    /// <returns>How many files were deleted.</returns>
    Task<int> DeleteAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// In Blob Storage: every blob in <c>content</c>, <c>media</c>, <c>settings</c> (except the version
/// marker) and <c>index</c>. Versioning keeps each one's last content for 90 days and soft delete
/// keeps deleted blobs for 30 days, so docs/deploy.md's restore steps can bring files back.
/// </summary>
internal sealed class BlobSiteFiles(BlobServiceClient blobs) : ISiteFiles
{
    private static readonly string[] Containers = [ContentContainers.Content, ContentContainers.Media, ContentContainers.Settings, "index"];

    /// <inheritdoc />
    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var name in Containers)
        {
            var container = blobs.GetBlobContainerClient(name);
            if (!await container.ExistsAsync(cancellationToken))
            {
                continue;
            }

            await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, null, cancellationToken))
            {
                if (name == ContentContainers.Settings && blob.Name == ContentContainers.VersionFile)
                {
                    continue;
                }

                if ((await container.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)).Value)
                {
                    deleted++;
                }
            }
        }

        return deleted;
    }
}

/// <summary>
/// In a directory (tests): every file under <c>content</c>, <c>media</c> and <c>settings</c>.
/// Refuses a directory inside a git working tree, so it can never empty the repository's sample site.
/// </summary>
internal sealed class FileSiteFiles(string root) : ISiteFiles
{
    /// <inheritdoc />
    public Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(root)); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                throw new InvalidOperationException($"Won't delete the files of {root}: it's inside a git working tree.");
            }
        }

        var deleted = 0;
        foreach (var name in new[] { ContentContainers.Content, ContentContainers.Media, ContentContainers.Settings })
        {
            var folder = Path.Combine(root, name);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(file);
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }
}

/// <summary>
/// The factory reset (T-0100): deletes all content, media and settings and the stored vectors, then
/// makes every replica reload, leaving an empty site with default settings. Editors, the audit log
/// and analytics stay. For clearing test content, or a clean slate before importing a backup.
/// </summary>
internal sealed class SiteReset(ISiteFiles files, IVectorStore vectors, IEmbedder embedder, IContentStore store, ContentReloader reloader)
{
    /// <summary>
    /// Resets the site. Not stopped with the request: once files are deleted, the version bump and
    /// the reload must follow, or replicas keep serving what's gone (F-0109).
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    public async Task<int> RunAsync()
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            var deleted = await files.DeleteAllAsync(cancellationToken);
            foreach (var item in await vectors.ListAsync(embedder.ModelId, cancellationToken))
            {
                await vectors.DeleteAsync(embedder.ModelId, item, cancellationToken);
            }

            return deleted;
        }
        finally
        {
            await reloader.ReloadAfterWriteAsync(store);
        }
    }

    /// <summary>
    /// Maps <c>POST /admin/settings/reset</c>: admins only, with an antiforgery token (a form field
    /// binds, so it's checked) and the site's name typed as confirmation.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapSiteReset(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/settings/reset", async ([FromForm] string? confirm, HttpContext context, ContentCatalogProvider content, SiteReset reset, IAuditLog audit, TimeProvider time) =>
        {
            if (!string.Equals(confirm?.Trim(), content.Current.Site.Name, StringComparison.Ordinal))
            {
                return Results.Redirect("/admin/settings?resetError=1#danger-zone");
            }

            var deleted = await reset.RunAsync();
            var actor = EditorClaims.Email(context.User);
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "site-reset", deleted.ToString(CultureInfo.InvariantCulture) + " files deleted"), CancellationToken.None);
            return Results.Redirect("/admin/settings?reset=1#danger-zone");
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);
}
