using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content.Sources;
using Weesite.Tests;

namespace Weesite.Content.Tests;

/// <summary>
/// Which earlier versions the app deletes (F-0240): only those beyond the newest few that were
/// replaced long enough ago, counted from when they were replaced, not from when they were written.
/// </summary>
internal sealed class VersionRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static (string VersionId, DateTimeOffset WrittenAt, bool IsCurrent) Written(string id, int daysAgo, bool current = false) =>
        (id, Now.AddDays(-daysAgo), current);

    [Test]
    public async Task A_version_written_long_ago_but_replaced_today_stays()
    {
        // The lifecycle rule's flaw: a post unchanged for 200 days, edited today, lost its earlier
        // text within a day, because the version's age counted from when it was written.
        var expired = new VersionRetention(0, 90).Expired([Written("old", 200), Written("new", 0, current: true)], Now);

        await Assert.That(expired).IsEmpty();
    }

    [Test]
    public async Task A_version_replaced_beyond_the_days_and_the_newest_count_goes()
    {
        var expired = new VersionRetention(1, 90).Expired(
            [Written("v1", 300), Written("v2", 200), Written("v3", 100), Written("v4", 0, current: true)],
            Now);

        // v3 is the newest earlier version (kept by count); v2 was replaced 100 days ago, v1 200.
        await Assert.That(expired).IsEquivalentTo(["v2", "v1"]);
    }

    [Test]
    public async Task The_newest_versions_stay_however_old()
    {
        var expired = new VersionRetention(50, 90).Expired(
            [.. Enumerable.Range(1, 50).Select(i => Written($"v{i}", 1000 - i)), Written("current", 0, current: true)],
            Now);

        await Assert.That(expired).IsEmpty();
    }

    [Test]
    public async Task Beyond_the_newest_count_only_versions_replaced_long_ago_go()
    {
        // 60 earlier versions, one a day, then the current one today: the newest 50 stay, and the
        // 10 older ones were all replaced more than 5 days ago.
        var versions = Enumerable.Range(1, 60).Select(i => Written($"v{i:D2}", 61 - i)).Append(Written("current", 0, current: true));

        var expired = new VersionRetention(50, 5).Expired(versions, Now);

        await Assert.That(expired.Count).IsEqualTo(10);
        await Assert.That(expired).Contains("v10");
        await Assert.That(expired).DoesNotContain("v11");
    }

    [Test]
    public async Task Beyond_the_newest_count_a_version_replaced_recently_stays()
    {
        var expired = new VersionRetention(0, 90).Expired(
            [Written("v1", 200), Written("v2", 30), Written("v3", 0, current: true)],
            Now);

        // v2 replaced v1 30 days ago, so v1 stays too.
        await Assert.That(expired).IsEmpty();
    }

    [Test]
    public async Task The_last_text_of_a_deleted_file_stays()
    {
        // No current version: nothing replaced the newest one, so it isn't counted as replaced.
        var expired = new VersionRetention(0, 0).Expired([Written("v1", 400), Written("v2", 300)], Now);

        await Assert.That(expired).IsEquivalentTo(["v1"]);
    }

    [Test]
    public async Task Versions_written_in_the_same_second_are_ordered_by_their_id()
    {
        var expired = new VersionRetention(0, 0).Expired(
            [("2026-01-01T00:00:00.0000002Z", Now.AddDays(-1), false), ("2026-01-01T00:00:00.0000001Z", Now.AddDays(-1), false), ("c", Now, true)],
            Now);

        // The later ID was replaced only now, so it stays; the earlier one was replaced by it.
        await Assert.That(expired).IsEquivalentTo(["2026-01-01T00:00:00.0000001Z"]);
    }

    [Test]
    public async Task Settings_set_the_limits_and_default_to_50_and_90()
    {
        var empty = VersionRetention.From(new ConfigurationBuilder().Build());
        var set = VersionRetention.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Content:VersionsKept"] = "10", ["Content:VersionDays"] = "-1" })
            .Build());

        await Assert.That(empty).IsEqualTo(new VersionRetention(50, 90));
        await Assert.That(set).IsEqualTo(new VersionRetention(10, 0));
    }

    [Test]
    [Category("containers")]
    public async Task Against_Azurite_saves_and_deletes_work_with_the_clean_up()
    {
        // Azurite keeps no versions, so the clean-up finds none: saves and deletes go on as before.
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        var store = new BlobContentStore(blobs, new VersionRetention(0, 0), new Clock(DateTimeOffset.UtcNow.AddDays(1)), NullLogger<BlobContentStore>.Instance);

        var etag = await store.WriteAsync("posts/a.md", "one", null, CancellationToken.None);
        etag = await store.WriteAsync("posts/a.md", "two", etag, CancellationToken.None);
        var settings = await store.WriteSettingsFileAsync("site.json", "{}", null, CancellationToken.None);
        settings = await store.WriteSettingsFileAsync("site.json", "{ }", settings, CancellationToken.None);
        var deleted = await store.DeleteAsync("posts/a.md", etag!, CancellationToken.None);

        await Assert.That(settings).IsNotNull();
        await Assert.That((await store.ReadSettingsFileAsync("site.json", CancellationToken.None))?.Text).IsEqualTo("{ }");
        await Assert.That(deleted).IsTrue();
        await Assert.That(await store.ReadAsync("posts/a.md", CancellationToken.None)).IsNull();
    }

    [Test]
    [Arguments("content", "posts/a.md")]
    [Arguments("settings", "site.json")]
    public async Task A_save_deletes_the_versions_the_retention_lets_go(string container, string path)
    {
        // Storage with versioning, through the SDK: the list it sends, and the deletes it makes.
        using var storage = new VersionedStorage(
            ("2026-01-01T00:00:00.0000000Z", Now.AddDays(-300)),
            ("2026-04-01T00:00:00.0000000Z", Now.AddDays(-200)),
            ("2026-08-01T00:00:00.0000000Z", Now.AddDays(-100)),
            ("2026-11-23T12:00:00.0000000Z", Now));
        var store = new BlobContentStore(storage.Client, new VersionRetention(1, 90), new Clock(Now), NullLogger<BlobContentStore>.Instance);

        var etag = container == "content"
            ? await store.WriteAsync(path, "text", null, CancellationToken.None)
            : await store.WriteSettingsFileAsync(path, "{}", null, CancellationToken.None);

        await Assert.That(etag).IsNotNull();
        await Assert.That(storage.Listed).IsEquivalentTo([$"{container}?prefix={path}"]);
        await Assert.That(storage.Deleted).IsEquivalentTo([$"{container}/{path}@2026-01-01T00:00:00.0000000Z", $"{container}/{path}@2026-04-01T00:00:00.0000000Z"]);
    }

    [Test]
    public async Task The_version_marker_keeps_no_earlier_values()
    {
        using var storage = new VersionedStorage(
            ("2026-01-01T00:00:00.0000000Z", Now.AddDays(-300)),
            ("2026-04-01T00:00:00.0000000Z", Now.AddDays(-200)),
            ("2026-11-23T11:00:00.0000000Z", Now.AddHours(-1)),
            ("2026-11-23T12:00:00.0000000Z", Now));
        var store = new BlobContentStore(storage.Client, VersionRetention.Default, new Clock(Now.AddSeconds(1)), NullLogger<BlobContentStore>.Instance);

        await store.BumpVersionAsync(CancellationToken.None);

        await Assert.That(storage.Deleted.Count).IsEqualTo(3);
        await Assert.That(storage.Deleted).DoesNotContain("settings/version@2026-11-23T12:00:00.0000000Z");
    }

    [Test]
    public async Task A_save_goes_through_when_versions_can_t_be_deleted()
    {
        // Before infra/app grants the version delete role, storage answers 403.
        using var storage = new VersionedStorage(("2026-01-01T00:00:00.0000000Z", Now.AddDays(-300)), ("2026-11-23T12:00:00.0000000Z", Now)) { DeleteStatus = HttpStatusCode.Forbidden };
        var store = new BlobContentStore(storage.Client, new VersionRetention(0, 90), new Clock(Now), NullLogger<BlobContentStore>.Instance);

        var etag = await store.WriteAsync("posts/a.md", "text", null, CancellationToken.None);

        await Assert.That(etag).IsNotNull();
        await Assert.That(storage.Deleted).IsEmpty();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A storage account with blob versioning, as far as the store uses it: every blob has the
    /// given versions, the newest current; uploads succeed; deletes of a version are recorded.
    /// </summary>
    private sealed class VersionedStorage : HttpMessageHandler
    {
        private readonly (string Id, DateTimeOffset WrittenAt)[] _versions;
        private readonly HttpClient _http;

        public VersionedStorage(params (string Id, DateTimeOffset WrittenAt)[] versions)
        {
            _versions = versions;
            _http = new HttpClient(this, disposeHandler: false);
            Client = new BlobServiceClient(
                new Uri("https://account.blob.core.windows.net/"),
                new BlobClientOptions { Transport = new HttpClientTransport(_http), Retry = { MaxRetries = 0 } });
        }

        public BlobServiceClient Client { get; }

        public HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.Accepted;

        public List<string> Listed { get; } = [];

        public List<string> Deleted { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
            if (request.Method == HttpMethod.Get && query["comp"] == "list")
            {
                Listed.Add($"{path}?prefix={query["prefix"]}");
                var newest = _versions.Max(v => v.WrittenAt);
                var blobs = string.Concat(_versions.Select(v =>
                    $"<Blob><Name>{query["prefix"]}</Name><VersionId>{v.Id}</VersionId><IsCurrentVersion>{(v.WrittenAt == newest ? "true" : "false")}</IsCurrentVersion>" +
                    $"<Properties><Last-Modified>{v.WrittenAt.ToString("R", CultureInfo.InvariantCulture)}</Last-Modified><Etag>0x1</Etag><Content-Length>4</Content-Length><BlobType>BlockBlob</BlobType></Properties></Blob>"));
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults ServiceEndpoint=\"https://account.blob.core.windows.net/\" ContainerName=\"{path}\"><Prefix>{query["prefix"]}</Prefix><Blobs>{blobs}</Blobs><NextMarker /></EnumerationResults>", Encoding.UTF8, "application/xml"),
                };
            }
            else if (request.Method == HttpMethod.Delete && query["versionid"] is { } version)
            {
                response = new HttpResponseMessage(DeleteStatus) { Content = new ByteArrayContent([]) };
                if (DeleteStatus == HttpStatusCode.Accepted)
                {
                    Deleted.Add($"{path}@{version}");
                }
            }

            response.Headers.ETag = new EntityTagHeaderValue("\"0x2\"");
            response.Content.Headers.LastModified = Now;
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _http.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
