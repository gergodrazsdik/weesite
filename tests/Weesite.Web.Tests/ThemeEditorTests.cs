using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;
using Weesite.Web.Theming;

namespace Weesite.Web.Tests;

/// <summary>The theme editor and its contrast check (T-0049; brief, Theme editor).</summary>
internal sealed partial class ThemeEditorTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> None =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    [Test]
    public async Task The_defaults_match_site_css()
    {
        var css = await File.ReadAllTextAsync(Path.Combine(SiteFactory.SeedPath, "..", "src", "Weesite.Web", "wwwroot", "site.css"));
        foreach (var mode in ThemePalette.Modes)
        {
            foreach (var token in ThemePalette.Tokens)
            {
                await Assert.That(css).Contains($"--color-{token}-{mode}: {ThemePalette.Defaults[mode][token]};");
            }
        }

        await Assert.That(ThemePalette.Tokens).IsEquivalentTo(ThemeStylesheet.Tokens);
    }

    [Test]
    public async Task The_defaults_and_every_preset_pass_the_contrast_check()
    {
        await Assert.That(ThemePalette.Check(None).Where(r => !r.Passes)).IsEmpty();
        foreach (var (name, preset) in ThemePalette.Presets)
        {
            await Assert.That(ThemePalette.Check(preset).Where(r => !r.Passes).Select(r => $"{name} {r.Mode} {r.Pair}")).IsEmpty();
        }
    }

    [Test]
    public async Task Every_tag_colour_has_aa_contrast_in_both_modes()
    {
        var css = await File.ReadAllTextAsync(Path.Combine(SiteFactory.SeedPath, "..", "src", "Weesite.Web", "wwwroot", "site.css"));
        string[] Pair(string name)
        {
            var match = Regex.Match(css, $@"--{name}: light-dark\((#[0-9a-f]{{6}}), (#[0-9a-f]{{6}})\);");
            return match.Success ? [match.Groups[1].Value, match.Groups[2].Value] : throw new InvalidOperationException($"--{name} not found");
        }

        foreach (var colour in Weesite.Content.TagDetails.Colours)
        {
            var text = Pair($"tag-{colour}");
            var soft = Pair($"tag-{colour}-soft");
            await Assert.That(ThemePalette.Ratio(text[0], soft[0])!.Value).IsGreaterThanOrEqualTo(4.5);
            await Assert.That(ThemePalette.Ratio(text[1], soft[1])!.Value).IsGreaterThanOrEqualTo(4.5);
            await Assert.That(css).Contains($".tag.{colour} {{");
        }
    }

    [Test]
    [Arguments("#000000", "#ffffff", 21.0)]
    [Arguments("#fff", "#fff", 1.0)]
    [Arguments("#767676", "#ffffff", 4.54)]
    public async Task Contrast_ratios_follow_wcag(string foreground, string background, double expected) =>
        await Assert.That(ThemePalette.Ratio(foreground, background)!.Value).IsEqualTo(expected).Within(0.01);

    [Test]
    public async Task A_preset_saved_by_an_admin_becomes_the_sites_theme_and_reset_removes_it()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/theme", UriKind.Relative));

        // F-0082: a preset or the defaults replace every field, so an invalid one doesn't block them.
        await Assert.That(page).Contains("value=\"preset:Forest\" formnovalidate>");
        await Assert.That(page).Contains("value=\"reset\" formnovalidate>");

        using var loaded = await Post(admin, page, "preset:Forest");
        var filled = await loaded.Content.ReadAsStringAsync();
        using var saved = await Post(admin, filled, "save");
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var themeUrl = ThemeLink().Match(home).Groups[1].Value;
        var css = await visitor.GetStringAsync(new Uri(themeUrl, UriKind.Relative));

        await Assert.That(filled).Contains("Forest loaded");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/theme?saved=1");
        await Assert.That(themeUrl).StartsWith("/theme.");
        await Assert.That(css).Contains("--color-accent-light: #17663a;");
        await Assert.That(css).Contains("--color-bg-dark: #0f1512;");

        var again = await admin.GetStringAsync(new Uri("/admin/theme", UriKind.Relative));
        using var reset = await Post(admin, again, "reset");
        using var savedReset = await Post(admin, await reset.Content.ReadAsStringAsync(), "save");
        var plain = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(savedReset.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(ThemeLink().IsMatch(plain)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).DoesNotContain("\"theme\"");
    }

    [Test]
    [Arguments("Light[2]", "#777777", "pairs don't have enough contrast")]
    [Arguments("Dark[0]", "#e6e8eb", "pairs don't have enough contrast")]
    [Arguments("Light[4]", "red", "isn't a colour such as #1d4ed8")]
    [Arguments("Light[4]", "#12345", "isn't a colour such as #1d4ed8")]
    public async Task A_theme_that_fails_the_check_isnt_saved(string field, string value, string message)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var before = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        var page = await admin.GetStringAsync(new Uri("/admin/theme", UriKind.Relative));
        using var response = await Post(admin, page, "save", (field, value));

        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(WebUtility.HtmlDecode(html)).Contains(message);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).IsEqualTo(before);

        // F-0081: a malformed colour is marked on its field; a contrast failure is about a pair.
        await FieldErrors.AssertMarkedAsync(html, message.StartsWith("isn't", StringComparison.Ordinal) ? "theme-light-accent" : null);
    }

    [Test]
    public async Task Authors_cant_change_the_theme()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        using var response = await author.GetAsync(new Uri("/admin/theme", UriKind.Relative));

        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    // Posts the page's fields as they are, with some changed, and the button pressed.
    private static async Task<HttpResponseMessage> Post(HttpClient client, string page, string action, params (string Name, string Value)[] changes)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "theme",
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value,
            ["Form.ETag"] = WebUtility.HtmlDecode(ETag().Match(page).Groups[1].Value),
            ["Action"] = action,
        };
        foreach (Match field in Field().Matches(page))
        {
            values[WebUtility.HtmlDecode(field.Groups[1].Value)] = WebUtility.HtmlDecode(field.Groups[2].Value);
        }

        foreach (var (name, value) in changes)
        {
            values["Form." + name] = value;
        }

        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri("/admin/theme", UriKind.Relative), form);
    }

    [GeneratedRegex("name=\"(Form\\.(?:Light|Dark)\\[\\d+\\])\" value=\"([^\"]*)\"")]
    private static partial Regex Field();

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"(/theme\\.[0-9a-f]+\\.css)\"")]
    private static partial Regex ThemeLink();

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("name=\"Form.ETag\" value=\"([^\"]*)\"")]
    private static partial Regex ETag();
}
