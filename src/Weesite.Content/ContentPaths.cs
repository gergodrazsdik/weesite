namespace Weesite.Content;

/// <summary>
/// What a post's or page's path may look like: <c>posts/</c> or <c>pages/</c>, at most four
/// folders, a <c>.md</c> file (any case, as the site loads them). The editor checks every path it's
/// given, so a request can't reach another kind of file through it (T-0122).
/// </summary>
public static class ContentPaths
{
    /// <summary>Gets whether a path is a content file's.</summary>
    /// <param name="path">The path, such as <c>posts/hello-world.md</c>.</param>
    /// <returns>True when it is one.</returns>
    public static bool IsValid(string? path) =>
        path is { Length: > 0 and <= 400 } p
        && !p.Contains('\\', StringComparison.Ordinal)
        && !p.Any(char.IsControl)
        && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && p.Split('/') is { Length: >= 2 and <= 6 } parts
        && parts[0] is "posts" or "pages"
        && parts.All(part => part.Length > 0 && part is not ("." or ".."));
}
