using System.Globalization;
using Weesite.Content;

namespace Weesite.Web.Seo;

/// <summary>Paths and timestamps of content, shared by feeds, the sitemap and JSON-LD.</summary>
internal static class ContentUrls
{
    /// <summary>Gets an item's path: <c>/blog/{slug}</c> for posts, <c>/{slug}</c> for pages.</summary>
    /// <param name="item">The post or page.</param>
    /// <returns>The path.</returns>
    public static string PathOf(ContentItem item) =>
        item.Kind == ContentKind.Post ? $"/blog/{item.Slug}" : $"/{item.Slug}";

    /// <summary>Gets a tag page's path.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The path.</returns>
    public static string TagPath(string tag) => $"/blog/tags/{tag}";

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
    /// Makes the rendered HTML's site-relative links and image sources absolute, for feed readers
    /// that show it away from the site.
    /// </summary>
    /// <param name="html">The rendered HTML (from the Markdown renderer).</param>
    /// <param name="origin">The origin, without a trailing slash.</param>
    /// <returns>The HTML with absolute URLs.</returns>
    public static string AbsoluteHtml(string html, string origin) =>
        html
            .Replace("=\"/", $"=\"{origin}/", StringComparison.Ordinal)
            .Replace(", /media/", $", {origin}/media/", StringComparison.Ordinal);
}
