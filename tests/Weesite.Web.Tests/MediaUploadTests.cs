using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Weesite.Tests;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;

namespace Weesite.Web.Tests;

/// <summary>Upload addresses for the editor MCP endpoint (T-0137): create_media_upload and PUT /mcp/editor/upload.</summary>
internal sealed class MediaUploadTests
{
    private static readonly string SamplePng = Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png");

    [Test]
    public async Task An_upload_address_stores_the_file_once()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);
        using var http = site.Client(role: null);

        var result = await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "square.png", ["alt"] = "A blue square" });
        var address = JsonElement.Parse(Text(result));
        using var first = await PutAsync(http, address, await File.ReadAllBytesAsync(SamplePng));
        using var again = await PutAsync(http, address, await File.ReadAllBytesAsync(SamplePng));

        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(address.GetProperty("url").GetString()).IsEqualTo("http://localhost/mcp/editor/upload");
        await Assert.That(address.GetProperty("method").GetString()).IsEqualTo("PUT");
        await Assert.That(address.GetProperty("authorization").GetString()).StartsWith("Upload ");
        await Assert.That(address.GetProperty("maxBytes").GetInt64()).IsEqualTo(20 * 1024 * 1024);
        await Assert.That(address.GetProperty("curl").GetString()).Contains("-T <local file>").And.Contains(address.GetProperty("authorization").GetString()!);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var stored = JsonElement.Parse(await first.Content.ReadAsStringAsync());
        await Assert.That(stored.GetProperty("markdown").GetString()).StartsWith("![A blue square](/media/").And.EndsWith("-square.png)");
        await Assert.That(stored.GetProperty("isImage").GetBoolean()).IsTrue();
        await Assert.That(Directory.GetFiles(Path.Combine(copy.Root, "media"), "*-square.png")).HasSingleItem();
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await again.Content.ReadAsStringAsync()).Contains("was used already");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "mcp-upload-address" && e.Target == "square.png (via Test app)")).IsTrue();
        await Assert.That(audit.Any(e => e.Action == "mcp-media-uploaded" && e.Target!.EndsWith("-square.png (via Test app)", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task An_upload_address_needs_a_known_type_and_an_image_s_description()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var noAlt = await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "square.png" });
        var unknown = await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "setup.exe" });
        var document = await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "notes.pdf" });

        await Assert.That(noAlt.IsError).IsTrue();
        await Assert.That(Text(noAlt)).Contains("Describe the image");
        await Assert.That(unknown.IsError).IsTrue();
        await Assert.That(Text(unknown)).Contains(".jpg");
        await Assert.That(document.IsError).IsNotEqualTo(true);
    }

    [Test]
    public async Task An_app_holds_at_most_five_unused_addresses()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);
        var arguments = new Dictionary<string, object?> { ["fileName"] = "notes.txt" };

        var made = new List<CallToolResult>();
        for (var i = 0; i < 6; i++)
        {
            made.Add(await mcp.CallToolAsync("create_media_upload", arguments));
        }

        site.Clock.Advance(TimeSpan.FromMinutes(11));
        var later = await mcp.CallToolAsync("create_media_upload", arguments);

        await Assert.That(made.Take(5).All(r => r.IsError != true)).IsTrue();
        await Assert.That(made[5].IsError).IsTrue();
        await Assert.That(Text(made[5])).Contains("hasn't used yet");
        await Assert.That(later.IsError).IsNotEqualTo(true);
    }

    [Test]
    public async Task An_expired_address_or_a_revoked_app_s_address_takes_nothing()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);
        using var http = site.Client(role: null);
        var text = "Some notes."u8.ToArray();

        var expiring = JsonElement.Parse(Text(await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "expired.txt" })));
        site.Clock.Advance(TimeSpan.FromMinutes(11));
        using var expired = await PutAsync(http, expiring, text);

        var revoking = JsonElement.Parse(Text(await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "revoked.txt" })));
        var store = site.Services.GetRequiredService<IOAuthStore>();
        foreach (var grant in await store.ListGrantsAsync(null, CancellationToken.None))
        {
            await store.DeleteGrantAsync(grant.Id, CancellationToken.None);
        }

        using var revoked = await PutAsync(http, revoking, text);

        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await expired.Content.ReadAsStringAsync()).Contains("expired");
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await revoked.Content.ReadAsStringAsync()).Contains("access ended");
        await Assert.That(Directory.Exists(Path.Combine(copy.Root, "media")) ? Directory.GetFiles(Path.Combine(copy.Root, "media"), "*.txt") : []).IsEmpty();
    }

    [Test]
    public async Task The_upload_endpoint_takes_only_an_upload_token()
    {
        // Neither a signed-in editor's cookie nor the app's own access token will do.
        await using var site = new OAuthTests.OAuthSite();
        using var browser = site.Client();
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback");
        var access = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId)).GetProperty("access_token").GetString();
        using var app = site.Client(role: null);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var anonymous = site.Client(role: null);

        using var body = new ByteArrayContent("notes"u8.ToArray());
        using var signedIn = await browser.PutAsync(new Uri("/mcp/editor/upload", UriKind.Relative), body);
        using var bearer = await app.PutAsync(new Uri("/mcp/editor/upload", UriKind.Relative), body);
        using var madeUp = Put(new string('a', 43), "notes"u8.ToArray());
        using var made = await anonymous.SendAsync(madeUp);

        foreach (var response in new[] { signedIn, bearer, made })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await Assert.That(await signedIn.Content.ReadAsStringAsync()).Contains("Send the Authorization header from create_media_upload");
        await Assert.That(await made.Content.ReadAsStringAsync()).Contains("unknown or was used already");
    }

    [Test]
    public async Task A_wrong_length_keeps_the_address_and_a_wrong_file_uses_it_up()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);
        using var http = site.Client(role: null);
        var address = JsonElement.Parse(Text(await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "photo.png", ["alt"] = "A photo" })));
        var token = address.GetProperty("authorization").GetString()!["Upload ".Length..];

        using var tooLarge = await PutAsync(http, address, new byte[(20 * 1024 * 1024) + 1]);
        using var unknownLength = new HttpRequestMessage(HttpMethod.Put, new Uri("/mcp/editor/upload", UriKind.Relative))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Upload", token) },
            Content = new UnknownLength("notes"u8.ToArray()),
        };
        using var noLength = await http.SendAsync(unknownLength);
        using var notAnImage = await PutAsync(http, address, "not a PNG at all"u8.ToArray());
        using var afterwards = await PutAsync(http, address, await File.ReadAllBytesAsync(SamplePng));

        await Assert.That(tooLarge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(noLength.StatusCode).IsEqualTo(HttpStatusCode.LengthRequired);
        await Assert.That(notAnImage.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(afterwards.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(Directory.Exists(Path.Combine(copy.Root, "media")) ? Directory.GetFiles(Path.Combine(copy.Root, "media"), "*-photo.png") : []).IsEmpty();
    }

    [Test]
    public async Task A_browser_page_on_another_site_cannot_send_to_the_upload_endpoint()
    {
        // No CORS headers: a browser stops a cross-origin PUT at its preflight.
        await using var site = new OAuthTests.OAuthSite();
        using var http = site.Client(role: null);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri("/mcp/editor/upload", UriKind.Relative));
        preflight.Headers.Add("Origin", "https://elsewhere.example");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");

        using var response = await http.SendAsync(preflight);

        await Assert.That(response.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
    }

    [Test]
    public async Task The_clean_up_deletes_expired_upload_addresses()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);
        await mcp.CallToolAsync("create_media_upload", new Dictionary<string, object?> { ["fileName"] = "notes.txt" });
        var cleanUp = site.Services.GetRequiredService<OAuthCleanUp>();

        var before = await cleanUp.CleanUpAsync(CancellationToken.None);
        site.Clock.Advance(TimeSpan.FromMinutes(11));
        var after = await cleanUp.CleanUpAsync(CancellationToken.None);

        await Assert.That(before.Uploads).IsEqualTo(0);
        await Assert.That(after.Uploads).IsEqualTo(1);
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_hands_out_an_upload_address_once()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var store = new TableOAuthStore(new TableServiceClient(azurite.GetConnectionString()), TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        MediaUploadTicket Ticket(string hash, string grant, DateTimeOffset expires) =>
            new(hash, grant, "editor@example.com", "App", "photo.png", "A photo", expires);

        await store.SaveUploadAsync(Ticket("abc123", "grant1", now.AddMinutes(10)), CancellationToken.None);
        await store.SaveUploadAsync(Ticket("def456", "grant1", now.AddMinutes(10)), CancellationToken.None);
        await store.SaveUploadAsync(Ticket("old789", "grant1", now.AddMinutes(-1)), CancellationToken.None);
        await store.SaveUploadAsync(Ticket("ghi012", "grant2", now.AddMinutes(10)), CancellationToken.None);

        await Assert.That(await store.CountUploadsAsync("grant1", now, CancellationToken.None)).IsEqualTo(2);
        var taken = await Task.WhenAll(store.TakeUploadAsync("abc123", CancellationToken.None), store.TakeUploadAsync("abc123", CancellationToken.None));
        await Assert.That(taken.Count(t => t is not null)).IsEqualTo(1);
        await Assert.That(taken.First(t => t is not null)).IsEqualTo(Ticket("abc123", "grant1", now.AddMinutes(10)));
        await Assert.That(await store.TakeUploadAsync("abc123", CancellationToken.None)).IsNull();
        await Assert.That(await store.TakeUploadAsync("bad/key", CancellationToken.None)).IsNull();
        await Assert.That(await store.CountUploadsAsync("grant1", now, CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await store.DeleteExpiredUploadsAsync(now, CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await store.TakeUploadAsync("old789", CancellationToken.None)).IsNull();
        await Assert.That(await store.TakeUploadAsync("ghi012", CancellationToken.None)).IsNotNull();
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static HttpRequestMessage Put(string token, byte[] data) =>
        new(HttpMethod.Put, new Uri("/mcp/editor/upload", UriKind.Relative))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Upload", token) },
            Content = new ByteArrayContent(data),
        };

    private static async Task<HttpResponseMessage> PutAsync(HttpClient http, JsonElement address, byte[] data)
    {
        using var request = Put(address.GetProperty("authorization").GetString()!["Upload ".Length..], data);
        return await http.SendAsync(request);
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself.")]
    private static async Task<McpClient> ConnectAsync(OAuthTests.OAuthSite site)
    {
        using var browser = site.Client();
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback");
        var access = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId)).GetProperty("access_token").GetString();
        var http = site.Client(role: null);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp/editor"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan });
    }

    // A body sent without Content-Length, as a stream of unknown length is.
    private sealed class UnknownLength(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
