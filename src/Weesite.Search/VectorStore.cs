using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
        Model != model ? new Dictionary<string, float[]>() : Chunks
            .Select(c => (c.Key, Vector: MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(c.Value)).ToArray()))
            .Where(c => c.Vector.Length == Dimensions && Dimensions > 0)
            .ToDictionary(c => c.Key, c => c.Vector, StringComparer.Ordinal);
}

/// <summary>Vectors in the private <c>index</c> Blob container: <c>{model}/{post|page}/{slug}.json</c>.</summary>
public sealed class BlobVectorStore(BlobServiceClient blobs) : IVectorStore
{
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
        await foreach (var blob in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            var key = blob.Name[prefix.Length..];
            if (!key.EndsWith(".json", StringComparison.Ordinal) || !VectorKeys.IsItem(key[..^5]))
            {
                continue;
            }

            var download = await Container.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
            if (download.Value.Content.ToObjectFromJson<VectorFile>(VectorFile.Options) is { } file)
            {
                items[key[..^5]] = file.Read(model);
            }
        }

        return items;
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
