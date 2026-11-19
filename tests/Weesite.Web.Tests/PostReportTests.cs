using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The posts report (T-0163): every post with its numbers in a range, sorting, and its CSV.</summary>
internal sealed class PostReportTests
{
    private const string HelloWorld = "posts/hello-world.md";

    [Test]
    public async Task The_report_counts_each_posts_views_comments_and_reactions_in_the_range()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        await SeedAsync(factory);
        using var admin = Client(factory, "Admin");

        var week = await PageAsync(admin, new Uri("/admin/analytics/posts?days=7", UriKind.Relative));
        var month = await PageAsync(admin, new Uri("/admin/analytics/posts", UriKind.Relative));

        // Views by people (3, from 2 visitors), the Markdown copy read by an agent, the earlier
        // address by a bot, one approved comment (not the waiting one) and the net reactions.
        await Assert.That(Row(week, "posts%2Fhello-world.md\"")).Contains(
            "<td><span class=\"pill published\">Published</span></td><td class=\"date\">2026-01-15</td><td class=\"num\">3</td><td class=\"num\">2</td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">1</td>");
        // Thirty days take in the older view, comment and reaction too.
        await Assert.That(Row(month, "posts%2Fhello-world.md\"")).Contains(
            "<td class=\"num\">4</td><td class=\"num\">3</td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">2</td><td class=\"num\">2</td>");

        // A slug in another script counts both as stored (decoded) and as linked (percent-encoded).
        await Assert.That(Row(week, "<span class=\"slug\">/blog/kerékpárral-a-romos-apátsághoz</span>")).Contains("<td class=\"num\">2</td><td class=\"num\">2</td>");

        // Every post, unpublished ones with their status.
        await Assert.That(Row(week, "posts%2Fnotes-for-a-future-post.md\"")).Contains("<span class=\"pill draft\">Draft</span>");
        await Assert.That(Row(week, "posts%2Fa-post-from-the-future.md\"")).Contains("<span class=\"pill scheduled\">Scheduled</span>");

        // The range is links, the default sort is page views, most first, and the CSV follows.
        await Assert.That(week).Contains("<a href=\"/admin/analytics/posts?days=7\" aria-current=\"page\">Last 7 days</a>");
        await Assert.That(week).Contains("<th scope=\"col\" class=\"num\" aria-sort=\"descending\"><a href=\"/admin/analytics/posts?days=7&sort=views&dir=asc\">Page views<span class=\"sort-mark\" aria-hidden=\"true\">↓</span></a></th>");
        await Assert.That(week).Contains("<a class=\"btn\" href=\"/admin/analytics/posts.csv?days=7\">Download CSV</a>");
        await Assert.That(week).Contains("<a class=\"btn\" href=\"/admin/analytics?days=7\">Back to analytics</a>");
        await Assert.That(FirstRow(week)).Contains("posts%2Fhello-world.md\"");

        // The analytics page links to it.
        var analytics = await PageAsync(admin, new Uri("/admin/analytics?days=90", UriKind.Relative));
        await Assert.That(analytics).Contains("<a class=\"btn\" href=\"/admin/analytics/posts?days=90\">Posts report</a>");
    }

    [Test]
    public async Task Column_headings_sort_the_report()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, "Admin");

        var byTitle = await PageAsync(admin, new Uri("/admin/analytics/posts?days=7&sort=title&dir=asc", UriKind.Relative));
        var byTitleDown = await PageAsync(admin, new Uri("/admin/analytics/posts?days=7&sort=title&dir=desc", UriKind.Relative));
        var unknown = await PageAsync(admin, new Uri("/admin/analytics/posts?days=abc&sort=nope&dir=up", UriKind.Relative));

        await Assert.That(byTitle).Contains("<th scope=\"col\" aria-sort=\"ascending\"><a href=\"/admin/analytics/posts?days=7&sort=title&dir=desc\">Title");
        // Another column starts from its own default: numbers from the most.
        await Assert.That(byTitle).Contains("<a href=\"/admin/analytics/posts?days=7&sort=comments&dir=desc\">Comments</a>");
        await Assert.That(byTitle.IndexOf("A 1952 Hudson Hornet", StringComparison.Ordinal)).IsLessThan(byTitle.IndexOf("Hello, world", StringComparison.Ordinal));
        await Assert.That(byTitleDown.IndexOf("A 1952 Hudson Hornet", StringComparison.Ordinal)).IsGreaterThan(byTitleDown.IndexOf("Hello, world", StringComparison.Ordinal));

        // Values it doesn't know fall back to 30 days by page views.
        await Assert.That(unknown).Contains("<a href=\"/admin/analytics/posts?days=30\" aria-current=\"page\">Last 30 days</a>");
        await Assert.That(unknown).Contains("aria-sort=\"descending\"><a href=\"/admin/analytics/posts?days=30&sort=views&dir=asc\">Page views");
    }

    [Test]
    public async Task The_csv_has_a_row_per_post_quoted_where_needed()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        await SeedAsync(factory);
        using var admin = Client(factory, "Admin");
        var today = factory.Services.GetRequiredService<ContentCatalogProvider>().Current.Site.DayOf(DateTimeOffset.UtcNow);

        using var response = await admin.GetAsync(new Uri("/admin/analytics/posts.csv?days=7", UriKind.Relative));
        var csv = await response.Content.ReadAsStringAsync();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.ToString()).IsEqualTo("text/csv; charset=utf-8");
        await Assert.That(response.Content.Headers.GetValues("Content-Disposition").Single()).IsEqualTo($"attachment; filename=\"posts-{today:yyyy-MM-dd}-7d.csv\"");
        await Assert.That(lines[0]).IsEqualTo("Title,Status,Date,Path,Page views,Visitors,AI agent views,Bot views,Comments,Reactions");
        // In the page's order: page views, most first. The title has a comma, so it's quoted.
        await Assert.That(lines[1]).IsEqualTo("\"Hello, world\",published,2026-01-15,/blog/hello-world,3,2,1,1,1,1");
        await Assert.That(lines).Contains("Notes for a future post,draft,2026-11-09,/blog/notes-for-a-future-post,0,0,0,0,0,0");
        await Assert.That(lines.Length).IsEqualTo(1 + factory.Services.GetRequiredService<ContentCatalogProvider>().Current.Posts.Count
            + factory.Services.GetRequiredService<ContentCatalogProvider>().Current.Unpublished.Count(i => i.Kind == ContentKind.Post));
    }

    [Test]
    [Arguments("plain", "plain")]
    [Arguments("a, b", "\"a, b\"")]
    [Arguments("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [Arguments("two\nlines", "\"two\nlines\"")]
    [Arguments("=1+1", "'=1+1")]
    [Arguments("  =1+1", "'  =1+1")]
    [Arguments("+1", "'+1")]
    [Arguments("-2", "'-2")]
    [Arguments("@sum", "'@sum")]
    [Arguments("\tx", "'\tx")]
    [Arguments("\rx", "\"'\rx\"")]
    [Arguments("=HYPERLINK(\"x\",\"y\")", "\"'=HYPERLINK(\"\"x\"\",\"\"y\"\")\"")]
    public async Task Csv_fields_are_escaped_and_formulas_written_as_text(string value, string field) =>
        await Assert.That(PostReport.Field(value)).IsEqualTo(field);

    [Test]
    public async Task Authors_cannot_see_the_report_or_its_csv()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, "Author");

        using var page = await author.GetAsync(new Uri("/admin/analytics/posts", UriKind.Relative));
        using var csv = await author.GetAsync(new Uri("/admin/analytics/posts.csv", UriKind.Relative));

        await Assert.That(page.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
        await Assert.That(csv.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string role)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    // A week's numbers and, ten days back, some only thirty days show.
    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-10);
        await factory.Services.GetRequiredService<MemoryPageViewStore>().AddAsync(
        [
            new PageView(now, "/blog/hello-world", null, ClientKind.Browser, null, "a"),
            new PageView(now, "/blog/hello-world", null, ClientKind.Browser, null, "a"),
            new PageView(now, "/blog/hello-world", "news.example", ClientKind.Browser, null, "b"),
            new PageView(now, "/blog/hello-world.md", null, ClientKind.Agent, "ClaudeBot", "x"),
            new PageView(now, "/blog/welcome", null, ClientKind.Bot, "Googlebot", "y"),
            new PageView(now, "/blog/kerékpárral-a-romos-apátsághoz", null, ClientKind.Browser, null, "d"),
            new PageView(now, "/blog/ker%C3%A9kp%C3%A1rral-a-romos-ap%C3%A1ts%C3%A1ghoz", null, ClientKind.Browser, null, "e"),
            new PageView(now, "/about", null, ClientKind.Browser, null, "a"),
            new PageView(old, "/blog/hello-world", null, ClientKind.Browser, null, "c"),
        ], CancellationToken.None);

        var reactions = factory.Services.GetRequiredService<IReactionStore>();
        await reactions.AddAsync(HelloWorld, ReactionKind.Like, 1, now, CancellationToken.None);
        await reactions.AddAsync(HelloWorld, ReactionKind.Love, 1, now, CancellationToken.None);
        await reactions.AddAsync(HelloWorld, ReactionKind.Love, -1, now, CancellationToken.None);
        await reactions.AddAsync(HelloWorld, ReactionKind.Thanks, 1, old, CancellationToken.None);

        var comments = factory.Services.GetRequiredService<ICommentStore>();
        foreach (var (at, status) in new[] { (now, CommentStatus.Approved), (now, CommentStatus.Pending), (old, CommentStatus.Approved) })
        {
            await comments.SaveAsync(new Comment(Comment.NewId(at), HelloWorld, "Reader", "Nice.", null, status, at, null, null, new string('0', 64)), CancellationToken.None);
        }
    }

    // A page, decoded, without the white space between tags.
    private static async Task<string> PageAsync(HttpClient client, Uri address) =>
        System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(await client.GetStringAsync(address)), @">\s+<", "><");

    // The rest of the table row from this text on.
    private static string Row(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        return html[start..html.IndexOf("</tr>", start, StringComparison.Ordinal)];
    }

    private static string FirstRow(string html)
    {
        var body = html.IndexOf("<tbody>", StringComparison.Ordinal);
        return html[body..html.IndexOf("</tr>", body, StringComparison.Ordinal)];
    }
}
