using System.Text;
using Weesite.Content;
using Weesite.Web.Seo;

namespace Weesite.Web.Agents;

/// <summary>
/// Plain-text views of the site for agents: one item as Markdown, the <c>llms.txt</c> index and
/// <c>llms-full.txt</c> with everything (<see href="https://llmstxt.org/">llmstxt.org</see>).
/// Site-relative links are made absolute, so the text stands on its own.
/// </summary>
internal static class AgentText
{
    /// <summary>Gets the path of an item's Markdown version.</summary>
    /// <param name="item">The post or page.</param>
    /// <returns>Such as <c>/blog/hello-world.md</c>.</returns>
    public static string MarkdownPath(ContentItem item) => ContentUrls.PathOf(item) + ".md";

    /// <summary>Renders an item as a Markdown document: front matter, title, body.</summary>
    /// <param name="item">The post or page.</param>
    /// <param name="site">The site settings.</param>
    /// <param name="origin">The site's origin.</param>
    /// <returns>The document.</returns>
    public static string Markdown(ContentItem item, SiteSettings site, string origin)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(site);
        var text = new StringBuilder();
        text.Append("---\n");
        Field(text, "title", item.Title);
        Field(text, "url", origin + ContentUrls.PathOf(item));
        if (item.Date is { } date)
        {
            Field(text, "date", ContentDates.Iso(date));
        }

        if (item.Updated is { } updated)
        {
            Field(text, "updated", ContentDates.Iso(updated));
        }

        if (site.FindAuthor(item.AuthorId) is { } author)
        {
            Field(text, "author", author.Name);
        }

        if (item.Tags.Count > 0)
        {
            text.Append("tags: [").AppendJoin(", ", item.Tags).Append("]\n");
        }

        if (item.Summary is { } summary)
        {
            Field(text, "summary", summary);
        }

        Field(text, "language", item.Language);
        text.Append("---\n\n# ").Append(item.Title).Append("\n\n");
        text.Append(AbsoluteLinks(item.Markdown.Trim(), origin)).Append('\n');
        return text.ToString();
    }

    /// <summary>Renders <c>llms.txt</c>: the site, then its posts and pages, linked to their Markdown.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="origin">The site's origin.</param>
    /// <returns>The index.</returns>
    public static string Index(ContentCatalog catalog, string origin)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var site = catalog.Site;
        var text = new StringBuilder();
        text.Append("# ").Append(site.Name).Append("\n\n");
        if (site.Tagline is { } tagline)
        {
            text.Append("> ").Append(tagline).Append("\n\n");
        }

        if (site.Authors.Count > 0)
        {
            text.Append("By ").AppendJoin(", ", site.Authors.Select(a => a.Name)).Append(". ");
        }

        text.Append("Every post and page is also available as Markdown: add `.md` to its URL, or ask for `text/markdown` in the `Accept` header.\n");

        Section(text, "Posts", catalog.Posts, origin);
        Section(text, "Pages", catalog.Pages, origin);

        text.Append("\n## Optional\n\n");
        text.Append("- [Everything in one file](").Append(origin).Append("/llms-full.txt): every post and page as Markdown\n");
        text.Append("- [Atom feed](").Append(origin).Append("/feed.xml): the newest posts\n");
        text.Append("- [JSON Feed](").Append(origin).Append("/feed.json): the newest posts\n");
        text.Append("- [Sitemap](").Append(origin).Append("/sitemap.xml): every URL\n");
        return text.ToString();
    }

    /// <summary>Renders <c>llms-full.txt</c>: every post (newest first), then every page, as Markdown.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="origin">The site's origin.</param>
    /// <returns>The text.</returns>
    public static string Full(ContentCatalog catalog, string origin)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var text = new StringBuilder();
        text.Append("# ").Append(catalog.Site.Name).Append("\n\n");
        if (catalog.Site.Tagline is { } tagline)
        {
            text.Append("> ").Append(tagline).Append("\n\n");
        }

        foreach (var item in catalog.Posts.Concat(catalog.Pages))
        {
            text.Append('\n').Append(Markdown(item, catalog.Site, origin));
        }

        return text.ToString();
    }

    /// <summary>Makes site-relative Markdown links and images absolute.</summary>
    /// <param name="markdown">The Markdown.</param>
    /// <param name="origin">The site's origin.</param>
    /// <returns>The Markdown with absolute links.</returns>
    public static string AbsoluteLinks(string markdown, string origin) =>
        markdown
            .Replace("](/", $"]({origin}/", StringComparison.Ordinal)
            .Replace("]: /", $"]: {origin}/", StringComparison.Ordinal);

    private static void Section(StringBuilder text, string title, IReadOnlyList<ContentItem> items, string origin)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.Append("\n## ").Append(title).Append("\n\n");
        foreach (var item in items)
        {
            text.Append("- [").Append(item.Title).Append("](").Append(origin).Append(MarkdownPath(item)).Append(')');
            if (item.Summary is { } summary)
            {
                text.Append(": ").Append(summary);
            }

            text.Append('\n');
        }
    }

    // A YAML scalar, double-quoted when it could be misread.
    private static void Field(StringBuilder text, string name, string value)
    {
        var plain = value.Length > 0
            && value.IndexOfAny([':', '#', '"', '\'', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '%', '@', '`', '\n']) < 0
            && value.Trim() == value;
        text.Append(name).Append(": ");
        if (plain)
        {
            text.Append(value);
        }
        else
        {
            text.Append('"').Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)).Append('"');
        }

        text.Append('\n');
    }
}
