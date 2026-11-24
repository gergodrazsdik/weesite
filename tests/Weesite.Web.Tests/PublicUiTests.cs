using System.Net;
using System.Text.RegularExpressions;

namespace Weesite.Web.Tests;

/// <summary>The public site's fixes from the UI and UX review (T-0177): wording, links and names.</summary>
internal sealed partial class PublicUiTests
{
    private const string Footnoted = """
        ---
        title: "With a footnote"
        slug: with-a-footnote
        date: 2026-07-03
        author: example-author
        tags: [notes]
        ---

        A claim with a source.[^1]

        [^1]: The source.
        """;

    [Test]
    public async Task The_home_pages_years_lead_to_their_part_of_the_blog()
    {
        // U-06: each year beside "All posts, by year" links to its heading on /blog.
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        var blog = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));

        var years = YearLink().Matches(home).Select(m => m.Groups[1].Value).ToList();
        await Assert.That(years).IsNotEmpty();
        foreach (var year in years)
        {
            await Assert.That(blog).Contains($"id=\"y{year}\"");
        }
    }

    [Test]
    public async Task The_not_found_page_names_the_site_in_its_title()
    {
        // U-30: "Page not found · Example Site", as every other page's tab.
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        using var missing = await client.GetAsync(new Uri("/no-such-page", UriKind.Relative));

        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await missing.Content.ReadAsStringAsync()).Contains("<title>Page not found · Example Site</title>");
    }

    [Test]
    public async Task A_page_with_sections_has_on_this_page_and_one_without_has_none()
    {
        // U-29: the posts' rule, on every page: Privacy and Terms have one, About has no sections.
        // Laid out as a post's: beside the text, and at the top for narrower screens.
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        var privacy = await client.GetStringAsync(new Uri("/privacy", UriKind.Relative));
        var terms = await client.GetStringAsync(new Uri("/terms", UriKind.Relative));
        var about = await client.GetStringAsync(new Uri("/about", UriKind.Relative));

        await Assert.That(privacy).Contains("<aside aria-label=\"About this page\"><nav class=\"toc-side\" aria-labelledby=\"contents-heading\"><h2 id=\"contents-heading\" class=\"side-label\">On this page</h2>");
        await Assert.That(privacy).Contains("<li><a href=\"#page-title\">Privacy policy</a></li>");
        await Assert.That(privacy).Contains("<li><a href=\"#who-processes-the-data\">Who processes the data</a></li>");
        await Assert.That(privacy).Contains("<details class=\"toc-top wide\">");
        await Assert.That(privacy).Contains("<div class=\"toc-top narrow\">");
        await Assert.That(privacy).Contains("post-contents");
        await Assert.That(terms).Contains("class=\"toc-side\"");
        await Assert.That(about).DoesNotContain("class=\"toc-side\"");
        await Assert.That(about).DoesNotContain("class=\"article-layout");
    }

    [Test]
    public async Task Search_names_its_field_hints_when_empty_and_counts_the_results()
    {
        // U-25, U-26.
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        var empty = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/search", UriKind.Relative)));
        var phrase = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/search?q=%22static%20rendering%22", UriKind.Relative)));

        await Assert.That(empty).Contains("aria-label=\"Search posts and pages\"");
        await Assert.That(empty).Contains("<p class=\"empty\">Type a word or a phrase to search for, then press Search.</p>");
        await Assert.That(empty).DoesNotContain("hybrid");
        await Assert.That(phrase).Contains("<title>Results for “static rendering” · Example Site</title>");
        await Assert.That(phrase).Contains(">Results for “static rendering”</h2>");
        await Assert.That(phrase).Contains("aria-label=\"Search again\"");
        await Assert.That(ResultCount().IsMatch(phrase)).IsTrue();
    }

    [Test]
    [Arguments("rendering", "rendering")]
    [Arguments("\"static rendering\"", "static rendering")]
    [Arguments("„statikus” ", "statikus")]
    [Arguments("\"a\" or \"b\"", "\"a\" or \"b\"")]
    [Arguments("\"", "\"")]
    public async Task A_query_loses_only_the_quotes_around_all_of_it(string query, string shown) =>
        await Assert.That(SearchQuery.Unquoted(query)).IsEqualTo(shown);

    [Test]
    public async Task Tag_pages_count_posts_as_every_list_does()
    {
        // U-09: "2 posts", as the home page, the blog and the tags index word it; no full stop.
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        var tag = await client.GetStringAsync(new Uri("/blog/tags/welcome", UriKind.Relative));

        await Assert.That(Regex.IsMatch(Lede().Match(tag).Groups[1].Value, "^[0-9]+ posts?$")).IsTrue();
    }

    [Test]
    public async Task Footnote_links_are_named_in_the_sites_language()
    {
        // U-19: "Footnote 1" and "Back to the text" for screen readers; the marks stay visible.
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "with-a-footnote.md"), Footnoted);
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        var post = await client.GetStringAsync(new Uri("/blog/with-a-footnote", UriKind.Relative));

        await Assert.That(post).Contains("class=\"footnote-ref\"><sup><span aria-hidden=\"true\">1</span><span class=\"visually-hidden\" lang=\"en\">Footnote 1</span></sup></a>");
        await Assert.That(post).Contains("class=\"footnote-back-ref\"><span aria-hidden=\"true\">&#8617;</span><span class=\"visually-hidden\" lang=\"en\">Back to the text</span></a>");
    }

    [Test]
    public async Task A_hungarian_site_translates_breadcrumbs_dates_and_footnotes()
    {
        // U-58, U-61, U-19.
        using var copy = new SiteCopy();
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"language\": \"en\",", "\"language\": \"hu\",", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "with-a-footnote.md"), Footnoted);
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        var tags = await client.GetStringAsync(new Uri("/blog/tags", UriKind.Relative));
        var tag = await client.GetStringAsync(new Uri("/blog/tags/notes", UriKind.Relative));
        var blog = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));
        var post = await client.GetStringAsync(new Uri("/blog/with-a-footnote", UriKind.Relative));

        await Assert.That(tags).Contains("<a href=\"/blog\">blog</a> / <span aria-current=\"page\">címkék</span>");
        await Assert.That(tag).Contains("<a href=\"/blog/tags\">címkék</a> /");
        await Assert.That(blog).Contains(">júl. 3.</time>");
        await Assert.That(Regex.IsMatch(blog, ">[a-zé]+\\. 0[0-9]\\.</time>")).IsFalse();
        await Assert.That(post).Contains("<span class=\"visually-hidden\" lang=\"hu\">1. lábjegyzet</span>");
        await Assert.That(post).Contains("<span class=\"visually-hidden\" lang=\"hu\">Vissza a szöveghez</span>");
    }

    [GeneratedRegex("<a href=\"/blog#y([0-9]{4})\">\\1</a>")]
    private static partial Regex YearLink();

    [GeneratedRegex("<span class=\"count\">(1 result|[0-9]+ results|The first [0-9]+ results)</span>")]
    private static partial Regex ResultCount();

    [GeneratedRegex("<p class=\"lede\">([^<]*)</p>")]
    private static partial Regex Lede();
}
