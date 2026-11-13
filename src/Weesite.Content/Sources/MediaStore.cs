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

/// <summary>A stored media file's name, and an image's size when it's kept with it (F-0107).</summary>
/// <param name="Name">The stored name.</param>
/// <param name="Size">The width and height in pixels, after orientation, when known.</param>
public sealed record StoredMedia(string Name, (int Width, int Height)? Size = null);

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

    /// <summary>Lists every stored name, with the image sizes kept with them.</summary>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The files.</returns>
    async Task<IReadOnlyList<StoredMedia>> ListWithSizesAsync(CancellationToken cancellationToken) =>
        [.. (await ListAsync(cancellationToken)).Select(name => new StoredMedia(name))];

    /// <summary>Keeps an image's size with it, so the next load needn't read the image to measure it.</summary>
    /// <param name="name">The stored name.</param>
    /// <param name="width">The width in pixels, after orientation.</param>
    /// <param name="height">The height in pixels, after orientation.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task.</returns>
    Task KeepSizeAsync(string name, int width, int height, CancellationToken cancellationToken) => Task.CompletedTask;

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
    // An image's size, in its blob's metadata: the content load reads it from the listing instead
    // of downloading every original to measure it (F-0107).
    private const string WidthKey = "width";
    private const string HeightKey = "height";

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
    public async Task<IReadOnlyList<StoredMedia>> ListWithSizesAsync(CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(ContentContainers.Media);
        if (!await container.ExistsAsync(cancellationToken))
        {
            return [];
        }

        var files = new List<StoredMedia>();
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix: null, cancellationToken))
        {
            var size = blob.Metadata is { } metadata
                && metadata.TryGetValue(WidthKey, out var width) && int.TryParse(width, System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 0
                && metadata.TryGetValue(HeightKey, out var height) && int.TryParse(height, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0
                    ? (w, h)
                    : ((int, int)?)null;
            files.Add(new StoredMedia(blob.Name, size));
        }

        return files;
    }

    /// <inheritdoc />
    public async Task KeepSizeAsync(string name, int width, int height, CancellationToken cancellationToken)
    {
        var blob = blobs.GetBlobContainerClient(ContentContainers.Media).GetBlobClient(name);
        try
        {
            await blob.SetMetadataAsync(SizeMetadata(width, height), cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Deleted meanwhile: nothing to keep.
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(ContentContainers.Media);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        // An original image gets its size with it; its variants and other files don't need one.
        var options = new BlobUploadOptions();
        if (Media.MediaIndex.IsOriginalImage(name))
        {
            try
            {
                var (width, height) = Media.ImageProcessor.Measure(data.ToArray());
                options.Metadata = SizeMetadata(width, height);
            }
            catch (NetVips.VipsException)
            {
                // Measured, and skipped, when the content loads.
            }
        }

        await container.GetBlobClient(name).UploadAsync(new BinaryData(data), options, cancellationToken);
    }

    private static Dictionary<string, string> SizeMetadata(int width, int height) => new()
    {
        [WidthKey] = width.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [HeightKey] = height.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

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
