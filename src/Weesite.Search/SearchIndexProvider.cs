using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>
/// Holds the search index for the current content catalog and rebuilds it when the catalog
/// changes (a reload builds a new catalog). Blob stays the only source of truth.
/// </summary>
public sealed class SearchIndexProvider(ContentCatalogProvider content) : IDisposable
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
                    _index = SearchIndex.Build(catalog);
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
        services.AddSingleton<SearchIndexProvider>();
        services.AddHostedService<SearchWarmup>();
        return services;
    }
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
