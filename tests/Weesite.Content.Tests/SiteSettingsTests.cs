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

    [Test]
    public async Task Only_web_and_mail_links_are_kept()
    {
        // T-0122: the forms write only these, but site.json can be edited or imported.
        var site = SiteSettings.Parse("""
            {
                "name": "S",
                "links": [
                    { "label": "Web", "url": "https://example.com/" },
                    { "label": "Mail", "url": "mailto:a@example.com" },
                    { "label": "Script", "url": "javascript:alert(1)" },
                    { "label": "Data", "url": "data:text/html,x" }
                ],
                "authors": [
                    { "id": "a", "name": "A", "links": [{ "label": "Bad", "url": "javascript:alert(1)" }, { "label": "Good", "url": "https://a.example/" }] }
                ]
            }
            """);

        await Assert.That(site.Links.Select(l => l.Label)).IsEquivalentTo(["Web", "Mail"]);
        await Assert.That(site.Authors.Single().Links.Select(l => l.Label)).IsEquivalentTo(["Good"]);
    }
}
