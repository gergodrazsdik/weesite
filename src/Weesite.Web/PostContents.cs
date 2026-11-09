using System.Net;
using System.Text.RegularExpressions;

namespace Weesite.Web;

/// <summary>A post's contents for "On this page" (ADR 0006): its second-level headings.</summary>
internal static partial class PostContents
{
    /// <summary>Gets the second-level headings of rendered HTML, with their IDs.</summary>
    /// <param name="html">The post's HTML.</param>
    /// <returns>Each heading's ID and text.</returns>
    public static IReadOnlyList<(string Id, string Text)> Headings(string html) =>
        [.. Heading().Matches(html).Select(m => (m.Groups[1].Value, WebUtility.HtmlDecode(Tag().Replace(m.Groups[2].Value, "")).Trim()))];

    [GeneratedRegex("<h2 id=\"([^\"]+)\">(.*?)</h2>", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Heading();

    [GeneratedRegex("<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();
}
