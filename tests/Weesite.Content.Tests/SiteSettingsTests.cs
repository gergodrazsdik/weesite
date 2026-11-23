using System.Globalization;
using System.Text.Json;

namespace Weesite.Content.Tests;

/// <summary>Site settings: how dates are written (F-0036), unsafe links, nulls (F-0235).</summary>
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

    [Test]
    [Arguments("""{"name": null}""")]
    [Arguments("""{"language": null}""")]
    [Arguments("""{"dateFormat": null}""")]
    [Arguments("""{"authors": null}""")]
    [Arguments("""{"links": null}""")]
    [Arguments("""{"menu": null}""")]
    [Arguments("""{"home": null}""")]
    [Arguments("""{"tags": null}""")]
    [Arguments("""{"home": {"show": null}}""")]
    [Arguments("""{"theme": {"light": null}}""")]
    [Arguments("""{"theme": {"dark": null}}""")]
    [Arguments("""{"authors": [{"id": null, "name": "A"}]}""")]
    [Arguments("""{"authors": [{"id": "a", "name": null}]}""")]
    [Arguments("""{"authors": [{"id": "a", "name": "A", "links": null}]}""")]
    [Arguments("""{"links": [{"label": null, "url": "https://example.com/"}]}""")]
    [Arguments("""{"menu": [{"label": null, "href": "/"}]}""")]
    public async Task A_null_where_a_value_belongs_makes_the_file_unreadable(string json)
    {
        // F-0235: it loaded as a "never null" member that was null, and every load threw later.
        await Assert.That(() => SiteSettings.Parse(json)).Throws<JsonException>();
    }

    [Test]
    public async Task Null_entries_in_lists_and_maps_are_left_out()
    {
        var site = SiteSettings.Parse("""
            {
                "authors": [null, { "id": "a", "name": "A", "links": [null, { "label": "Web", "url": "https://a.example/" }] }],
                "links": [null, { "label": "Web", "url": "https://example.com/" }],
                "menu": [null, { "label": "Home", "href": "/" }],
                "tags": { "web": null, "art": { "colour": "blue" } },
                "theme": { "light": { "accent": null, "text": "#111111" }, "dark": { "accent": null } }
            }
            """);

        await Assert.That(site.Authors.Single().Links.Single().Label).IsEqualTo("Web");
        await Assert.That(site.Links.Single().Label).IsEqualTo("Web");
        await Assert.That(site.Menu.Single().Label).IsEqualTo("Home");
        await Assert.That(site.Tags.Keys).IsEquivalentTo(["art"]);
        await Assert.That(site.ColourOf("art")).IsEqualTo("blue");
        await Assert.That(site.Theme!.Light.Keys).IsEquivalentTo(["text"]);
        await Assert.That(site.Theme.Dark).IsEmpty();
    }

    [Test]
    public async Task Null_optional_members_are_fine()
    {
        var site = SiteSettings.Parse("""{"tagline": null, "theme": null, "indexNow": null, "contactForm": null, "home": {"show": "page", "page": null}}""");

        await Assert.That(site.Name).IsEqualTo("Weesite");
        await Assert.That(site.Theme).IsNull();
    }
}
