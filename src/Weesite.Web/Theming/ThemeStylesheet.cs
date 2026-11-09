using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Weesite.Content;

namespace Weesite.Web.Theming;

/// <summary>A site's theme as CSS, with a content hash for its URL.</summary>
/// <param name="Css">The stylesheet: one <c>:root</c> rule of colour variables.</param>
/// <param name="Hash">The first 10 hex digits of its SHA-256.</param>
/// <param name="Problems">Overrides that were ignored, and why.</param>
internal sealed record ThemeCss(string Css, string Hash, IReadOnlyList<string> Problems)
{
    /// <summary>Gets the URL the stylesheet is served at.</summary>
    public string Url => $"/theme.{Hash}.css";
}

/// <summary>
/// Turns the theme in site settings into <c>/theme.{hash}.css</c>: only the colour tokens
/// site.css defines, only values that parse as colours, so the theme can't inject other CSS.
/// </summary>
internal static partial class ThemeStylesheet
{
    /// <summary>Gets the colour tokens a theme may set.</summary>
    public static IReadOnlySet<string> Tokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bg", "surface", "text", "muted", "accent", "link", "border", "code-bg", "focus",
    };

    /// <summary>Builds the stylesheet for a site's settings.</summary>
    /// <param name="site">The settings.</param>
    /// <returns>The stylesheet, or null if the site has no overrides.</returns>
    public static ThemeCss? Build(SiteSettings site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (site.Theme is not { } theme)
        {
            return null;
        }

        var problems = new List<string>();
        var declarations = new StringBuilder();
        foreach (var (mode, colours) in new[] { ("light", theme.Light), ("dark", theme.Dark) })
        {
            foreach (var (token, value) in colours.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (!Tokens.Contains(token))
                {
                    problems.Add($"theme.{mode}.{token}: unknown token");
                }
                else if (!ColourPattern().IsMatch(value.Trim()))
                {
                    problems.Add($"theme.{mode}.{token}: '{value}' isn't a colour");
                }
                else
                {
                    declarations.Append(CultureInfo.InvariantCulture, $"  --color-{token}-{mode}: {value.Trim()};\n");
                }
            }
        }

        if (declarations.Length == 0)
        {
            return null;
        }

        var css = $":root {{\n{declarations}}}\n";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..10];
        return new ThemeCss(css, hash, problems);
    }

    // Hex (3, 4, 6 or 8 digits), or a colour function with numbers, percentages, angles,
    // commas, slashes and spaces only.
    [GeneratedRegex(@"^(#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})|(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\([0-9.%\s,/+\-a-z]{1,80}\))$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ColourPattern();
}
