using System.Text.RegularExpressions;

namespace Weesite.Content;

/// <summary>Rules for URL slugs.</summary>
public static partial class Slugs
{
    /// <summary>
    /// Gets the first path segments the app itself uses; content pages can't take them. The app's
    /// routes and static folders win over a page at <c>/{slug}</c>, so a page under one of these
    /// names would be unreachable. Tests check that every route and every folder in <c>wwwroot</c>
    /// is here, and so is every path the output cache or the page view count leaves out (F-0120).
    /// Names with a dot or an underscore (<c>feed.xml</c>, <c>site.css</c>, <c>_framework</c>) can't
    /// be slugs, so they need no place here.
    /// </summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "blog", "admin", "media", "feed", "mcp", "search", "health", "alive", "error", "not-found",
        "og", "oauth", "theme", "fonts", "images", "icons", "legal", "site-icon",
        "signin-microsoft", "signin-google",
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
