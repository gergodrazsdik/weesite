using System.Globalization;

namespace Weesite.Content;

/// <summary>
/// Times as the site shows them (T-0157): stored times stay UTC; the admin, the digests and the
/// analytics show them in the site's time zone (an IANA name such as <c>Europe/Budapest</c>), on a
/// 24-hour or 12-hour clock. Without a zone, times are UTC and say so.
/// </summary>
public static class SiteTime
{
    /// <summary>The 24-hour clock's setting value, the default.</summary>
    public const string Clock24 = "24h";

    /// <summary>The 12-hour clock's setting value.</summary>
    public const string Clock12 = "12h";

    /// <summary>Finds a time zone by its IANA name.</summary>
    /// <param name="id">The name, such as <c>Europe/Budapest</c>; empty for UTC.</param>
    /// <returns>The zone, or UTC when the name is empty or unknown.</returns>
    public static TimeZoneInfo Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>Whether a name is a time zone this system knows.</summary>
    /// <param name="id">The name.</param>
    /// <returns>True for a known zone.</returns>
    public static bool IsZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out _);

    /// <summary>Whether a zone is UTC itself, so times say "UTC".</summary>
    /// <param name="zone">The zone.</param>
    /// <returns>True for UTC.</returns>
    public static bool IsUtc(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return zone.Id is "UTC" or "Etc/UTC" or "Etc/Universal" or "Universal" or "Etc/Zulu" or "Zulu" || ReferenceEquals(zone, TimeZoneInfo.Utc);
    }

    /// <summary>Gets the IANA zone names to choose from, sorted, with UTC first.</summary>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> Names()
    {
        var names = TimeZoneInfo.GetSystemTimeZones()
            .Select(z => z.HasIanaId ? z.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : null)
            .Where(n => n is not null && n.Contains('/', StringComparison.Ordinal) && !n.StartsWith("Etc/", StringComparison.Ordinal))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return ["UTC", .. names];
    }

    /// <summary>A moment in a zone.</summary>
    /// <param name="at">The moment.</param>
    /// <param name="zone">The zone.</param>
    /// <returns>The same moment, with the zone's offset.</returns>
    public static DateTimeOffset Local(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone);

    /// <summary>The day a moment falls on in a zone.</summary>
    /// <param name="at">The moment.</param>
    /// <param name="zone">The zone.</param>
    /// <returns>The day.</returns>
    public static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(Local(at, zone).DateTime);

    /// <summary>Formats a moment for people: the date and the time in a zone, to the minute or second.</summary>
    /// <param name="at">The moment.</param>
    /// <param name="zone">The zone.</param>
    /// <param name="twelveHour">Whether the clock is 12-hour (<c>6:30 PM</c>) rather than 24-hour (<c>18:30</c>).</param>
    /// <param name="seconds">Whether to show seconds.</param>
    /// <returns>Such as <c>2026-11-19 18:30</c>; in UTC, <c>2026-11-19 17:30 UTC</c>.</returns>
    public static string Format(DateTimeOffset at, TimeZoneInfo zone, bool twelveHour, bool seconds = false)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var clock = (twelveHour, seconds) switch
        {
            (true, true) => "h:mm:ss tt",
            (true, false) => "h:mm tt",
            (false, true) => "HH:mm:ss",
            _ => "HH:mm",
        };
        var text = Local(at, zone).ToString("yyyy-MM-dd " + clock, CultureInfo.InvariantCulture);
        return IsUtc(zone) ? text + " UTC" : text;
    }

    /// <summary>Formats a moment's time of day only, as <see cref="Format"/> does.</summary>
    /// <param name="at">The moment.</param>
    /// <param name="zone">The zone.</param>
    /// <param name="twelveHour">Whether the clock is 12-hour.</param>
    /// <returns>Such as <c>18:30</c> or <c>6:30 PM</c>.</returns>
    public static string FormatClock(DateTimeOffset at, TimeZoneInfo zone, bool twelveHour) =>
        Local(at, zone).ToString(twelveHour ? "h:mm tt" : "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The moment a wall-clock time in a zone stands for.</summary>
    /// <param name="local">The date and time as read on a clock in the zone.</param>
    /// <param name="zone">The zone.</param>
    /// <returns>The moment; null when the clocks skip that time (a daylight-saving gap). A time that
    /// happens twice, when the clocks go back, is taken as the first.</returns>
    public static DateTimeOffset? FromLocal(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            return null;
        }

        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }
}
