using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Tests;
using Weesite.Web.Analytics;

namespace Weesite.Web.Tests;

/// <summary>Page-view analytics (brief, Analytics; T-0052): what counts and what's recorded.</summary>
internal sealed class AnalyticsTests
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    [Test]
    [Arguments(Chrome, ClientKind.Browser, null)]
    [Arguments("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", ClientKind.Bot, "Googlebot")]
    [Arguments("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)", ClientKind.Agent, "GPTBot")]
    [Arguments("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ClaudeBot/1.0; +claudebot@anthropic.com)", ClientKind.Agent, "ClaudeBot")]
    [Arguments("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ChatGPT-User/1.0; +https://openai.com/bot)", ClientKind.Agent, "ChatGPT-User")]
    [Arguments("curl/8.9.1", ClientKind.Bot, "curl")]
    [Arguments("SomeCrawler/1.0", ClientKind.Bot, null)]
    [Arguments("NotABrowser", ClientKind.Bot, null)]
    [Arguments("", ClientKind.Bot, null)]
    public async Task User_agents_are_labelled(string userAgent, ClientKind kind, string? name)
    {
        var (actualKind, actualName) = UserAgents.Classify(userAgent);

        await Assert.That(actualKind).IsEqualTo(kind);
        await Assert.That(actualName).IsEqualTo(name);
    }

    [Test]
    public async Task The_visitor_hash_changes_with_the_days_salt()
    {
        var monday = new byte[32];
        var tuesday = Enumerable.Repeat((byte)1, 32).ToArray();

        var first = PageViewRecorder.Visitor(monday, "203.0.113.7", Chrome);
        var again = PageViewRecorder.Visitor(monday, "203.0.113.7", Chrome);
        var nextDay = PageViewRecorder.Visitor(tuesday, "203.0.113.7", Chrome);
        var otherAddress = PageViewRecorder.Visitor(monday, "203.0.113.8", Chrome);

        await Assert.That(first).Matches("^[0-9a-f]{16}$");
        await Assert.That(again).IsEqualTo(first);
        await Assert.That(nextDay).IsNotEqualTo(first);
        await Assert.That(otherAddress).IsNotEqualTo(first);
    }

    [Test]
    public async Task Public_pages_are_counted_with_the_referrer_and_without_the_query()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();
        var store = site.Services.GetRequiredService<MemoryPageViewStore>();

        using var home = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        home.Headers.UserAgent.ParseAdd(Chrome);
        home.Headers.Referrer = new Uri("https://news.example/item?id=1");
        home.Headers.Add("X-Forwarded-For", "198.51.100.1, 203.0.113.7");
        (await client.SendAsync(home)).Dispose();
        using var search = new HttpRequestMessage(HttpMethod.Get, new Uri("/search?q=private+words", UriKind.Relative));
        search.Headers.UserAgent.ParseAdd(Chrome);
        (await client.SendAsync(search)).Dispose();

        var views = await WaitForAsync(store, 2);
        var first = views.Single(v => v.Path == "/");
        await Assert.That(first.Kind).IsEqualTo(ClientKind.Browser);
        await Assert.That(first.ReferrerHost).IsEqualTo("news.example");
        await Assert.That(first.Visitor).Matches("^[0-9a-f]{16}$");
        await Assert.That(views.Select(v => v.Path)).Contains("/search");
    }

    [Test]
    public async Task Agents_reading_markdown_are_counted_as_agents()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();
        var store = site.Services.GetRequiredService<MemoryPageViewStore>();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/blog/hello-world", UriKind.Relative));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ClaudeBot/1.0)");
        request.Headers.Accept.ParseAdd("text/markdown");
        (await client.SendAsync(request)).Dispose();

        var view = (await WaitForAsync(store, 1)).Single();
        await Assert.That(view.Kind).IsEqualTo(ClientKind.Agent);
        await Assert.That(view.Client).IsEqualTo("ClaudeBot");
        await Assert.That(view.Path).IsEqualTo("/blog/hello-world");
    }

    [Test]
    public async Task Admin_pages_assets_feeds_errors_and_editors_are_not_counted()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();
        var store = site.Services.GetRequiredService<MemoryPageViewStore>();

        foreach (var path in new[] { "/admin/sign-in", "/feed.xml", "/no-such-page", "/health", "/favicon.ico" })
        {
            (await client.GetAsync(new Uri(path, UriKind.Relative))).Dispose();
        }

        using var editor = new HttpRequestMessage(HttpMethod.Get, new Uri("/about", UriKind.Relative));
        editor.Headers.Add("Cookie", "weesite.auth=anything");
        (await client.SendAsync(editor)).Dispose();

        // One counted page last: once it's stored, the others would have been too.
        (await client.GetAsync(new Uri("/blog", UriKind.Relative))).Dispose();

        var views = await WaitForAsync(store, 1);
        await Assert.That(views.Select(v => v.Path)).IsEquivalentTo(["/blog"]);
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_keeps_views_by_day_and_cleans_up()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var store = new TablePageViewStore(tables);
        var other = new TablePageViewStore(tables);
        var old = new DateOnly(2025, 1, 10);
        var day = new DateOnly(2026, 11, 10);
        var at = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

        // More than one transaction's worth (100) in one day, and one old view.
        var views = Enumerable.Range(0, 150)
            .Select(i => new PageView(at.AddSeconds(i), $"/p{i}", i == 0 ? "news.example" : null, ClientKind.Browser, null, "0123456789abcdef"))
            .Append(new PageView(new DateTimeOffset(2025, 1, 10, 12, 0, 0, TimeSpan.Zero), "/old", null, ClientKind.Agent, "GPTBot", "fedcba9876543210"))
            .ToList();
        await store.AddAsync(views, CancellationToken.None);

        var salt = await store.SaltAsync(day, CancellationToken.None);
        var sameSalt = await other.SaltAsync(day, CancellationToken.None);
        var oldSalt = await store.SaltAsync(old, CancellationToken.None);
        var read = await ToListAsync(store.ReadAsync(day, day, CancellationToken.None));
        var oldRead = await ToListAsync(store.ReadAsync(old, old, CancellationToken.None));
        var deleted = await store.CleanUpAsync(new DateOnly(2025, 10, 10), day.AddDays(-1), CancellationToken.None);
        var afterOld = await ToListAsync(store.ReadAsync(old, old, CancellationToken.None));
        var afterDay = await ToListAsync(store.ReadAsync(day, day, CancellationToken.None));
        var newSalt = await store.SaltAsync(old, CancellationToken.None);

        await Assert.That(read.Count).IsEqualTo(150);
        await Assert.That(read[0].ReferrerHost).IsEqualTo("news.example");
        await Assert.That(oldRead.Single().Client).IsEqualTo("GPTBot");
        await Assert.That(sameSalt).IsEquivalentTo(salt);
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(afterOld).IsEmpty();
        await Assert.That(afterDay.Count).IsEqualTo(150);
        await Assert.That(await store.SaltAsync(day, CancellationToken.None)).IsEquivalentTo(salt);
        await Assert.That(newSalt).IsNotEquivalentTo(oldSalt);
    }

    private static async Task<List<PageView>> ToListAsync(IAsyncEnumerable<PageView> views)
    {
        var list = new List<PageView>();
        await foreach (var view in views)
        {
            list.Add(view);
        }

        return list;
    }

    [Test]
    public async Task The_report_counts_people_per_day_and_lists_bots_apart()
    {
        var day1 = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var day3 = day1.AddDays(2);
        PageView View(DateTimeOffset at, string path, string visitor, ClientKind kind = ClientKind.Browser, string? client = null, string? referrer = null) =>
            new(at, path, referrer, kind, client, visitor);
        var views = new[]
        {
            View(day1, "/", "a", referrer: "news.example"),
            View(day1, "/blog", "a"),
            View(day1, "/", "b"),
            View(day3, "/", "a"),
            View(day3, "/about", "c", referrer: "news.example"),
            View(day3, "/", "x", ClientKind.Agent, "GPTBot"),
            View(day3, "/", "y", ClientKind.Bot, "Googlebot"),
            View(day3, "/blog", "y", ClientKind.Bot, "Googlebot"),
            View(day3.AddDays(1), "/", "late"),
        };

        var report = AnalyticsReport.Build(views, new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 10));

        await Assert.That(report.Days.Select(d => d.Views)).IsEquivalentTo([3, 0, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(report.Days.Select(d => d.Visitors)).IsEquivalentTo([2, 0, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(report.Views).IsEqualTo(5);
        await Assert.That(report.Visitors).IsEqualTo(4);
        await Assert.That(report.AgentViews).IsEqualTo(1);
        await Assert.That(report.BotViews).IsEqualTo(2);
        await Assert.That(report.Pages[0]).IsEqualTo(new ReportRow("/", 3, 3));
        await Assert.That(report.Referrers.Single()).IsEqualTo(new ReportRow("news.example", 2, 2));
        await Assert.That(report.Clients.Select(c => c.Name ?? "")).IsEquivalentTo(["Googlebot", "GPTBot"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("/admin/analytics?days=abc", "<a href=\"/admin/analytics?days=30\" aria-current=\"page\">")]
    [Arguments("/admin/analytics?days=99999999999", "<a href=\"/admin/analytics?days=30\" aria-current=\"page\">")]
    [Arguments("/admin/backup?added=abc&kept=x", "<h1>Backup</h1>")]
    public async Task Query_values_that_arent_numbers_fall_back_to_the_default(string path, string expected)
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        using var response = await admin.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains(expected);
    }

    [Test]
    public async Task Admins_see_the_dashboard_and_authors_do_not()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        var store = factory.Services.GetRequiredService<MemoryPageViewStore>();
        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(
        [
            new PageView(now, "/blog/hello-world", "news.example", ClientKind.Browser, null, "a"),
            new PageView(now, "/blog/hello-world", null, ClientKind.Browser, null, "b"),
            new PageView(now, "/", null, ClientKind.Agent, "ClaudeBot", "c"),
        ], CancellationToken.None);
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        using var author = factory.CreateClient(new() { AllowAutoRedirect = false });
        author.DefaultRequestHeaders.Add("X-Test-Role", "Author");

        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/analytics?days=7", UriKind.Relative)));
        using var denied = await author.GetAsync(new Uri("/admin/analytics", UriKind.Relative));

        await Assert.That(html).Contains("<a href=\"/admin/analytics?days=7\" aria-current=\"page\">Last 7 days</a>");
        await Assert.That(html).Contains("<div><dt>Page views</dt><dd>2</dd></div>");
        await Assert.That(html).Contains("<div><dt>AI agents</dt><dd>1</dd></div>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">/blog/hello-world</th>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">news.example</th>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">AI agent: ClaudeBot</th>");
        await Assert.That(html).Contains("data-height=\"100\"");
        await Assert.That(denied.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
    }

    // The writer stores views in the background: wait for them, up to five seconds.
    private static async Task<IReadOnlyList<PageView>> WaitForAsync(MemoryPageViewStore store, int count)
    {
        for (var i = 0; i < 50 && store.All.Count < count; i++)
        {
            await Task.Delay(100);
        }

        await Task.Delay(100);
        return store.All;
    }
}
