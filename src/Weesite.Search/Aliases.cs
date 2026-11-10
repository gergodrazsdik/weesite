using System.Text;
using System.Text.RegularExpressions;

namespace Weesite.Search;

/// <summary>
/// Names that keyword search would otherwise split or miss (T-0079). FTS5 reads ".NET" as the
/// word "net" and "C#" as "c", so they never matched "dotnet" or "csharp". Each written form gets
/// a shared token: the index adds it after the written form, between markers that snippets drop
/// again; a query has its written forms replaced by it; and the meaning leg embeds the written
/// form for either spelling, so stored vectors stay valid.
/// </summary>
internal static partial class Aliases
{
    // Between these the index holds a written form and its tokens: \u0005.NET\u0003dotnet\u0004.
    // Control characters, so the tokenizer treats them as separators.
    private const char Open = '\u0005';
    private const char Split = '\u0003';
    private const char Close = '\u0004';
    private const char MarkStart = '\u0001';
    private const char MarkEnd = '\u0002';

    // The shared token, the written form shown for it, and any more tokens it implies (ASP.NET is
    // .NET too). Add names here as they turn up.
    private static readonly (string Token, string Written, string Extra)[] Names =
    [
        ("aspnet", "ASP.NET", " dotnet"),
        ("dotnet", ".NET", ""),
        ("csharp", "C#", ""),
        ("fsharp", "F#", ""),
        ("nodejs", "Node.js", ""),
    ];

    /// <summary>Adds the shared tokens after each written form, for the index.</summary>
    /// <param name="text">A title, heading, text or code.</param>
    /// <returns>The text with its written forms annotated.</returns>
    public static string Annotate(string text) =>
        string.IsNullOrEmpty(text) ? text : WrittenForms().Replace(text, m =>
        {
            var (token, _, extra) = Find(m);
            return $"{Open}{m.Value}{Split}{token}{extra}{Close}";
        });

    /// <summary>Replaces the written forms in a query with their shared tokens.</summary>
    /// <param name="query">The visitor's words.</param>
    /// <returns>The query for the keyword index.</returns>
    public static string ForKeywords(string query) =>
        WrittenForms().Replace(query, m => $" {Find(m).Token} ");

    /// <summary>Replaces the shared tokens in a query with their written forms, as passages have them.</summary>
    /// <param name="query">The visitor's words.</param>
    /// <returns>The query for the embedding model.</returns>
    public static string ForMeaning(string query) =>
        Tokens().Replace(query, m => Names.First(n => n.Token.Equals(m.Value, StringComparison.OrdinalIgnoreCase)).Written);

    /// <summary>
    /// Removes the annotations from a snippet or a stored heading: the written form stays, marked
    /// when its tokens matched. A snippet that starts or ends inside an annotation keeps what it
    /// can show.
    /// </summary>
    /// <param name="snippet">Text from the index, with match markers.</param>
    /// <returns>The text as written.</returns>
    public static string Strip(string snippet)
    {
        if (snippet.IndexOfAny([Open, Split, Close]) < 0)
        {
            return snippet;
        }

        var text = Annotation().Replace(snippet, m =>
        {
            var written = m.Groups["written"].Value;
            return Matched(m.Groups["tokens"].Value) && !written.Contains(MarkStart, StringComparison.Ordinal)
                ? $"{MarkStart}{written}{MarkEnd}"
                : written;
        });

        // Starts inside the tokens: show the written form of the first one.
        text = LeadingTokens().Replace(text, m =>
        {
            var tokens = m.Groups["tokens"].Value;
            var first = tokens.Replace(MarkStart.ToString(), "", StringComparison.Ordinal).Replace(MarkEnd.ToString(), "", StringComparison.Ordinal).Trim().Split(' ')[0];
            var written = Names.FirstOrDefault(n => n.Token == first).Written ?? "";
            return m.Groups["ellipsis"].Value + (Matched(tokens) ? $"{MarkStart}{written}{MarkEnd}" : written);
        });

        // Ends inside the tokens: keep the written form and the ellipsis.
        text = TrailingTokens().Replace(text, m => m.Groups["written"].Value + (m.Groups["rest"].Value is [.., '…'] ? "…" : ""));

        var clean = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is not (Open or Split or Close))
            {
                clean.Append(ch);
            }
        }

        return clean.ToString();
    }

    private static bool Matched(string tokens) => tokens.Contains(MarkStart, StringComparison.Ordinal);

    private static (string Token, string Written, string Extra) Find(Match match) =>
        Names.First(n => match.Groups[n.Token].Success);

    // A written form not inside a longer word: ".NET" but not "ADO.NET" or "example.net".
    [GeneratedRegex(@"(?<![\w.#])(?:(?<aspnet>ASP\.NET)|(?<dotnet>\.NET)|(?<csharp>C#)|(?<fsharp>F#)|(?<nodejs>Node\.js))(?![\w#])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WrittenForms();

    [GeneratedRegex(@"\b(?:aspnet|dotnet|csharp|fsharp|nodejs)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex("\u0005(?<written>[^\u0003\u0005]*)\u0003(?<tokens>[^\u0004\u0005]*)\u0004")]
    private static partial Regex Annotation();

    [GeneratedRegex("^(?<ellipsis>…?)(?<tokens>[^\u0003\u0004\u0005]*)\u0004")]
    private static partial Regex LeadingTokens();

    [GeneratedRegex("\u0005(?<written>[^\u0003\u0005]*)(?:\u0003(?<rest>[^\u0004]*))?$")]
    private static partial Regex TrailingTokens();
}
