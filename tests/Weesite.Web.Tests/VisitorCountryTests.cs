using System.Net;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Tests;
using Weesite.Web.Analytics;

namespace Weesite.Web.Tests;

/// <summary>Visitor countries in the analytics (T-0143), with a test database written here.</summary>
internal sealed class VisitorCountryTests
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static MmdbWriter TestDatabase() => new MmdbWriter()
        .Add("203.0.113.0/24", "NL")
        .Add("198.51.100.0/24", null)
        .Add("2001:db8::/32", "de");

    [Test]
    [Arguments("203.0.113.7", "NL")]
    [Arguments("::ffff:203.0.113.7", "NL")]
    [Arguments("203.0.113.7:4567", "NL")]
    [Arguments("2001:db8::1", "DE")]
    [Arguments("[2001:db8::1]:443", "DE")]
    [Arguments("198.51.100.1", null)]
    [Arguments("192.0.2.1", null)]
    [Arguments("2001:db9::1", null)]
    [Arguments("not an address", null)]
    [Arguments("", null)]
    public async Task Addresses_are_looked_up_in_the_database(string address, string? country)
    {
        var lookup = new CountryLookup(NullLogger<CountryLookup>.Instance);
        var before = lookup.Find(address);
        using var stream = new MemoryStream(TestDatabase().ToBytes());

        var metadata = lookup.Load(stream, "v1");

        await Assert.That(before).IsNull();
        await Assert.That(metadata.DatabaseType).IsEqualTo("Weesite-Test-Country");
        await Assert.That(lookup.Version).IsEqualTo("v1");
        await Assert.That(lookup.Find(address)).IsEqualTo(country);
    }

    [Test]
    public async Task A_file_that_isnt_a_country_database_is_refused_and_the_one_in_use_stays()
    {
        var lookup = new CountryLookup(NullLogger<CountryLookup>.Instance);
        using var good = new MemoryStream(TestDatabase().ToBytes());
        lookup.Load(good, "v1");
        using var garbage = new MemoryStream(new byte[4096]);
        using var asn = new MemoryStream(new MmdbWriter { DatabaseType = "Weesite-Test-ASN" }.Add("203.0.113.0/24", "XX").ToBytes());

        await Assert.That(() => lookup.Load(garbage, "v2")).Throws<InvalidDataException>();
        await Assert.That(() => lookup.Load(asn, "v3")).Throws<InvalidDataException>();
        await Assert.That(lookup.Version).IsEqualTo("v1");
        await Assert.That(lookup.Find("203.0.113.7")).IsEqualTo("NL");
    }

    [Test]
    [Arguments("NL", "Netherlands")]
    [Arguments("de", "Germany")]
    [Arguments("ZZ", null)]
    [Arguments("Q1", null)]
    [Arguments("NLD", null)]
    [Arguments(null, null)]
    public async Task Countries_are_named_in_English(string? code, string? name)
    {
        await Assert.That(CountryLookup.EnglishName(code)).IsEqualTo(name);
    }

    [Test]
    public async Task People_get_a_country_and_bots_and_agents_do_not()
    {
        using var folder = new TempFolder();
        TestDatabase().Save(folder.File("country.mmdb"));
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(b => b.UseSetting("Analytics:CountryDatabase", folder.File("country.mmdb")));
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MemoryPageViewStore>();
        await WaitForDatabaseAsync(factory);

        await GetAsync(client, "/", Chrome, "203.0.113.7");
        await GetAsync(client, "/about", Chrome, "192.0.2.1");
        await GetAsync(client, "/blog", "curl/8.9.1", "203.0.113.7");
        await GetAsync(client, "/blog", "Mozilla/5.0 (compatible; ClaudeBot/1.0)", "203.0.113.7");

        var views = await WaitForViewsAsync(store, 4);
        await Assert.That(views.Single(v => v.Path == "/").Country).IsEqualTo("NL");
        await Assert.That(views.Single(v => v.Path == "/about").Country).IsNull();
        await Assert.That(views.Where(v => v.Path == "/blog").Select(v => v.Country)).IsEquivalentTo(new string?[] { null, null });
    }

    [Test]
    public async Task No_country_is_recorded_when_the_settings_turn_it_off()
    {
        using var copy = new SiteCopy();
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        var json = await File.ReadAllTextAsync(settings);
        await File.WriteAllTextAsync(settings, "{\n  \"visitorCountries\": false," + json.TrimStart()[1..]);
        using var folder = new TempFolder();
        TestDatabase().Save(folder.File("country.mmdb"));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithWebHostBuilder(b => b.UseSetting("Analytics:CountryDatabase", folder.File("country.mmdb")));
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MemoryPageViewStore>();
        await WaitForDatabaseAsync(factory);

        await GetAsync(client, "/", Chrome, "203.0.113.7");

        var view = (await WaitForViewsAsync(store, 1)).Single();
        await Assert.That(view.Kind).IsEqualTo(ClientKind.Browser);
        await Assert.That(view.Country).IsNull();
    }

    [Test]
    public async Task Without_a_database_no_country_is_recorded()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(b => b.UseSetting("Analytics:CountryDatabase", "/no/such/country.mmdb"));
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MemoryPageViewStore>();

        await GetAsync(client, "/", Chrome, "203.0.113.7");

        var view = (await WaitForViewsAsync(store, 1)).Single();
        await Assert.That(view.Country).IsNull();
        await Assert.That(factory.Services.GetRequiredService<CountryLookup>().Version).IsNull();
    }

    [Test]
    public async Task A_new_database_file_is_picked_up()
    {
        using var folder = new TempFolder();
        var path = folder.File("country.mmdb");
        TestDatabase().Save(path);
        var lookup = new CountryLookup(NullLogger<CountryLookup>.Instance);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Analytics:CountryDatabase", path)]).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var loader = new CountryDatabaseLoader(lookup, configuration, services, TimeProvider.System, NullLogger<CountryDatabaseLoader>.Instance);

        await loader.RefreshAsync(CancellationToken.None);
        var first = lookup.Find("203.0.113.7");
        new MmdbWriter().Add("203.0.113.0/24", "BE").Save(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await loader.RefreshAsync(CancellationToken.None);

        await Assert.That(first).IsEqualTo("NL");
        await Assert.That(lookup.Find("203.0.113.7")).IsEqualTo("BE");
    }

    [Test]
    public async Task The_report_lists_peoples_countries_with_unknown_ones_together()
    {
        var at = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        PageView View(string visitor, string? country, ClientKind kind = ClientKind.Browser) => new(at, "/", null, kind, null, visitor, country);
        var views = new[]
        {
            View("a", "NL"), View("a", "NL"), View("b", "NL"),
            View("c", "DE"),
            View("d", null), View("e", "ZZ"), View("f", "x1"), View("g", "nld"),
            View("h", "US", ClientKind.Bot), View("i", "US", ClientKind.Agent),
        };

        var report = AnalyticsReport.Build(views, new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 8));

        await Assert.That(report.Countries).IsEquivalentTo(
            new[] { new ReportRow("ZZ", 4, 4), new ReportRow("NL", 3, 2), new ReportRow("DE", 1, 1) },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_dashboard_shows_the_countries_and_credits_db_ip()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        var store = factory.Services.GetRequiredService<MemoryPageViewStore>();
        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(
        [
            new PageView(now, "/", null, ClientKind.Browser, null, "a", "NL"),
            new PageView(now, "/", null, ClientKind.Browser, null, "b", "NL"),
            new PageView(now, "/", null, ClientKind.Browser, null, "c"),
        ], CancellationToken.None);
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        var html = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/analytics?days=7", UriKind.Relative)));

        await Assert.That(html).Contains("<h2 id=\"countries-heading\" class=\"side-label\">Countries</h2>");
        await Assert.That(html).Contains("<th scope=\"col\">Country</th>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">Netherlands</th>");
        await Assert.That(html).Contains("<th scope=\"row\" class=\"name\">Unknown</th>");
        await Assert.That(html).Contains("IP to country data by <a href=\"https://db-ip.com\">DB-IP</a>");
        await Assert.That(html).DoesNotContain("Countries aren't being recorded");
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_keeps_the_country_and_reads_old_rows_without_one()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var store = new TablePageViewStore(tables);
        var at = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        await store.AddAsync([new PageView(at, "/new", null, ClientKind.Browser, null, "0123456789abcdef", "NL")], CancellationToken.None);

        // A row written before T-0143 has no Country column.
        await tables.GetTableClient("pageviews").AddEntityAsync(new TableEntity("2026-11-10", "0-old")
        {
            ["At"] = at,
            ["Path"] = "/old",
            ["Kind"] = "Browser",
            ["Visitor"] = "fedcba9876543210",
        });

        var views = new List<PageView>();
        await foreach (var view in store.ReadAsync(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 10), CancellationToken.None))
        {
            views.Add(view);
        }

        await Assert.That(views.Single(v => v.Path == "/new").Country).IsEqualTo("NL");
        await Assert.That(views.Single(v => v.Path == "/old").Country).IsNull();
    }

    private static async Task GetAsync(HttpClient client, string path, string userAgent, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Add("X-Forwarded-For", address);
        (await client.SendAsync(request)).Dispose();
    }

    // The loader runs in the background after startup: wait for it, up to five seconds.
    private static async Task WaitForDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        var lookup = factory.Services.GetRequiredService<CountryLookup>();
        for (var i = 0; i < 50 && lookup.Version is null; i++)
        {
            await Task.Delay(100);
        }

        await Assert.That(lookup.Version).IsNotNull();
    }

    // The writer stores views in the background: wait for them, up to five seconds.
    private static async Task<IReadOnlyList<PageView>> WaitForViewsAsync(MemoryPageViewStore store, int count)
    {
        for (var i = 0; i < 50 && store.All.Count < count; i++)
        {
            await Task.Delay(100);
        }

        await Task.Delay(100);
        return store.All;
    }

    private sealed class TempFolder : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("weesite-geoip-").FullName;

        public string File(string name) => Path.Combine(_root, name);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
