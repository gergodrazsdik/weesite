using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Weesite.Content.Sources;

/// <summary>A media file opened for reading.</summary>
/// <param name="Content">The bytes; the caller disposes it.</param>
/// <param name="Length">The size in bytes.</param>
/// <param name="ETag">The version tag, for conditional requests.</param>
/// <param name="LastModified">When it last changed.</param>
public sealed record MediaFile(Stream Content, long Length, string ETag, DateTimeOffset LastModified);

/// <summary>Images and attachments, stored under content-hashed names (<c>{hash}-{name}</c>).</summary>
public interface IMediaStore
{
    /// <summary>Opens a media file.</summary>
    /// <param name="name">The stored name, already validated by the caller.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The file, or null if there is none by that name.</returns>
    Task<MediaFile?> OpenAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists every stored name.</summary>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The names.</returns>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stores a file, replacing any with the same name (names carry the content's hash).</summary>
    /// <param name="name">The media name.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task.</returns>
    Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Deletes a file (T-0110).</summary>
    /// <param name="name">The media name, already validated by the caller.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>True when it was deleted, false when there was none.</returns>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Media from the <c>media</c> Blob container.</summary>
/// <param name="blobs">The storage account.</param>
public sealed class BlobMediaStore(BlobServiceClient blobs) : IMediaStore
{
    /// <inheritdoc />
    public async Task<MediaFile?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var blob = blobs.GetBlobContainerClient(ContentContainers.Media).GetBlobClient(name);
        try
        {
            var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            var details = download.Value.Details;
            return new MediaFile(download.Value.Content, details.ContentLength, details.ETag.ToString("H"), details.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(ContentContainers.Media);
        if (!await container.ExistsAsync(cancellationToken))
        {
            return [];
        }

        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            names.Add(blob.Name);
        }

        return names;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(ContentContainers.Media);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient(name).UploadAsync(new BinaryData(data), overwrite: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var deleted = await blobs.GetBlobContainerClient(ContentContainers.Media).GetBlobClient(name)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        return deleted.Value;
    }
}

/// <summary>Media from a directory (tests).</summary>
/// <param name="root">The directory with the media files.</param>
public sealed class FileMediaStore(string root) : IMediaStore
{
    /// <inheritdoc />
    public Task<MediaFile?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var file = new FileInfo(Path.Combine(root, name));
        if (!file.Exists || !file.FullName.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
        {
            return Task.FromResult<MediaFile?>(null);
        }

        var etag = $"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"";
        return Task.FromResult<MediaFile?>(new MediaFile(file.OpenRead(), file.Length, etag, file.LastWriteTimeUtc));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
            Directory.Exists(root) ? Directory.EnumerateFiles(root).Select(Path.GetFileName).OfType<string>().ToList() : []);

    /// <inheritdoc />
    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var file = Path.GetFullPath(Path.Combine(root, name));
        if (!file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' isn't a media name.", nameof(name));
        }

        Directory.CreateDirectory(root);
        await AtomicFile.WriteAllBytesAsync(file, data, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var file = Path.GetFullPath(Path.Combine(root, name));
        if (!file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
        {
            return Task.FromResult(false);
        }

        File.Delete(file);
        return Task.FromResult(true);
    }
}
