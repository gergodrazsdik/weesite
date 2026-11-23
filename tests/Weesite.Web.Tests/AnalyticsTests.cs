using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content;
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
    [Arguments("Grafana k6/2.4.0 (https://k6.io/)", ClientKind.Bot, "Grafana k6")]
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

        using var home = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        home.Headers.UserAgent.ParseAdd(Chrome);
        home.Headers.Referrer = new Uri("https://news.example/item?id=1");
        home.Headers.Add("X-Forwarded-For", "198.51.100.1, 203.0.113.7");
        (await client.SendAsync(home)).Dispose();
        using var search = new HttpRequestMessage(HttpMethod.Get, new Uri("/search?q=private+words", UriKind.Relative));
        search.Headers.UserAgent.ParseAdd(Chrome);
        (await client.SendAsync(search)).Dispose();

        var views = await WaitForViewsAsync(site, 2);
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

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/blog/hello-world", UriKind.Relative));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ClaudeBot/1.0)");
        request.Headers.Accept.ParseAdd("text/markdown");
        (await client.SendAsync(request)).Dispose();

        var view = (await WaitForViewsAsync(site, 1)).Single();
        await Assert.That(view.Kind).IsEqualTo(ClientKind.Agent);
        await Assert.That(view.Client).IsEqualTo("ClaudeBot");
        await Assert.That(view.Path).IsEqualTo("/blog/hello-world");
    }

    [Test]
    public async Task Admin_pages_assets_feeds_errors_and_editors_are_not_counted()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        foreach (var path in new[] { "/admin/sign-in", "/feed.xml", "/no-such-page", "/health", "/favicon.ico" })
        {
            (await client.GetAsync(new Uri(path, UriKind.Relative))).Dispose();
        }

        foreach (var cookie in new[] { "weesite.auth", "__Host-weesite.auth", "weesite.editor" })
        {
            using var editor = new HttpRequestMessage(HttpMethod.Get, new Uri("/about", UriKind.Relative));
            editor.Headers.Add("Cookie", cookie + "=anything");
            (await client.SendAsync(editor)).Dispose();
        }

        // One counted page last: once it's stored, the others would have been too.
        (await client.GetAsync(new Uri("/blog", UriKind.Relative))).Dispose();

        var views = await WaitForViewsAsync(site, 1);
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

    [Test]
    [Category("containers")]
    public async Task The_table_store_keeps_finished_days_and_reads_today_and_yesterday_fresh()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var clock = new OAuthTests.TestClock();
        var store = new TablePageViewStore(tables, clock);
        var other = new TablePageViewStore(tables, clock);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        PageView On(DateOnly day, string path) => new(new DateTimeOffset(day.ToDateTime(new TimeOnly(path.EndsWith("-2", StringComparison.Ordinal) ? 1 : 0, 0)), TimeSpan.Zero), path, null, ClientKind.Browser, null, "0123456789abcdef");

        await store.AddAsync([On(today.AddDays(-3), "/old"), On(today.AddDays(-1), "/yesterday"), On(today, "/today")], CancellationToken.None);
        var first = await ToListAsync(store.ReadAsync(today.AddDays(-3), today, CancellationToken.None));

        // Written after the first read: another replica would see them; this one keeps the
        // finished day it read for a few minutes, and reads yesterday and today again (F-0202).
        await other.AddAsync([On(today.AddDays(-3), "/old-2"), On(today.AddDays(-1), "/yesterday-2"), On(today, "/today-2")], CancellationToken.None);
        var second = await ToListAsync(store.ReadAsync(today.AddDays(-3), today, CancellationToken.None));
        var fresh = await ToListAsync(other.ReadAsync(today.AddDays(-3), today, CancellationToken.None));

        await Assert.That(first.Select(v => v.Path)).IsEquivalentTo(["/old", "/yesterday", "/today"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(second.Select(v => v.Path)).IsEquivalentTo(["/old", "/yesterday", "/yesterday-2", "/today", "/today-2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(fresh.Count).IsEqualTo(6);

        // After the few minutes, this one reads the finished day again (F-0222: the expiry follows the clock).
        clock.Advance(TimeSpan.FromMinutes(4));
        var stillKept = await ToListAsync(store.ReadAsync(today.AddDays(-3), today.AddDays(-3), CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(2));
        var readAgain = await ToListAsync(store.ReadAsync(today.AddDays(-3), today.AddDays(-3), CancellationToken.None));
        await Assert.That(stillKept.Count).IsEqualTo(1);
        await Assert.That(readAgain.Count).IsEqualTo(2);
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

        // The page shows at once, with a skeleton; the report comes from its own address, without
        // the admin's layout (F-0202).
        var page = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/analytics?days=7", UriKind.Relative)));
        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/analytics/report?days=7", UriKind.Relative)));
        using var denied = await author.GetAsync(new Uri("/admin/analytics", UriKind.Relative));
        using var deniedReport = await author.GetAsync(new Uri("/admin/analytics/report", UriKind.Relative));

        await Assert.That(page).Contains("<a href=\"/admin/analytics?days=7\" aria-current=\"page\">Last 7 days</a>");
        await Assert.That(page).Contains("<section class=\"analytics loading\" aria-busy=\"true\" aria-labelledby=\"analytics-loading\" data-report=\"/admin/analytics/report?days=7\"");
        await Assert.That(page).DoesNotContain("<dt>Page views</dt>");
        await Assert.That(html).StartsWith("<section class=\"analytics\"");
        await Assert.That(html).DoesNotContain("admin-nav");
        await Assert.That(html).Contains("<div><dt>Page views</dt><dd>2</dd></div>");
        await Assert.That(html).Contains("<div><dt>AI agents</dt><dd>1</dd></div>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">/blog/hello-world</th>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">news.example</th>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">AI agent: ClaudeBot</th>");
        await Assert.That(html).Contains("data-height=\"100\"");
        await Assert.That(denied.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
        await Assert.That(deniedReport.StatusCode).IsNotEqualTo(System.Net.HttpStatusCode.OK);
    }

    [Test]
    public async Task Finished_days_expire_with_the_clock_and_stay_within_the_budget()
    {
        // F-0222: the cache follows the given clock, and holds no more bytes than its budget.
        var clock = new OAuthTests.TestClock();
        var cache = new DayCache<string>(clock, budget: 100, TimeSpan.FromMinutes(5));
        var day = new DateOnly(2026, 11, 1);

        cache.Set(day, "one", 40, cache.Generation);
        clock.Advance(TimeSpan.FromMinutes(4));
        var kept = cache.TryGet(day, out var first);
        cache.Set(day.AddDays(1), "two", 40, cache.Generation);
        cache.Set(day.AddDays(2), "three", 40, cache.Generation);
        var oldestGone = !cache.TryGet(day, out _);
        cache.Set(day.AddDays(3), "too big", 101, cache.Generation);
        var size = cache.Size;
        clock.Advance(TimeSpan.FromMinutes(5));
        var expired = !cache.TryGet(day.AddDays(1), out _);

        await Assert.That(kept).IsTrue();
        await Assert.That(first).IsEqualTo("one");
        await Assert.That(oldestGone).IsTrue();
        await Assert.That(cache.TryGet(day.AddDays(3), out _)).IsFalse();
        await Assert.That(size).IsEqualTo(80);
        await Assert.That(expired).IsTrue();
    }

    [Test]
    public async Task A_read_that_began_before_a_clear_is_not_kept()
    {
        // A reset forgets the finished days; a read still on its way mustn't bring them back.
        var cache = new DayCache<string>(TimeProvider.System, budget: 100, TimeSpan.FromMinutes(5));
        var day = new DateOnly(2026, 11, 1);
        var generation = cache.Generation;

        cache.Clear();
        cache.Set(day, "stale", 10, generation);
        cache.Set(day.AddDays(1), "fresh", 10, cache.Generation);

        await Assert.That(cache.TryGet(day, out _)).IsFalse();
        await Assert.That(cache.TryGet(day.AddDays(1), out _)).IsTrue();
    }

    [Test]
    [Category("containers")]
    public async Task Reports_from_kept_days_are_the_same_as_from_the_table()
    {
        // F-0222: finished days are kept with shared strings, and the reports from them, in any time
        // zone (a zone's day spans two UTC days), are the same as from the table and from memory.
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var clock = new OAuthTests.TestClock();
        var store = new TablePageViewStore(tables, clock);
        var memory = new MemoryPageViewStore();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var first = today.AddDays(-9);
        var views = SampleViews(first, 7).ToList();
        await store.AddAsync(views, CancellationToken.None);
        await memory.AddAsync(views, CancellationToken.None);
        await using var site = new SiteFactory();
        var catalog = site.Services.GetRequiredService<ContentCatalogProvider>().Current;

        // The days of the zones' reports, with the UTC day either side.
        var from = first.AddDays(1);
        var to = first.AddDays(5);
        var fromTable = await ToListAsync(store.ReadAsync(from.AddDays(-1), to.AddDays(1), CancellationToken.None));
        var kept = store.Days.Count;
        var fromMemory = await ToListAsync(store.ReadAsync(from.AddDays(-1), to.AddDays(1), CancellationToken.None));
        var raw = await ToListAsync(memory.ReadAsync(from.AddDays(-1), to.AddDays(1), CancellationToken.None));

        await Assert.That(kept).IsEqualTo(7);
        await Assert.That(fromTable.Count).IsEqualTo(views.Count);
        foreach (var zone in new[] { "UTC", "America/New_York", "Asia/Kathmandu", "Pacific/Auckland" })
        {
            var timeZone = SiteTime.Zone(zone);
            var expected = JsonSerializer.Serialize(AnalyticsReport.Build(raw, from, to, timeZone));
            await Assert.That(JsonSerializer.Serialize(AnalyticsReport.Build(fromTable, from, to, timeZone))).IsEqualTo(expected);
            await Assert.That(JsonSerializer.Serialize(AnalyticsReport.Build(fromMemory, from, to, timeZone))).IsEqualTo(expected);
        }

        static string Posts(ContentCatalog catalog, IEnumerable<PageView> views, DateOnly from, DateOnly to) =>
            JsonSerializer.Serialize(PostReport.Build(catalog, views, [], new Dictionary<string, int>(), from, to, DateTimeOffset.UtcNow)
                .Select(r => new { r.Item.SourcePath, r.Views, r.Visitors, r.AgentViews, r.BotViews }));
        await Assert.That(Posts(catalog, fromMemory, from, to)).IsEqualTo(Posts(catalog, raw, from, to));

        // A day's views share their strings, and the estimate counts each once.
        var day = fromMemory.Where(v => DateOnly.FromDateTime(v.At.UtcDateTime) == from).ToList();
        await Assert.That(ReferenceEquals(day.First(v => v.Path == "/blog/hello-world").Path, day.Last(v => v.Path == "/blog/hello-world").Path)).IsTrue();
        await Assert.That(store.Days.Size).IsLessThan(views.Count * 150L);
    }

    // Views over some days, at every hour: people, bots and agents, paths of a post (its Markdown
    // copy too), referrers, countries, and a visitor hash seen on two days.
    private static IEnumerable<PageView> SampleViews(DateOnly first, int days)
    {
        string[] paths = ["/", "/blog/hello-world", "/blog/hello-world.md", "/about", "/blog/welcome"];
        for (var d = 0; d < days; d++)
        {
            var day = first.AddDays(d);
            for (var hour = 0; hour < 24; hour++)
            {
                var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 15)), TimeSpan.Zero);
                for (var i = 0; i < 4; i++)
                {
                    var kind = i == 3 ? (hour % 2 == 0 ? ClientKind.Bot : ClientKind.Agent) : ClientKind.Browser;
                    var visitor = i == 0 ? "same-on-every-day" : $"{day:MMdd}-{(hour + i) % 7}";
                    yield return new PageView(
                        at.AddMinutes(i),
                        paths[(hour + i) % paths.Length],
                        i == 1 ? "news.example" : null,
                        kind,
                        kind == ClientKind.Browser ? null : (kind == ClientKind.Bot ? "Googlebot" : "GPTBot"),
                        visitor,
                        kind == ClientKind.Browser ? (hour % 3 == 0 ? null : "NL") : null);
                }
            }
        }
    }

    [Test]
    [Category("containers")]
    public async Task Deleting_rows_goes_page_by_page_and_skips_rows_already_gone()
    {
        // F-0221: more rows than a page, and one another replica deleted after the query found it.
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var table = tables.GetTableClient("rows");
        await table.CreateIfNotExistsAsync();
        foreach (var partition in new[] { "a", "b" })
        {
            var rows = Enumerable.Range(0, partition == "a" ? TableRowPages.PageSize + 50 : 30)
                .Select(i => new TableTransactionAction(TableTransactionActionType.Add, new TableEntity(partition, $"{i:D5}")));
            foreach (var chunk in rows.Chunk(100))
            {
                await table.SubmitTransactionAsync(chunk);
            }
        }

        var deleted = await TableRowPages.DeleteAsync(new GhostRowTable(table), null, CancellationToken.None);
        var left = 0;
        await foreach (var unused in table.QueryAsync<TableEntity>())
        {
            left++;
        }

        await Assert.That(deleted).IsEqualTo(TableRowPages.PageSize + 80);
        await Assert.That(left).IsEqualTo(0);
    }

    // A table whose query also finds a row that isn't there (deleted meanwhile).
    private sealed class GhostRowTable(TableClient table) : TableClient
    {
        public override Azure.AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default) =>
            new Pageable<T>(Pages<T>(filter, maxPerPage, select, cancellationToken));

        public override Task<Azure.Response<IReadOnlyList<Azure.Response>>> SubmitTransactionAsync(IEnumerable<TableTransactionAction> transactionActions, CancellationToken cancellationToken = default) =>
            table.SubmitTransactionAsync(transactionActions, cancellationToken);

        public override Task<Azure.Response> DeleteEntityAsync(string partitionKey, string rowKey, Azure.ETag ifMatch = default, CancellationToken cancellationToken = default) =>
            table.DeleteEntityAsync(partitionKey, rowKey, ifMatch, cancellationToken);

        private async IAsyncEnumerable<Azure.Page<T>> Pages<T>(string? filter, int? maxPerPage, IEnumerable<string>? select, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            where T : class, ITableEntity
        {
            var ghost = true;
            await foreach (var page in table.QueryAsync<T>(filter, maxPerPage, select, cancellationToken).AsPages())
            {
                var values = page.Values.ToList();
                if (ghost && values.Count > 0)
                {
                    var row = Activator.CreateInstance<T>();
                    row.PartitionKey = values[0].PartitionKey;
                    row.RowKey = "00000-ghost";
                    values.Insert(1, row);
                    ghost = false;
                }

                yield return Azure.Page<T>.FromValues(values, page.ContinuationToken, page.GetRawResponse());
            }
        }
    }

    private sealed class Pageable<T>(IAsyncEnumerable<Azure.Page<T>> pages) : Azure.AsyncPageable<T>
        where T : notnull
    {
        public override IAsyncEnumerable<Azure.Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null) => pages;
    }

    private static async Task<List<TItem>> ToListAsync<TItem>(IAsyncEnumerable<TItem> items)
    {
        var list = new List<TItem>();
        await foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    /// <summary>
    /// Waits for the writer, which stores views in the background, to have stored a number of them
    /// and to have none queued: on that condition, not for a time (F-0228).
    /// </summary>
    /// <param name="site">The app.</param>
    /// <param name="count">How many views.</param>
    /// <returns>Every view stored.</returns>
    internal static async Task<IReadOnlyList<PageView>> WaitForViewsAsync(WebApplicationFactory<Program> site, int count)
    {
        var store = site.Services.GetRequiredService<MemoryPageViewStore>();
        var queue = site.Services.GetRequiredService<PageViewRecorder>().Queue;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (store.All.Count < count || queue.Count > 0)
        {
            await Task.Delay(20, timeout.Token);
        }

        return store.All;
    }
}
