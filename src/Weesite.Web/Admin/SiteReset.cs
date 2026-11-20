using System.Globalization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Weesite.Content;
using Weesite.Content.Sources;
using Weesite.Search;
using Weesite.Web.Agents.OAuth;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Admin;

/// <summary>What the reset can delete (T-0167), in the order the reset page lists them.</summary>
internal enum ResetPart
{
    /// <summary>Posts and pages.</summary>
    Content,

    /// <summary>Media files.</summary>
    Media,

    /// <summary>Site settings: name, menu, theme, tags, author profiles.</summary>
    Settings,

    /// <summary>The search index's stored embeddings.</summary>
    SearchIndex,

    /// <summary>Comments.</summary>
    Comments,

    /// <summary>Reaction counts and events.</summary>
    Reactions,

    /// <summary>Contact messages.</summary>
    Messages,

    /// <summary>Page views (analytics).</summary>
    Analytics,

    /// <summary>Editors added in the admin; the admins from the deployment always stay.</summary>
    Editors,

    /// <summary>Connected apps: OAuth clients and grants.</summary>
    ConnectedApps,

    /// <summary>The audit log.</summary>
    AuditLog,

    /// <summary>The counters against floods: hourly limits and used proof-of-work challenges.</summary>
    SpamCounters,

    /// <summary>Uploaded backups not imported yet.</summary>
    Imports,
}

/// <summary>The reset's parts: their keys in the form, and which are ticked at first.</summary>
internal static class ResetParts
{
    /// <summary>Gets every part, in the page's order.</summary>
    public static IReadOnlyList<ResetPart> All { get; } = Enum.GetValues<ResetPart>();

    /// <summary>Gets the parts ticked at first: what the seed or a backup puts back (the maintainer, 2026-11-20).</summary>
    public static IReadOnlySet<ResetPart> Defaults { get; } = new HashSet<ResetPart> { ResetPart.Content, ResetPart.Media, ResetPart.Settings, ResetPart.SearchIndex };

    /// <summary>A part's key in the form and in addresses.</summary>
    /// <param name="part">The part.</param>
    /// <returns>Such as <c>search-index</c>.</returns>
    public static string Key(ResetPart part) => part switch
    {
        ResetPart.Content => "content",
        ResetPart.Media => "media",
        ResetPart.Settings => "settings",
        ResetPart.SearchIndex => "search-index",
        ResetPart.Comments => "comments",
        ResetPart.Reactions => "reactions",
        ResetPart.Messages => "messages",
        ResetPart.Analytics => "analytics",
        ResetPart.Editors => "editors",
        ResetPart.ConnectedApps => "connected-apps",
        ResetPart.AuditLog => "audit-log",
        ResetPart.SpamCounters => "spam-counters",
        ResetPart.Imports => "imports",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>Reads keys back into parts, ignoring unknown ones.</summary>
    /// <param name="keys">The keys.</param>
    /// <returns>The parts.</returns>
    public static IReadOnlySet<ResetPart> Parse(IEnumerable<string?>? keys) =>
        (keys ?? []).SelectMany(k => All.Where(p => Key(p) == k)).ToHashSet();
}

/// <summary>Deletes the site's files: content, media, settings and the search index's files.</summary>
internal interface ISiteFiles
{
    /// <summary>Deletes every file in some of the site's containers.</summary>
    /// <param name="containers">The containers, such as <c>content</c>.</param>
    /// <param name="cancellationToken">Stops the deletion.</param>
    /// <returns>How many files were deleted.</returns>
    Task<int> DeleteAsync(IReadOnlyCollection<string> containers, CancellationToken cancellationToken);
}

/// <summary>
/// In Blob Storage: every blob of the containers asked for, but the settings container's version
/// marker, which the replicas watch to reload.
/// </summary>
/// <param name="blobs">The blob service.</param>
internal sealed class BlobSiteFiles(BlobServiceClient blobs) : ISiteFiles
{
    /// <inheritdoc />
    public async Task<int> DeleteAsync(IReadOnlyCollection<string> containers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containers);
        var deleted = 0;
        foreach (var name in containers)
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
/// In a directory (tests): every file under the folders asked for (<c>content</c>, <c>media</c>,
/// <c>settings</c>). Refuses a directory inside a git working tree, so it can never empty the
/// repository's sample site.
/// </summary>
/// <param name="root">The content root.</param>
internal sealed class FileSiteFiles(string root) : ISiteFiles
{
    /// <inheritdoc />
    public Task<int> DeleteAsync(IReadOnlyCollection<string> containers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containers);
        for (var directory = new DirectoryInfo(Path.GetFullPath(root)); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                throw new InvalidOperationException($"Won't delete the files of {root}: it's inside a git working tree.");
            }
        }

        var deleted = 0;
        foreach (var name in containers)
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

/// <summary>The site's own stores the reset clears, beside its files.</summary>
/// <param name="Comments">The comments.</param>
/// <param name="CommentCatalog">The published comments, cached.</param>
/// <param name="Reactions">The reactions.</param>
/// <param name="Messages">The contact messages.</param>
/// <param name="PageViews">The page views.</param>
/// <param name="Editors">The editors added in the admin.</param>
/// <param name="OAuth">The connected apps.</param>
/// <param name="Audit">The audit log.</param>
/// <param name="Limits">The counters against floods.</param>
/// <param name="Imports">The uploaded backups.</param>
/// <param name="Reindex">The re-embedding requests.</param>
/// <param name="Time">The clock, for today's salt.</param>
internal sealed record ResetStores(
    ICommentStore Comments,
    CommentCatalog CommentCatalog,
    IReactionStore Reactions,
    IMessageStore Messages,
    IPageViewStore PageViews,
    IEditorStore Editors,
    IOAuthStore OAuth,
    IAuditLog Audit,
    IVisitorStore Limits,
    IImportStaging Imports,
    IReindexRequests Reindex,
    TimeProvider Time);

/// <summary>
/// The reset (T-0100, its own page since T-0167): deletes the parts an admin ticks, then makes every
/// replica reload. By default what the seed or a backup puts back: content, media, settings and the
/// search index. What visitors sent, the analytics, added editors, connected apps, the audit log and
/// the technical rest only when ticked. Data Protection keys and the country data are never offered.
/// </summary>
/// <param name="files">The site's files.</param>
/// <param name="vectors">The stored vectors.</param>
/// <param name="embedder">The embedding model, whose vectors are stored.</param>
/// <param name="store">The content store, to bump its version.</param>
/// <param name="reloader">Reloads every replica.</param>
/// <param name="stores">The other stores.</param>
internal sealed class SiteReset(ISiteFiles files, IVectorStore vectors, IEmbedder embedder, IContentStore store, ContentReloader reloader, ResetStores stores)
{
    /// <summary>
    /// Deletes what's asked. Not stopped with the request: once files are deleted, the version bump
    /// and the reload must follow, or replicas keep serving what's gone (F-0109).
    /// </summary>
    /// <param name="parts">What to delete.</param>
    /// <param name="actor">Who asked, for a re-embed of content that stays.</param>
    /// <returns>How many items each part had.</returns>
    public async Task<IReadOnlyDictionary<ResetPart, int>> RunAsync(IReadOnlySet<ResetPart> parts, string actor)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var cancellationToken = CancellationToken.None;
        var done = new Dictionary<ResetPart, int>();
        try
        {
            foreach (var (part, container) in new[] { (ResetPart.Content, ContentContainers.Content), (ResetPart.Media, ContentContainers.Media), (ResetPart.Settings, ContentContainers.Settings) })
            {
                if (parts.Contains(part))
                {
                    done[part] = await files.DeleteAsync([container], cancellationToken);
                }
            }

            if (parts.Contains(ResetPart.SearchIndex))
            {
                var count = await files.DeleteAsync(["index"], cancellationToken);
                foreach (var item in await vectors.ListAsync(embedder.ModelId, cancellationToken))
                {
                    await vectors.DeleteAsync(embedder.ModelId, item, cancellationToken);
                    count++;
                }

                done[ResetPart.SearchIndex] = count;
            }

            if (parts.Contains(ResetPart.Comments))
            {
                var all = await stores.Comments.ListAsync(cancellationToken);
                foreach (var comment in all)
                {
                    await stores.Comments.DeleteAsync(comment.Post, comment.Id, cancellationToken);
                }

                await stores.CommentCatalog.RefreshAsync(cancellationToken);
                done[ResetPart.Comments] = all.Count;
            }

            if (parts.Contains(ResetPart.Reactions))
            {
                done[ResetPart.Reactions] = await stores.Reactions.ClearAsync(cancellationToken);

                // The counters remember who reacted today: kept, they'd refuse a reaction again and take
                // back one that's gone (F-0196).
                if (!parts.Contains(ResetPart.SpamCounters))
                {
                    await stores.Limits.CleanUpAsync(DateOnly.MaxValue, cancellationToken);
                }
            }

            if (parts.Contains(ResetPart.Messages))
            {
                done[ResetPart.Messages] = await stores.Messages.DeleteOlderThanAsync(DateTimeOffset.MaxValue, cancellationToken);
            }

            if (parts.Contains(ResetPart.Analytics))
            {
                // Today's salt stays: the recorders hold it, and a new one would count today's visitors twice.
                var today = DateOnly.FromDateTime(stores.Time.GetUtcNow().UtcDateTime);
                done[ResetPart.Analytics] = await stores.PageViews.CleanUpAsync(DateOnly.MaxValue, today, cancellationToken);
            }

            if (parts.Contains(ResetPart.Editors))
            {
                var added = await stores.Editors.ListAsync(cancellationToken);
                foreach (var editor in added)
                {
                    await stores.Editors.RemoveAsync(editor.Email, cancellationToken);
                }

                done[ResetPart.Editors] = added.Count;
            }

            if (parts.Contains(ResetPart.ConnectedApps))
            {
                var grants = await stores.OAuth.ListGrantsAsync(null, cancellationToken);
                foreach (var grant in grants)
                {
                    await stores.OAuth.DeleteGrantAsync(grant.Id, cancellationToken);
                }

                var clients = await stores.OAuth.ListClientsAsync(cancellationToken);
                foreach (var client in clients)
                {
                    await stores.OAuth.DeleteClientAsync(client.Id, cancellationToken);
                }

                done[ResetPart.ConnectedApps] = grants.Count + clients.Count;
            }

            if (parts.Contains(ResetPart.AuditLog))
            {
                done[ResetPart.AuditLog] = await stores.Audit.ClearAsync(cancellationToken);
            }

            if (parts.Contains(ResetPart.SpamCounters))
            {
                done[ResetPart.SpamCounters] = await stores.Limits.CleanUpAsync(DateOnly.MaxValue, cancellationToken);
            }

            if (parts.Contains(ResetPart.Imports))
            {
                done[ResetPart.Imports] = await stores.Imports.ClearAsync(cancellationToken);
            }

            // The content stays but its vectors went: embed it again.
            if (parts.Contains(ResetPart.SearchIndex) && !parts.Contains(ResetPart.Content))
            {
                await stores.Reindex.RequestAsync(actor, cancellationToken);
            }

            return done;
        }
        finally
        {
            if (parts.Overlaps([ResetPart.Content, ResetPart.Media, ResetPart.Settings, ResetPart.SearchIndex]))
            {
                await reloader.ReloadAfterWriteAsync(store);
            }
        }
    }

    /// <summary>
    /// Maps <c>POST /admin/reset</c>: admins only, with an antiforgery token (form fields bind, so
    /// it's checked), the parts ticked and the site's name typed as confirmation. Done, it writes the
    /// audit entry (after the audit log was cleared, if it was). Only an admin who deleted
    /// themselves, as an added editor, is signed out; the rest go back to the page, which says
    /// what went.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapSiteReset(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/reset", async ([FromForm(Name = "part")] string[]? part, [FromForm] string? confirm, HttpContext context, ContentCatalogProvider content, SiteReset reset, IAuditLog audit, ConfiguredAdmins admins, TimeProvider time) =>
        {
            var parts = ResetParts.Parse(part);
            var again = string.Concat(ResetParts.All.Where(parts.Contains).Select(p => "&part=" + ResetParts.Key(p)));
            if (parts.Count == 0)
            {
                return Results.Redirect("/admin/reset?error=nothing" + again);
            }

            if (!string.Equals(confirm?.Trim(), content.Current.Site.Name, StringComparison.Ordinal))
            {
                return Results.Redirect("/admin/reset?error=confirm" + again);
            }

            var actor = EditorClaims.Email(context.User);
            var done = await reset.RunAsync(parts, actor);
            var summary = string.Join(", ", ResetParts.All.Where(done.ContainsKey).Select(p => string.Create(CultureInfo.InvariantCulture, $"{ResetParts.Key(p)} {done[p]}")));
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "site-reset", summary), CancellationToken.None);
            if (parts.Contains(ResetPart.Editors) && !admins.Contains(actor))
            {
                await context.SignOutAsync(EditorClaims.Scheme);
                return Results.Redirect("/admin/sign-in?reset=1");
            }

            return Results.Redirect("/admin/reset?done=1" + again);
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);
}
