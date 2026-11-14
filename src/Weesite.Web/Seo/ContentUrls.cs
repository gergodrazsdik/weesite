using System.Globalization;
using Weesite.Content;

namespace Weesite.Web.Seo;

/// <summary>Paths and timestamps of content, shared by feeds, the sitemap and JSON-LD.</summary>
internal static partial class ContentUrls
{
    /// <summary>
    /// Gets an item's path: <c>/blog/{slug}</c> for posts, <c>/{slug}</c> for pages. A slug in another
    /// script is percent-encoded (F-0091 L1), so feeds, the sitemap and absolute links are plain
    /// ASCII; the routes decode it again.
    /// </summary>
    /// <param name="item">The post or page.</param>
    /// <returns>The path.</returns>
    public static string PathOf(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind == ContentKind.Post ? PostPath(item.Slug) : PagePath(item.Slug);
    }

    /// <summary>Gets a post's path from its slug, percent-encoded as <see cref="PathOf"/>.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>Such as <c>/blog/hello-world</c>.</returns>
    public static string PostPath(string slug) => $"/blog/{Uri.EscapeDataString(slug)}";

    /// <summary>Gets a page's path from its slug, percent-encoded as <see cref="PathOf"/>.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>Such as <c>/about</c>.</returns>
    public static string PagePath(string slug) => $"/{Uri.EscapeDataString(slug)}";

    /// <summary>Gets a tag page's path, percent-encoded as <see cref="PathOf"/>.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The path.</returns>
    public static string TagPath(string tag) => $"/blog/tags/{Uri.EscapeDataString(tag)}";

    /// <summary>Gets a date as an RFC 3339 timestamp at midnight UTC.</summary>
    /// <param name="date">The date.</param>
    /// <returns>Such as <c>2026-01-15T00:00:00Z</c>.</returns>
    public static string Timestamp(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";

    /// <summary>Gets when an item last changed: its update date, else its publication date.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The date, or null for pages without dates.</returns>
    public static DateOnly? LastChanged(ContentItem item) => item.Updated ?? item.Date;

    /// <summary>
    /// Makes the rendered HTML's links, image sources and srcsets absolute, for feed readers that
    /// show it away from the site: each resolved against the item's own address, as a browser on
    /// it would, so relative (<c>notes</c>, <c>../x</c>, <c>#part</c>) and protocol-relative
    /// (<c>//host/x</c>) ones come out right (F-0116).
    /// </summary>
    /// <param name="html">The rendered HTML (from the Markdown renderer).</param>
    /// <param name="address">The item's absolute address.</param>
    /// <returns>The HTML with absolute URLs.</returns>
    public static string AbsoluteHtml(string html, string address)
    {
        var baseUri = new Uri(address, UriKind.Absolute);
        string Resolve(string url) =>
            Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(url), out var resolved)
                ? System.Net.WebUtility.HtmlEncode(resolved.AbsoluteUri)
                : url;

        html = UrlAttribute().Replace(html, m => m.Groups["before"].Value + Resolve(m.Groups["url"].Value) + "\"");
        return SrcsetAttribute().Replace(html, m => m.Groups["before"].Value + string.Join(", ", m.Groups["set"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(candidate => candidate.Split(' ', 2) is [var url, .. var rest] ? string.Join(' ', [Resolve(url), .. rest]) : candidate)) + "\"");
    }

    [System.Text.RegularExpressions.GeneratedRegex("(?<before>\\s(?:href|src)=\")(?<url>[^\"]*)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex UrlAttribute();

    [System.Text.RegularExpressions.GeneratedRegex("(?<before>\\ssrcset=\")(?<set>[^\"]*)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex SrcsetAttribute();
}
