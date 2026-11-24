using System.Net;

namespace Weesite.Web.Tests;

/// <summary>The /search page and the header search box.</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class SearchTests(SharedSite shared)
{
    [Test]
    public async Task Search_lists_matching_posts_and_pages_with_snippets()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/search?q=rendering", UriKind.Relative));

        await Assert.That(html).Contains("<h2 id=\"search-results\" class=\"results-heading\">Results for “rendering”</h2>");
        await Assert.That(html).Contains("<a href=\"/blog/notes-on-static-rendering\">Notes on static rendering</a>");
        await Assert.That(html).Contains("<p class=\"snippet\"><code>");
        await Assert.That(html).Contains("<mark>");
        await Assert.That(html).Contains("<span>post</span>");
        await Assert.That(html).Contains("<meta name=\"robots\" content=\"noindex\"");
    }

    [Test]
    public async Task Filters_keep_the_query_and_show_only_their_kind()
    {
        // "site" is in the About page and in posts, so each filter has results of its own kind.
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var all = await client.GetStringAsync(new Uri("/search?q=site", UriKind.Relative));
        var pages = await client.GetStringAsync(new Uri("/search?q=site&kind=pages", UriKind.Relative));
        var posts = await client.GetStringAsync(new Uri("/search?q=site&kind=posts", UriKind.Relative));

        await Assert.That(all).Contains("<a href=\"/search?q=site\" aria-current=\"page\">All</a>");
        await Assert.That(all).Contains("<a href=\"/search?q=site&amp;kind=pages\">Pages</a>");
        await Assert.That(pages).Contains("<a href=\"/search?q=site&amp;kind=pages\" aria-current=\"page\">Pages</a>");
        await Assert.That(pages).Contains("<input type=\"hidden\" name=\"kind\" value=\"pages\" />");
        await Assert.That(all).Contains("<span>post</span>");
        await Assert.That(all).Contains("<span>page</span>");
        await Assert.That(pages).Contains("<span>page</span>");
        await Assert.That(pages).DoesNotContain("<span>post</span>");
        await Assert.That(posts).Contains("<span>post</span>");
        await Assert.That(posts).DoesNotContain("<span>page</span>");
    }

    [Test]
    public async Task Search_without_a_query_shows_only_the_form()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/search", UriKind.Relative));

        await Assert.That(html).Contains("<h1 id=\"search-title\" class=\"page-title\">Search</h1>");
        await Assert.That(html).DoesNotContain("class=\"filters\"");
        await Assert.That(html).DoesNotContain("search-results");
    }

    [Test]
    public async Task Search_without_matches_says_so()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/search?q=zzzqqq", UriKind.Relative));

        await Assert.That(html).Contains("Nothing found.");
    }

    [Test]
    public async Task A_filter_that_rules_out_every_match_says_what_does_match()
    {
        // "alhambra" is only in posts (U-68): Pages says so, counts them and links to them all.
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var all = await client.GetStringAsync(new Uri("/search?q=alhambra", UriKind.Relative));
        var pages = await client.GetStringAsync(new Uri("/search?q=alhambra&kind=pages", UriKind.Relative));
        var none = await client.GetStringAsync(new Uri("/search?q=zzzqqq&kind=pages", UriKind.Relative));
        var posts = System.Text.RegularExpressions.Regex.Count(all, "<span>post</span>");

        await Assert.That(posts).IsGreaterThan(1);
        await Assert.That(all).DoesNotContain("<span>page</span>");
        await Assert.That(pages).Contains($"<p class=\"empty\">No pages match, but {posts} posts do. <a href=\"/search?q=alhambra\">Show all results</a></p>");
        await Assert.That(pages).DoesNotContain("Nothing found.");
        await Assert.That(none).Contains("Nothing found.");
        await Assert.That(none).DoesNotContain("Show all results");
    }

    [Test]
    public async Task Query_text_is_escaped_where_it_is_shown()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/search?q=%3Cscript%3Ealert(1)%3C%2Fscript%3E", UriKind.Relative));

        await Assert.That(html).DoesNotContain("<script>alert(1)</script>");
        await Assert.That(html).Contains("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog/hello-world")]
    public async Task Every_page_has_the_search_box(string path)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        await Assert.That(html).Contains("<form class=\"search-box\" role=\"search\" method=\"get\" action=\"/search\" aria-label=\"Search the site\">");
        await Assert.That(html).Contains("name=\"q\"");
    }

    [Test]
    public async Task Search_page_is_a_200_for_any_query()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/search?q={new string('a', 5000)}", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
