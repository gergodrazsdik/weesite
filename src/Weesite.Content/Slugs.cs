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
        "hu", "og", "oauth", "_content", "_framework",
    };

    /// <summary>Gets the slugs a post can't have, because <c>/blog/{slug}</c> routes use them.</summary>
    public static IReadOnlySet<string> ReservedForPosts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "tags",
    };

    /// <summary>Checks a slug: lowercase letters and digits, single hyphens between them.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool IsValid(string? slug) => slug is not null && SlugPattern().IsMatch(slug);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SlugPattern();
}
