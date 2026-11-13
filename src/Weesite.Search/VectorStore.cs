using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>
/// Chunk vectors kept between index builds (T-0069): one file per post or page and model, each
/// chunk's vector under a hash of the text it was made from. An index build reuses them and
/// embeds only new or changed chunks; the re-index job rewrites them all.
/// </summary>
public interface IVectorStore
{
    /// <summary>Reads every stored item of a model.</summary>
    /// <param name="model">The model's ID (<see cref="IEmbedder.ModelId"/>).</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Each item's vectors by passage hash, keyed by <see cref="VectorKeys.Item"/>.</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>>> LoadAsync(string model, CancellationToken cancellationToken);

    /// <summary>Lists the stored items of a model.</summary>
    /// <param name="model">The model's ID.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>Their keys (<see cref="VectorKeys.Item"/>).</returns>
    Task<IReadOnlyList<string>> ListAsync(string model, CancellationToken cancellationToken);

    /// <summary>Replaces an item's vectors.</summary>
    /// <param name="model">The model's ID.</param>
    /// <param name="item">The item's key (<see cref="VectorKeys.Item"/>).</param>
    /// <param name="vectors">Its chunks' vectors by passage hash.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(string model, string item, IReadOnlyDictionary<string, float[]> vectors, CancellationToken cancellationToken);

    /// <summary>Deletes an item's vectors.</summary>
    /// <param name="model">The model's ID.</param>
    /// <param name="item">The item's key.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string model, string item, CancellationToken cancellationToken);
}

/// <summary>Keys of stored vectors.</summary>
public static class VectorKeys
{
    /// <summary>Gets an item's key: <c>post/slug</c> or <c>page/slug</c>.</summary>
    /// <param name="item">The post or page.</param>
    /// <returns>The key.</returns>
    public static string Item(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (item.Kind == ContentKind.Page ? "page/" : "post/") + item.Slug;
    }

    /// <summary>Gets the hash a passage's vector is stored under.</summary>
    /// <param name="passage">The text that was embedded.</param>
    /// <returns>32 lower-case hex digits.</returns>
    public static string Passage(string passage) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(passage)))[..32];

    /// <summary>Gets whether a string is a valid item key, so it can name a file.</summary>
    /// <param name="key">The string.</param>
    /// <returns>True for <c>post/</c> or <c>page/</c> and a slug.</returns>
    public static bool IsItem(string key) =>
        key is not null && (key.StartsWith("post/", StringComparison.Ordinal) || key.StartsWith("page/", StringComparison.Ordinal)) && Slugs.IsValid(key[5..]);
}

/// <summary>The file format: the model, the dimensions and each chunk's vector as Base64 of its floats.</summary>
internal sealed record VectorFile(string Model, int Dimensions, Dictionary<string, string> Chunks)
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static VectorFile From(string model, IReadOnlyDictionary<string, float[]> vectors) =>
        new(model, vectors.Values.FirstOrDefault()?.Length ?? 0, vectors.ToDictionary(v => v.Key, v => Convert.ToBase64String(MemoryMarshal.AsBytes(v.Value.AsSpan())), StringComparer.Ordinal));

    // Vectors of another model or size are dropped: they can't be compared with this one's.
    public IReadOnlyDictionary<string, float[]> Read(string model) =>
        Model != model || Chunks is null ? new Dictionary<string, float[]>() : Chunks
            .Select(c => (c.Key, Vector: MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(c.Value)).ToArray()))
            .Where(c => c.Vector.Length == Dimensions && Dimensions > 0)
            .ToDictionary(c => c.Key, c => c.Vector, StringComparer.Ordinal);
}

/// <summary>Vectors in the private <c>index</c> Blob container: <c>{model}/{post|page}/{slug}.json</c>.</summary>
/// <param name="blobs">The storage account.</param>
/// <param name="logger">Logs files it skips.</param>
public sealed partial class BlobVectorStore(BlobServiceClient blobs, ILogger<BlobVectorStore>? logger = null) : IVectorStore
{
    // Downloads at once while loading: a reload reads only the files that changed, but the first
    // load reads them all.
    private const int Downloads = 8;

    // The files as last read, by blob name, with their versions: every content reload loads the
    // vectors again, and most files haven't changed (F-0111).
    private readonly ConcurrentDictionary<string, (ETag Version, IReadOnlyDictionary<string, float[]> Vectors)> _read = new(StringComparer.Ordinal);

    private BlobContainerClient Container => blobs.GetBlobContainerClient("index");

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>>> LoadAsync(string model, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, IReadOnlyDictionary<string, float[]>>(StringComparer.Ordinal);
        if (!await Container.ExistsAsync(cancellationToken))
        {
            return items;
        }

        var prefix = model + "/";
        var listed = new List<(string Name, string Key, ETag Version)>();
        await foreach (var blob in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            var key = blob.Name[prefix.Length..];
            if (key.EndsWith(".json", StringComparison.Ordinal) && VectorKeys.IsItem(key[..^5]))
            {
                listed.Add((blob.Name, key[..^5], blob.Properties.ETag ?? default));
            }
        }

        var changed = listed.Where(b => !_read.TryGetValue(b.Name, out var read) || read.Version != b.Version || b.Version == default);
        await Parallel.ForEachAsync(changed, new ParallelOptions { MaxDegreeOfParallelism = Downloads, CancellationToken = cancellationToken }, async (blob, token) =>
        {
            // One file that's gone or unreadable is skipped, not every stored vector (F-0111).
            try
            {
                var download = await Container.GetBlobClient(blob.Name).DownloadContentAsync(token);
                var vectors = download.Value.Content.ToObjectFromJson<VectorFile>(VectorFile.Options)?.Read(model) ?? new Dictionary<string, float[]>();
                _read[blob.Name] = (blob.Version, vectors);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _read.TryRemove(blob.Name, out _);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                _read.TryRemove(blob.Name, out _);
                if (logger is not null)
                {
                    LogUnreadable(logger, blob.Name, ex);
                }
            }
        });

        foreach (var blob in listed)
        {
            if (_read.TryGetValue(blob.Name, out var read))
            {
                items[blob.Key] = read.Vectors;
            }
        }

        // Files deleted since (a reset, the re-index job) leave the cache too.
        var names = listed.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in _read.Keys.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && !names.Contains(n)))
        {
            _read.TryRemove(name, out _);
        }

        return items;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The vector file {Name} can't be read; its item is embedded again")]
    private static partial void LogUnreadable(ILogger logger, string name, Exception exception);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(string model, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        if (!await Container.ExistsAsync(cancellationToken))
        {
            return keys;
        }

        var prefix = model + "/";
        await foreach (var blob in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            var key = blob.Name[prefix.Length..];
            if (key.EndsWith(".json", StringComparison.Ordinal) && VectorKeys.IsItem(key[..^5]))
            {
                keys.Add(key[..^5]);
            }
        }

        return keys;
    }

    /// <inheritdoc />
    public async Task SaveAsync(string model, string item, IReadOnlyDictionary<string, float[]> vectors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (!VectorKeys.IsItem(item))
        {
            throw new ArgumentException($"Not an item key: {item}", nameof(item));
        }

        await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await Container.GetBlobClient($"{model}/{item}.json").UploadAsync(BinaryData.FromObjectAsJson(VectorFile.From(model, vectors), VectorFile.Options), overwrite: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string model, string item, CancellationToken cancellationToken)
    {
        if (VectorKeys.IsItem(item))
        {
            await Container.DeleteBlobIfExistsAsync($"{model}/{item}.json", cancellationToken: cancellationToken);
        }
    }
}

/// <summary>Vectors in memory, gone at restart: content from files (tests).</summary>
public sealed class MemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<(string Model, string Item), IReadOnlyDictionary<string, float[]>> _items = new();

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>>> LoadAsync(string model, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>>>(
            _items.Where(i => i.Key.Model == model).ToDictionary(i => i.Key.Item, i => i.Value, StringComparer.Ordinal));

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(string model, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. _items.Keys.Where(k => k.Model == model).Select(k => k.Item)]);

    /// <inheritdoc />
    public Task SaveAsync(string model, string item, IReadOnlyDictionary<string, float[]> vectors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        _items[(model, item)] = vectors.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string model, string item, CancellationToken cancellationToken)
    {
        _items.TryRemove((model, item), out _);
        return Task.CompletedTask;
    }
}
