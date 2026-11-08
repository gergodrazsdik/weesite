using System.Net;

namespace Weesite.Web.Tests;

/// <summary>The app starts, renders its pages and answers the platform's probes.</summary>
internal sealed class SmokeTests
{
    [Test]
    public async Task Home_page_renders_as_html()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        await Assert.That(html).Contains("<h1>Example Site</h1>");
    }

    [Test]
    public async Task Pages_render_without_framework_script()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        // Static server rendering only: the public site must work without JavaScript.
        await Assert.That(html).DoesNotContain("blazor.web.js");
    }

    [Test]
    public async Task Unknown_path_returns_404_with_the_not_found_page()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/no-such-page", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(html).Contains("Page not found");
    }

    [Test]
    public async Task NoIndex_setting_adds_the_robots_header()
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(builder => builder.UseSetting("Site:NoIndex", "true"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        await Assert.That(response.Headers.GetValues("X-Robots-Tag")).Contains("noindex, nofollow");
    }

    [Test]
    public async Task Pages_are_indexable_by_default()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        await Assert.That(response.Headers.Contains("X-Robots-Tag")).IsFalse();
    }

    [Test]
    [Arguments("/health")]
    [Arguments("/alive")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).IsEqualTo("Healthy");
    }
}
