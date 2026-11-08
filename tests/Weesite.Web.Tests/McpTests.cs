using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>The MCP endpoint at /mcp, through the official client over Streamable HTTP.</summary>
internal sealed class McpTests
{
    [SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport and disposes it with itself.")]
    private static async Task<McpClient> ConnectAsync(SiteFactory factory)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
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
    public async Task Llms_txt_points_agents_at_the_mcp_server()
    {
        await using var factory = new SiteFactory();
        using var http = factory.CreateClient();

        var text = await http.GetStringAsync(new Uri("/llms.txt", UriKind.Relative));

        await Assert.That(text).Contains("- [MCP server](http://localhost/mcp)");
    }

    [Test]
    [Arguments("/blog/hello-world", "hello-world")]
    [Arguments("/about/", "about")]
    [Arguments("https://example.com/about.md?x=1", "about")]
    [Arguments("/blog/", null)]
    [Arguments("/a/b", null)]
    [Arguments("", null)]
    public async Task Find_accepts_paths_and_urls(string url, string? slug)
    {
        await using var factory = new SiteFactory();
        var catalog = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>();
        _ = factory.CreateClient();

        await Assert.That(ContentTools.Find(catalog.Current, url)?.Slug).IsEqualTo(slug);
    }
}
