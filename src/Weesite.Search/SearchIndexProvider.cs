using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>
/// Holds the search index for the current content catalog and rebuilds it when the catalog
/// changes (a reload builds a new catalog). Blob stays the only source of truth: chunk vectors are
/// kept in the vector store, so a build embeds only new or changed chunks (T-0069) and stores them.
/// A build embeds at most <c>Search:EmbedLimit</c> chunks (200); when more are missing, as after a
/// model change, it asks for a re-index once, and the re-index job embeds the rest.
/// </summary>
public sealed class SearchIndexProvider(
    ContentCatalogProvider content,
    IEmbedder embedder,
    ILogger<SearchIndexProvider>? logger = null,
    IVectorStore? store = null,
    IConfiguration? configuration = null,
    IReindexRequests? reindex = null) : IDisposable
{
    /// <summary>The most chunks a build embeds unless <c>Search:EmbedLimit</c> says otherwise.</summary>
    public const int DefaultEmbedLimit = 200;

    private readonly Lock _lock = new();
    private readonly int _embedLimit = configuration?.GetValue<int?>("Search:EmbedLimit") ?? DefaultEmbedLimit;
    private bool _reindexRequested;
    private ContentCatalog? _catalog;
    private SearchIndex? _index;

    // What the store holds (as last loaded or saved), by item, and every known vector by passage hash.
    private Dictionary<string, IReadOnlyDictionary<string, float[]>> _stored = new(StringComparer.Ordinal);
    private Dictionary<string, float[]> _known = new(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether an index has been built (readiness, T-0054).</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>Gets the embedding model's ID.</summary>
    public string ModelId => embedder.ModelId;

    /// <summary>Gets the index for the current catalog, building it if needed.</summary>
    public SearchIndex Current
    {
        get
        {
            var catalog = content.Current;
            lock (_lock)
            {
                if (_index is null || !ReferenceEquals(_catalog, catalog))
                {
                    var previous = _index;
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var known = _known;
                    _index = SearchIndex.Build(catalog, embedder, hash => known.GetValueOrDefault(hash), _embedLimit);
                    var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (logger is not null)
                    {
                        SearchLog.Built(logger, _index.ChunkCount, embedder.ModelId, _index.EmbeddedCount, elapsed);
                    }

                    _catalog = catalog;
                    IsBuilt = true;
                    previous?.Dispose();
                    Remember(_index.ItemVectors);
                    if (_index.MissingCount > 0)
                    {
                        AskForReindex(_index.MissingCount);
                    }
                }

                return _index;
            }
        }
    }

    /// <summary>Gets the save of newly embedded vectors that the last build started (tests wait for it).</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads the stored vectors, so the next build reuses them. Called at startup and after each
    /// content reload, which is how vectors the re-index job wrote reach every replica.
    /// </summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>A task.</returns>
    public async Task LoadVectorsAsync(CancellationToken cancellationToken)
    {
        if (store is null || embedder.Dimensions == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>> items;
        try
        {
            items = await store.LoadAsync(embedder.ModelId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without stored vectors the build embeds everything: slower, not broken.
            if (logger is not null)
            {
                SearchLog.LoadFailed(logger, ex);
            }

            return;
        }

        lock (_lock)
        {
            _stored = new Dictionary<string, IReadOnlyDictionary<string, float[]>>(items, StringComparer.Ordinal);
            _known = Known(_stored);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _index?.Dispose();
            _index = null;
        }
    }

    // Under the lock: keeps the new vectors for later builds and saves the items whose chunks
    // changed in the background. Items no longer in the catalog stay stored: a replica that hasn't
    // reloaded yet mustn't delete what another just saved; the re-index job removes them.
    private void Remember(IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>> items)
    {
        var changed = items.Where(i => !_stored.TryGetValue(i.Key, out var old) || !SameChunks(old, i.Value)).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var (key, vectors) in changed)
        {
            _stored[key] = vectors;
        }

        _known = Known(_stored);
        if (store is not null)
        {
            Saving = SaveAsync(store, changed);
        }
    }

    // Every vector of the stored items, by passage hash. Rebuilt from them on each change, so a
    // passage that was edited away doesn't keep its vector until a restart (F-0064).
    private static Dictionary<string, float[]> Known(Dictionary<string, IReadOnlyDictionary<string, float[]>> stored)
    {
        var known = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var vector in stored.Values.SelectMany(i => i))
        {
            known[vector.Key] = vector.Value;
        }

        return known;
    }

    /// <summary>Gets how many vectors the provider keeps for builds (tests).</summary>
    internal int KnownVectors
    {
        get
        {
            lock (_lock)
            {
                return _known.Count;
            }
        }
    }

    private async Task SaveAsync(IVectorStore target, List<KeyValuePair<string, IReadOnlyDictionary<string, float[]>>> items)
    {
        await Task.Yield();
        try
        {
            foreach (var (key, vectors) in items)
            {
                await target.SaveAsync(embedder.ModelId, key, vectors, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The vectors stay in memory; the next build that misses them embeds them again.
            if (logger is not null)
            {
                SearchLog.SaveFailed(logger, items.Count, ex);
            }
        }
    }

    // Under the lock: once per process, so replicas don't queue a run on every rebuild.
    private void AskForReindex(int missing)
    {
        if (logger is not null)
        {
            SearchLog.Missing(logger, missing, _embedLimit);
        }

        if (reindex is null || _reindexRequested)
        {
            return;
        }

        _reindexRequested = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await reindex.RequestAsync("search index (chunks without vectors)", CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger is not null)
                {
                    SearchLog.RequestFailed(logger, ex);
                }
            }
        });
    }

    private static bool SameChunks(IReadOnlyDictionary<string, float[]> a, IReadOnlyDictionary<string, float[]> b) =>
        a.Count == b.Count && a.Keys.All(b.ContainsKey);
}

/// <summary>Registers search.</summary>
public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Adds the search index. Call after the content source is added: the index is built at
    /// startup, once the content has loaded (hosted services start in registration order).
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddSearch(this IServiceCollection services)
    {
        services.AddSingleton<IEmbedder>(provider =>
        {
            // Search:EmbeddingModel picks the model (ADR 0008): bge-small-en-v1.5 unless set;
            // Search:ModelPath overrides where its files are.
            var configuration = provider.GetRequiredService<IConfiguration>();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Search");
            var name = configuration["Search:EmbeddingModel"];
            if (EmbeddingModel.Find(name) is not { } model)
            {
                SearchLog.UnknownModel(logger, name!, string.Join(", ", EmbeddingModel.All.Select(m => m.Name)));
                return new NoEmbedder();
            }

            var configured = configuration["Search:ModelPath"];
            var directory = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "Models", model.Name)
                : configured;
            if (!OnnxEmbedder.IsAvailable(directory, model))
            {
                SearchLog.NoModel(logger, directory);
                return new NoEmbedder();
            }

            // A model that won't load leaves search on keywords rather than the site down.
            try
            {
                return new OnnxEmbedder(directory, model);
            }
            catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
            {
                SearchLog.ModelFailed(logger, directory, ex);
                return new NoEmbedder();
            }
        });
        services.TryAddSingleton<IVectorStore, MemoryVectorStore>();
        services.AddSingleton<SearchIndexProvider>();
        services.AddSingleton<Reindexer>();
        services.AddSingleton<HybridSearch>();
        services.AddHostedService<SearchWarmup>();
        return services;
    }
}

/// <summary>Log messages of search.</summary>
internal static partial class SearchLog
{
    /// <summary>Logs that the embedding model is missing.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="directory">Where the model was expected.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "No embedding model in {Directory}; search uses keywords only (run `just model`)")]
    public static partial void NoModel(ILogger logger, string directory);

    /// <summary>Logs that <c>Search:EmbeddingModel</c> names no model Weesite knows.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The configured name.</param>
    /// <param name="models">The names it could be.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Search:EmbeddingModel is {Name}, which isn't one of {Models}; search uses keywords only")]
    public static partial void UnknownModel(ILogger logger, string name, string models);

    /// <summary>Logs that the embedding model failed to load.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="directory">The model's directory.</param>
    /// <param name="exception">What went wrong.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The embedding model in {Directory} failed to load; search uses keywords only")]
    public static partial void ModelFailed(ILogger logger, string directory, Exception exception);

    /// <summary>Logs a finished index build.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="chunks">The number of chunks.</param>
    /// <param name="model">The embedding model's ID.</param>
    /// <param name="embedded">How many chunks were embedded rather than found stored.</param>
    /// <param name="milliseconds">How long it took.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Search index built: {Chunks} chunks, embeddings {Model} ({Embedded} embedded, the rest stored), {Milliseconds} ms")]
    public static partial void Built(ILogger logger, int chunks, string model, int embedded, long milliseconds);

    /// <summary>Logs that chunks were left without vectors.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="missing">How many.</param>
    /// <param name="limit">The embed limit.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Missing} chunks have no stored vector and exceed the embed limit ({Limit}): they're found by keywords only until the re-index job runs")]
    public static partial void Missing(ILogger logger, int missing, int limit);

    /// <summary>Logs that asking for a re-index failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">What went wrong.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Asking for a re-index failed; use Re-embed all in the admin")]
    public static partial void RequestFailed(ILogger logger, Exception exception);

    /// <summary>Logs that reading the stored vectors failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">What went wrong.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the stored vectors failed; the search index embeds every chunk")]
    public static partial void LoadFailed(ILogger logger, Exception exception);

    /// <summary>Logs that storing new vectors failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="items">How many items were to be saved.</param>
    /// <param name="exception">What went wrong.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing the vectors of {Items} items failed; they're embedded again next time")]
    public static partial void SaveFailed(ILogger logger, int items, Exception exception);
}

/// <summary>
/// Builds the index at startup, and again in the background after each content reload, so no
/// search waits for it (ADR 0005).
/// </summary>
internal sealed partial class SearchWarmup(SearchIndexProvider search, ContentCatalogProvider content, ILogger<SearchWarmup> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await search.LoadVectorsAsync(cancellationToken);
        _ = search.Current;
        content.Changed += Rebuild;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        content.Changed -= Rebuild;
        return Task.CompletedTask;
    }

    private void Rebuild(object? sender, EventArgs e) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await search.LoadVectorsAsync(CancellationToken.None);
                _ = search.Current;
            }
            catch (Exception ex)
            {
                LogRebuildFailed(ex);
                throw;
            }
        });

    [LoggerMessage(Level = LogLevel.Error, Message = "Rebuilding the search index after a content reload failed")]
    private partial void LogRebuildFailed(Exception exception);
}
