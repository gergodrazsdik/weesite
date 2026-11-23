using System.Text;
using System.Text.RegularExpressions;

namespace Weesite.Web.Visitors;

/// <summary>Why a visitor's text was refused.</summary>
internal enum TextProblem
{
    /// <summary>Nothing: the text is fine.</summary>
    None,

    /// <summary>Empty, or only spaces.</summary>
    Empty,

    /// <summary>Longer than allowed.</summary>
    TooLong,

    /// <summary>HTML or BBCode links, images or scripts, which aren't rendered: spam mostly sends them.</summary>
    Markup,

    /// <summary>More links than allowed.</summary>
    TooManyLinks,
}

/// <summary>A link found in a visitor's text.</summary>
/// <param name="Index">Where it starts.</param>
/// <param name="Length">How long it is.</param>
/// <param name="Url">The address to link to: <c>https://</c> added to one starting with <c>www.</c>.</param>
internal sealed record TextLink(int Index, int Length, string Url);

/// <summary>
/// Visitors' text is plain text (ADR 0011): normalized, then checked. It's always shown escaped,
/// so markup is only a spam signal: HTML links, images and scripts (and any tag with attributes)
/// and BBCode links and images are refused, since people writing to a site rarely type them and
/// spam nearly always does. Everything else, such as <c>List&lt;T&gt;</c>, "press &lt;Enter&gt;",
/// <c>arr[i]</c>, <c>[code]</c> or <c>&lt;b&gt;</c>, is kept and shown as typed (F-0214), and so is
/// Markdown. Addresses count as links.
/// </summary>
internal static partial class VisitorText
{
    /// <summary>The most links a message or comment may have.</summary>
    public const int MaxLinks = 3;

    /// <summary>
    /// Normalizes multi-line text: Unicode NFC, line endings as <c>\n</c>, tabs as spaces, other
    /// control and format characters except joiners removed, trailing spaces of each line and blank
    /// lines and whitespace (any Unicode space) at either end trimmed, and at most two blank lines in a row.
    /// </summary>
    /// <param name="text">The text as sent.</param>
    /// <returns>The text to check and keep.</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var unified = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\t', ' ');
        var kept = new StringBuilder(unified.Length);
        foreach (var rune in unified.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (rune.Value == '\n' || !IsInvisible(rune))
            {
                kept.Append(rune);
            }
        }

        var lines = kept.ToString().Split('\n').Select(l => l.TrimEnd());
        var result = BlankLines().Replace(string.Join('\n', lines), "\n\n\n");
        // Every kind of space, so a name of no-break or ideographic spaces is empty (F-0216).
        return result.Trim();
    }

    /// <summary>Normalizes a one-line field, such as a name: as <see cref="Normalize"/>, with every run of spaces and line breaks one space.</summary>
    /// <param name="text">The text as sent.</param>
    /// <returns>The text to check and keep.</returns>
    public static string NormalizeLine(string? text) => Spaces().Replace(Normalize(text), " ");

    /// <summary>Checks normalized text.</summary>
    /// <param name="text">The normalized text.</param>
    /// <param name="maxLength">The most characters (UTF-16 code units, as a browser's <c>maxlength</c> counts).</param>
    /// <param name="maxLinks">The most links.</param>
    /// <param name="quotable">Links a quote may repeat without counting: those of the comments a
    /// reply can quote (F-0250). A link on a line starting with <c>&gt;</c> that's one of them
    /// doesn't count; one that isn't counts as any other.</param>
    /// <returns>What's wrong, or <see cref="TextProblem.None"/>.</returns>
    public static TextProblem Check(string text, int maxLength, int maxLinks = MaxLinks, IReadOnlySet<string>? quotable = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? TextProblem.Empty
            : text.Length > maxLength ? TextProblem.TooLong
            : SpamTag().IsMatch(text) || TagWithAttributes().IsMatch(text) || BbCode().IsMatch(text) ? TextProblem.Markup
            : Links(text).Count(link => !(quotable is not null && quotable.Contains(link.Url) && IsQuoted(text, link.Index))) > maxLinks ? TextProblem.TooManyLinks
            : TextProblem.None;
    }

    // Whether the line holding a place starts with ">", as a quote's lines do.
    private static bool IsQuoted(string text, int index)
    {
        var start = text.AsSpan(0, index).LastIndexOf('\n') + 1;
        return text.AsSpan(start, index - start).TrimStart().StartsWith('>');
    }

    /// <summary>Finds the links: addresses starting with <c>http://</c>, <c>https://</c> or <c>www.</c>, without the punctuation after them.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The links, in order.</returns>
    public static IReadOnlyList<TextLink> Links(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var links = new List<TextLink>();
        foreach (Match match in Link().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '*', '_');

            // A closing bracket belongs to the address only when it opened one: "(see https://x.y/a_(b))".
            while (value.Length > 0 && value[^1] is ')' or ']' && value.Count(c => c == Opening(value[^1])) < value.Count(c => c == value[^1]))
            {
                value = value[..^1].TrimEnd('.', ',', ';', ':', '!', '?');
            }

            if (value.Length > "www.".Length)
            {
                var url = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? $"https://{value}" : value;
                links.Add(new TextLink(match.Index, value.Length, url));
            }
        }

        return links;
    }

    private static char Opening(char closing) => closing == ')' ? '(' : '[';

    // Control and format characters (zero-width spaces, direction overrides) except the joiners
    // emoji and some scripts need.
    private static bool IsInvisible(Rune rune) =>
        Rune.IsControl(rune)
        || (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format && rune.Value is not (0x200C or 0x200D));

    [GeneratedRegex(@"\n{4,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // The HTML spam uses: a link, an image, a script, a frame or what can carry one, opening or
    // closing, whatever follows its name: "<a href=…>", "</a>", "<IMG src=…>", "<script>".
    // "<b>", "<Enter>" or "List<T>" are fine.
    [GeneratedRegex(@"</?(a|img|image|script|iframe|frame|frameset|object|embed|applet|svg|math|style|link|meta|base|form)(?=[\s/>])[^<>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SpamTag();

    // Any tag with an attribute ("<x href=…>", "<p onclick='…'>") or a slash straight after its
    // name ("<svg/onload=…>", "<br/>", F-0187). Not "Map<K, V>", "<T = string>" or "<Foo />".
    [GeneratedRegex(@"<[a-zA-Z][a-zA-Z0-9-]*(/|\s[^<>]*?[a-zA-Z_:][-\w:.]*=[""'\w])[^<>]*>")]
    private static partial Regex TagWithAttributes();

    // BBCode's links and images: "[url=…]", "[url]", "[/url]", "[img]", "[email=…]". Not "[b]",
    // "[code]" or "arr[i]".
    [GeneratedRegex(@"\[/?(url|link|img|email)(=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbCode();

    [GeneratedRegex(@"(?:\bhttps?://|\bwww\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
}
