using System.Text.RegularExpressions;

namespace Weesite.Web;

/// <summary>
/// A post's or page's rendered HTML as its page shows it (U-19): the footnote links get names a
/// screen reader can say, "Footnote 1" and "Back to the text", in the site's language. The stored
/// HTML stays the same for every language, and feeds keep the plain marks.
/// </summary>
internal static partial class ArticleHtml
{
    /// <summary>Names the footnote links and their back links.</summary>
    /// <param name="html">The rendered HTML. Raw HTML is never passed through (ADR 0002), so only the renderer writes these links.</param>
    /// <param name="language">The site's language, for the words' <c>lang</c>: the text may be in another.</param>
    /// <param name="footnote">The name of a footnote link, with <c>{0}</c> for its number: "Footnote {0}".</param>
    /// <param name="back">The name of a back link: "Back to the text".</param>
    /// <returns>The HTML.</returns>
    public static string WithFootnoteNames(string html, string language, string footnote, string back)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(footnote);
        if (!html.Contains("class=\"footnote-", StringComparison.Ordinal))
        {
            return html;
        }

        var lang = Escape(language);
        var backName = $"<span aria-hidden=\"true\">&#8617;</span><span class=\"visually-hidden\" lang=\"{lang}\">{Escape(back)}</span>";
        html = FootnoteLink().Replace(html, m =>
            $"{m.Groups["open"].Value}<sup><span aria-hidden=\"true\">{m.Groups["n"].Value}</span><span class=\"visually-hidden\" lang=\"{lang}\">{Escape(string.Format(System.Globalization.CultureInfo.CurrentCulture, footnote, m.Groups["n"].Value))}</span></sup></a>");
        return BackLink().Replace(html, m => $"{m.Groups["open"].Value}{backName}</a>");
    }

    // Only the characters HTML needs escaped: WebUtility.HtmlEncode would also write letters such as
    // "á" as character references.
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    // Markdig's footnote reference: <a id="fnref:1" href="#fn:1" class="footnote-ref"><sup>1</sup></a>.
    [GeneratedRegex("(?<open><a id=\"fnref:[0-9]+\" href=\"#fn:[0-9]+\" class=\"footnote-ref\">)<sup>(?<n>[0-9]+)</sup></a>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FootnoteLink();

    // And the back link at the end of the note: <a href="#fnref:1" class="footnote-back-ref">&#8617;</a>.
    [GeneratedRegex("(?<open><a href=\"#fnref:[0-9]+\" class=\"footnote-back-ref\">)&#8617;</a>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BackLink();
}
