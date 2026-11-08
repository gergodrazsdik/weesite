using System.Globalization;

namespace Weesite.Web;

/// <summary>How dates appear on the site.</summary>
internal static class ContentDates
{
    /// <summary>Formats a date for reading, such as "15 January 2026" in English.</summary>
    /// <param name="date">The date.</param>
    /// <returns>The text.</returns>
    public static string Long(DateOnly date) => date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Formats a date for the <c>datetime</c> attribute.</summary>
    /// <param name="date">The date.</param>
    /// <returns>ISO 8601, such as 2026-01-15.</returns>
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
