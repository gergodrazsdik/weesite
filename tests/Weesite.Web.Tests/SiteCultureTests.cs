using System.Globalization;

namespace Weesite.Web.Tests;

/// <summary>The culture of the site's language (ADR 0008, T-0060).</summary>
internal sealed class SiteCultureTests
{
    [Test]
    [Arguments("en", "en-US", "en_US", null)]
    [Arguments("hu", "hu-HU", "hu_HU", null)]
    [Arguments("en-GB", "en-GB", "en_GB", null)]
    [Arguments("ar", "ar-SA", "ar_SA", "rtl")]
    [Arguments("no-such-language", "en-US", "en_US", null)]
    [Arguments("", "en-US", "en_US", null)]
    public async Task A_language_gives_a_specific_culture_its_locale_and_direction(string language, string culture, string locale, string? direction)
    {
        var resolved = SiteCulture.For(language);

        await Assert.That(resolved.Name).IsEqualTo(culture);
        await Assert.That(SiteCulture.OpenGraphLocale(resolved)).IsEqualTo(locale);
        await Assert.That(SiteCulture.Direction(resolved)).IsEqualTo(direction);
    }

    [Test]
    public async Task The_short_date_abbreviates_the_month_and_pads_the_day()
    {
        var english = SiteCulture.For("en");
        var hungarian = SiteCulture.For("hu");
        var date = new DateOnly(2026, 1, 5);

        await Assert.That(SiteCulture.MonthDayPattern(english)).IsEqualTo("MMM dd");
        await Assert.That(date.ToString(SiteCulture.MonthDayPattern(english), english)).IsEqualTo("Jan 05");
        await Assert.That(date.ToString(SiteCulture.MonthDayPattern(hungarian), hungarian)).IsEqualTo("jan. 05.");

        await Assert.That(SiteCulture.LongestMonthDay(english)).IsEqualTo(6);
        await Assert.That(SiteCulture.LongestMonthDay(hungarian)).IsEqualTo(10);

        // A quoted literal keeps its letters, such as Spanish "d 'de' MMMM".
        var spanish = SiteCulture.MonthDayPattern(CultureInfo.GetCultureInfo("es-ES"));
        await Assert.That(spanish).DoesNotContain("MMMM");
        await Assert.That(spanish.Replace("'de'", "", StringComparison.Ordinal)).DoesNotContain("e");
    }

    [Test]
    public async Task A_hungarian_site_writes_dates_and_metadata_in_hungarian()
    {
        using var copy = new SiteCopy();
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings))
            .Replace("\"language\": \"en\",", "\"language\": \"hu\",", StringComparison.Ordinal)
            .Replace("\"dateFormat\": \"yyyy-MM-dd\"", "\"dateFormat\": \"yyyy. MMMM d.\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        var post = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));

        await Assert.That(home).Contains("<html lang=\"hu\"");
        await Assert.That(home).DoesNotContain("dir=\"");
        await Assert.That(home).Contains("<meta property=\"og:locale\" content=\"hu_HU\" />");
        await Assert.That(post).Contains("<time datetime=\"2026-01-15\">2026. január 15.</time>");
        // The sample posts say lang: en, so on a Hungarian site they're English items (ADR 0008).
        await Assert.That(post).Contains("<meta property=\"og:locale\" content=\"en_US\" />");
        await Assert.That(post).Contains("<h1 id=\"post-title\" class=\"page-title\" lang=\"en\">Hello, world</h1>");
        await Assert.That(home).Contains(">okt. 12.</time>");
    }

    [Test]
    public async Task An_english_site_keeps_its_dates_and_says_en_US()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(home).Contains("<meta property=\"og:locale\" content=\"en_US\" />");
        await Assert.That(home).Contains(">Oct 12</time>");
    }
}
