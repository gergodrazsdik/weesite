using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Weesite.Web.Tests;

/// <summary>Posts, pages, the blog list, redirects and 404s, on the sample site.</summary>
internal sealed class ContentPagesTests
{
    [Test]
    public async Task Blog_lists_the_posts_newest_first()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));

        await Assert.That(html).Contains("<h1>Blog</h1>");
        var newer = html.IndexOf("Notes on static rendering", StringComparison.Ordinal);
        var older = html.IndexOf("Hello, world", StringComparison.Ordinal);
        await Assert.That(newer).IsGreaterThan(-1);
        await Assert.That(older).IsGreaterThan(newer);
    }

    [Test]
    public async Task Post_shows_title_date_author_tags_and_body()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));

        await Assert.That(html).Contains("<h1>Hello, world</h1>");
        await Assert.That(html).Contains("<time datetime=\"2026-01-15\">15 January 2026</time>");
        await Assert.That(html).Contains("by Example Author");
        await Assert.That(html).Contains("href=\"/blog/tags/welcome\"");
        await Assert.That(html).Contains("<title>Hello, world · Example Site</title>");
    }

    [Test]
    public async Task Post_body_is_rendered_markdown_with_highlighted_code()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/notes-on-static-rendering", UriKind.Relative));

        await Assert.That(html).Contains("<h2 id=\"a-list\">A list</h2>");
        await Assert.That(html).Contains("<span class=\"hl-keyword\">var</span>");
        await Assert.That(html).Contains("width=\"1800\" height=\"1200\"");
    }

    [Test]
    public async Task Content_page_renders_at_its_slug()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/about", UriKind.Relative));

        await Assert.That(html).Contains("<h1>About</h1>");
        await Assert.That(html).Contains("aria-current=\"page\"");
    }

    [Test]
    public async Task Earlier_slug_redirects_permanently()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/blog/welcome", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MovedPermanently);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("/blog/hello-world");
    }

    [Test]
    [Arguments("/blog/no-such-post")]
    [Arguments("/no-such-page")]
    public async Task Unknown_slugs_are_not_found(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(html).Contains("Page not found");
    }

    [Test]
    public async Task Home_lists_the_latest_posts()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).Contains("Latest posts");
        await Assert.That(html).Contains("href=\"/blog/hello-world\"");
    }
}
