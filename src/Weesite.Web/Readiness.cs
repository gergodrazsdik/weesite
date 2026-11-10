using Microsoft.Extensions.Diagnostics.HealthChecks;
using Weesite.Content;
using Weesite.Search;

namespace Weesite.Web;

/// <summary>
/// Readiness (brief, Startup, health and deploys; T-0054): the app reports ready on <c>/health</c>
/// only once the content has loaded and the search index (with the embedding model) is built, so
/// Container Apps sends no traffic to a half-started replica and a new revision takes over only
/// once it's ready. <c>/alive</c> stays the process check.
/// </summary>
internal static class Readiness
{
    /// <summary>Adds the content and search checks to <c>/health</c>.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddReadinessChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<ContentReady>("content")
            .AddCheck<SearchReady>("search");
        return services;
    }

    /// <summary>Healthy once the content and settings have loaded.</summary>
    internal sealed class ContentReady(ContentCatalogProvider content) : IHealthCheck
    {
        /// <inheritdoc />
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(content.IsLoaded ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("The content hasn't loaded yet."));
    }

    /// <summary>Healthy once the search index has been built (which loads the embedding model).</summary>
    internal sealed class SearchReady(SearchIndexProvider search) : IHealthCheck
    {
        /// <inheritdoc />
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(search.IsBuilt ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("The search index isn't built yet."));
    }
}
