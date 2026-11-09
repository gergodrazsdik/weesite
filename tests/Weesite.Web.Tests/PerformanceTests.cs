using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content;

namespace Weesite.Web.Tests;

/// <summary>Output caching and response compression (T-0056).</summary>
internal sealed class PerformanceTests
{
    [Test]
    public async Task A_public_page_is_served_from_memory_the_second_time()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var first = await client.GetAsync(new Uri("/blog", UriKind.Relative));
        using var second = await client.GetAsync(new Uri("/blog", UriKind.Relative));

        await Assert.That(first.Headers.Age).IsNull();
        await Assert.That(second.Headers.Age).IsNotNull();
        await Assert.That(await second.Content.ReadAsStringAsync()).IsEqualTo(await first.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Each_theme_choice_gets_its_own_copy()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        _ = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Add("Cookie", "theme=dark");
        using var dark = await client.SendAsync(request);

        await Assert.That(await dark.Content.ReadAsStringAsync()).Contains("data-theme=\"dark\"");
    }

    [Test]
    public async Task A_content_reload_replaces_the_cached_pages()
    {
        using var copy = new SiteCopy();
        await using var factory = new SiteFactory(copy.Root);
        using var client = factory.CreateClient();
        var before = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));

        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "content", "posts", "fresh-from-the-cache.md"),
            "---\ntitle: \"Fresh from the cache\"\nslug: fresh-from-the-cache\ndate: 2026-11-01\n---\n\nNew.\n");
        await factory.Services.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None);
        var after = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));

        await Assert.That(before).DoesNotContain("Fresh from the cache");
        await Assert.That(after).Contains("Fresh from the cache");
    }

    [Test]
    [Arguments("/search?q=rendering")]
    [Arguments("/blog?utm_source=example")]
    [Arguments("/admin/sign-in")]
    [Arguments("/no-such-page")]
    public async Task Searches_queries_admin_pages_and_errors_are_never_cached(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var first = await client.GetAsync(new Uri(path, UriKind.Relative));
        using var second = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(second.Headers.Age).IsNull();
    }

    [Test]
    public async Task Signed_in_requests_skip_the_cache()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var anonymous = factory.CreateClient();
        using var editor = factory.CreateClient();
        editor.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        _ = await anonymous.GetStringAsync(new Uri("/blog", UriKind.Relative));
        using var signedIn = await editor.GetAsync(new Uri("/blog", UriKind.Relative));

        await Assert.That(signedIn.Headers.Age).IsNull();
    }

    [Test]
    [Arguments("br")]
    [Arguments("gzip")]
    public async Task Public_pages_are_compressed_and_admin_pages_are_not(string encoding)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var page = await Get(client, "/blog/notes-on-static-rendering", encoding);
        using var feed = await Get(client, "/feed.xml", encoding);
        using var admin = await Get(client, "/admin/sign-in", encoding);

        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.Content.Headers.ContentEncoding).IsEquivalentTo([encoding]);
        await Assert.That(feed.Content.Headers.ContentEncoding).IsEquivalentTo([encoding]);
        await Assert.That(admin.Content.Headers.ContentEncoding).IsEmpty();
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string path, string encoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
        return await client.SendAsync(request);
    }
}
