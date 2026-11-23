using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weesite.Content;
using Weesite.Web.Admin;
using Weesite.Web.Seo;

namespace Weesite.Web.Tests;

/// <summary>IndexNow (T-0146): the setting, the key file, and which changes are sent.</summary>
internal sealed class IndexNowTests
{
    private const string Key = "indexnow-0123456789abcdef0123456789abcdef";

    [Test]
    public async Task Turning_it_on_generates_a_prefixed_key_served_as_a_file_and_off_keeps_it()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));
        using var visitor = factory.CreateClient();
        var settings = Path.Combine(copy.Root, "settings", "site.json");

        await SaveAsync(admin, indexNow: true);
        var key = StoredKey(settings, out var enabled);
        using var file = await visitor.GetAsync(new Uri($"/{key}.txt", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var other = await visitor.GetAsync(new Uri($"/{IndexNowSettings.KeyPrefix}{new string('0', 32)}.txt", UriKind.Relative));

        await Assert.That(enabled).IsTrue();
        await Assert.That(IndexNowSettings.IsKey(key)).IsTrue();
        await Assert.That(key).StartsWith("indexnow-");
        await Assert.That(file.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(file.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        await Assert.That(await file.Content.ReadAsStringAsync()).IsEqualTo(key);
        await Assert.That(page).Contains($"<code>{key}</code>");
        await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Off: the file goes, the key stays; on again: the same key.
        await SaveAsync(admin, indexNow: false);
        using var gone = await visitor.GetAsync(new Uri($"/{key}.txt", UriKind.Relative));
        var kept = StoredKey(settings, out var off);
        await SaveAsync(admin, indexNow: true);

        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((kept, off)).IsEqualTo((key, false));
        await Assert.That(StoredKey(settings, out _)).IsEqualTo(key);
    }

    [Test]
    public async Task Only_changes_to_what_the_public_site_shows_are_sent()
    {
        using var copy = Copy(enabled: true);
        await using var site = new SiteFactory(copy.Root);
        await using var factory = Configured(site, batchSeconds: 3600);
        var editor = factory.Services.GetRequiredService<ContentEditor>();
        var queue = factory.Services.GetRequiredService<IndexNowQueue>();

        // A draft: nothing public changed.
        var draft = Draft("quiet-draft", ContentStatus.Draft);
        var created = (ContentSaved)await editor.SaveAsync(null, draft, null, CancellationToken.None);
        await Assert.That(Taken(queue)).IsEmpty();

        // Published: its address.
        var published = (ContentSaved)await editor.SaveAsync(created.Path, draft with { Status = ContentStatus.Published }, created.ETag, CancellationToken.None);
        await Assert.That(Taken(queue)).IsEquivalentTo(["https://example.org/blog/quiet-draft"]);

        // A new slug: the new address and the old one, which now redirects.
        var renamed = (ContentSaved)await editor.SaveAsync(published.Path, draft with { Status = ContentStatus.Published, Slug = "louder-post" }, published.ETag, CancellationToken.None);
        await Assert.That(Taken(queue)).IsEquivalentTo(["https://example.org/blog/louder-post", "https://example.org/blog/quiet-draft"]);

        // Unchanged: nothing.
        await editor.SaveAsync(renamed.Path, (await editor.OpenAsync(renamed.Path, CancellationToken.None))!.Draft, renamed.ETag, CancellationToken.None);
        await Assert.That(Taken(queue)).IsEmpty();

        // Deleted: its address, so the engines drop it.
        await Assert.That(await editor.DeleteAsync(renamed.Path, renamed.ETag, CancellationToken.None)).IsTrue();
        await Assert.That(Taken(queue)).IsEquivalentTo(["https://example.org/blog/louder-post"]);
    }

    [Test]
    public async Task Nothing_is_sent_while_it_is_off()
    {
        using var copy = Copy(enabled: false);
        await using var site = new SiteFactory(copy.Root);
        await using var factory = Configured(site, batchSeconds: 3600);
        var editor = factory.Services.GetRequiredService<ContentEditor>();

        await editor.SaveAsync(null, Draft("unannounced", ContentStatus.Published), null, CancellationToken.None);

        await Assert.That(Taken(factory.Services.GetRequiredService<IndexNowQueue>())).IsEmpty();
    }

    [Test]
    public async Task A_failing_endpoint_doesnt_stop_the_sender()
    {
        // F-0208: the HTTP client's resilience handler throws exceptions of its own (Polly's timeout
        // and circuit breaker), and a timeout arrives as a TaskCanceledException; neither may stop
        // the sender, or the app with it.
        using var copy = Copy(enabled: true);
        using var endpoint = new FakeIndexNow();
        endpoint.Failures.Enqueue(new InvalidOperationException("Not an HTTP error"));
        endpoint.Failures.Enqueue(new TaskCanceledException("A timeout, not shutting down"));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = Configured(site, batchSeconds: 0, endpoint);
        var editor = factory.Services.GetRequiredService<ContentEditor>();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        // The first two sends fail; the third change still gets through.
        await editor.SaveAsync(null, Draft("first-try", ContentStatus.Published), null, CancellationToken.None);
        await UntilAsync(() => endpoint.Failures.Count == 1);
        await editor.SaveAsync(null, Draft("second-try", ContentStatus.Published), null, CancellationToken.None);
        await UntilAsync(() => endpoint.Failures.Count == 0);
        await editor.SaveAsync(null, Draft("third-try", ContentStatus.Published), null, CancellationToken.None);
        var body = await endpoint.Bodies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(body["urlList"]!.AsArray().Select(u => u!.GetValue<string>())).Contains("https://example.org/blog/third-try");
        await Assert.That(lifetime.ApplicationStopping.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Changes_are_posted_to_IndexNow_with_the_key_and_its_file()
    {
        using var copy = Copy(enabled: true);
        using var endpoint = new FakeIndexNow();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = Configured(site, batchSeconds: 0, endpoint);
        var editor = factory.Services.GetRequiredService<ContentEditor>();

        await editor.SaveAsync(null, Draft("announced", ContentStatus.Published), null, CancellationToken.None);
        var body = await endpoint.Bodies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(body["host"]!.GetValue<string>()).IsEqualTo("example.org");
        await Assert.That(body["key"]!.GetValue<string>()).IsEqualTo(Key);
        await Assert.That(body["keyLocation"]!.GetValue<string>()).IsEqualTo($"https://example.org/{Key}.txt");
        await Assert.That(body["urlList"]!.AsArray().Select(u => u!.GetValue<string>())).IsEquivalentTo(["https://example.org/blog/announced"]);
    }

    // The sample site with IndexNow on or off, and its key.
    private static SiteCopy Copy(bool enabled)
    {
        var copy = new SiteCopy();
        var file = Path.Combine(copy.Root, "settings", "site.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["indexNow"] = new JsonObject { ["enabled"] = enabled, ["key"] = Key };
        File.WriteAllText(file, json.ToJsonString());
        return copy;
    }

    // Site:BaseUrl names the addresses of saves made outside a request, as here.
    private static WebApplicationFactory<Program> Configured(SiteFactory site, int batchSeconds, FakeIndexNow? endpoint = null) =>
        site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Site:BaseUrl", "https://example.org");
            builder.UseSetting("IndexNow:BatchSeconds", batchSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (endpoint is not null)
            {
                builder.ConfigureTestServices(services => services.AddHttpClient(IndexNowSender.ClientName).ConfigurePrimaryHttpMessageHandler(() => endpoint));
            }
        });

    // Waits for a condition the sender's background work makes true, up to 30 s.
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static ContentDraft Draft(string slug, ContentStatus status) =>
        new() { Title = slug, Slug = slug, Date = new DateOnly(2026, 11, 18), Status = status, Body = "Text." };

    // What the queue holds now; the sender waits an hour before it takes anything.
    private static List<string> Taken(IndexNowQueue queue)
    {
        var urls = new List<string>();
        while (queue.Reader.TryRead(out var url))
        {
            urls.Add(url.Url);
        }

        return urls;
    }

    private static async Task SaveAsync(HttpClient admin, bool indexNow)
    {
        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        var fields = Forms.Values(page, "settings", "save").Where(f => f.Key != "Form.IndexNow").ToList();
        if (indexNow)
        {
            fields.Add(new("Form.IndexNow", "true"));
        }

        using var form = new FormUrlEncodedContent(fields);
        using var response = await admin.PostAsync(new Uri("/admin/settings", UriKind.Relative), form);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
    }

    private static string? StoredKey(string settings, out bool enabled)
    {
        var indexNow = JsonNode.Parse(File.ReadAllText(settings))!["indexNow"];
        enabled = indexNow?["enabled"]?.GetValue<bool>() == true;
        return indexNow?["key"]?.GetValue<string>();
    }

    private sealed class FakeIndexNow : HttpMessageHandler
    {
        public Channel<JsonObject> Bodies { get; } = Channel.CreateUnbounded<JsonObject>();

        /// <summary>Gets what the next requests throw, one each, before it answers again.</summary>
        public Queue<Exception> Failures { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Failures)
            {
                if (Failures.TryDequeue(out var failure))
                {
                    throw failure;
                }
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            await Bodies.Writer.WriteAsync(JsonNode.Parse(body)!.AsObject(), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
