using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Weesite.Web.Admin;
using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>
/// Asking the editor in the app before set_status publishes (T-0198): an option of the consent,
/// asked through MCP elicitation, over multi round-trip requests with a protected request state.
/// </summary>
internal sealed class McpConfirmPublishTests
{
    private const string Draft = "posts/notes-for-a-future-post.md";
    private const string Title = "Notes for a future post";
    private const string Another = "posts/another-draft.md";

    [Test]
    public async Task Without_the_option_set_status_publishes_without_asking()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        var asked = new List<string>();
        await using var mcp = await McpEditorTests.ConnectAsync(site, publish: true, handlers: Answering("accept", asked));

        var result = await PublishAsync(mcp, Draft, await VersionAsync(mcp, Draft));

        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(Json(result).GetProperty("status").GetString()).IsEqualTo("published");
        await Assert.That(asked).IsEmpty();
        await Assert.That(mcp.ServerInstructions).Contains("They let this app write drafts, edit and publish.").And.DoesNotContain("asked to be asked first");
    }

    [Test]
    public async Task With_the_option_set_status_publishes_once_the_editor_confirms()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        var asked = new List<string>();
        await using var mcp = await McpEditorTests.ConnectAsync(site, publish: true, ask: true, handlers: Answering("accept", asked));

        var published = await PublishAsync(mcp, Draft, await VersionAsync(mcp, Draft));
        var unpublished = await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = Draft, ["version"] = Json(published).GetProperty("version").GetString(), ["status"] = "draft" });
        var scheduled = await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = Draft, ["version"] = Json(unpublished).GetProperty("version").GetString(), ["status"] = "scheduled", ["publishAt"] = "2099-01-01T09:00:00+02:00" });

        await Assert.That(published.IsError).IsNotEqualTo(true);
        await Assert.That(Json(published).GetProperty("status").GetString()).IsEqualTo("published");
        await Assert.That(unpublished.IsError).IsNotEqualTo(true);
        await Assert.That(scheduled.IsError).IsNotEqualTo(true);
        await Assert.That(Json(scheduled).GetProperty("status").GetString()).IsEqualTo("scheduled");

        // Unpublishing doesn't ask; publishing and scheduling do, naming the item and the time.
        await Assert.That(asked).IsEquivalentTo([$"Publish '{Title}' now?", $"Schedule '{Title}' for 2099-01-01 07:00 UTC?"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(mcp.ServerInstructions).Contains("set_status asks them in this app before it publishes or schedules");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(20, CancellationToken.None);
        await Assert.That(audit.Count(e => e.Action == "mcp-publish-confirmed")).IsEqualTo(2);
        await Assert.That(audit.Count(e => e.Action.StartsWith("mcp-status-", StringComparison.Ordinal))).IsEqualTo(3);
        await Assert.That(audit.Any(e => e.Action == "oauth-grant" && e.Target == "Test app (drafts, edits and publishing, asking before publishing)")).IsTrue();
    }

    [Test]
    public async Task When_the_editor_declines_nothing_changes()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        var asked = new List<string>();
        await using var mcp = await McpEditorTests.ConnectAsync(site, publish: true, ask: true, handlers: Answering("decline", asked));
        var version = await VersionAsync(mcp, Draft);

        var declined = await PublishAsync(mcp, Draft, version);

        await Assert.That(declined.IsError).IsTrue();
        await Assert.That(McpEditorTests.Text(declined)).Contains($"You declined in the app, so '{Title}' wasn't published. Nothing changed.");
        await Assert.That(asked.Count).IsEqualTo(1);
        await Assert.That(await VersionAsync(mcp, Draft)).IsEqualTo(version);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "notes-for-a-future-post.md"))).Contains("status: draft");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(20, CancellationToken.None);
        await Assert.That(audit.Count(e => e.Action == "mcp-publish-declined" && e.Target == Draft + " (published, via Test app)")).IsEqualTo(1);
        await Assert.That(audit.Any(e => e.Action.StartsWith("mcp-status-", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task An_app_that_cant_ask_doesnt_publish()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await McpEditorTests.ConnectAsync(site, publish: true, ask: true);
        var version = await VersionAsync(mcp, Draft);

        var refused = await PublishAsync(mcp, Draft, version);
        var archived = await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = Draft, ["version"] = version, ["status"] = "archived" });

        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(McpEditorTests.Text(refused)).Contains(PublishConfirmations.CannotAsk);
        await Assert.That(archived.IsError).IsNotEqualTo(true);
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(20, CancellationToken.None);
        await Assert.That(audit.Count(e => e.Action == "mcp-publish-not-asked")).IsEqualTo(1);
        await Assert.That(audit.Count(e => e.Action == "mcp-status-archived")).IsEqualTo(1);
        await Assert.That(audit.Any(e => e.Action == "mcp-status-published")).IsFalse();
    }

    [Test]
    public async Task A_changed_request_state_is_refused()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        using var app = await AppAsync(site);
        var version = await VersionAsync(site, Draft);
        var asking = await CallAsync(app, Draft, version);
        var state = asking["requestState"]!.GetValue<string>();

        var tampered = await CallAsync(app, Draft, version, state[..^2] + (state[^2] == 'A' ? "B" : "A") + state[^1], "accept");
        var madeUp = await CallAsync(app, Draft, version, "not-a-state", "accept");

        await Assert.That(asking["resultType"]!.GetValue<string>()).IsEqualTo("input_required");
        await Assert.That(asking["inputRequests"]![PublishConfirmations.Key]!["params"]!["message"]!.GetValue<string>()).IsEqualTo($"Publish '{Title}' now?");
        await Assert.That(ErrorText(tampered)).Contains("This confirmation isn't valid");
        await Assert.That(ErrorText(madeUp)).Contains("This confirmation isn't valid");
        await Assert.That(await VersionAsync(site, Draft)).IsEqualTo(version);
    }

    [Test]
    public async Task A_request_state_answers_once_for_its_item_and_change_within_ten_minutes()
    {
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "another-draft.md"), "---\ntitle: Another draft\nslug: another-draft\ndate: 2026-11-20\nstatus: draft\n---\n\nText.\n");
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        using var app = await AppAsync(site);
        using var other = await AppAsync(site);
        var version = await VersionAsync(site, Draft);
        var state = (await CallAsync(app, Draft, version))["requestState"]!.GetValue<string>();
        var otherVersion = await VersionAsync(site, Another);

        // Another item, another change, another app: each needs its own question.
        var anotherItem = await CallAsync(app, Another, otherVersion, state, "accept");
        var anotherChange = await CallAsync(app, Draft, version, state, "accept", status: "scheduled", publishAt: "2099-01-01T09:00:00Z");
        var anotherApp = await CallAsync(other, Draft, version, state, "accept");
        var published = await CallAsync(app, Draft, version, state, "accept");

        // Used once: after it published, and the item went back to a draft, it publishes nothing.
        var draftAgain = await CallAsync(app, Draft, Field(published, "version"), status: "draft");
        var replayed = await CallAsync(app, Draft, Field(draftAgain, "version"), state, "accept");

        await Assert.That(ErrorText(anotherItem)).Contains("for another item or another change");
        await Assert.That(ErrorText(anotherChange)).Contains("for another item or another change");
        await Assert.That(ErrorText(anotherApp)).Contains("for another item or another change");
        await Assert.That(Field(published, "status")).IsEqualTo("published");
        await Assert.That(Field(draftAgain, "status")).IsEqualTo("draft");
        await Assert.That(ErrorText(replayed)).Contains("or this confirmation was used already");

        // Ten minutes to answer, by the site's clock.
        var current = Field(draftAgain, "version");
        var late = (await CallAsync(app, Draft, current))["requestState"]!.GetValue<string>();
        site.Clock.Advance(PublishConfirmations.Lifetime + TimeSpan.FromSeconds(1));
        var expired = await CallAsync(app, Draft, current, late, "accept");

        await Assert.That(ErrorText(expired)).Contains("the 10 minutes to answer it are over");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "notes-for-a-future-post.md"))).Contains("status: draft");
    }

    [Test]
    public async Task The_consent_page_offers_the_option_and_the_grant_keeps_it()
    {
        await using var site = new OAuthTests.OAuthSite();
        using var browser = site.Client();
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback");
        var html = await browser.GetStringAsync(OAuthTests.AuthorizeUrl(clientId, "http://127.0.0.1:5555/callback", OAuthTests.Challenge(OAuthTests.Verifier())));

        // Only with publishing: asking means nothing to an app that can't publish.
        var drafts = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId, ask: true)).GetProperty("access_token").GetString();
        var publishing = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId, publish: true, ask: true)).GetProperty("access_token").GetString();

        await Assert.That(html).Contains("name=\"ask_before_publishing\"").And.Contains("Ask me in the app before anything is published");
        await Assert.That((await site.Tokens.ValidateAsync(drafts, "http://localhost/mcp/editor", CancellationToken.None))!.AskBeforePublishing).IsFalse();
        await Assert.That((await site.Tokens.ValidateAsync(publishing, "http://localhost/mcp/editor", CancellationToken.None))!.AskBeforePublishing).IsTrue();
    }

    // An MCP client whose user answers every question the same way.
    private static McpClientHandlers Answering(string action, List<string> asked) => new()
    {
        ElicitationHandler = (request, _) =>
        {
            asked.Add(request!.Message);
            return ValueTask.FromResult(new ElicitResult { Action = action });
        },
    };

    private static JsonElement Json(CallToolResult result) => JsonElement.Parse(McpEditorTests.Text(result));

    private static async Task<CallToolResult> PublishAsync(McpClient mcp, string path, string version) =>
        await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = path, ["version"] = version, ["status"] = "published" });

    private static async Task<string> VersionAsync(McpClient mcp, string path) =>
        Json(await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = path })).GetProperty("version").GetString()!;

    private static async Task<string> VersionAsync(OAuthTests.OAuthSite site, string path) =>
        (await site.Services.GetRequiredService<Weesite.Content.ContentEditor>().OpenAsync(path, CancellationToken.None))!.ETag;

    // An app that asked to be asked, calling the endpoint itself: it sees the request state and
    // can send back whatever it likes, as an app could.
    private static async Task<HttpClient> AppAsync(OAuthTests.OAuthSite site)
    {
        using var browser = site.Client();
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback");
        var access = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId, publish: true, ask: true)).GetProperty("access_token").GetString();
        var app = site.Client(role: null);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        app.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        app.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        app.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2026-07-28");
        return app;
    }

    // A set_status call on the 2026-07-28 protocol, with the request state and the answer of a retry.
    private static async Task<JsonObject> CallAsync(HttpClient app, string path, string version, string? state = null, string? answer = null, string status = "published", string? publishAt = null)
    {
        var arguments = new JsonObject { ["path"] = path, ["version"] = version, ["status"] = status };
        if (publishAt is not null)
        {
            arguments["publishAt"] = publishAt;
        }

        var parameters = new JsonObject
        {
            ["name"] = "set_status",
            ["arguments"] = arguments,
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject { ["elicitation"] = new JsonObject { ["form"] = new JsonObject() } },
            },
        };
        if (state is not null)
        {
            parameters["requestState"] = state;
            parameters["inputResponses"] = new JsonObject { [PublishConfirmations.Key] = new JsonObject { ["action"] = answer } };
        }

        using var content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = parameters }.ToJsonString(), Encoding.UTF8, "application/json");
        content.Headers.Add("Mcp-Method", "tools/call");
        content.Headers.Add("Mcp-Name", "set_status");
        using var response = await app.PostAsync(new Uri("/mcp/editor", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();

        // One JSON-RPC message, as JSON or as a server-sent event.
        var json = body.TrimStart() is ['{', ..] ? body : string.Concat(body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l["data:".Length..]));
        var message = JsonNode.Parse(json)!.AsObject();
        return message["result"]?.AsObject() ?? throw new InvalidOperationException($"set_status answered {json}");
    }

    // A field of a tool's result, which comes as JSON text.
    private static string Field(JsonObject result, string name) =>
        result["isError"]?.GetValue<bool>() != true
            ? JsonNode.Parse(string.Concat(result["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>())))![name]!.GetValue<string>()
            : throw new InvalidOperationException($"An error: {result.ToJsonString()}");

    // The text of a tool's error result.
    private static string ErrorText(JsonObject result) =>
        result["isError"]?.GetValue<bool>() == true
            ? string.Concat(result["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>()))
            : throw new InvalidOperationException($"Not an error: {result.ToJsonString()}");
}
