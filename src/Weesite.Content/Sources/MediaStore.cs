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
    /// <remarks>
    /// Reads the blob's properties only; the bytes are downloaded when the stream is read, from
    /// where it was moved to. So /media can say the length, answer ranges, and answer HEAD and
    /// 304 without starting a download (F-0236).
    /// </remarks>
    public async Task<MediaFile?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var blob = blobs.GetBlobContainerClient(ContentContainers.Media).GetBlobClient(name);
        try
        {
            var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            var content = new BlobReadStream(blob, properties.ContentLength, properties.ETag);
            return new MediaFile(content, properties.ContentLength, properties.ETag.ToString("H"), properties.LastModified);
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

/// <summary>
/// A blob as a seekable, read-only stream that downloads only when read: one ranged download from
/// the position on, opened again after a seek. Its length is the blob's, and every read asks for
/// the version whose properties were read, so the bytes match the length and tag sent (F-0236).
/// </summary>
/// <param name="blob">The blob.</param>
/// <param name="length">Its length.</param>
/// <param name="etag">Its version.</param>
internal sealed class BlobReadStream(BlobClient blob, long length, ETag etag) : Stream
{
    private Stream? _body;
    private long _position;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        if (target != _position)
        {
            _body?.Dispose();
            _body = null;
            _position = target;
        }

        return _position;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _position >= length)
        {
            return 0;
        }

        _body ??= blob.DownloadStreaming(Options()).Value.Content;
        var read = _body.Read(buffer);
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || _position >= length)
        {
            return 0;
        }

        _body ??= (await blob.DownloadStreamingAsync(Options(), cancellationToken)).Value.Content;
        var read = await _body.ReadAsync(buffer, cancellationToken);
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_body is not null)
        {
            await _body.DisposeAsync();
            _body = null;
        }

        await base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _body?.Dispose();
            _body = null;
        }

        base.Dispose(disposing);
    }

    // From the position to the end, of the version the length and tag came from.
    private BlobDownloadOptions Options() => new()
    {
        Range = new HttpRange(_position),
        Conditions = new BlobRequestConditions { IfMatch = etag },
    };
}

/// <summary>Media from a directory (tests).</summary>
/// <param name="root">The directory with the media files.</param>
public sealed class FileMediaStore(string root) : IMediaStore
{
    // Image sizes kept in memory, for every file store in the process, by path, length and time:
    // a file has no metadata to keep them in, and tests start hundreds of apps on the same sample
    // media, each of which would decode every image to measure it again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), (int Width, int Height)> Sizes = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredMedia>> ListWithSizesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<StoredMedia>>([]);
        }

        return Task.FromResult<IReadOnlyList<StoredMedia>>([.. new DirectoryInfo(root).EnumerateFiles().Select(file =>
            new StoredMedia(file.Name, Sizes.TryGetValue((file.FullName, file.Length, file.LastWriteTimeUtc), out var size) ? size : null))]);
    }

    /// <inheritdoc />
    public Task KeepSizeAsync(string name, int width, int height, CancellationToken cancellationToken)
    {
        var file = new FileInfo(Path.Combine(root, name));
        if (file.Exists && file.FullName.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            Sizes[(file.FullName, file.Length, file.LastWriteTimeUtc)] = (width, height);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MediaFile?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var file = new FileInfo(Path.Combine(root, name));
        if (!file.Exists || !file.FullName.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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
