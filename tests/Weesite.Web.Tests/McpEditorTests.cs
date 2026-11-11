using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The editor MCP endpoint at /mcp/editor (ADR 0007, T-0082): bearer tokens and the read tools.</summary>
internal sealed class McpEditorTests
{
    [Test]
    public async Task Without_a_token_it_points_the_client_at_the_resource_metadata()
    {
        await using var site = new OAuthTests.OAuthSite();
        using var anonymous = site.Client(role: null);
        using var cookie = site.Client();
        using var bad = site.Client(role: null);
        bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        using var none = await PostAsync(anonymous);
        using var signedIn = await PostAsync(cookie);
        using var invalid = await PostAsync(bad);

        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(none.Headers.WwwAuthenticate.ToString()).IsEqualTo("Bearer resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp/editor\"");
        // The admin cookie doesn't count: a page elsewhere can't call the editor tools with it.
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(invalid.Headers.WwwAuthenticate.ToString()).Contains("error=\"invalid_token\"");
    }

    [Test]
    public async Task The_resource_metadata_names_the_authorization_server()
    {
        await using var site = new OAuthTests.OAuthSite();
        using var client = site.Client(role: null);

        foreach (var path in new[] { "/.well-known/oauth-protected-resource/mcp/editor", "/.well-known/oauth-protected-resource" })
        {
            var json = JsonElement.Parse(await client.GetStringAsync(new Uri(path, UriKind.Relative)));
            await Assert.That(json.GetProperty("resource").GetString()).IsEqualTo("http://localhost/mcp/editor");
            await Assert.That(json.GetProperty("authorization_servers")[0].GetString()).IsEqualTo("http://localhost");
        }
    }

    [Test]
    public async Task An_admin_gets_the_editor_tools_only()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var tools = await mcp.ListToolsAsync();

        await Assert.That(tools.Select(t => t.Name)).IsEquivalentTo(["list_items", "get_item", "list_media"]);
        await Assert.That(tools.All(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true)).IsTrue();
    }

    [Test]
    public async Task List_items_shows_drafts_and_scheduled_items_with_their_paths()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var all = Items(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["limit"] = 200 }));
        var drafts = Items(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["status"] = "draft" }));
        var pages = Items(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["kind"] = "page" }));
        var found = Items(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["query"] = "espresso" }));

        await Assert.That(all.Any(i => i.GetProperty("path").GetString() == "posts/notes-for-a-future-post.md" && i.GetProperty("status").GetString() == "draft")).IsTrue();
        await Assert.That(all.Any(i => i.GetProperty("title").GetString() == "A post from the future" && i.GetProperty("status").GetString() == "scheduled" && i.GetProperty("publishAt").GetString() is not null)).IsTrue();
        await Assert.That(drafts.All(i => i.GetProperty("status").GetString() == "draft")).IsTrue();
        await Assert.That(pages.All(i => i.GetProperty("kind").GetString() == "page")).IsTrue();
        await Assert.That(pages.Any(i => i.GetProperty("slug").GetString() == "about")).IsTrue();
        await Assert.That(found.Select(i => i.GetProperty("slug").GetString())).Contains("notes-on-dialing-in-espresso");
    }

    [Test]
    public async Task Get_item_returns_the_file_and_its_version()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var result = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "posts/notes-for-a-future-post.md" });
        var json = JsonElement.Parse(Text(result));
        var missing = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "posts/nothing.md" });

        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(json.GetProperty("markdown").GetString()).StartsWith("---\n").And.Contains("title: Notes for a future post").And.Contains("status: draft");
        await Assert.That(json.GetProperty("version").GetString()).IsNotNull().And.IsNotEmpty();
        await Assert.That(missing.IsError).IsTrue();
        await Assert.That(Text(missing)).Contains("No post or page you may edit at posts/nothing.md");
    }

    [Test]
    public async Task An_author_sees_and_reads_only_their_own_posts()
    {
        await using var site = new OAuthTests.OAuthSite(role: EditorRole.Author);
        await using var mcp = await ConnectAsync(site, EditorRole.Author);

        var items = Items(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["limit"] = 200 }));
        var other = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md" });
        var page = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "pages/about.md" });

        // The seed's posts belong to its sample author, not to this editor.
        await Assert.That(items).IsEmpty();
        await Assert.That(other.IsError).IsTrue();
        await Assert.That(page.IsError).IsTrue();
    }

    [Test]
    public async Task List_media_gives_each_file_its_markdown()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var files = JsonElement.Parse(Text(await mcp.CallToolAsync("list_media", new Dictionary<string, object?> { ["query"] = "sample" })));

        await Assert.That(files.EnumerateArray().Any(f => f.GetProperty("path").GetString() == "/media/b368a21f9e93-sample.png" && f.GetProperty("isImage").GetBoolean())).IsTrue();
        await Assert.That(files.EnumerateArray().All(f => f.GetProperty("markdown").GetString()!.Contains("/media/", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task A_revoked_grant_or_a_removed_editor_loses_the_endpoint()
    {
        await using var site = new OAuthTests.OAuthSite();
        using var browser = site.Client();
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback");
        var tokens = await OAuthTests.OAuthSite.GrantAsync(browser, clientId);
        var access = tokens.GetProperty("access_token").GetString()!;
        using var app = site.Client(role: null);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var before = await PostAsync(app);
        await site.Tokens.RevokeAsync(access, clientId, CancellationToken.None);
        using var after = await PostAsync(app);

        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static List<JsonElement> Items(CallToolResult result) =>
        [.. JsonElement.Parse(Text(result)).EnumerateArray()];

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    // An MCP initialize request, enough to see whether the endpoint lets the caller in.
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp/editor", UriKind.Relative))
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request);
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself.")]
    private static async Task<McpClient> ConnectAsync(OAuthTests.OAuthSite site, EditorRole role = EditorRole.Admin)
    {
        using var browser = site.Client(role);
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
}
