using System.ComponentModel;
using System.Net;
using ModelContextProtocol.Server;
using Weesite.Content;
using Weesite.Search;
using Weesite.Web.Seo;

namespace Weesite.Web.Agents;

/// <summary>A post or page as an MCP tool returns it.</summary>
/// <param name="Title">The title.</param>
/// <param name="Url">The absolute URL.</param>
/// <param name="Markdown">The URL of its Markdown version.</param>
/// <param name="Kind"><c>post</c> or <c>page</c>.</param>
/// <param name="Date">The publication date (posts), <c>yyyy-MM-dd</c>.</param>
/// <param name="Summary">The summary, if any.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Section">For search results: the heading of the section that matched best.</param>
/// <param name="Snippet">For search results: an excerpt of that section.</param>
internal sealed record ContentEntry(
    string Title,
    string Url,
    string Markdown,
    string Kind,
    string? Date,
    string? Summary,
    IReadOnlyList<string> Tags,
    string? Section = null,
    string? Snippet = null);

/// <summary>
/// The site's MCP tools (brief, SEO, feeds and agent support): read-only access to the public
/// content, served stateless at <c>/mcp</c> over Streamable HTTP.
/// </summary>
[McpServerToolType]
internal sealed class ContentTools(ContentCatalogProvider content, HybridSearch search, SiteUrls urls, IHttpContextAccessor http)
{
    private const int MaxLimit = 50;

    /// <summary>Searches posts and pages by keywords and meaning.</summary>
    /// <param name="query">What to look for, in plain words.</param>
    /// <param name="limit">The most results to return (1 to 50).</param>
    /// <returns>The results, best first.</returns>
    [McpServerTool(Name = "search_content", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Search the site's posts and pages by keywords and meaning. Returns titles, URLs, the best-matching section and a snippet, best first. Use get_content to read one in full.")]
    public IReadOnlyList<ContentEntry> SearchContent(
        [Description("What to look for, in plain words (at most 200 characters).")] string query,
        [Description("The most results to return, 1 to 50.")] int limit = 10)
    {
        var origin = Origin();
        return search.Search(query, Math.Clamp(limit, 1, MaxLimit)).Hits
            .Select(h => Entry(h.Item, origin) with { Section = h.Heading, Snippet = PlainSnippet(h.SnippetHtml) })
            .ToList();
    }

    /// <summary>Gets a post or page as Markdown.</summary>
    /// <param name="url">Its URL or path.</param>
    /// <returns>The Markdown, or a message saying it wasn't found.</returns>
    [McpServerTool(Name = "get_content", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get a post or page in full as Markdown, with front matter (title, URL, dates, author, tags, summary). Accepts its URL or path, such as /blog/some-post or /about.")]
    public string GetContent([Description("The post's or page's URL or path, as search_content or list_content return it.")] string url)
    {
        var catalog = content.Current;
        var item = Find(catalog, url);
        return item is null
            ? $"No post or page at '{url}'. Use search_content or list_content to find one."
            : AgentText.Markdown(item, catalog.Site, Origin());
    }

    /// <summary>Lists posts and pages.</summary>
    /// <param name="kind"><c>post</c>, <c>page</c>, or empty for both.</param>
    /// <param name="tag">Only posts with this tag.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <returns>Posts newest first, then pages.</returns>
    [McpServerTool(Name = "list_content", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the site's posts (newest first) and pages, with titles, URLs, dates, summaries and tags. Filter by kind or tag.")]
    public IReadOnlyList<ContentEntry> ListContent(
        [Description("\"post\", \"page\", or empty for both.")] string? kind = null,
        [Description("Only posts with this tag, such as \"web\".")] string? tag = null,
        [Description("The most entries to return, 1 to 50.")] int limit = 20)
    {
        var catalog = content.Current;
        var items = kind?.Trim().ToUpperInvariant() switch
        {
            "POST" or "POSTS" => catalog.Posts,
            "PAGE" or "PAGES" => catalog.Pages,
            _ => catalog.Posts.Concat(catalog.Pages),
        };
        if (!string.IsNullOrWhiteSpace(tag))
        {
            items = items.Where(i => i.Tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase));
        }

        var origin = Origin();
        return items.Take(Math.Clamp(limit, 1, MaxLimit)).Select(i => Entry(i, origin)).ToList();
    }

    /// <summary>Finds an item by its URL or path: <c>/blog/{slug}</c> or <c>/{slug}</c>, with or without <c>.md</c>.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="url">An absolute URL or a path.</param>
    /// <returns>The item, or null.</returns>
    internal static ContentItem? Find(ContentCatalog catalog, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var path = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute.AbsolutePath
            : url.Trim();
        // The other tools give percent-encoded URLs; slugs may be in any script (F-0113).
        path = Uri.UnescapeDataString(path.Split('?', '#')[0].TrimEnd('/'));
        if (path.EndsWith(".md", StringComparison.Ordinal))
        {
            path = path[..^3];
        }

        return path.StartsWith("/blog/", StringComparison.Ordinal)
            ? catalog.FindPost(path["/blog/".Length..])
            : path.StartsWith('/', StringComparison.Ordinal) && !path.AsSpan(1).Contains('/') ? catalog.FindPage(path[1..]) : null;
    }

    private static ContentEntry Entry(ContentItem item, string origin) => new(
        item.Title,
        origin + ContentUrls.PathOf(item),
        origin + AgentText.MarkdownPath(item),
        item.Kind == ContentKind.Post ? "post" : "page",
        item.Date is { } date ? ContentDates.Iso(date) : null,
        item.Summary,
        item.Tags);

    // Search snippets are HTML with <mark>; agents get plain text.
    private static string PlainSnippet(string html) =>
        WebUtility.HtmlDecode(html.Replace("<mark>", "", StringComparison.Ordinal).Replace("</mark>", "", StringComparison.Ordinal));

    private string Origin() =>
        http.HttpContext is { } context ? urls.Origin(context.Request) : "";
}
