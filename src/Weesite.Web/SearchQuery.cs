using System.Buffers;

namespace Weesite.Web;

/// <summary>A search query as the results page shows it.</summary>
internal static class SearchQuery
{
    // Straight, typographic and angle quotes, any of which a reader may put around a phrase.
    private static readonly SearchValues<char> Quotes = SearchValues.Create("\"'“”„«»‘’");

    /// <summary>
    /// Gets a query without the quotes around all of it, if it has them, for the heading's own
    /// typographic quotes: “static rendering”, not “"static rendering"” (U-26).
    /// </summary>
    /// <param name="query">The query.</param>
    /// <returns>The query, trimmed, without its outer quotes; as it was when it quotes more than one phrase.</returns>
    public static string Unquoted(string? query)
    {
        var text = (query ?? "").Trim();
        return text.Length >= 2 && Quotes.Contains(text[0]) && Quotes.Contains(text[^1]) && !text.AsSpan(1, text.Length - 2).ContainsAny(Quotes)
            ? text[1..^1].Trim()
            : text;
    }
}
