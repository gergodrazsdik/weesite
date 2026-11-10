using Microsoft.Extensions.Diagnostics.HealthChecks;
using Weesite.Content;
using Weesite.Search;

namespace Weesite.Web.Tests;

/// <summary>/health waits for the content and the search index (T-0054).</summary>
internal sealed class ReadinessTests
{
    [Test]
    public async Task Content_is_ready_once_it_has_loaded()
    {
        var content = new ContentCatalogProvider();
        var check = new Readiness.ContentReady(content);

        var before = await check.CheckHealthAsync(new HealthCheckContext());
        content.Set(ContentCatalog.Build(SiteSettings.Empty, []));
        var after = await check.CheckHealthAsync(new HealthCheckContext());

        await Assert.That(before.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(after.Status).IsEqualTo(HealthStatus.Healthy);
    }

    [Test]
    public async Task Search_is_ready_once_its_index_is_built()
    {
        var content = new ContentCatalogProvider();
        content.Set(ContentCatalog.Build(SiteSettings.Empty, []));
        using var search = new SearchIndexProvider(content, new NoEmbedder());
        var check = new Readiness.SearchReady(search);

        var before = await check.CheckHealthAsync(new HealthCheckContext());
        _ = search.Current;
        var after = await check.CheckHealthAsync(new HealthCheckContext());

        await Assert.That(before.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(after.Status).IsEqualTo(HealthStatus.Healthy);
    }
}
