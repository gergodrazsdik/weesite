using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>
/// Holds the search index for the current content catalog and rebuilds it in the background when
/// the catalog changes (a reload builds a new catalog), serving the previous index meanwhile, so no
/// request waits for a build (F-0102). Blob stays the only source of truth: chunk vectors are kept
/// in the vector store, so a build embeds only new or changed chunks (T-0069) and stores them.
/// A build embeds at most <c>Search:EmbedLimit</c> chunks (200); when more are missing, as after a
/// model change, it asks for a re-index, and the re-index job embeds the rest.
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

    // The catalog whose build last failed: not tried again until the catalog or the stored vectors
    // change, so a failing build doesn't run again on every request (F-0247).
    private ContentCatalog? _failed;

    // The index before the current one: a request may still be searching it, so it's disposed
    // only when the next one replaces it.
    private SearchIndex? _retired;

    // The background build: whether it runs, and whether stored vectors were read since the last
    // build began, so the next build uses them even when the catalog is the same (F-0111).
    private bool _building;
    private bool _vectorsChanged;

    // Whether the index is on keywords until the model has loaded, and a build waits for it.
    private bool _waitingForModel;

    // Set once an index no longer waits for the model: built with it, or without one for good.
    private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _build = Task.CompletedTask;
    private bool _disposed;

    // What the store holds (as last loaded or saved), by item, and every known vector by passage hash.
    private Dictionary<string, IReadOnlyDictionary<string, float[]>> _stored = new(StringComparer.Ordinal);
    private Dictionary<string, float[]> _known = new(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether an index has been built (readiness, T-0054).</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>
    /// Gets a task that finishes once an index is built with the embedding model, or once it's
    /// clear none will be (no model, or it failed to load, or a build failed): for callers that
    /// need search by meaning, such as tests. Readiness doesn't wait for it (ADR 0012).
    /// </summary>
    public Task WhenComplete => _complete.Task;

    /// <summary>Gets the embedding model's ID.</summary>
    public string ModelId => embedder.ModelId;

    /// <summary>
    /// Raised, outside the provider's lock, after a new index has replaced the previous one: what
    /// was rendered from the previous one (a post's related posts) is out of date (F-0245).
    /// </summary>
    public event EventHandler? Swapped;

    /// <summary>
    /// Gets the latest index. The first call waits for the first build; later ones never wait:
    /// when the catalog has changed since, they start a build in the background and get the
    /// previous index until it's done.
    /// </summary>
    public SearchIndex Current
    {
        get
        {
            Task first;
            lock (_lock)
            {
                if (_index is not null)
                {
                    if (!ReferenceEquals(_catalog, content.Current) && !ReferenceEquals(_failed, content.Current))
                    {
                        StartBuild();
                    }

                    return _index;
                }

                first = StartBuild();
            }

            first.GetAwaiter().GetResult();
            lock (_lock)
            {
                return _index ?? throw new ObjectDisposedException(nameof(SearchIndexProvider));
            }
        }
    }

    /// <summary>Gets the save of newly embedded vectors that the last build started (tests wait for it).</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Builds the index for the current catalog and stored vectors, unless it's already built for
    /// them, and waits for it.
    /// </summary>
    /// <returns>The index.</returns>
    public async Task<SearchIndex> RefreshAsync()
    {
        Task build;
        lock (_lock)
        {
            build = StartBuild();
        }

        await build;
        lock (_lock)
        {
            return _index ?? throw new ObjectDisposedException(nameof(SearchIndexProvider));
        }
    }

    /// <summary>
    /// Reads the stored vectors, so the next build reuses them. Called at startup and after each
    /// content reload, which is how vectors the re-index job wrote reach every replica.
    /// </summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>A task.</returns>
    public async Task LoadVectorsAsync(CancellationToken cancellationToken)
    {
        // While the model loads, its ID isn't known yet: the build after the load reads them (ADR 0012).
        if (store is null || embedder is LoadingEmbedder { IsLoaded: false } || embedder.Dimensions == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>> items;
        try
        {
            items = await store.LoadAsync(embedder.ModelId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A storage timeout is a failure like any other, also at startup, where it would
            // otherwise stop the app from starting (F-0208, F-0230).
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
            _vectorsChanged = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _retired?.Dispose();
            _index?.Dispose();
            (_retired, _index) = (null, null);
        }
    }

    // Under the lock: starts the build loop unless it runs; it builds until the index matches the
    // catalog and the vectors, so a change made while it runs gets its own build.
    private Task StartBuild()
    {
        if (!_building && !_disposed)
        {
            _building = true;
            _build = Task.Run(BuildAsync);
        }

        return _build;
    }

    private async Task BuildAsync()
    {
        await Task.Yield();
        while (true)
        {
            ContentCatalog catalog;
            Dictionary<string, float[]> known;
            lock (_lock)
            {
                catalog = content.Current;
                if (_disposed || (_index is not null && (ReferenceEquals(_catalog, catalog) || ReferenceEquals(_failed, catalog)) && !_vectorsChanged))
                {
                    _building = false;
                    return;
                }

                _vectorsChanged = false;
                known = _known;
            }

            // While the model loads, the index is built on keywords, so the replica can report
            // ready without it; the build after the load adds the embeddings (ADR 0012).
            var loading = embedder as LoadingEmbedder;
            var keywordsOnly = loading is { IsLoaded: false };

            // Outside the lock: embedding new chunks can take minutes on a small replica, and
            // requests keep the previous index meanwhile.
            SearchIndex? index = null;
            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                index = keywordsOnly
                    ? SearchIndex.Build(catalog, new NoEmbedder())
                    : SearchIndex.Build(catalog, embedder, hash => known.GetValueOrDefault(hash), _embedLimit);
                var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (logger is not null)
                {
                    if (keywordsOnly)
                    {
                        SearchLog.BuiltOnKeywords(logger, index.ChunkCount, elapsed);
                    }
                    else
                    {
                        SearchLog.Built(logger, index.ChunkCount, embedder.ModelId, index.EmbeddedCount, elapsed);
                    }
                }

                lock (_lock)
                {
                    if (_disposed)
                    {
                        _building = false;
                        return;
                    }

                    _retired?.Dispose();
                    (_retired, _index, _catalog, _failed) = (_index, index, catalog, null);
                    index = null;
                    IsBuilt = true;
                    if (keywordsOnly)
                    {
                        // Nothing to save or ask for without the model; once it's in, a build with it.
                        WaitForModel(loading!);
                    }
                    else
                    {
                        _complete.TrySetResult();
                        Remember(_index.ItemVectors);
                        if (_index.MissingCount > 0)
                        {
                            AskForReindex(_index.MissingCount);
                        }
                        else
                        {
                            // Asked again the next time chunks are missing, as after a large import.
                            _reindexRequested = false;
                        }
                    }
                }

                Swapped?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One chunk the model can't embed, or SQLite failing: the previous index stays, and
                // the app starts on keywords when there's none yet (F-0247).
                if (logger is not null)
                {
                    SearchLog.BuildFailed(logger, ex);
                }

                if (!Failed(catalog))
                {
                    throw;
                }
            }
            catch
            {
                lock (_lock)
                {
                    _building = false;
                }

                throw;
            }
            finally
            {
                index?.Dispose();
            }
        }
    }

    // Under the lock: once the model has loaded, reads the stored vectors and builds again, though
    // the catalog may be the same (ADR 0012). Once per wait, however many builds ran meanwhile.
    private void WaitForModel(LoadingEmbedder loading)
    {
        if (_waitingForModel)
        {
            return;
        }

        _waitingForModel = true;
        _ = loading.WhenLoaded.ContinueWith(_ => ModelLoadedAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async Task ModelLoadedAsync()
    {
        // Failing to read them is logged there; the build then embeds what it can.
        await LoadVectorsAsync(CancellationToken.None);
        lock (_lock)
        {
            _waitingForModel = false;
            _vectorsChanged = true;
            StartBuild();
        }
    }

    // After a failed build: remembers the catalog, and gives a first index on keywords alone.
    // False when even that failed: the build loop then stops and rethrows.
    private bool Failed(ContentCatalog catalog)
    {
        bool first;
        lock (_lock)
        {
            _failed = catalog;
            first = _index is null && !_disposed;
        }

        if (!first)
        {
            return true;
        }

        SearchIndex? keywords = null;
        try
        {
            keywords = SearchIndex.Build(catalog, new NoEmbedder());
            lock (_lock)
            {
                if (!_disposed)
                {
                    (_index, _catalog) = (keywords, catalog);
                    keywords = null;
                    IsBuilt = true;
                    _complete.TrySetResult();
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger is not null)
            {
                SearchLog.BuildFailed(logger, ex);
            }

            lock (_lock)
            {
                _building = false;
            }

            return false;
        }
        finally
        {
            keywords?.Dispose();
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing cancels it, so a cancellation is a storage timeout (F-0230).
            // The vectors stay in memory; the next build that misses them embeds them again.
            if (logger is not null)
            {
                SearchLog.SaveFailed(logger, items.Count, ex);
            }
        }
    }

    // Under the lock: once until a build has every vector again, so replicas don't queue a run
    // on every rebuild while the job works (the request itself skips one already waiting).
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing cancels it, so a cancellation is a timeout (F-0230).
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

/// <summary>
/// The embedding model's load, begun as soon as the configuration is read: the app begins it at
/// the start of <c>Program</c>, while Key Vault waits for its first token (ADR 0012), and search
/// registers it. What it has to say is logged once a logger exists (<see cref="Report"/>).
/// </summary>
public sealed class EmbedderStart
{
    private readonly Action<ILogger> _report;
    private int _reported;

    private EmbedderStart(IEmbedder embedder, Action<ILogger> report) => (Embedder, _report) = (embedder, report);

    /// <summary>Gets the embedder: loading in the background, or none when there's no model.</summary>
    public IEmbedder Embedder { get; }

    /// <summary>
    /// Begins loading the model <c>Search:EmbeddingModel</c> names (ADR 0008: bge-small-en-v1.5
    /// unless set), from <c>Search:ModelPath</c> or the app's <c>Models</c> folder.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The load, begun.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The embedder goes to the service container through AddSearch's factory, and the container disposes it.")]
    public static EmbedderStart Begin(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var name = configuration["Search:EmbeddingModel"];
        if (EmbeddingModel.Find(name) is not { } model)
        {
            return new(new NoEmbedder(), logger => SearchLog.UnknownModel(logger, name!, string.Join(", ", EmbeddingModel.All.Select(m => m.Name))));
        }

        var configured = configuration["Search:ModelPath"];
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "Models", model.Name)
            : configured;
        if (!OnnxEmbedder.IsAvailable(directory, model))
        {
            return new(new NoEmbedder(), logger => SearchLog.NoModel(logger, directory));
        }

        // Loaded in the background (F-0107). A model that won't load leaves search on keywords
        // rather than the site down; what happened is logged once there's a logger.
        TimeSpan elapsed = default;
        Exception? failure = null;
        var loading = new LoadingEmbedder(() =>
        {
            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var embedder = new OnnxEmbedder(directory, model);
                elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                return embedder;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Not only ONNX Runtime's own errors: a tokenizer file that fails to read
                // would otherwise make every member of the embedder throw (F-0247).
                failure = ex;
                return new NoEmbedder();
            }
        });

        return new(loading, logger => loading.WhenLoaded.ContinueWith(
            _ =>
            {
                if (failure is not null)
                {
                    SearchLog.ModelFailed(logger, directory, failure);
                }
                else
                {
                    SearchLog.ModelLoaded(logger, model.Name, (long)elapsed.TotalMilliseconds);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default));
    }

    /// <summary>Logs, once, what the load found, and how it went when it has finished.</summary>
    /// <param name="logger">The logger.</param>
    public void Report(ILogger logger)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            _report(logger);
        }
    }
}

/// <summary>Registers search.</summary>
public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Adds the search index. Call after the content source is added: the index is built at
    /// startup, once the content has loaded (hosted services start in registration order).
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="start">The model's load, when the app started it before building the host (ADR 0012); otherwise it starts when the embedder is first asked for.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddSearch(this IServiceCollection services, EmbedderStart? start = null)
    {
        services.AddSingleton<IEmbedder>(provider =>
        {
            var begun = start ?? EmbedderStart.Begin(provider.GetRequiredService<IConfiguration>());
            begun.Report(provider.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Search"));
            return begun.Embedder;
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

    /// <summary>Logs that the embedding model loaded (F-0107).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The model.</param>
    /// <param name="milliseconds">How long it took.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding model {Name} loaded in {Milliseconds} ms")]
    public static partial void ModelLoaded(ILogger logger, string name, long milliseconds);

    /// <summary>Logs a build on keywords alone, made while the embedding model loads (ADR 0012).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="chunks">The number of chunks.</param>
    /// <param name="milliseconds">How long it took.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Search index built on keywords while the embedding model loads: {Chunks} chunks, {Milliseconds} ms")]
    public static partial void BuiltOnKeywords(ILogger logger, int chunks, long milliseconds);

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

    /// <summary>Logs that building the search index failed (F-0247).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">What went wrong.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Building the search index failed; the previous index stays (keywords only when there's none) until the content changes")]
    public static partial void BuildFailed(ILogger logger, Exception exception);

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
        try
        {
            await search.LoadVectorsAsync(cancellationToken);
            await search.RefreshAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            // Search failing mustn't stop the site from starting (F-0247); the first search
            // tries again.
            LogStartFailed(ex);
        }

        content.Changed += Rebuild;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        content.Changed -= Rebuild;
        return Task.CompletedTask;
    }

    // The vectors first, so the build after a reload uses what the re-index job stored; a build a
    // request started meanwhile is followed by one with them (F-0111).
    private void Rebuild(object? sender, EventArgs e) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await search.LoadVectorsAsync(CancellationToken.None);
                await search.RefreshAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing cancels it, so a cancellation is a timeout, logged like any failure (F-0230).
                LogRebuildFailed(ex);
            }
        });

    [LoggerMessage(Level = LogLevel.Error, Message = "Rebuilding the search index after a content reload failed")]
    private partial void LogRebuildFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Building the search index at startup failed; the site starts without it")]
    private partial void LogStartFailed(Exception exception);
}
