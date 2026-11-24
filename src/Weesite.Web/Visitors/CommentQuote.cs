using System.Globalization;
using System.Resources;

namespace Weesite.Web.Visitors;

/// <summary>
/// The language Reply writes its quote header in ("Ada wrote on May 3, 2026:"): the header becomes
/// part of the comment's text, so its words and its date are in one language (U-16). That's the
/// post's language when the public site has its text in it, else the site's when it has, else
/// English, the language the site's text falls back to.
/// </summary>
internal static class CommentQuote
{
    private static readonly ResourceManager Resources = new("Weesite.Web.Resources.PublicResource", typeof(PublicResource).Assembly);

    /// <summary>Gets the culture of the quote header.</summary>
    /// <param name="post">The post's language, if it has its own.</param>
    /// <param name="site">The site's language.</param>
    /// <returns>A culture the public text is in, for both the header's words and its date.</returns>
    public static CultureInfo Culture(string? post, string? site)
    {
        foreach (var language in new[] { post, site })
        {
            if (!string.IsNullOrWhiteSpace(language) && Translated(SiteCulture.For(language)) is { } culture)
            {
                return culture;
            }
        }

        return SiteCulture.For("en");
    }

    /// <summary>Gets the quote header's template in a culture: <c>{0}</c> the name, <c>{1}</c> the date.</summary>
    /// <param name="culture">The culture, from <see cref="Culture"/>.</param>
    /// <returns>The template.</returns>
    public static string Header(CultureInfo culture) => Resources.GetString("CommentQuoteHeader", culture) ?? "{0} wrote on {1}:";

    // The culture itself when the public text is in its language: its own file (hu-HU reads
    // PublicResource.hu), or the default file for English (en-GB stays en-GB, so its dates do too).
    private static CultureInfo? Translated(CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName == "en")
        {
            return culture;
        }

        for (var c = culture; !c.Equals(CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (Resources.GetResourceSet(c, createIfNotExists: true, tryParents: false) is not null)
            {
                return culture;
            }
        }

        return null;
    }
}
