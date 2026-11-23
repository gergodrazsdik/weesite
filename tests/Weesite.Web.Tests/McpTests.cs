using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>The MCP endpoint at /mcp, through the official client over Streamable HTTP.</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class McpTests(SharedSite shared)
{
    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself.")]
    private static async Task<McpClient> ConnectAsync(SiteFactory factory)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        // The discovery probe times out after 5 s by default; under a full parallel test run that
        // fired and surfaced as a 499 (F-0034). The initialization timeout (60 s) still applies.
        return await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan });
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Test]
    public async Task Lists_the_three_read_only_tools()
    {
        await using var factory = new SiteFactory();
        await using var client = await ConnectAsync(factory);

        var tools = await client.ListToolsAsync();

        await Assert.That(tools.Select(t => t.Name)).IsEquivalentTo(["search_content", "get_content", "list_content"]);
        await Assert.That(tools.All(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true)).IsTrue();
    }

    [Test]
    public async Task Search_content_returns_urls_and_plain_snippets()
    {
        await using var factory = new SiteFactory();
        await using var client = await ConnectAsync(factory);

        var result = await client.CallToolAsync("search_content", new Dictionary<string, object?> { ["query"] = "rendering" });
        var text = Text(result);

        await Assert.That(result.IsError).IsNotEqualTo(true);
        await Assert.That(text).Contains("\"url\":\"http://localhost/blog/notes-on-static-rendering\"");
        await Assert.That(text).Contains("\"markdown\":\"http://localhost/blog/notes-on-static-rendering.md\"");
        await Assert.That(text).DoesNotContain("<mark>");
    }

    [Test]
    [Arguments("/blog/hello-world")]
    [Arguments("http://localhost/blog/hello-world.md")]
    public async Task Get_content_returns_the_markdown(string url)
    {
        await using var factory = new SiteFactory();
        await using var client = await ConnectAsync(factory);

        var result = await client.CallToolAsync("get_content", new Dictionary<string, object?> { ["url"] = url });

        await Assert.That(Text(result)).Contains("---\ntitle: \"Hello, world\"");
        await Assert.That(Text(result)).Contains("\n# Hello, world\n");
    }

    [Test]
    public async Task Get_content_says_when_nothing_is_there()
    {
        await using var factory = new SiteFactory();
        await using var client = await ConnectAsync(factory);

        var result = await client.CallToolAsync("get_content", new Dictionary<string, object?> { ["url"] = "/blog/nope" });

        await Assert.That(Text(result)).StartsWith("No post or page at '/blog/nope'.");
    }

    [Test]
    public async Task List_content_filters_by_kind_and_tag()
    {
        await using var factory = new SiteFactory();
        await using var client = await ConnectAsync(factory);

        var pages = Text(await client.CallToolAsync("list_content", new Dictionary<string, object?> { ["kind"] = "page" }));
        var tagged = Text(await client.CallToolAsync("list_content", new Dictionary<string, object?> { ["tag"] = "welcome" }));

        await Assert.That(pages).Contains("\"url\":\"http://localhost/about\"");
        await Assert.That(pages).DoesNotContain("/blog/");
        await Assert.That(tagged).Contains("/blog/hello-world");
        await Assert.That(tagged).DoesNotContain("/blog/notes-on-static-rendering");
    }

    [Test]
    public async Task List_content_comes_in_pages_with_the_placed_special_pages_and_refuses_a_wrong_kind()
    {
        // F-0249: at most 50 entries, with no way past them and no word that there were more; an
        // unknown kind listed everything. F-0233: the privacy policy and terms were left out.
        await using var factory = new SiteFactory();
        await using var client = await ConnectAsync(factory);

        var first = JsonElement.Parse(Text(await client.CallToolAsync("list_content", new Dictionary<string, object?> { ["limit"] = 2 })));
        var total = first.GetProperty("total").GetInt32();
        var last = JsonElement.Parse(Text(await client.CallToolAsync("list_content", new Dictionary<string, object?> { ["limit"] = 2, ["offset"] = total - 1 })));
        var pages = JsonElement.Parse(Text(await client.CallToolAsync("list_content", new Dictionary<string, object?> { ["kind"] = "page", ["limit"] = 50 })));
        var wrong = await client.CallToolAsync("list_content", new Dictionary<string, object?> { ["kind"] = "article" });

        await Assert.That(first.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(total).IsGreaterThan(2);
        await Assert.That(first.GetProperty("nextOffset").GetInt32()).IsEqualTo(2);
        await Assert.That(last.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(last.TryGetProperty("nextOffset", out var next) && next.ValueKind != JsonValueKind.Null).IsFalse();
        await Assert.That(pages.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("url").GetString()))
            .Contains("http://localhost/privacy").And.Contains("http://localhost/terms").And.Contains("http://localhost/about");
        await Assert.That(pages.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("url").GetString() == "http://localhost/privacy").GetProperty("markdown").GetString())
            .IsEqualTo("http://localhost/privacy.md");
        await Assert.That(wrong.IsError).IsTrue();
        await Assert.That(Text(wrong)).Contains("kind is post, page or empty.");
    }

    [Test]
    public async Task Get_content_reads_a_placed_special_page()
    {
        // F-0233: "No post or page at '/privacy'".
        await using var factory = new SiteFactory();
        await using var client = await ConnectAsync(factory);

        var result = await client.CallToolAsync("get_content", new Dictionary<string, object?> { ["url"] = "http://localhost/privacy.md" });

        await Assert.That(Text(result)).Contains("\n# Privacy policy\n");
    }

    [Test]
    public async Task Llms_txt_points_agents_at_the_mcp_server()
    {
        var factory = shared.Factory;
        using var http = factory.CreateClient();

        var text = await http.GetStringAsync(new Uri("/llms.txt", UriKind.Relative));

        await Assert.That(text).Contains("- [MCP server](http://localhost/mcp)");
    }

    private static readonly string[] Slugs = ["hello-world", "első-bejegyzés"];

    [Test]
    [Arguments("/blog/hello-world", "hello-world")]
    [Arguments("/about/", "about")]
    [Arguments("https://example.com/about.md?x=1", "about")]
    [Arguments("/blog/", null)]
    [Arguments("/a/b", null)]
    [Arguments("", null)]
    [Arguments("https://example.com/blog/els%C5%91-bejegyz%C3%A9s", "első-bejegyzés")]
    [Arguments("/blog/első-bejegyzés.md", "első-bejegyzés")]
    public async Task Find_accepts_paths_and_urls(string url, string? slug)
    {
        // A catalog alone: no app needed. With a post in another script, as the other tools give
        // its URL, percent-encoded (F-0113).
        var catalog = Weesite.Content.ContentCatalog.Build(Weesite.Content.SiteSettings.Empty,
            Slugs.Select(s => Weesite.Content.ContentParser.Parse($"posts/{s}.md", $"---\ntitle: T\nslug: {s}\ndate: 2026-01-01\n---\nText.\n"))
                .Append(Weesite.Content.ContentParser.Parse("pages/about.md", "---\nkind: page\ntitle: About\nslug: about\n---\nText.\n")));

        await Assert.That(ContentTools.Find(catalog, url)?.Slug).IsEqualTo(slug);
    }

    [Test]
    [Arguments("/privacy", "Privacy")]
    [Arguments("https://example.com/privacy.md", "Privacy")]
    [Arguments("/", "Welcome")]
    [Arguments("https://example.com", "Welcome")]
    [Arguments("/index.md", "Welcome")]
    [Arguments("/terms", null)]
    [Arguments("/about", "About")]
    public async Task Find_reaches_the_special_pages_where_site_settings_place_them(string url, string? title)
    {
        // F-0233: special pages have no slug of their own; Site settings give them their address.
        // The terms page here is published but placed nowhere.
        var site = Weesite.Content.SiteSettings.Empty with
        {
            PrivacyPage = "pages/privacy.md",
            Home = new Weesite.Content.HomeSettings { Show = "page", Page = "pages/welcome.md" },
        };
        var catalog = Weesite.Content.ContentCatalog.Build(site,
            new[] { ("privacy", "Privacy"), ("welcome", "Welcome"), ("terms", "Terms") }
                .Select(p => Weesite.Content.ContentParser.Parse($"pages/{p.Item1}.md", $"---\nkind: page\ntitle: {p.Item2}\n---\nText.\n"))
                .Append(Weesite.Content.ContentParser.Parse("pages/about.md", "---\nkind: page\ntitle: About\nslug: about\n---\nText.\n")));

        await Assert.That(ContentTools.Find(catalog, url)?.Title).IsEqualTo(title);
    }
}
