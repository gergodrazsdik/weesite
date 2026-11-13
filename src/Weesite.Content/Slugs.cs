using System.Text.RegularExpressions;

namespace Weesite.Content;

/// <summary>Rules for URL slugs.</summary>
public static partial class Slugs
{
    /// <summary>
    /// Gets the first path segments the app itself uses; content pages can't take them.
    /// </summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "blog", "admin", "media", "feed", "feed.xml", "feed.json", "llms.txt", "llms-full.txt", "mcp",
        "sitemap.xml", "robots.txt", "theme.css", "search", "health", "alive", "error", "not-found",
        "og", "oauth", "_content", "_framework",
    };

    /// <summary>Gets the slugs a post can't have, because <c>/blog/{slug}</c> routes use them.</summary>
    public static IReadOnlySet<string> ReservedForPosts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "tags",
    };

    /// <summary>
    /// The longest slug a save accepts (F-0079). Files with longer ones still load; the editor and
    /// MCP only refuse to write new ones.
    /// </summary>
    public const int MaxLength = 100;

    /// <summary>
    /// Checks a slug or a tag: lowercase letters of any script and digits, single hyphens between
    /// them, in the composed form (NFC), so <c>ékezet</c> and <c>konténerek</c> are slugs
    /// (F-0091 L1). Addresses carry them percent-encoded (<c>ContentUrls</c>).
    /// </summary>
    /// <param name="slug">The slug.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool IsValid(string? slug) =>
        slug is not null && SlugPattern().IsMatch(slug) && slug.IsNormalized(System.Text.NormalizationForm.FormC);

    // Lowercase, other (unicase scripts), modifier letters, combining marks and decimal digits;
    // no capitals, so an address has one spelling.
    [GeneratedRegex(@"^[\p{Ll}\p{Lo}\p{Lm}\p{Mn}\p{Mc}\p{Nd}]+(?:-[\p{Ll}\p{Lo}\p{Lm}\p{Mn}\p{Mc}\p{Nd}]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SlugPattern();
}
