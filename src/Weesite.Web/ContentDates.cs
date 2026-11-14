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

    /// <summary>Formats a date under its year's heading, in the site language's short form: "Oct 12" in English (ADR 0006, ADR 0008).</summary>
    /// <param name="date">The date.</param>
    /// <returns>The text.</returns>
    public static string MonthDay(DateOnly date) => date.ToString(SiteCulture.MonthDayPattern(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture);

    /// <summary>Formats a time for the admin: in UTC, said so, to the minute (F-0120).</summary>
    /// <param name="at">The time, in any offset.</param>
    /// <returns>Such as <c>2026-01-15 09:30 UTC</c>.</returns>
    public static string Utc(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Formats a time for the admin to the second, for versions and log entries that can be seconds apart (F-0091 L7).</summary>
    /// <param name="at">The time, in any offset.</param>
    /// <returns>Such as <c>2026-01-15 09:30:05 UTC</c>.</returns>
    public static string UtcSeconds(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Formats a date for the <c>datetime</c> attribute.</summary>
    /// <param name="date">The date.</param>
    /// <returns>ISO 8601, such as 2026-01-15.</returns>
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
