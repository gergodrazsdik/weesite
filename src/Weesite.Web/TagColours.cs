using Weesite.Content;

namespace Weesite.Web;

/// <summary>The CSS class of a tag's colour (ADR 0006).</summary>
internal static class TagColours
{
    /// <summary>Gets the colour class of a tag: its colour, or empty.</summary>
    /// <param name="site">The site settings.</param>
    /// <param name="tag">The tag.</param>
    /// <returns>Such as <c>blue</c>, or an empty string.</returns>
    public static string ClassOf(SiteSettings site, string tag) => site.ColourOf(tag) ?? "";
}
