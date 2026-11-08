using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>
/// Holds the search index for the current content catalog and rebuilds it when the catalog
/// changes (a reload builds a new catalog). Blob stays the only source of truth.
/// </summary>
public sealed class SearchIndexProvider(ContentCatalogProvider content, IEmbedder embedder, ILogger<SearchIndexProvider>? logger = null) : IDisposable
{
    private readonly Lock _lock = new();
    private ContentCatalog? _catalog;
    private SearchIndex? _index;

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
                    _index = SearchIndex.Build(catalog, embedder);
                    var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (logger is not null)
                    {
                        SearchLog.Built(logger, _index.ChunkCount, embedder.ModelId, elapsed);
                    }

                    _catalog = catalog;
                    previous?.Dispose();
                }

                return _index;
            }
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
            var configured = provider.GetRequiredService<IConfiguration>()["Search:ModelPath"];
            var directory = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "Models", OnnxEmbedder.ModelName)
                : configured;
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Search");
            if (!OnnxEmbedder.IsAvailable(directory))
            {
                SearchLog.NoModel(logger, directory);
                return new NoEmbedder();
            }

            // A model that won't load leaves search on keywords rather than the site down.
            try
            {
                return new OnnxEmbedder(directory);
            }
            catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
            {
                SearchLog.ModelFailed(logger, directory, ex);
                return new NoEmbedder();
            }
        });
        services.AddSingleton<SearchIndexProvider>();
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
    /// <param name="milliseconds">How long it took.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Search index built: {Chunks} chunks, embeddings {Model}, {Milliseconds} ms")]
    public static partial void Built(ILogger logger, int chunks, string model, long milliseconds);
}

/// <summary>Builds the index at startup, so the first search doesn't wait for it.</summary>
internal sealed class SearchWarmup(SearchIndexProvider search) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = search.Current;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
