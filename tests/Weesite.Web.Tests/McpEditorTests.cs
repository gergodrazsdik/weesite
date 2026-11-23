using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The editor MCP endpoint at /mcp/editor (ADR 0007, T-0082): bearer tokens, and the read and write tools.</summary>
internal sealed class McpEditorTests
{
    private static readonly string[] InspectorRedirects = ["http://127.0.0.1:5555/callback"];

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
    public async Task Browsers_from_any_origin_may_call_it_without_credentials()
    {
        await using var site = new OAuthTests.OAuthSite();
        using var client = site.Client(role: null);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri("/mcp/editor", UriKind.Relative));
        preflight.Headers.Add("Origin", "https://inspector.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization, content-type, mcp-protocol-version");

        using var allowed = await client.SendAsync(preflight);

        await Assert.That(allowed.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");
        await Assert.That(allowed.Headers.Contains("Access-Control-Allow-Credentials")).IsFalse();

        // The challenge is readable, so the client can find the authorization server.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp/editor", UriKind.Relative));
        request.Headers.Add("Origin", "https://inspector.example");
        using var challenge = await client.SendAsync(request);
        await Assert.That(challenge.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(challenge.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");
        await Assert.That(challenge.Headers.GetValues("Access-Control-Expose-Headers").Single()).Contains("WWW-Authenticate");
    }

    [Test]
    public async Task A_browser_client_on_another_site_goes_through_the_whole_flow()
    {
        // T-0122: ASP.NET Core 11's cross-origin request protection, on by default, mustn't refuse a
        // browser-based MCP client's posts (no cookies, a bearer token or a form of its own).
        await using var site = new OAuthTests.OAuthSite();
        using var browser = site.Client();
        using var app = site.Client(role: null);
        app.DefaultRequestHeaders.Add("Origin", "https://inspector.example");
        app.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        app.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");

        using var registered = await app.PostAsJsonAsync(new Uri("/oauth/register", UriKind.Relative), new { client_name = "Inspector", redirect_uris = InspectorRedirects });
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;

        var verifier = OAuthTests.Verifier();
        var code = (await OAuthTests.OAuthSite.ConsentAsync(browser, clientId, "http://127.0.0.1:5555/callback", OAuthTests.Challenge(verifier)))["code"].ToString();
        var tokens = await OAuthTests.OAuthSite.TokenAsync(app, ("grant_type", "authorization_code"), ("code", code), ("client_id", clientId), ("redirect_uri", "http://127.0.0.1:5555/callback"), ("code_verifier", verifier));
        var refreshed = await OAuthTests.OAuthSite.TokenAsync(app, ("grant_type", "refresh_token"), ("refresh_token", tokens.GetProperty("refresh_token").GetString()!), ("client_id", clientId));

        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.GetProperty("access_token").GetString());
        using var mcp = await PostAsync(app);
        await Assert.That(mcp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(mcp.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");

        app.DefaultRequestHeaders.Authorization = null;
        using var form = new FormUrlEncodedContent([new("token", refreshed.GetProperty("refresh_token").GetString()!), new("client_id", clientId)]);
        using var revoked = await app.PostAsync(new Uri("/oauth/revoke", UriKind.Relative), form);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await site.Tokens.ValidateAsync(refreshed.GetProperty("access_token").GetString(), "http://localhost/mcp/editor", CancellationToken.None)).IsNull();
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

        await Assert.That(tools.Select(t => t.Name)).IsEquivalentTo(["list_items", "get_item", "list_media", "create_item", "update_item", "set_status", "upload_media", "preview_item", "list_versions", "get_version_diff", "markdown_guide", "replace_media", "create_media_upload", "list_tags", "list_comments", "moderate_comment", "delete_comment"]);
        await Assert.That(tools.Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true).Select(t => t.Name)).IsEquivalentTo(["list_items", "get_item", "list_media", "preview_item", "list_versions", "get_version_diff", "markdown_guide", "list_tags", "list_comments"]);
        await Assert.That(tools.Where(t => t.ProtocolTool.Annotations?.DestructiveHint == true).Select(t => t.Name)).IsEquivalentTo(["update_item", "set_status", "replace_media", "create_media_upload", "moderate_comment", "delete_comment"]);
    }

    [Test]
    public async Task Comments_are_listed_and_moderated_by_an_app_allowed_to_publish()
    {
        // ADR 0011: approving makes a visitor's words public, so it needs publishing.
        await using var site = new OAuthTests.OAuthSite();
        var store = site.Services.GetRequiredService<Weesite.Web.Visitors.ICommentStore>();
        var now = DateTimeOffset.UtcNow;
        var waiting = new Weesite.Web.Visitors.Comment(Weesite.Web.Visitors.Comment.NewId(now), "posts/hello-world.md", "Ada", "Nice post", null, Weesite.Web.Visitors.CommentStatus.Pending, now, null, null, "x");
        var other = new Weesite.Web.Visitors.Comment(Weesite.Web.Visitors.Comment.NewId(now.AddSeconds(1)), "posts/hello-world.md", "Bob", "Spam", null, Weesite.Web.Visitors.CommentStatus.Pending, now, null, null, "y");
        await store.SaveAsync(waiting, CancellationToken.None);
        await store.SaveAsync(other, CancellationToken.None);
        await using var drafts = await ConnectAsync(site);
        await using var publisher = await ConnectAsync(site, publish: true);

        var listed = JsonElement.Parse(Text(await drafts.CallToolAsync("list_comments", new Dictionary<string, object?>())));
        var version = listed.GetProperty("comments").EnumerateArray().Single(c => c.GetProperty("id").GetString() == waiting.Id).GetProperty("version").GetString();
        var refused = await drafts.CallToolAsync("moderate_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = waiting.Id, ["decision"] = "approve", ["version"] = version });

        // Approving takes the version read (F-0209): without it, or with an old one, nothing happens.
        var noVersion = await publisher.CallToolAsync("moderate_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = waiting.Id, ["decision"] = "approve" });
        var stale = await publisher.CallToolAsync("moderate_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = waiting.Id, ["decision"] = "approve", ["version"] = "0123456789abcdef" });
        var approved = await publisher.CallToolAsync("moderate_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = waiting.Id, ["decision"] = "approve", ["version"] = version });
        var current = JsonElement.Parse(Text(await publisher.CallToolAsync("list_comments", new Dictionary<string, object?> { ["status"] = "approved" }))).GetProperty("comments")[0].GetProperty("version").GetString();
        var again = await publisher.CallToolAsync("moderate_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = waiting.Id, ["decision"] = "approve", ["version"] = current });
        var wrongDecision = await publisher.CallToolAsync("moderate_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = other.Id, ["decision"] = "maybe" });
        var deleted = await publisher.CallToolAsync("delete_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = other.Id });
        var missing = await publisher.CallToolAsync("delete_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = other.Id });
        var published = JsonElement.Parse(Text(await publisher.CallToolAsync("list_comments", new Dictionary<string, object?> { ["status"] = "approved" })));

        await Assert.That(listed.GetProperty("total").GetInt32()).IsEqualTo(2);
        await Assert.That(listed.GetProperty("comments")[0].GetProperty("postTitle").GetString()).IsEqualTo("Hello, world");
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(Text(refused)).Contains("needs an app allowed to publish");
        await Assert.That(version).IsNotNull().And.IsNotEmpty();
        await Assert.That(noVersion.IsError).IsTrue();
        await Assert.That(Text(noVersion)).Contains("pass the comment's version");
        await Assert.That(stale.IsError).IsTrue();
        await Assert.That(Text(stale)).Contains("changed since you read it");
        await Assert.That(approved.IsError).IsNotEqualTo(true);
        await Assert.That(again.IsError).IsTrue();
        await Assert.That(Text(again)).Contains("Nothing waits");
        await Assert.That(wrongDecision.IsError).IsTrue();
        await Assert.That(deleted.IsError).IsNotEqualTo(true);
        await Assert.That(missing.IsError).IsTrue();
        await Assert.That(published.GetProperty("comments").EnumerateArray().Select(c => c.GetProperty("text").GetString() ?? "")).IsEquivalentTo(["Nice post"]);
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "comment-approved" && e.Target!.Contains("(via ", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task An_author_moderates_only_comments_on_their_own_posts()
    {
        await using var site = new OAuthTests.OAuthSite(role: EditorRole.Author);
        var store = site.Services.GetRequiredService<Weesite.Web.Visitors.ICommentStore>();
        var now = DateTimeOffset.UtcNow;
        var comment = new Weesite.Web.Visitors.Comment(Weesite.Web.Visitors.Comment.NewId(now), "posts/hello-world.md", "Ada", "Nice post", null, Weesite.Web.Visitors.CommentStatus.Pending, now, null, null, "x");
        await store.SaveAsync(comment, CancellationToken.None);
        await using var mcp = await ConnectAsync(site, EditorRole.Author, publish: true);

        var listed = JsonElement.Parse(Text(await mcp.CallToolAsync("list_comments", new Dictionary<string, object?> { ["status"] = "all" })));
        var refused = await mcp.CallToolAsync("moderate_comment", new Dictionary<string, object?> { ["post"] = "posts/hello-world.md", ["id"] = comment.Id, ["decision"] = "approve", ["version"] = "0123456789abcdef" });

        await Assert.That(listed.GetProperty("total").GetInt32()).IsEqualTo(0);
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(Text(refused)).Contains("your own posts only");
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
        var plural = Items(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["kind"] = "Pages", ["status"] = "Published" }));

        // F-0115: a wrong value says so, rather than finding nothing.
        var wrongStatus = await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["status"] = "public" });
        var wrongKind = await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["kind"] = "notes" });

        await Assert.That(all.Any(i => i.GetProperty("path").GetString() == "posts/notes-for-a-future-post.md" && i.GetProperty("status").GetString() == "draft")).IsTrue();
        await Assert.That(all.Any(i => i.GetProperty("title").GetString() == "A post from the future" && i.GetProperty("status").GetString() == "scheduled" && i.GetProperty("publishAt").GetString() is not null)).IsTrue();
        await Assert.That(drafts.All(i => i.GetProperty("status").GetString() == "draft")).IsTrue();
        await Assert.That(pages.All(i => i.GetProperty("kind").GetString() == "page")).IsTrue();
        await Assert.That(pages.Any(i => i.GetProperty("slug").GetString() == "about")).IsTrue();
        await Assert.That(found.Select(i => i.GetProperty("slug").GetString())).Contains("notes-on-dialing-in-espresso");
        await Assert.That(plural.Select(i => i.GetProperty("slug").GetString())).Contains("about");
        await Assert.That(wrongStatus.IsError).IsTrue();
        await Assert.That(Text(wrongStatus)).Contains("status is draft, scheduled, published, archived or empty.");
        await Assert.That(wrongKind.IsError).IsTrue();
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
    public async Task Replace_media_is_for_admins()
    {
        // F-0143: refused before anything is stored.
        await using var site = new OAuthTests.OAuthSite(role: EditorRole.Author);
        await using var mcp = await ConnectAsync(site, EditorRole.Author);

        var result = await mcp.CallToolAsync("replace_media", await ReplaceSampleAsync());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(Text(result)).Contains("Only admins can replace files");
    }

    [Test]
    public async Task Replace_media_reaches_published_posts_only_with_edits_allowed()
    {
        // F-0143: the sample image is in a published post; refused before anything is stored.
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var result = await mcp.CallToolAsync("replace_media", await ReplaceSampleAsync());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(Text(result)).Contains("This app may write drafts only");
    }

    private static async Task<Dictionary<string, object?>> ReplaceSampleAsync() => new()
    {
        ["name"] = "b368a21f9e93-sample.png",
        ["fileName"] = "square.png",
        ["contentBase64"] = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"))),
    };

    [Test]
    public async Task Markdown_guide_says_what_the_site_supports()
    {
        // F-0136: the extensions, the file name in a fence and the languages the site highlights.
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var guide = Text(await mcp.CallToolAsync("markdown_guide", new Dictionary<string, object?>()));

        await Assert.That(guide).StartsWith("# Markdown for posts and pages\n\nThe Markdown Example Site renders");
        await Assert.That(guide).Contains("Not supported");
        await Assert.That(guide).Contains("image_alt");
        await Assert.That(guide).Contains("filename=\"index.js\"");
        await Assert.That(guide).Contains("Footnotes");
        await Assert.That(guide).Contains("csharp");
        await Assert.That(mcp.ServerInstructions).Contains("markdown_guide");

        // F-0146: the languages under their own heading, after "Not supported", not inside it.
        await Assert.That(guide.IndexOf("## Code languages", StringComparison.Ordinal)).IsGreaterThan(guide.IndexOf("## Not supported", StringComparison.Ordinal));
        await Assert.That(guide[guide.IndexOf("## Code languages", StringComparison.Ordinal)..]).Contains("csharp");
        await Assert.That(guide[guide.IndexOf("## Not supported", StringComparison.Ordinal)..guide.IndexOf("## Code languages", StringComparison.Ordinal)]).DoesNotContain("csharp");
    }

    [Test]
    public async Task Preview_item_renders_as_the_site_does_and_says_what_it_would_warn_about()
    {
        // F-0123, F-0125.
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var stored = JsonElement.Parse(Text(await mcp.CallToolAsync("preview_item", new Dictionary<string, object?> { ["path"] = "posts/notes-for-a-future-post.md" })));
        var draft = JsonElement.Parse(Text(await mcp.CallToolAsync("preview_item", new Dictionary<string, object?>
        {
            ["markdown"] = Post("Not saved", "not-saved", "summary: " + new string('s', 170) + "\n", "# A heading\n\n![](/media/b368a21f9e93-sample.png)"),
        })));
        var nothing = await mcp.CallToolAsync("preview_item", new Dictionary<string, object?>());
        var versions = await mcp.CallToolAsync("list_versions", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md" });
        var long_ = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = Post("Long summary", "long-summary", "summary: " + new string('s', 170) + "\n") });

        await Assert.That(stored.GetProperty("previewUrl").GetString()).Contains("/admin/preview/");
        await Assert.That(draft.GetProperty("html").GetString()).Contains("<h2");
        await Assert.That(draft.TryGetProperty("previewUrl", out var unsaved) && unsaved.ValueKind != JsonValueKind.Null).IsFalse();
        await Assert.That(draft.GetProperty("problems").EnumerateArray().Select(p => p.GetString())).Contains(p => p!.Contains("alt text", StringComparison.Ordinal));
        await Assert.That(draft.GetProperty("problems").EnumerateArray().Select(p => p.GetString())).Contains(p => p!.Contains("170 characters", StringComparison.Ordinal));
        await Assert.That(nothing.IsError).IsTrue();
        await Assert.That(versions.IsError).IsNotEqualTo(true);
        await Assert.That(long_.IsError).IsTrue();
        await Assert.That(Text(long_)).Contains("The summary can be at most 160 characters");
    }

    [Test]
    public async Task Preview_item_checks_the_front_matter_image_and_names_lines_in_the_file()
    {
        // F-0146.
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var missing = Problems(await mcp.CallToolAsync("preview_item", new Dictionary<string, object?>
        {
            ["markdown"] = Post("Lead", "lead", "image: /media/abcdef012345-gone.png\n", "Text.\n\n![](/media/b368a21f9e93-sample.png)"),
        }));
        var elsewhere = Problems(await mcp.CallToolAsync("preview_item", new Dictionary<string, object?>
        {
            ["markdown"] = Post("Lead", "lead", "image: https://example.com/x.png\nimage_alt: A square\n"),
        }));

        await Assert.That(missing).Contains("The front matter image /media/abcdef012345-gone.png isn't in Media.");
        await Assert.That(missing).Contains("The front matter image has no image_alt: link previews describe it with the title.");

        // Front matter: lines 1–6, an empty line, the text from line 8.
        await Assert.That(missing).Contains("image /media/b368a21f9e93-sample.png has no alt text (line 10)");
        await Assert.That(elsewhere).IsEquivalentTo(["The front matter image https://example.com/x.png isn't a /media/ address: link previews show the site's generated card instead."]);
    }

    [Test]
    public async Task A_refused_save_names_the_line_in_the_file_the_app_sent()
    {
        // T-0122: the save counted lines in the file the server writes, not the one the app sent.
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var result = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
        {
            ["markdown"] = "---\n# A comment the server drops\ntitle: No alt\nslug: no-alt\ndate: 2026-11-12\n---\n\nText.\n\n![](/media/b368a21f9e93-sample.png)\n",
        });

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(Text(result)).Contains("has no alt text (line 10)");
    }

    private static List<string> Problems(CallToolResult result) =>
        [.. JsonElement.Parse(Text(result)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()!)];

    [Test]
    public async Task The_lists_come_in_pages_that_say_how_many_there_are()
    {
        // F-0163: a list cut off at the limit looked complete.
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var all = JsonElement.Parse(Text(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["limit"] = 200 })));
        var first = JsonElement.Parse(Text(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["limit"] = 2 })));
        var second = JsonElement.Parse(Text(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["limit"] = 2, ["offset"] = first.GetProperty("nextOffset").GetInt32() })));
        var total = all.GetProperty("total").GetInt32();
        var last = JsonElement.Parse(Text(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["limit"] = 2, ["offset"] = total - 1 })));
        var media = JsonElement.Parse(Text(await mcp.CallToolAsync("list_media", new Dictionary<string, object?> { ["limit"] = 1 })));

        string?[] Paths(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString())];
        await Assert.That(total).IsGreaterThan(4).And.IsEqualTo(all.GetProperty("items").GetArrayLength());
        await Assert.That(all.TryGetProperty("nextOffset", out _)).IsFalse();
        await Assert.That(first.GetProperty("total").GetInt32()).IsEqualTo(total);
        await Assert.That(first.GetProperty("nextOffset").GetInt32()).IsEqualTo(2);
        await Assert.That(second.GetProperty("nextOffset").GetInt32()).IsEqualTo(4);
        await Assert.That(Paths(first).Concat(Paths(second))).IsEquivalentTo(Paths(all).Take(4));
        await Assert.That(Paths(last)).IsEquivalentTo(Paths(all).TakeLast(1));
        await Assert.That(last.TryGetProperty("nextOffset", out _)).IsFalse();
        await Assert.That(media.GetProperty("files").GetArrayLength()).IsEqualTo(1);
        await Assert.That(media.TryGetProperty("nextOffset", out _)).IsEqualTo(media.GetProperty("total").GetInt32() > 1);
    }

    [Test]
    public async Task List_media_gives_each_file_its_markdown()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var files = JsonElement.Parse(Text(await mcp.CallToolAsync("list_media", new Dictionary<string, object?> { ["query"] = "sample" }))).GetProperty("files");

        await Assert.That(files.EnumerateArray().Any(f => f.GetProperty("path").GetString() == "/media/b368a21f9e93-sample.png" && f.GetProperty("isImage").GetBoolean())).IsTrue();
        await Assert.That(files.EnumerateArray().All(f => f.GetProperty("markdown").GetString()!.Contains("/media/", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task List_tags_gives_the_tags_in_use_most_used_first_with_their_details()
    {
        // T-0140: so an app reuses the site's tags instead of inventing near-duplicates.
        // Another author's draft: the signed-in author mustn't learn its tag.
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "unannounced.md"), Post("Unannounced", "unannounced", "status: draft\nauthor: example-author\ntags: [secret-project]\n"));
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var authorSite = new OAuthTests.OAuthSite(role: EditorRole.Author, contentPath: copy.Root);
        await using var admin = await ConnectAsync(site);
        await using var author = await ConnectAsync(authorSite, EditorRole.Author);

        var tags = Tags(await admin.CallToolAsync("list_tags", new Dictionary<string, object?>()));
        var authorsTags = Tags(await author.CallToolAsync("list_tags", new Dictionary<string, object?>()));

        var counts = tags.Select(t => t.GetProperty("posts").GetInt32() + t.GetProperty("notPublic").GetInt32()).ToList();
        await Assert.That(counts).IsOrderedByDescending(c => c);
        var dotnet = tags.Single(t => t.GetProperty("name").GetString() == "dotnet");
        await Assert.That(dotnet.GetProperty("posts").GetInt32()).IsGreaterThan(0);
        await Assert.That(dotnet.GetProperty("colour").GetString()).IsEqualTo("violet");
        await Assert.That(dotnet.GetProperty("description").GetString()).IsEqualTo(".NET and C#");
        var secret = tags.Single(t => t.GetProperty("name").GetString() == "secret-project");
        await Assert.That((secret.GetProperty("posts").GetInt32(), secret.GetProperty("notPublic").GetInt32())).IsEqualTo((0, 1));
        await Assert.That(authorsTags.Select(t => t.GetProperty("name").GetString())).DoesNotContain("secret-project");
        await Assert.That(authorsTags.Select(t => t.GetProperty("name").GetString())).Contains("dotnet");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task The_browser_and_an_app_share_the_editor_s_cached_role(bool browserFirst)
    {
        // T-0122: the cookie check and the bearer handler cached different types under one key, so
        // whichever came second within 30 s failed with a 500.
        await using var site = new OAuthTests.OAuthSite();
        var cookie = Cookie(site.Services);
        if (browserFirst)
        {
            await SignIn.RecheckRoleAsync(cookie);
        }

        await using var mcp = await ConnectAsync(site);
        var items = await mcp.CallToolAsync("list_items", new Dictionary<string, object?>());
        var again = Cookie(site.Services);
        await SignIn.RecheckRoleAsync(again);

        await Assert.That(items.IsError).IsNotEqualTo(true);
        await Assert.That(cookie.Principal).IsNotNull();
        await Assert.That(again.Principal).IsNotNull();
    }

    private static Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext Cookie(IServiceProvider services)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services };
        var principal = EditorClaims.Principal("Google", "sub", "editor@example.com", "Editor", EditorRole.Admin, DateTimeOffset.UtcNow);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, EditorClaims.Scheme);
        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme(EditorClaims.Scheme, null, typeof(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler));
        return new(http, scheme, new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions(), ticket);
    }

    [Test]
    public async Task An_app_s_token_follows_its_editor_s_role_without_being_revoked()
    {
        // T-0122: the bearer handler looks the role up itself (cached for RoleRecheck), so a
        // demotion applies to an app at once, and an editor gone from the list loses it, even
        // where nothing revoked the grant.
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);
        var store = site.Services.GetRequiredService<IEditorStore>();
        var cache = site.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();

        await store.SetAsync(new Editor("editor@example.com", EditorRole.Author, "test", DateTimeOffset.UtcNow), CancellationToken.None);
        cache.Remove(SignIn.RoleCacheKey("editor@example.com"));
        var demoted = await mcp.CallToolAsync("replace_media", await ReplaceSampleAsync());

        await store.RemoveAsync("editor@example.com", CancellationToken.None);
        cache.Remove(SignIn.RoleCacheKey("editor@example.com"));
        var removed = await Assert.ThrowsAsync<HttpRequestException>(async () => await mcp.CallToolAsync("list_items", new Dictionary<string, object?>()));

        await Assert.That(demoted.IsError).IsTrue();
        await Assert.That(Text(demoted)).Contains("Only admins can replace files");
        await Assert.That(removed!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
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

        // F-0118: the name promised a removed editor too; that goes through the editors page's path.
        var second = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId)).GetProperty("access_token").GetString()!;
        using var other = site.Client(role: null);
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second);
        using var beforeRemoval = await PostAsync(other);
        var admin = EditorClaims.Principal("test", "boss", "boss@example.com", "Boss", EditorRole.Admin);
        var removed = await site.Services.GetRequiredService<EditorChanges>().RemoveAsync(admin, "editor@example.com", CancellationToken.None);
        using var afterRemoval = await PostAsync(other);

        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(beforeRemoval.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(removed).IsTypeOf<EditorOutcome.Removed>();
        await Assert.That(afterRemoval.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Create_item_saves_a_draft_whatever_the_front_matter_says()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);

        var result = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = Post("A note from an agent", "a-note-from-an-agent", "status: published\n") });
        var saved = JsonElement.Parse(Text(result));

        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(saved.GetProperty("path").GetString()).IsEqualTo("posts/a-note-from-an-agent.md");
        await Assert.That(saved.GetProperty("status").GetString()).IsEqualTo("draft");
        await Assert.That(saved.GetProperty("previewUrl").GetString()).IsEqualTo("http://localhost/admin/preview/post/a-note-from-an-agent");
        var file = await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "a-note-from-an-agent.md"));
        await Assert.That(file).Contains("status: draft").And.Contains("author: editor-");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "mcp-item-created" && e.Target == "posts/a-note-from-an-agent.md (via Test app)")).IsTrue();
    }

    [Test]
    public async Task Writes_explain_what_is_wrong()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);

        var noTitle = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = "---\nslug: x\ndate: 2026-11-01\n---\n\nText." });
        var noDate = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = "---\ntitle: X\nslug: x\n---\n\nText." });
        var noAlt = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = Post("Pictures", "pictures", body: "![](/media/b368a21f9e93-sample.png)") });
        var taken = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = Post("Hello again", "hello-world") });

        await Assert.That(Text(noTitle)).Contains("The title is missing.");
        await Assert.That(Text(noDate)).Contains("A post needs a date.");
        await Assert.That(Text(noAlt)).Contains("needs a description (alt text)");
        await Assert.That(Text(taken)).Contains("The slug 'hello-world' is already used");
        await Assert.That(new[] { noTitle, noDate, noAlt, taken }.All(r => r.IsError == true)).IsTrue();
    }

    [Test]
    public async Task Update_item_saves_at_the_version_read_and_keeps_the_status()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);
        var read = JsonElement.Parse(Text(await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "posts/notes-for-a-future-post.md" })));
        var changed = read.GetProperty("markdown").GetString()!.Replace("This post is a draft.", "This post is still a draft.", StringComparison.Ordinal).Replace("status: draft", "status: published", StringComparison.Ordinal);

        var first = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = "posts/notes-for-a-future-post.md", ["version"] = read.GetProperty("version").GetString(), ["markdown"] = changed });
        var stale = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = "posts/notes-for-a-future-post.md", ["version"] = read.GetProperty("version").GetString(), ["markdown"] = changed.Replace("still a draft", "a draft again", StringComparison.Ordinal) });
        // F-0174: the same text again at the version saved writes nothing.
        var again = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = "posts/notes-for-a-future-post.md", ["version"] = JsonElement.Parse(Text(first)).GetProperty("version").GetString(), ["markdown"] = changed });
        var blank = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = "posts/notes-for-a-future-post.md", ["version"] = " ", ["markdown"] = changed });

        await Assert.That(first.IsError).IsNotEqualTo(true);
        await Assert.That(JsonElement.Parse(Text(first)).GetProperty("status").GetString()).IsEqualTo("draft");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "notes-for-a-future-post.md"))).Contains("This post is still a draft.");
        await Assert.That(stale.IsError).IsTrue();
        await Assert.That(Text(stale)).Contains("Someone saved this item since the version you have");
        await Assert.That(again.IsError).IsNotEqualTo(true);
        await Assert.That(JsonElement.Parse(Text(again)).GetProperty("unchanged").GetBoolean()).IsTrue();
        await Assert.That(JsonElement.Parse(Text(again)).GetProperty("version").GetString()).IsEqualTo(JsonElement.Parse(Text(first)).GetProperty("version").GetString());
        await Assert.That(JsonElement.Parse(Text(first)).GetProperty("unchanged").GetBoolean()).IsFalse();
        await Assert.That(blank.IsError).IsTrue();
        await Assert.That(Text(blank)).Contains("Give the version from get_item");
    }

    [Test]
    public async Task Set_status_publishes_schedules_and_archives()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site, publish: true);
        using var visitor = site.Client(role: null);
        var created = JsonElement.Parse(Text(await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = Post("Going public", "going-public") })));
        var path = created.GetProperty("path").GetString();

        var published = JsonElement.Parse(Text(await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = path, ["version"] = created.GetProperty("version").GetString(), ["status"] = "published" })));
        await Assert.That(published.GetProperty("status").GetString()).IsEqualTo("published");
        await Assert.That(published.GetProperty("url").GetString()).IsEqualTo("http://localhost/blog/going-public");
        using (var page = await visitor.GetAsync(new Uri("/blog/going-public", UriKind.Relative)))
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var noTime = await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = path, ["version"] = published.GetProperty("version").GetString(), ["status"] = "scheduled" });
        var past = await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = path, ["version"] = published.GetProperty("version").GetString(), ["status"] = "scheduled", ["publishAt"] = "2020-01-01T00:00:00Z" });
        await Assert.That(Text(noTime)).Contains("needs a publish time (publishAt)");
        await Assert.That(Text(past)).Contains("is in the past");

        var scheduled = JsonElement.Parse(Text(await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = path, ["version"] = published.GetProperty("version").GetString(), ["status"] = "scheduled", ["publishAt"] = "2099-01-01T09:00:00Z" })));
        await Assert.That(scheduled.GetProperty("status").GetString()).IsEqualTo("scheduled");

        var archived = JsonElement.Parse(Text(await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = path, ["version"] = scheduled.GetProperty("version").GetString(), ["status"] = "archived" })));
        await Assert.That(archived.GetProperty("status").GetString()).IsEqualTo("archived");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Count(e => e.Action.StartsWith("mcp-status-", StringComparison.Ordinal))).IsEqualTo(3);
    }

    [Test]
    public async Task An_author_creates_their_own_posts_only()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(role: EditorRole.Author, contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site, EditorRole.Author);

        var page = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = "---\ntitle: Mine\nslug: mine\nkind: page\n---\n\nText." });
        var post = await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = Post("By an author", "by-an-author", "author: example-author\n") });
        var items = Items(await mcp.CallToolAsync("list_items", new Dictionary<string, object?> { ["limit"] = 200 }));
        var other = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md", ["version"] = "x", ["markdown"] = Post("Taken over", "hello-world") });

        await Assert.That(Text(page)).Contains("Authors can create posts only.");
        await Assert.That(post.IsError).IsNotEqualTo(true);
        // The front matter can't make someone else the author.
        await Assert.That(items.Select(i => i.GetProperty("slug").GetString()!)).IsEquivalentTo(["by-an-author"]);
        await Assert.That(Text(other)).Contains("No post or page you may edit");
    }

    [Test]
    public async Task Upload_media_stores_a_described_image()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);
        var png = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png")));

        var stored = await mcp.CallToolAsync("upload_media", new Dictionary<string, object?> { ["fileName"] = "square.png", ["contentBase64"] = png, ["alt"] = "A blue square" });
        var noAlt = await mcp.CallToolAsync("upload_media", new Dictionary<string, object?> { ["fileName"] = "square.png", ["contentBase64"] = png });
        var garbage = await mcp.CallToolAsync("upload_media", new Dictionary<string, object?> { ["fileName"] = "notes.txt", ["contentBase64"] = "not base64!" });

        var file = JsonElement.Parse(Text(stored));
        await Assert.That(stored.IsError).IsNotEqualTo(true);
        await Assert.That(file.GetProperty("markdown").GetString()).StartsWith("![A blue square](/media/").And.EndsWith("-square.png)");
        await Assert.That(Text(noAlt)).Contains("Describe the image");
        await Assert.That(Text(garbage)).Contains("isn't valid base64");
    }

    [Test]
    public async Task Upload_media_takes_at_most_10_MB()
    {
        // F-0066: base64 in JSON holds a file several times over; larger files go through the media page.
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);
        var big = Convert.ToBase64String(new byte[(10 * 1024 * 1024) + 1]);

        var refused = await mcp.CallToolAsync("upload_media", new Dictionary<string, object?> { ["fileName"] = "big.zip", ["contentBase64"] = big });
        var empty = await mcp.CallToolAsync("upload_media", new Dictionary<string, object?> { ["fileName"] = "empty.txt", ["contentBase64"] = "" });
        var emptyReplacement = await mcp.CallToolAsync("replace_media", new Dictionary<string, object?> { ["name"] = "000000000000-nothing.png", ["fileName"] = "empty.png", ["contentBase64"] = "" });

        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(Text(refused)).Contains("larger than 10 MB. Upload it in /admin");
        await Assert.That(Text(empty)).Contains("The file is empty.");
        await Assert.That(Text(emptyReplacement)).Contains("The file is empty.");
        await Assert.That(Directory.Exists(Path.Combine(copy.Root, "media")) ? Directory.GetFiles(Path.Combine(copy.Root, "media"), "*big*") : []).IsEmpty();
    }

    [Test]
    public async Task An_app_allowed_drafts_only_cannot_change_what_visitors_see()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site);
        var draft = JsonElement.Parse(Text(await mcp.CallToolAsync("create_item", new Dictionary<string, object?> { ["markdown"] = Post("Still a draft", "still-a-draft") })));
        var published = JsonElement.Parse(Text(await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md" })));

        var edited = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = draft.GetProperty("path").GetString(), ["version"] = draft.GetProperty("version").GetString(), ["markdown"] = Post("Still a draft", "still-a-draft", body: "More text.") });
        var live = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md", ["version"] = published.GetProperty("version").GetString(), ["markdown"] = published.GetProperty("markdown").GetString() + "\nAn addition." });
        var publish = await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = draft.GetProperty("path").GetString(), ["version"] = draft.GetProperty("version").GetString(), ["status"] = "published" });

        await Assert.That(edited.IsError).IsNotEqualTo(true);
        await Assert.That(live.IsError).IsTrue();
        await Assert.That(Text(live)).Contains("This app may write drafts only");
        await Assert.That(publish.IsError).IsTrue();
        await Assert.That(Text(publish)).Contains("This app may write drafts only");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "oauth-grant" && e.Target == "Test app (drafts only)")).IsTrue();
    }

    [Test]
    public async Task An_app_allowed_edits_changes_public_items_but_not_their_status()
    {
        using var copy = new SiteCopy();
        await using var site = new OAuthTests.OAuthSite(contentPath: copy.Root);
        await using var mcp = await ConnectAsync(site, choice: "edit");
        var published = JsonElement.Parse(Text(await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md" })));

        var live = await mcp.CallToolAsync("update_item", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md", ["version"] = published.GetProperty("version").GetString(), ["markdown"] = published.GetProperty("markdown").GetString() + "\nAn addition." });
        var saved = JsonElement.Parse(Text(live));
        var unpublish = await mcp.CallToolAsync("set_status", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md", ["version"] = saved.GetProperty("version").GetString(), ["status"] = "draft" });

        await Assert.That(live.IsError).IsNotEqualTo(true);
        await Assert.That(saved.GetProperty("status").GetString()).IsEqualTo("published");
        await Assert.That(unpublish.IsError).IsTrue();
        await Assert.That(Text(unpublish)).Contains("This app may edit but not publish");
        var reread = JsonElement.Parse(Text(await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["path"] = "posts/hello-world.md" })));
        await Assert.That(reread.GetProperty("markdown").GetString()).Contains("An addition.");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "oauth-grant" && e.Target == "Test app (drafts and edits)")).IsTrue();
    }

    [Test]
    public async Task The_endpoints_introduce_themselves_with_the_site_name()
    {
        await using var site = new OAuthTests.OAuthSite();
        await using var mcp = await ConnectAsync(site);

        var info = mcp.ServerInfo!;
        await Assert.That(info.Title).IsEqualTo("Example Site (editor)");
        await Assert.That(info.Name).IsEqualTo("weesite-editor");
        await Assert.That(info.WebsiteUrl).IsEqualTo("http://localhost");
        await Assert.That(mcp.ServerInstructions).Contains("You work on Example Site");
    }

    [Test]
    public async Task The_editor_instructions_say_whom_the_app_works_for_and_what_it_may_do()
    {
        // F-0161: a client connected as an author took the refusals for a wrong role.
        await using var adminSite = new OAuthTests.OAuthSite();
        await using var authorSite = new OAuthTests.OAuthSite(role: EditorRole.Author);
        await using var admin = await ConnectAsync(adminSite, EditorRole.Admin, publish: true);
        await using var author = await ConnectAsync(authorSite, EditorRole.Author);
        await using var editing = await ConnectAsync(authorSite, EditorRole.Author, choice: "edit");

        await Assert.That(admin.ServerInstructions).Contains("They're an admin: every post and page, and the media.")
            .And.Contains("They let this app write drafts, edit and publish.");
        await Assert.That(author.ServerInstructions).Contains("They're an author: their own posts only, not pages or other authors' posts")
            .And.Contains("Admin and author are the only roles")
            .And.Contains("They let this app write drafts only")
            .And.DoesNotContain("editor who");
        await Assert.That(editing.ServerInstructions).Contains("write drafts and edit, but not publish");
    }

    private static string Post(string title, string slug, string extra = "", string body = "Some text.") =>
        $"---\ntitle: {title}\nslug: {slug}\ndate: 2026-11-12\n{extra}---\n\n{body}\n";

    // A list comes as a JSON array, or wrapped as { "result": [...] } in structured form.
    private static List<JsonElement> Tags(CallToolResult result)
    {
        var json = JsonElement.Parse(Text(result));
        return [.. (json.ValueKind == JsonValueKind.Object ? json.GetProperty("result") : json).EnumerateArray()];
    }

    private static List<JsonElement> Items(CallToolResult result) =>
        [.. JsonElement.Parse(Text(result)).GetProperty("items").EnumerateArray()];

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
    private static async Task<McpClient> ConnectAsync(OAuthTests.OAuthSite site, EditorRole role = EditorRole.Admin, bool publish = false, string? choice = null)
    {
        using var browser = site.Client(role);
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback");
        var access = (await OAuthTests.OAuthSite.GrantAsync(browser, clientId, publish, choice)).GetProperty("access_token").GetString();
        var http = site.Client(role: null);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp/editor"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan });
    }
}
