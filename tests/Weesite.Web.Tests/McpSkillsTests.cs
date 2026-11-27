using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Protocol;
using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>The editor's MCP skill (T-0199): served at /mcp/editor only, with a manifest that matches its files.</summary>
// The SDK's skills extension is experimental (MCPEXP005), and so is what the tests read of it.
#pragma warning disable MCPEXP005
internal sealed partial class McpSkillsTests
{
    private const string SkillUri = "skill://weesite-editor/SKILL.md";

    private static readonly string SkillFile = Path.Combine(Weesite.Tests.RepoPaths.Root, "src", "Weesite.Web", EditorSkills.Folder, "weesite-editor", "SKILL.md");

    // Names in the skill that are front matter fields, not tools.
    private static readonly string[] Fields = ["image_alt", "seo_title"];

    [Test]
    public async Task The_editor_endpoint_lists_the_skill_with_a_manifest_that_matches_its_file()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectEditorAsync(site);

        var skills = await mcp.ListSkillsAsync();
        var skill = skills.Single();
        var fetched = await mcp.GetSkillAsync(SkillUri);
        var content = await mcp.ReadSkillResourceAsync(skill, SkillUri);
        var bytes = await File.ReadAllBytesAsync(SkillFile);
        var entry = skill.Resources.Entries!.Single();

        await Assert.That(McpSkillsClientExtensions.SupportsSkills(mcp.ServerCapabilities)).IsTrue();
        await Assert.That(mcp.ServerCapabilities.Resources).IsNotNull();
        await Assert.That(skill.Name).IsEqualTo("weesite-editor");
        await Assert.That(skill.Uri).IsEqualTo(SkillUri);
        await Assert.That(skill.Description).Contains("Weesite");
        await Assert.That(fetched.Uri).IsEqualTo(SkillUri);

        // The digest is the SHA-256 of the file the app ships, and the read verified against it.
        await Assert.That(entry.Uri).IsEqualTo(SkillUri);
        await Assert.That(entry.Digest).IsEqualTo("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await Assert.That(entry.Size).IsEqualTo(bytes.Length);
        await Assert.That(((TextResourceContents)content.Contents.Single()).Text).IsEqualTo(Encoding.UTF8.GetString(bytes));
    }

    [Test]
    public async Task The_public_endpoint_serves_no_skills()
    {
        await using var factory = new SiteFactory();
        await using var editorSite = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectPublicAsync(factory);

        var capabilities = mcp.ServerCapabilities;
        // An unknown method answers -32601 with a 404, which the client surfaces as an HTTP error.
        var list = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(mcp, "skills/list", new JsonObject()));
        var get = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(mcp, "skills/get", new JsonObject { ["uri"] = SkillUri }));
        var read = await Assert.ThrowsAsync<HttpRequestException>(async () => await mcp.ReadResourceAsync(new Uri(SkillUri)));

        // The editor endpoint of another site in this process still serves it: the public one
        // dropped it from its own request's options, not from the server's.
        await using var editor = await ConnectEditorAsync(editorSite);
        var skills = await editor.ListSkillsAsync();
        await using var again = await ConnectPublicAsync(factory);

        await Assert.That(McpSkillsClientExtensions.SupportsSkills(capabilities)).IsFalse();
        await Assert.That(capabilities.Resources).IsNull();
        await Assert.That(list!.Message).Contains("\"code\":-32601");
        await Assert.That(get!.Message).Contains("\"code\":-32601");
        await Assert.That(read!.Message).Contains("\"code\":-32601");
        await Assert.That(skills).HasSingleItem();
        await Assert.That(McpSkillsClientExtensions.SupportsSkills(again.ServerCapabilities)).IsFalse();
    }

    [Test]
    [Arguments("skills/list", "{}")]
    [Arguments("skills/get", """{"uri":"skill://weesite-editor/SKILL.md"}""")]
    [Arguments("resources/read", """{"uri":"skill://weesite-editor/SKILL.md"}""")]
    public async Task Without_a_token_the_editor_endpoint_answers_nothing(string method, string parameters)
    {
        await using var site = new OAuthTests.OAuthSite();
        using var http = site.Client(role: null);
        using var body = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}""", Encoding.UTF8, "application/json");
        body.Headers.ContentType!.CharSet = null;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp/editor", UriKind.Relative)) { Content = body };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(text).DoesNotContain("weesite-editor");
        await Assert.That(text).DoesNotContain("get_item");
    }

    [Test]
    public async Task Every_tool_the_skill_names_is_an_editor_tool()
    {
        var text = await File.ReadAllTextAsync(SkillFile);
        var named = Code().Matches(text).Select(m => m.Groups[1].Value).Where(n => n.Contains('_', StringComparison.Ordinal)).Except(Fields).Distinct().ToList();

        await Assert.That(named).IsNotEmpty();
        await Assert.That(named.Where(n => !EditorTools.Names.Contains(n))).IsEmpty();

        // The site-specific Markdown stays in markdown_guide, which the skill points to.
        await Assert.That(named).Contains("markdown_guide");
    }

    private static async Task<JsonRpcResponse> SendAsync(McpClient mcp, string method, JsonObject parameters) =>
        await mcp.SendRequestAsync(new JsonRpcRequest { Method = method, Params = parameters });

    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself.")]
    private static async Task<McpClient> ConnectPublicAsync(SiteFactory factory)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan });
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself.")]
    private static async Task<McpClient> ConnectEditorAsync(OAuthTests.OAuthSite site)
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

    // Inline code in the skill: tool names, fields and paths.
    [GeneratedRegex(@"`([a-z][a-z_]{0,63})`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Code();
}
