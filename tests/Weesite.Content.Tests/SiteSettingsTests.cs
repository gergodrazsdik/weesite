using System.Globalization;

namespace Weesite.Content.Tests;

/// <summary>Site settings: how dates are written (F-0036).</summary>
internal sealed class SiteSettingsTests
{
    private static readonly DateOnly Date = new(2026, 1, 15);

    [Test]
    [Arguments("{}", "2026-01-15")]
    [Arguments("{\"dateFormat\": \"d MMMM yyyy\"}", "15 January 2026")]
    [Arguments("{\"dateFormat\": \"dd.MM.yyyy\"}", "15.01.2026")]
    [Arguments("{\"dateFormat\": \"\"}", "2026-01-15")]
    [Arguments("{\"dateFormat\": \"%\"}", "2026-01-15")]
    public async Task Dates_follow_the_date_format_setting(string json, string expected) =>
        await Assert.That(SiteSettings.Parse(json).FormatDate(Date, CultureInfo.GetCultureInfo("en"))).IsEqualTo(expected);
}
