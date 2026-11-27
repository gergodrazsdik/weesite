using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Weesite.Web.Admin;
using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>The experimental MCP files extension on the editor endpoint (T-0200): add_media_file, inline and out of band.</summary>
internal sealed partial class MediaFileUploadTests
{
    private static readonly string SamplePng = Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png");
    private static readonly byte[] Notes = "Some notes for the files extension."u8.ToArray();

    [Test]
    public async Task The_editor_endpoint_offers_the_extension_and_its_tool_to_clients_that_declare_it()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var declaring = await ConnectAsync(site, declare: true);
        await using var plain = await ConnectAsync(site, declare: false);
        await using var factory = new SiteFactory();
        await using var anonymous = await ConnectPublicAsync(factory);

        var settings = JsonSerializer.SerializeToElement(declaring.Mcp.ServerCapabilities.Extensions![MediaFileUploads.ExtensionId]);
        var tool = (await declaring.Mcp.ListToolsAsync()).SingleOrDefault(t => t.Name == MediaFileUploads.ToolName);
        var plainTools = await plain.Mcp.ListToolsAsync();
        var publicTools = await anonymous.ListToolsAsync();
        var publicCall = await ErrorCodeAsync(() => CallAsync(anonymous, TextFile()));

        await Assert.That(settings.TryGetProperty("upload", out _)).IsTrue();
        await Assert.That(settings.GetProperty("maxInlineBytes").GetInt32()).IsEqualTo(MediaFileUploads.MaxInlineBytes);
        await Assert.That(tool).IsNotNull();
        var file = tool!.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("file");
        await Assert.That(file.GetProperty("x-mcp-file").GetProperty("maxSize").GetInt64()).IsEqualTo(20 * 1024 * 1024);
        await Assert.That(file.GetProperty("x-mcp-file").GetProperty("accept").EnumerateArray().Select(e => e.GetString())).Contains(".png");
        await Assert.That(file.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToList()).IsEquivalentTo(["id", "mimeType", "size"]);

        // Clients without the extension see the tools as before; the public endpoint never has it.
        await Assert.That(plainTools.Select(t => t.Name)).DoesNotContain(MediaFileUploads.ToolName);
        await Assert.That(anonymous.ServerCapabilities.Extensions?.ContainsKey(MediaFileUploads.ExtensionId) ?? false).IsFalse();
        await Assert.That(publicTools.Select(t => t.Name)).DoesNotContain(MediaFileUploads.ToolName);
        await Assert.That(publicCall).IsEqualTo((int)McpErrorCode.InvalidParams);
    }

    [Test]
    public async Task A_file_sent_out_of_band_is_stored_once_the_retry_confirms_it()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var app = await ConnectAsync(site, declare: true);
        var png = await File.ReadAllBytesAsync(SamplePng);
        var arguments = Arguments("square.png", png.Length, "A blue square");

        var request = await CallAsync(app.Mcp, arguments);
        var upload = request["uploads"]!.AsArray().Single()!;
        var stagedOnly = MediaFiles(copy, "*-square.png");
        using var sent = await PutAsync(app.Http, upload, png);
        var result = await CallAsync(app.Mcp, arguments, request["requestState"]!.GetValue<string>());
        var again = await ErrorCodeAsync(() => CallAsync(app.Mcp, arguments, request["requestState"]!.GetValue<string>()));

        await Assert.That(request["resultType"]!.GetValue<string>()).IsEqualTo("upload_required");
        await Assert.That(upload["fileId"]!.GetValue<string>()).IsEqualTo("file-1");
        await Assert.That(upload["path"]!.GetValue<string>()).Matches(TargetPath());
        await Assert.That(upload["method"]!.GetValue<string>()).IsEqualTo("PUT");
        await Assert.That(upload["credentials"]!.GetValue<string>()).IsEqualTo("include");
        await Assert.That(upload["expiresAt"]!.GetValue<string>()).IsEqualTo(site.Clock.GetUtcNow().AddMinutes(10).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));

        // Nothing is in Media until the retry confirms the bytes.
        await Assert.That(stagedOnly).IsEmpty();
        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(result["isError"]?.GetValue<bool>() ?? false).IsFalse();
        await Assert.That(Text(result)).Contains("![A blue square](/media/").And.Contains("-square.png)");
        await Assert.That(MediaFiles(copy, "*-square.png")).HasSingleItem();
        await Assert.That(again).IsEqualTo((int)McpErrorCode.InvalidParams);

        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "mcp-upload-address" && e.Target == "square.png (via Test app, files extension)")).IsTrue();
        await Assert.That(audit.Any(e => e.Action == "mcp-media-uploaded" && e.Target!.EndsWith("-square.png (via Test app)", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task A_small_file_comes_inline_without_an_upload()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var app = await ConnectAsync(site, declare: false);

        var stored = await CallAsync(app.Mcp, Arguments("inline.txt", Notes.Length, data: Convert.ToBase64String(Notes)));
        var wrongSize = await CallAsync(app.Mcp, Arguments("short.txt", Notes.Length + 1, data: Convert.ToBase64String(Notes)));
        var garbage = await CallAsync(app.Mcp, Arguments("garbage.txt", 3, data: "not base64!"));
        var nullData = await CallAsync(app.Mcp, new JsonObject { ["file"] = new JsonObject { ["id"] = "f", ["name"] = "n.txt", ["mimeType"] = "text/plain", ["size"] = 3, ["data"] = null } });
        var digest = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("other"u8)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var wrongDigest = await CallAsync(app.Mcp, Arguments("digest.txt", Notes.Length, data: Convert.ToBase64String(Notes), digest: digest));

        // Inline needs no declaration of the extension, as the extension says.
        await Assert.That(stored["isError"]?.GetValue<bool>() ?? false).IsFalse();
        await Assert.That(Text(stored)).Contains("-inline.txt");
        await Assert.That(MediaFiles(copy, "*-inline.txt")).HasSingleItem();
        await Assert.That(Text(wrongSize)).Contains("not the");
        await Assert.That(Text(garbage)).Contains("isn't base64");
        await Assert.That(Text(nullData)).Contains("isn't base64");
        await Assert.That(Text(wrongDigest)).Contains("digest");
        await Assert.That(MediaFiles(copy, "*.txt")).HasSingleItem();
    }

    [Test]
    public async Task Out_of_band_needs_the_client_to_declare_uploads()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var app = await ConnectAsync(site, declare: false);

        var code = await ErrorCodeAsync(() => CallAsync(app.Mcp, TextFile()));

        await Assert.That(code).IsEqualTo((int)McpErrorCode.MissingRequiredClientCapability);
    }

    [Test]
    public async Task Metadata_that_media_refuses_gets_no_target()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var app = await ConnectAsync(site, declare: true);

        var tooBig = await CallAsync(app.Mcp, Arguments("big.zip", (20 * 1024 * 1024) + 1));
        var wrongType = await CallAsync(app.Mcp, Arguments("setup.exe", 10));
        var noAlt = await CallAsync(app.Mcp, Arguments("photo.jpg", 10));
        var empty = await CallAsync(app.Mcp, Arguments("empty.txt", 0));
        var noFile = await CallAsync(app.Mcp, new JsonObject { ["file"] = "notes.txt" });

        await Assert.That(Text(tooBig)).Contains("larger than 20 MB");
        await Assert.That(Text(wrongType)).Contains(".jpg");
        await Assert.That(Text(noAlt)).Contains("Describe the image");
        await Assert.That(Text(empty)).Contains("empty");
        await Assert.That(Text(noFile)).Contains("file is the file");
        await Assert.That(new[] { tooBig, wrongType, noAlt, empty, noFile }.All(r => r["isError"]?.GetValue<bool>() == true && r["uploads"] is null)).IsTrue();
    }

    [Test]
    public async Task Bytes_that_aren_t_what_the_name_says_are_refused_on_the_retry()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var app = await ConnectAsync(site, declare: true);
        var arguments = Arguments("fake.png", Notes.Length, "Not really an image");

        var request = await CallAsync(app.Mcp, arguments);
        using var sent = await PutAsync(app.Http, request["uploads"]![0]!, Notes);
        var result = await CallAsync(app.Mcp, arguments, request["requestState"]!.GetValue<string>());

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(result["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(MediaFiles(copy, "*-fake.png")).IsEmpty();
    }

    [Test]
    public async Task A_target_takes_one_write_of_the_declared_size_from_its_own_app()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var app = await ConnectAsync(site, declare: true);
        await using var other = await ConnectAsync(site, declare: true);

        var request = await CallAsync(app.Mcp, TextFile());
        var upload = request["uploads"]![0]!;
        using var foreign = await PutAsync(other.Http, upload, Notes);
        using var shorter = await PutAsync(app.Http, upload, Notes[..^1]);
        using var longer = await PutAsync(app.Http, upload, [.. Notes, 1]);
        using var anonymous = await PutAsync(site.Client(role: null), upload, Notes);
        using var first = await PutAsync(app.Http, upload, Notes);
        using var second = await PutAsync(app.Http, upload, Notes);
        using var unknown = await PutAsync(app.Http, new JsonObject { ["path"] = "/upload/files/nothing-here" }, Notes);

        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(shorter.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(longer.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_request_state_works_only_for_its_call_its_app_and_its_time()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var app = await ConnectAsync(site, declare: true);
        await using var other = await ConnectAsync(site, declare: true);
        var arguments = TextFile();

        var request = await CallAsync(app.Mcp, arguments);
        var state = request["requestState"]!.GetValue<string>();
        var early = await CallAsync(app.Mcp, arguments, state);
        using var sent = await PutAsync(app.Http, request["uploads"]![0]!, Notes);
        var foreign = await ErrorCodeAsync(() => CallAsync(other.Mcp, arguments, state));
        var changed = await ErrorCodeAsync(() => CallAsync(app.Mcp, TextFile("renamed.txt"), state));
        var tampered = await ErrorCodeAsync(() => CallAsync(app.Mcp, arguments, state[..^4] + "AAAA"));

        // Members in another order and the size spelt another way are the same arguments.
        var reordered = JsonNode.Parse("""{"file":{"size":3.5e1,"mimeType":"application/octet-stream","name":"notes.txt","id":"file-1"}}""")!.AsObject();
        var stored = await CallAsync(app.Mcp, reordered, state);

        var later = await CallAsync(app.Mcp, arguments);
        using var late = await PutAsync(app.Http, Advance(site, later, minutes: 11), Notes);
        site.Clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await ErrorCodeAsync(() => CallAsync(app.Mcp, arguments, later["requestState"]!.GetValue<string>()));

        await Assert.That(Text(early)).Contains("hasn't arrived");
        await Assert.That(foreign).IsEqualTo((int)McpErrorCode.InvalidParams);
        await Assert.That(changed).IsEqualTo((int)McpErrorCode.InvalidParams);
        await Assert.That(tampered).IsEqualTo((int)McpErrorCode.InvalidParams);
        await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await Assert.That(expired).IsEqualTo((int)McpErrorCode.InvalidParams);
        await Assert.That(Text(stored)).Contains("-notes.txt");
    }

    [Test]
    public async Task An_app_holds_at_most_five_unfinished_uploads()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var app = await ConnectAsync(site, declare: true);

        var made = new List<JsonNode>();
        for (var i = 0; i < 6; i++)
        {
            made.Add(await CallAsync(app.Mcp, TextFile()));
        }

        await Assert.That(made.Take(5).All(r => r["resultType"]?.GetValue<string>() == "upload_required")).IsTrue();
        await Assert.That(Text(made[5])).Contains("hasn't finished");
    }

    [Test]
    public async Task Targets_count_against_the_upload_limit()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { Request = { Path = "/mcp/editor/upload/files/abc", Method = "PUT" } };

        await Assert.That(RateLimits.Kind(context)).IsEqualTo("mcp-upload");
    }

    // Moves the clock past a target's lifetime and returns the target.
    private static JsonNode Advance(OAuthTests.OAuthSite site, JsonNode request, int minutes)
    {
        site.Clock.Advance(TimeSpan.FromMinutes(minutes));
        return request["uploads"]![0]!;
    }

    private static JsonObject TextFile(string name = "notes.txt") => Arguments(name, Notes.Length);

    private static JsonObject Arguments(string name, long size, string? alt = null, string? data = null, string? digest = null)
    {
        var file = new JsonObject { ["id"] = "file-1", ["name"] = name, ["mimeType"] = "application/octet-stream", ["size"] = size };
        if (data is not null)
        {
            file["data"] = data;
        }

        if (digest is not null)
        {
            file["digest"] = new JsonObject { ["algorithm"] = "sha-256", ["value"] = digest };
        }

        var arguments = new JsonObject { ["file"] = file };
        if (alt is not null)
        {
            arguments["alt"] = alt;
        }

        return arguments;
    }

    // tools/call as a raw request, so an upload_required result comes back as it is.
    private static async Task<JsonNode> CallAsync(McpClient mcp, JsonObject arguments, string? requestState = null)
    {
        var parameters = new JsonObject { ["name"] = MediaFileUploads.ToolName, ["arguments"] = arguments.DeepClone() };
        if (requestState is not null)
        {
            parameters["requestState"] = requestState;
        }

        var response = await mcp.SendRequestAsync(new JsonRpcRequest { Method = "tools/call", Params = parameters });
        return response.Result!;
    }

    // The JSON-RPC error code of a call that fails: the stateless transport answers errors with an
    // HTTP error status, which the client raises with the body.
    private static async Task<int?> ErrorCodeAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (McpProtocolException e)
        {
            return (int)e.ErrorCode;
        }
        catch (HttpRequestException e) when (Code().Match(e.Message) is { Success: true } match)
        {
            return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string Text(JsonNode result) =>
        string.Concat((result["content"]?.AsArray() ?? []).Select(c => c?["text"]?.GetValue<string>()));

    private static string[] MediaFiles(SiteCopy copy, string pattern) =>
        Directory.Exists(Path.Combine(copy.Root, "media")) ? Directory.GetFiles(Path.Combine(copy.Root, "media"), pattern) : [];

    private static async Task<HttpResponseMessage> PutAsync(HttpClient http, JsonNode upload, byte[] data)
    {
        using var content = new ByteArrayContent(data);
        return await http.PutAsync(new Uri("/mcp/editor" + upload["path"]!.GetValue<string>(), UriKind.Relative), content);
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself.")]
    private static async Task<McpClient> ConnectPublicAsync(SiteFactory factory)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan, Capabilities = Declaring() });
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself; App disposes both.")]
    private static async Task<App> ConnectAsync(OAuthTests.OAuthSite site, bool declare)
    {
        using var browser = site.Client(EditorRole.Admin);
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback");
        var access = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId)).GetProperty("access_token").GetString();
        var mcpHttp = site.Client(role: null);
        mcpHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(mcpHttp.BaseAddress!, "/mcp/editor"), TransportMode = HttpTransportMode.StreamableHttp },
            mcpHttp,
            ownsHttpClient: true);
        var mcp = await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan, Capabilities = declare ? Declaring() : null });
        var http = site.Client(role: null);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return new App(mcp, http);
    }

    private static ClientCapabilities Declaring() => new()
    {
        Extensions = new Dictionary<string, object> { [MediaFileUploads.ExtensionId] = new JsonObject { ["upload"] = new JsonObject() } },
    };

    [GeneratedRegex(@"^/upload/files/[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TargetPath();

    [GeneratedRegex(@"""code"":(-?\d{1,6})", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Code();

    // An app's MCP connection and an HTTP client with its token, for the targets.
    private sealed class App(McpClient mcp, HttpClient http) : IAsyncDisposable
    {
        public McpClient Mcp { get; } = mcp;

        public HttpClient Http { get; } = http;

        public async ValueTask DisposeAsync()
        {
            await Mcp.DisposeAsync();
            Http.Dispose();
        }
    }
}
