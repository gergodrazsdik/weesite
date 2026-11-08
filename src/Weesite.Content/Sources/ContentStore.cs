using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Weesite.Content.Sources;

/// <summary>A content file with its ETag, as the editor opens it.</summary>
/// <param name="Path">The path in the content container, such as <c>posts/hello-world.md</c>.</param>
/// <param name="Text">The Markdown with its front matter.</param>
/// <param name="ETag">The version this text is, for conflict checks on save.</param>
public sealed record StoredFile(string Path, string Text, string ETag);

/// <summary>An earlier version of a content file (Blob versioning).</summary>
/// <param name="VersionId">The version's ID.</param>
/// <param name="SavedAt">When it was written.</param>
/// <param name="IsCurrent">Whether it's the current version.</param>
public sealed record Revision(string VersionId, DateTimeOffset SavedAt, bool IsCurrent);

/// <summary>
/// Writes content files for the editor (ADR 0005): conditional writes for ETag conflict checks,
/// the version marker other replicas watch, and revisions.
/// </summary>
public interface IContentStore
{
    /// <summary>Reads a content file.</summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The file, or null when there's none.</returns>
    Task<StoredFile?> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a content file if it's still the version the editor opened: <paramref name="ifMatch"/>
    /// is that version's ETag, or null to create a file that mustn't exist yet.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="text">The new text.</param>
    /// <param name="ifMatch">The expected ETag, or null for a new file.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The new ETag, or null when the file changed (or exists) meanwhile.</returns>
    Task<string?> WriteAsync(string path, string text, string? ifMatch, CancellationToken cancellationToken);

    /// <summary>Changes the version marker, so other replicas reload.</summary>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The new version, as the source reports it.</returns>
    Task<string?> BumpVersionAsync(CancellationToken cancellationToken);

    /// <summary>Lists a file's versions, newest first.</summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The versions; empty where the store keeps none.</returns>
    Task<IReadOnlyList<Revision>> ListRevisionsAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads an earlier version of a file.</summary>
    /// <param name="path">The path.</param>
    /// <param name="versionId">The version.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The text, or null.</returns>
    Task<string?> ReadRevisionAsync(string path, string versionId, CancellationToken cancellationToken);
}

/// <summary>Content files in Blob Storage, with Blob versioning for revisions (infra/app turns it on).</summary>
public sealed class BlobContentStore(BlobServiceClient blobs) : IContentStore
{
    private BlobContainerClient Content => blobs.GetBlobContainerClient(ContentContainers.Content);

    /// <inheritdoc />
    public async Task<StoredFile?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var download = await Content.GetBlobClient(path).DownloadContentAsync(cancellationToken);
            return new StoredFile(path, download.Value.Content.ToString(), download.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> WriteAsync(string path, string text, string? ifMatch, CancellationToken cancellationToken)
    {
        var conditions = ifMatch is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(ifMatch) };
        try
        {
            await Content.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var result = await Content.GetBlobClient(path).UploadAsync(
                BinaryData.FromString(text),
                new BlobUploadOptions { Conditions = conditions, HttpHeaders = new BlobHttpHeaders { ContentType = "text/markdown; charset=utf-8" } },
                cancellationToken);
            return result.Value.ETag.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> BumpVersionAsync(CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(ContentContainers.Settings);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var result = await container.GetBlobClient(ContentContainers.VersionFile)
            .UploadAsync(BinaryData.FromString(Guid.NewGuid().ToString("N")), overwrite: true, cancellationToken);
        return result.Value.ETag.ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Revision>> ListRevisionsAsync(string path, CancellationToken cancellationToken)
    {
        var revisions = new List<Revision>();
        try
        {
            await foreach (var item in Content.GetBlobsAsync(BlobTraits.None, BlobStates.Version, path, cancellationToken))
            {
                if (item.Name == path && item.VersionId is { } id)
                {
                    revisions.Add(new Revision(id, item.Properties.LastModified ?? DateTimeOffset.MinValue, item.IsLatestVersion == true));
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 400 or 404)
        {
            // No versioning (the local emulator) or no container: no revisions.
            return [];
        }

        return [.. revisions.OrderByDescending(r => r.SavedAt)];
    }

    /// <inheritdoc />
    public async Task<string?> ReadRevisionAsync(string path, string versionId, CancellationToken cancellationToken)
    {
        try
        {
            var download = await Content.GetBlobClient(path).WithVersion(versionId).DownloadContentAsync(cancellationToken);
            return download.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status is 400 or 404)
        {
            return null;
        }
    }
}

/// <summary>
/// Content files in a directory (tests): ETags are content hashes, no revisions, and the version
/// marker lives in memory (only one app reads a directory).
/// </summary>
public sealed class FileContentStore(string root) : IContentStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private int _version;

    /// <inheritdoc />
    public async Task<StoredFile?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var file = FullPath(path);
        if (!File.Exists(file))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(file, cancellationToken);
        return new StoredFile(path, text, Hash(text));
    }

    /// <inheritdoc />
    public async Task<string?> WriteAsync(string path, string text, string? ifMatch, CancellationToken cancellationToken)
    {
        var file = FullPath(path);
        var gate = Locks.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var exists = File.Exists(file);
            if ((ifMatch is null && exists)
                || (ifMatch is not null && (!exists || Hash(await File.ReadAllTextAsync(file, cancellationToken)) != ifMatch)))
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, text, cancellationToken);
            return Hash(text);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<string?> BumpVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Interlocked.Increment(ref _version).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public Task<IReadOnlyList<Revision>> ListRevisionsAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Revision>>([]);

    /// <inheritdoc />
    public Task<string?> ReadRevisionAsync(string path, string versionId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    // Paths are "posts/x.md" or "pages/x.md" as the editor makes them; nothing may leave the root.
    private string FullPath(string path)
    {
        var content = Path.GetFullPath(Path.Combine(root, ContentContainers.Content));
        var full = Path.GetFullPath(Path.Combine(content, path));
        return full.StartsWith(content + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new ArgumentException($"'{path}' isn't a content path.", nameof(path));
    }
}
