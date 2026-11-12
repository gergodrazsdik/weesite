using System.Net;

namespace Weesite.Web.Tests;

/// <summary>The /search page and the header search box.</summary>
internal sealed class SearchTests
{
    [Test]
    public async Task Search_lists_matching_posts_and_pages_with_snippets()
    {
        await using var factory = new SiteFactory();
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
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var all = await client.GetStringAsync(new Uri("/search?q=example", UriKind.Relative));
        var pages = await client.GetStringAsync(new Uri("/search?q=example&kind=pages", UriKind.Relative));
        var posts = await client.GetStringAsync(new Uri("/search?q=example&kind=posts", UriKind.Relative));

        await Assert.That(all).Contains("<a href=\"/search?q=example\" aria-current=\"page\">All</a>");
        await Assert.That(all).Contains("<a href=\"/search?q=example&amp;kind=pages\">Pages</a>");
        await Assert.That(pages).Contains("<a href=\"/search?q=example&amp;kind=pages\" aria-current=\"page\">Pages</a>");
        await Assert.That(pages).Contains("<input type=\"hidden\" name=\"kind\" value=\"pages\" />");
        await Assert.That(pages).DoesNotContain("<span>post</span>");
        await Assert.That(posts).DoesNotContain("<span>page</span>");
    }

    [Test]
    public async Task Search_without_a_query_shows_only_the_form()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/search", UriKind.Relative));

        await Assert.That(html).Contains("<h1 id=\"search-title\" class=\"page-title\">Search</h1>");
        await Assert.That(html).DoesNotContain("class=\"filters\"");
        await Assert.That(html).DoesNotContain("search-results");
    }

    [Test]
    public async Task Search_without_matches_says_so()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/search?q=zzzqqq", UriKind.Relative));

        await Assert.That(html).Contains("Nothing found.");
    }

    [Test]
    public async Task Query_text_is_escaped_where_it_is_shown()
    {
        await using var factory = new SiteFactory();
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
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        await Assert.That(html).Contains("<form class=\"search-box\" role=\"search\" method=\"get\" action=\"/search\" aria-label=\"Search the site\">");
        await Assert.That(html).Contains("name=\"q\"");
    }

    [Test]
    public async Task Search_page_is_a_200_for_any_query()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/search?q={new string('a', 5000)}", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
