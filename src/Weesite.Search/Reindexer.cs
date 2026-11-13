using Microsoft.Extensions.Logging;
using Weesite.Content;
using Weesite.Content.Sources;

namespace Weesite.Search;

/// <summary>What a re-index did.</summary>
/// <param name="Items">The posts and pages embedded.</param>
/// <param name="Chunks">Their chunks.</param>
/// <param name="Removed">Stored items that no longer exist, deleted.</param>
public sealed record ReindexResult(int Items, int Chunks, int Removed);

/// <summary>
/// Asks for every chunk to be embedded again (T-0069): the editor's "Re-embed all", or a web
/// replica that found more chunks without vectors than it embeds itself. The re-index job, or the
/// web app when there's no queue, does the work.
/// </summary>
public interface IReindexRequests
{
    /// <summary>Asks for a re-index, unless one is already waiting to run (F-0091 L30).</summary>
    /// <param name="requestedBy">Who or what asked, for the logs.</param>
    /// <param name="cancellationToken">Stops the request (not the re-index).</param>
    /// <returns>True when it was asked for; false when a waiting one will do the same.</returns>
    Task<bool> RequestAsync(string requestedBy, CancellationToken cancellationToken);
}

/// <summary>
/// Embeds every chunk of the content again and stores the vectors, replacing what's stored and
/// removing items that are gone, then bumps the version marker so every replica reloads and picks
/// them up (brief, Bulk re-indexing). The re-index job runs it; so does the web app without a queue.
/// </summary>
public sealed partial class Reindexer(IEmbedder embedder, IVectorStore store, IContentStore content, ILogger<Reindexer> logger)
{
    /// <summary>Re-indexes a catalog.</summary>
    /// <param name="catalog">The content, freshly loaded.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What it did, or null without an embedding model.</returns>
    public async Task<ReindexResult?> RunAsync(ContentCatalog catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (embedder.Dimensions == 0)
        {
            LogNoModel();
            return null;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>> items;
        int chunks;
        using (var index = SearchIndex.Build(catalog, embedder))
        {
            items = index.ItemVectors;
            chunks = index.EmbeddedCount;
        }

        foreach (var (key, vectors) in items)
        {
            await store.SaveAsync(embedder.ModelId, key, vectors, cancellationToken);
        }

        var removed = 0;
        foreach (var key in await store.ListAsync(embedder.ModelId, cancellationToken))
        {
            if (!items.ContainsKey(key))
            {
                await store.DeleteAsync(embedder.ModelId, key, cancellationToken);
                removed++;
            }
        }

        await content.BumpVersionAsync(cancellationToken);
        var result = new ReindexResult(items.Count, chunks, removed);
        var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogDone(result.Items, result.Chunks, result.Removed, embedder.ModelId, elapsed);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No embedding model: nothing to re-index")]
    private partial void LogNoModel();

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-indexed {Items} items ({Chunks} chunks, {Removed} stale removed) with {Model} in {Milliseconds} ms")]
    private partial void LogDone(int items, int chunks, int removed, string model, long milliseconds);
}
