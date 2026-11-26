using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Weesite.Web.Tests;

/// <summary>Posts, pages, the blog list, redirects and 404s, on the sample site.</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class ContentPagesTests(SharedSite shared)
{
    [Test]
    public async Task Blog_lists_the_posts_newest_first()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));

        await Assert.That(html).Contains("<h1 class=\"page-title\">Blog</h1>");
        await Assert.That(html).Contains("<h2 class=\"year\" id=\"y2025\">2025</h2>");
        var newer = html.IndexOf("Notes on static rendering", StringComparison.Ordinal);
        var older = html.IndexOf("Hello, world", StringComparison.Ordinal);
        await Assert.That(newer).IsGreaterThan(-1);
        await Assert.That(older).IsGreaterThan(newer);
    }

    [Test]
    public async Task Post_shows_title_date_author_tags_and_body()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));

        await Assert.That(html).Contains("<h1 id=\"post-title\" class=\"page-title\">Hello, world</h1>");
        await Assert.That(html).Contains("<time datetime=\"2026-01-15\">2026-01-15</time>");
        await Assert.That(html).Contains("by Example Author");
        await Assert.That(html).Contains("href=\"/blog/tags/welcome\"");
        await Assert.That(html).Contains("<title>Hello, world · Example Site</title>");
    }

    [Test]
    public async Task The_seo_title_goes_only_into_the_title_element()
    {
        // The sample post's front matter has a shorter seo_title; the heading and previews keep the title.
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/testing-blazor-ssr-with-tunit-and-playwright", UriKind.Relative));

        await Assert.That(html).Contains("<title>Testing Blazor SSR with TUnit · Example Site</title>");
        await Assert.That(html).Contains("<h1 id=\"post-title\" class=\"page-title\">Testing a Blazor SSR site with TUnit and Playwright</h1>");
        await Assert.That(html).Contains("<meta property=\"og:title\" content=\"Testing a Blazor SSR site with TUnit and Playwright\" />");
    }

    [Test]
    public async Task Posts_have_contents_older_and_newer_links_and_breadcrumbs()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        // The neighbours come from the catalog, newest first, so the test holds as the sample grows.
        var posts = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current.Posts;
        var at = posts.ToList().FindIndex(p => p.Slug == "notes-on-static-rendering");
        var html = await client.GetStringAsync(new Uri("/blog/notes-on-static-rendering", UriKind.Relative));
        var oldest = await client.GetStringAsync(new Uri("/blog/" + Uri.EscapeDataString(posts[^1].Slug), UriKind.Relative));

        await Assert.That(html).Contains("<li><a href=\"#a-list\">A list</a></li>");
        await Assert.That(html).Contains($"<a href=\"/blog/{posts[at + 1].Slug}\" rel=\"prev\">");
        await Assert.That(html).Contains("rel=\"next\">");
        await Assert.That(html).Contains("<a href=\"/blog#y2026\">2026</a>");
        await Assert.That(html).Contains("<div class=\"bar\"><span>csharp</span></div>");
        await Assert.That(html).Contains("copy-code.");
        await Assert.That(oldest).DoesNotContain("rel=\"prev\"");
    }

    [Test]
    public async Task Posts_show_their_reading_time()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var post = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var list = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));

        await Assert.That(post).Contains("1 min read");
        await Assert.That(list).Contains("<span aria-hidden=\"true\">1 min</span><span class=\"visually-hidden\">1 min read</span>");
    }

    [Test]
    public async Task Related_posts_never_include_the_post_itself()
    {
        // The fake embedder's scores say little about meaning; the real model's related posts
        // are checked end to end (PageTests).
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var start = html.IndexOf("<section class=\"related\"", StringComparison.Ordinal);
        var related = start < 0 ? "" : html[start..];

        await Assert.That(related).DoesNotContain("href=\"/blog/hello-world\"");
    }

    [Test]
    public async Task A_site_with_one_post_says_1_post()
    {
        // T-0123: NVDA read "1 posts" on the home page.
        using var copy = new SiteCopy();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(copy.Root, "content", "posts")).Where(f => !f.EndsWith("hello-world.md", StringComparison.Ordinal)))
        {
            File.Delete(file);
        }

        await using var factory = new SiteFactory(copy.Root);
        using var client = factory.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(home).Contains("<span class=\"count\">1 post</span>");
    }

    [Test]
    public async Task Tag_index_lists_every_tag_with_its_post_count()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/tags", UriKind.Relative));

        await Assert.That(html).Contains("<h1 class=\"page-title\">Tags</h1>");
        await Assert.That(html).Contains("<a href=\"/blog/tags/performance\" rel=\"tag\"><span class=\"dot violet\" aria-hidden=\"true\"></span>#performance</a>");
        await Assert.That(html).Contains("1 post");
        await Assert.That(html.IndexOf("performance", StringComparison.Ordinal)).IsLessThan(html.IndexOf("welcome", StringComparison.Ordinal));
    }

    [Test]
    public async Task Tag_page_lists_its_posts()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/tags/welcome", UriKind.Relative));

        await Assert.That(html).Contains("<h1 class=\"page-title\">Posts tagged <span class=\"tag big amber\">#welcome</span></h1>");
        await Assert.That(html).Contains("<p class=\"lede\">1 post</p>");
        await Assert.That(html).Contains("href=\"/blog/tags/welcome/feed.xml\"");
        await Assert.That(html).Contains("href=\"/blog/hello-world\"");
        await Assert.That(html).DoesNotContain("href=\"/blog/notes-on-static-rendering\"");
        await Assert.That(html).Contains("<title>Posts tagged “welcome” · Example Site</title>");
    }

    [Test]
    public async Task Unknown_tag_is_not_found()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/blog/tags/no-such-tag", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Post_body_is_rendered_markdown_with_highlighted_code()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/notes-on-static-rendering", UriKind.Relative));

        await Assert.That(html).Contains("<h2 id=\"a-list\">A list</h2>");
        await Assert.That(html).Contains("<span class=\"hl-keyword\">var</span>");
        await Assert.That(html).Contains("width=\"1800\" height=\"1200\"");
    }

    [Test]
    public async Task Content_page_renders_at_its_slug()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/about", UriKind.Relative));

        await Assert.That(html).Contains("<h1 id=\"page-title\" class=\"page-title\">About</h1>");
        await Assert.That(html).Contains("<p class=\"lede\">Who writes this sample site.</p>");
        await Assert.That(html).Contains("aria-current=\"page\"");
    }

    [Test]
    public async Task The_home_page_s_years_and_posts_sit_under_latest_posts()
    {
        // F-0246: the years were h2, the level of "Latest posts" itself.
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).Contains("<h2 id=\"latest-posts\" class=\"label\">");
        await Assert.That(html).Contains("<h3 class=\"year\" id=\"y");
        await Assert.That(html).DoesNotContain("<h2 class=\"year\"");
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(html, "<h4[^>]*><a href=\"/blog/")).IsTrue();
    }

    [Test]
    public async Task The_terms_spelt_in_capitals_are_still_the_terms()
    {
        // F-0245: routing ignores case, the page chose by the path's exact spelling.
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/TERMS", UriKind.Relative));

        await Assert.That(html).Contains("Terms of use");
        await Assert.That(html).Contains("<link rel=\"canonical\" href=\"http://localhost/terms\" />");
    }

    [Test]
    public async Task Earlier_slug_redirects_permanently()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/blog/welcome", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MovedPermanently);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("/blog/hello-world");

        // Kept a day, not for good: the slug may be given to a new post later (F-0245).
        await Assert.That(response.Headers.CacheControl?.ToString()).IsEqualTo("public, max-age=86400");
    }

    [Test]
    [Arguments("/blog/no-such-post")]
    [Arguments("/no-such-page")]
    public async Task Unknown_slugs_are_not_found(string path)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(html).Contains("Page not found");
    }

    [Test]
    public async Task Home_lists_the_latest_posts()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).Contains("Latest posts");
        await Assert.That(html).Contains("href=\"/blog/moving-from-bicep-to-opentofu\"");
        await Assert.That(html).Contains("<a href=\"/blog\">All posts, by year <span aria-hidden=\"true\">→</span></a>");
        await Assert.That(html).Contains("<time datetime=\"2026-10-12\">Oct 12</time>");
        await Assert.That(html).Contains("class=\"banner banner-default\"");
    }
}
