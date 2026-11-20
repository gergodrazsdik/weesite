using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weesite.Content;
using Weesite.Tests;
using Weesite.Web.Admin;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>Reactions to posts (ADR 0011): the endpoints' rules, the stores and the analytics.</summary>
internal sealed class ReactionTests
{
    private const string Post = "hello-world";
    private const string Firefox = "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0";
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    private static readonly DateTimeOffset Noon = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Counts_are_answered_for_published_posts_only()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var published = await client.GetAsync(new Uri($"/visitor/reactions?post={Post}", UriKind.Relative));
        using var draft = await client.GetAsync(new Uri("/visitor/reactions?post=a-long-draft-of-walks-and-scripts", UriKind.Relative));
        using var unknown = await client.GetAsync(new Uri("/visitor/reactions?post=no-such-post", UriKind.Relative));
        using var none = await client.GetAsync(new Uri("/visitor/reactions", UriKind.Relative));
        var answer = await published.Content.ReadFromJsonAsync<ReactionAnswer>();

        await Assert.That(published.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(published.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(answer!.Counts).IsEquivalentTo(new Dictionary<string, int> { ["like"] = 0, ["love"] = 0, ["thanks"] = 0, ["insightful"] = 0 });
        await Assert.That(draft.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Nothing_answers_while_reactions_are_off()
    {
        using var copy = new SiteCopy();
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"reactions\": true", "\"reactions\": false", StringComparison.Ordinal));
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var counts = await client.GetAsync(new Uri($"/visitor/reactions?post={Post}", UriKind.Relative));
        using var react = await React(client, site, "like");
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(counts.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(react.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(page).DoesNotContain("<ws-reactions");
        await Assert.That(page).DoesNotContain("reactions.js");
    }

    [Test]
    public async Task A_post_renders_its_buttons_hidden_for_the_script_and_a_preview_has_none()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var reader = site.CreateClient();
        using var admin = site.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        var page = await reader.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));
        var preview = await admin.GetStringAsync(new Uri("/admin/preview/post/a-long-draft-of-walks-and-scripts", UriKind.Relative));

        await Assert.That(page).Contains("<ws-reactions class=\"reactions\" hidden data-post=\"hello-world\" data-key=\"posts/hello-world.md\"");
        await Assert.That(page).Contains("<li data-kind=\"like\" data-emoji=\"&#x1F44D;\" title=\"Like\" hidden><span class=\"reaction-emoji\" aria-hidden=\"true\">&#x1F44D;</span><span class=\"visually-hidden\">Like: </span><span class=\"reaction-count\">0</span></li>");
        await Assert.That(page).Contains("aria-expanded=\"false\" aria-controls=\"reaction-options-hello-world\"");
        await Assert.That(page).Contains("<span class=\"reaction-toggle-text\">React</span>");
        await Assert.That(page).Contains("aria-pressed=\"false\" title=\"Like\"><span aria-hidden=\"true\">&#x1F44D;</span><span class=\"visually-hidden\">Like</span></button>");
        await Assert.That(page).DoesNotContain("React to this post");
        await Assert.That(page).Contains("reactions.");
        await Assert.That(preview).DoesNotContain("<ws-reactions");
    }

    [Test]
    public async Task Posts_need_json_a_known_reaction_and_a_proof()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var formBody = new FormUrlEncodedContent([new("post", Post), new("kind", "like")]);
        using var brokenBody = new StringContent("{", Encoding.UTF8, "application/json");
        using var form = await client.PostAsync(new Uri("/visitor/reactions", UriKind.Relative), formBody);
        using var broken = await client.PostAsync(new Uri("/visitor/reactions", UriKind.Relative), brokenBody);
        using var unknownKind = await React(client, site, "wow");
        using var noProof = await Send(client, new { post = Post, kind = "like", undo = false, proof = (string?)null });
        using var wrongProof = await Send(client, new { post = Post, kind = "like", undo = false, proof = "12:forged" });

        var contactAnswer = ProofOfWork.Solve(site.Services.GetRequiredService<ProofOfWork>().Create("contact"));
        using var otherPurpose = await Send(client, new { post = Post, kind = "like", undo = false, proof = contactAnswer });

        var answer = await Answer(site);
        using var first = await Send(client, new { post = Post, kind = "like", undo = false, proof = answer });
        using var replayed = await Send(client, new { post = Post, kind = "love", undo = false, proof = answer });

        await Assert.That(form.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
        await Assert.That(broken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknownKind.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noProof.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(wrongProof.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(otherPurpose.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(site.Services.GetRequiredService<MemoryReactionStore>().Events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task One_reaction_counts_per_post_per_day_from_an_address_and_browser()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        var first = await Read(await React(client, site, "like"));
        var again = await Read(await React(client, site, "like"));
        var love = await Read(await React(client, site, "love"));
        var otherBrowser = await Read(await React(client, site, "like", userAgent: Chrome));
        var otherAddress = await Read(await React(client, site, "like", address: "198.51.100.40"));

        await Assert.That(first.Counted).IsTrue();
        await Assert.That(first.Counts["like"]).IsEqualTo(1);
        await Assert.That(again.Counted).IsFalse();
        await Assert.That(again.Counts["like"]).IsEqualTo(1);

        // A second reaction to the same post, without changing the first, doesn't count (T-0158).
        await Assert.That(love.Counted).IsFalse();
        await Assert.That(love.Counts).IsEquivalentTo(new Dictionary<string, int> { ["like"] = 1, ["love"] = 0, ["thanks"] = 0, ["insightful"] = 0 });
        await Assert.That(otherBrowser.Counts["like"]).IsEqualTo(2);
        await Assert.That(otherAddress.Counts["like"]).IsEqualTo(3);

        // The counts the page asks for include them at once on this replica.
        var counts = await client.GetFromJsonAsync<ReactionAnswer>(new Uri($"/visitor/reactions?post={Post}", UriKind.Relative));
        await Assert.That(counts!.Counts["like"]).IsEqualTo(3);
    }

    [Test]
    public async Task A_reaction_is_taken_back_on_its_day_only()
    {
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = Site(root, clock: clock);
        using var client = site.CreateClient();

        await React(client, site, "thanks");
        var undone = await Read(await React(client, site, "thanks", undo: true));
        var undoneAgain = await Read(await React(client, site, "thanks", undo: true));
        await React(client, site, "thanks");
        clock.Advance(TimeSpan.FromDays(1));
        var nextDay = await Read(await React(client, site, "thanks", undo: true));
        var notGiven = await Read(await React(client, site, "insightful", undo: true));

        await Assert.That(undone.Counted).IsTrue();
        await Assert.That(undone.Counts["thanks"]).IsEqualTo(0);
        await Assert.That(undoneAgain.Counted).IsFalse();
        await Assert.That(undoneAgain.Counts["thanks"]).IsEqualTo(0);
        await Assert.That(nextDay.Counted).IsFalse();
        await Assert.That(nextDay.Counts["thanks"]).IsEqualTo(1);
        await Assert.That(notGiven.Counts["insightful"]).IsEqualTo(0);
        await Assert.That(site.Services.GetRequiredService<MemoryReactionStore>().Events.Select(e => e.Delta)).IsEquivalentTo([1, -1, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_address_and_browser_has_an_hourly_limit()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root, perHour: 2);
        using var client = site.CreateClient();

        using var like = await React(client, site, "like");
        using var love = await React(client, site, "love", replaces: "like");
        using var thanks = await React(client, site, "thanks", replaces: "love");
        using var otherBrowser = await React(client, site, "thanks", userAgent: Chrome);

        await Assert.That(like.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(love.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(thanks.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(otherBrowser.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The refused change kept the reaction it would have replaced.
        var counts = await client.GetFromJsonAsync<ReactionAnswer>(new Uri($"/visitor/reactions?post={Post}", UriKind.Relative));
        await Assert.That(counts!.Counts["love"]).IsEqualTo(1);
        await Assert.That(site.Services.GetRequiredService<MemoryReactionStore>().Events.Select(e => (e.Kind, e.Delta)))
            .IsEquivalentTo([(ReactionKind.Like, 1), (ReactionKind.Like, -1), (ReactionKind.Love, 1), (ReactionKind.Thanks, 1)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_change_moves_the_reaction_and_a_made_up_one_takes_nothing_back()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var other = site.CreateClient();

        await React(other, site, "insightful", address: "198.51.100.50");
        await Read(await React(client, site, "like"));
        var changed = await Read(await React(client, site, "love", replaces: "like"));
        var madeUp = await Read(await React(client, site, "thanks", replaces: "insightful"));
        var removed = await Read(await React(client, site, "love", undo: true));
        var again = await Read(await React(client, site, "thanks"));

        await Assert.That(changed.Counted).IsTrue();
        await Assert.That(changed.Counts["like"]).IsEqualTo(0);
        await Assert.That(changed.Counts["love"]).IsEqualTo(1);

        // Someone else's insightful stays: this hash never gave one, and still has its love.
        await Assert.That(madeUp.Counted).IsFalse();
        await Assert.That(madeUp.Counts["insightful"]).IsEqualTo(1);
        await Assert.That(madeUp.Counts["thanks"]).IsEqualTo(0);
        await Assert.That(removed.Counts["love"]).IsEqualTo(0);

        // Taken back, the post takes a reaction from this hash again.
        await Assert.That(again.Counted).IsTrue();
        await Assert.That(again.Counts["thanks"]).IsEqualTo(1);
    }

    [Test]
    public async Task An_address_has_an_hourly_limit_whatever_its_browser_says()
    {
        // A script that changes its User-Agent is a new browser each time, but the same address (F-0188).
        await using var root = new SiteFactory();
        await using var site = Site(root, perAddress: 2);
        using var client = site.CreateClient();

        using var first = await React(client, site, "like", userAgent: "Agent/1");
        using var second = await React(client, site, "like", userAgent: "Agent/2");
        using var third = await React(client, site, "like", userAgent: "Agent/3");
        using var elsewhere = await React(client, site, "like", userAgent: "Agent/4", address: "198.51.100.60");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Counts_are_outside_the_per_minute_limit_and_reactions_are_not()
    {
        // Every post page asks for its counts: a reader going through posts quickly mustn't use up
        // what reacting needs.
        await using var root = new SiteFactory();
        await using var site = Site(root, perMinute: 1);
        using var client = site.CreateClient();

        var counts = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync(new Uri($"/visitor/reactions?post={Post}", UriKind.Relative));
            counts.Add(response.StatusCode);
        }

        using var first = await React(client, site, "like");
        using var second = await React(client, site, "love");

        await Assert.That(counts).IsEquivalentTo([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK]);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Nothing_stored_or_logged_holds_the_address()
    {
        using var log = new LogLines();
        await using var root = new SiteFactory();
        await using var site = Site(root, log: log);
        using var client = site.CreateClient();

        await React(client, site, "like", address: "203.0.113.77");
        await React(client, site, "like", address: "203.0.113.77", undo: true);
        await React(client, site, "love", address: "203.0.113.77");

        var keys = site.Services.GetRequiredService<MemoryVisitorStore>().Keys;
        var events = site.Services.GetRequiredService<MemoryReactionStore>().Events;
        await Assert.That(keys).IsNotEmpty();
        await Assert.That(keys.Where(k => k.Contains("203.0.113", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(events.Select(e => e.Post).Distinct()).IsEquivalentTo(["posts/hello-world.md"]);
        await Assert.That(log.Lines.Where(l => l.Contains("203.0.113", StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task The_analytics_show_reactions_by_day_kind_and_post()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = site.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        await React(client, site, "like");
        await React(client, site, "insightful", address: "198.51.100.42");
        await React(client, site, "like", address: "198.51.100.41");

        var page = await admin.GetStringAsync(new Uri("/admin/analytics?days=7", UriKind.Relative));

        await Assert.That(page).Contains("<div><dt>Reactions</dt><dd>3</dd></div>");

        // Each post by reaction, then the total (T-0158).
        await Assert.That(page).Contains("<th scope=\"col\" class=\"num\" title=\"Like\"><span aria-hidden=\"true\">&#x1F44D;</span><span class=\"visually-hidden\">Like</span></th>");
        await Assert.That(page).Contains("<th scope=\"row\" class=\"name\">Hello, world</th><td class=\"num\">2</td><td class=\"num\">0</td><td class=\"num\">0</td><td class=\"num\">1</td><td class=\"num total\">3</td><td class=\"num\">0</td>");
        await Assert.That(page).Contains("id=\"reactions-heading\"");
        await Assert.That(page).Contains("Like</dt><dd>2</dd>");
        await Assert.That(page).Contains("Insightful</dt><dd>1</dd>");
        await Assert.That(page).Contains("<th scope=\"row\" class=\"name\">Hello, world</th>");
    }

    [Test]
    public async Task The_report_nets_take_backs_and_names_posts()
    {
        await using var site = new SiteFactory();
        var catalog = site.Services.GetRequiredService<ContentCatalogProvider>().Current;
        var today = DateOnly.FromDateTime(Noon.UtcDateTime);
        ReactionEvent[] events =
        [
            new(Noon.AddDays(-1), "posts/hello-world.md", ReactionKind.Like, 1),
            new(Noon, "posts/hello-world.md", ReactionKind.Like, 1),
            new(Noon, "posts/hello-world.md", ReactionKind.Like, -1),
            new(Noon, "posts/gone.md", ReactionKind.Love, 1),
            new(Noon.AddDays(-9), "posts/hello-world.md", ReactionKind.Thanks, 1),
        ];

        var report = ReactionReport.Build(events, today.AddDays(-6), today, catalog);

        await Assert.That(report.Total).IsEqualTo(2);
        await Assert.That(report.Days.Count).IsEqualTo(7);
        await Assert.That(report.Days[^1].Count).IsEqualTo(1);
        await Assert.That(report.Days[^2].Count).IsEqualTo(1);
        await Assert.That(report.ByKind.Single(k => k.Kind == ReactionKind.Like).Count).IsEqualTo(1);
        await Assert.That(report.ByKind.Single(k => k.Kind == ReactionKind.Thanks).Count).IsEqualTo(0);
        await Assert.That(report.Posts.Select(p => p.Post)).IsEquivalentTo(["Hello, world", "posts/gone.md"]);
    }

    [Test]
    public async Task Site_settings_turn_reactions_on_and_off()
    {
        var on = SettingsForm.From(new OpenedSettings(SiteSettings.Parse("{\"name\":\"Example\",\"reactions\":true}"), null));
        var off = SettingsForm.From(new OpenedSettings(SiteSettings.Parse("{\"name\":\"Example\"}"), null));

        await Assert.That(on.Reactions).IsTrue();
        await Assert.That(off.Reactions).IsFalse();
        await Assert.That(SiteSettings.Empty.Reactions).IsFalse();

        on.Reactions = false;
        off.Reactions = true;
        var json = new JsonObject { ["name"] = "Example", ["reactions"] = true };
        on.Pad();
        on.ToChange([], Weesite.Content.Media.MediaIndex.Empty, out var error)!(json);
        var turnedOn = new JsonObject { ["name"] = "Example" };
        off.Pad();
        off.ToChange([], Weesite.Content.Media.MediaIndex.Empty, out _)!(turnedOn);

        await Assert.That(error).IsNull();
        await Assert.That(json.ContainsKey("reactions")).IsFalse();
        await Assert.That(turnedOn["reactions"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_keeps_totals_and_events()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var one = new TableReactionStore(tables);
        var two = new TableReactionStore(tables);

        await Assert.That(await one.GetAsync("posts/a.md", CancellationToken.None)).IsEqualTo(ReactionCounts.None);

        // Two replicas at once: no count is lost.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => (i % 2 == 0 ? one : two).AddAsync("posts/a.md", ReactionKind.Love, 1, Noon, CancellationToken.None)));
        var afterUndo = await two.AddAsync("posts/a.md", ReactionKind.Love, -1, Noon.AddDays(-400), CancellationToken.None);
        var floor = await one.AddAsync("posts/b.md", ReactionKind.Like, -1, Noon, CancellationToken.None);

        await Assert.That(afterUndo.Love).IsEqualTo(9);
        await Assert.That(floor.Like).IsEqualTo(0);
        await Assert.That((await one.GetAsync("posts/a.md", CancellationToken.None)).Love).IsEqualTo(9);

        var today = DateOnly.FromDateTime(Noon.UtcDateTime);
        var events = new List<ReactionEvent>();
        await foreach (var e in one.ReadEventsAsync(today, today, CancellationToken.None))
        {
            events.Add(e);
        }

        await Assert.That(events.Count).IsEqualTo(11);
        await Assert.That(events.Count(e => e is { Post: "posts/a.md", Kind: ReactionKind.Love, Delta: 1 })).IsEqualTo(10);

        var deleted = await one.CleanUpAsync(today.AddMonths(-13), CancellationToken.None);
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That((await one.GetAsync("posts/a.md", CancellationToken.None)).Love).IsEqualTo(9);

        // The factory reset empties both tables (T-0166).
        await Assert.That(await one.ClearAsync(CancellationToken.None)).IsGreaterThan(0);
        await Assert.That((await one.GetAsync("posts/a.md", CancellationToken.None)).Total).IsEqualTo(0);
        var left = new List<ReactionEvent>();
        await foreach (var e in one.ReadEventsAsync(today.AddMonths(-14), today, CancellationToken.None))
        {
            left.Add(e);
        }

        await Assert.That(left).IsEmpty();
    }

    // The site with the test sign-in (X-Test-Role), a quick proof of work and the given changes.
    private static WebApplicationFactory<Program> Site(SiteFactory root, TimeProvider? clock = null, int? perHour = null, LogLines? log = null, int? perMinute = null, int? perAddress = null) =>
        root.WithTestSignIn().WithWebHostBuilder(builder =>
        {
            // Quick to solve in the tests; the browser tests solve the real size.
            builder.UseSetting("Visitor:ProofMaxNumber", "500");
            if (perHour is { } limit)
            {
                builder.UseSetting("Visitor:ReactionsPerHour", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (perAddress is { } address)
            {
                builder.UseSetting("Visitor:ReactionsPerHourPerAddress", address.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (perMinute is { } minute)
            {
                builder.UseSetting("RateLimits:VisitorPerMinute", minute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (log is not null)
            {
                builder.ConfigureLogging(l => l.AddProvider(log));
            }

            if (clock is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock));
            }
        });

    private static Task<string> Answer(WebApplicationFactory<Program> site) =>
        Task.FromResult(ProofOfWork.Solve(site.Services.GetRequiredService<ProofOfWork>().Create("reaction"))!);

    private static async Task<HttpResponseMessage> React(HttpClient client, WebApplicationFactory<Program> site, string kind, bool undo = false, string address = "203.0.113.30", string userAgent = Firefox, string? replaces = null) =>
        await Send(client, new { post = Post, kind, undo, proof = await Answer(site), replaces }, address, userAgent);

    private static async Task<HttpResponseMessage> Send(HttpClient client, object body, string address = "203.0.113.30", string userAgent = Firefox)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/visitor/reactions", UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Forwarded-For", address);
        request.Headers.UserAgent.ParseAdd(userAgent);
        return await client.SendAsync(request);
    }

    private static async Task<ReactionAnswer> Read(HttpResponseMessage response)
    {
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<ReactionAnswer>())!;
        }
    }

    /// <summary>Every log line the app writes, for checking that none holds an address.</summary>
    internal sealed class LogLines : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(LogLines owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                owner.Lines.Enqueue($"{category} scope: {state}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}
