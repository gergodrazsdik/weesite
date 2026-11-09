using System.Globalization;

namespace Weesite.Web;

/// <summary>How dates appear on the site.</summary>
internal static class ContentDates
{
    /// <summary>Formats a date for reading, in the site's date format (<c>yyyy-MM-dd</c> unless <c>site.json</c> sets <c>dateFormat</c>).</summary>
    /// <param name="date">The date.</param>
    /// <param name="site">The site settings.</param>
    /// <returns>The text.</returns>
    public static string Display(DateOnly date, Weesite.Content.SiteSettings site) => site.FormatDate(date, CultureInfo.CurrentCulture);

    /// <summary>Formats a date for the <c>datetime</c> attribute.</summary>
    /// <param name="date">The date.</param>
    /// <returns>ISO 8601, such as 2026-01-15.</returns>
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
