using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Content;
using Weesite.Web.Theming;

namespace Weesite.Web.Tests;

/// <summary>The System/Light/Dark switch and a site's theme overrides.</summary>
internal sealed partial class ThemeTests
{
    [Test]
    public async Task Theme_css_takes_known_tokens_and_real_colours_only()
    {
        var site = new SiteSettings
        {
            Theme = new ThemeSettings
            {
                Light = new Dictionary<string, string> { ["accent"] = "#0055AA", ["link"] = "oklch(45% 0.2 260)", ["text"] = "red;}body{display:none", ["evil"] = "#000" },
                Dark = new Dictionary<string, string> { ["accent"] = "rgb(120 170 255)" },
            },
        };

        var theme = ThemeStylesheet.Build(site)!;

        await Assert.That(theme.Css).Contains("--color-accent-light: #0055AA;");
        await Assert.That(theme.Css).Contains("--color-link-light: oklch(45% 0.2 260);");
        await Assert.That(theme.Css).Contains("--color-accent-dark: rgb(120 170 255);");
        await Assert.That(theme.Css).DoesNotContain("display");
        await Assert.That(theme.Css).DoesNotContain("evil");
        await Assert.That(theme.Problems.Count).IsEqualTo(2);
        await Assert.That(theme.Url).IsEqualTo($"/theme.{theme.Hash}.css");
    }

    [Test]
    public async Task No_theme_means_no_stylesheet() =>
        await Assert.That(ThemeStylesheet.Build(SiteSettings.Empty)).IsNull();

    [Test]
    public async Task Switch_sets_the_cookie_and_the_page_renders_the_choice()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var home = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));
        await Assert.That(home).Contains("<html lang=\"en\">");
        var token = AntiforgeryField().Match(home).Groups[1].Value;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["mode"] = "dark",
            ["returnUrl"] = "/blog",
        });
        using var post = await client.PostAsync(new Uri("/theme", UriKind.Relative), form);

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(post.Headers.Location?.ToString()).IsEqualTo("/blog");
        await Assert.That(post.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("theme=dark", StringComparison.Ordinal))).IsTrue();

        var after = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));
        await Assert.That(after).Contains("data-theme=\"dark\"");
        await Assert.That(after).Contains("value=\"dark\" aria-pressed=\"true\"");
    }

    [Test]
    public async Task Switch_refuses_posts_without_the_antiforgery_token()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["mode"] = "dark" });
        using var post = await client.PostAsync(new Uri("/theme", UriKind.Relative), form);

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Site_theme_is_linked_and_served_immutable()
    {
        var content = CopySeedWithTheme();
        try
        {
            await using var factory = new SiteFactory(content);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));
            var url = ThemeLink().Match(html).Groups[1].Value;
            await Assert.That(url).StartsWith("/theme.");

            using var css = await client.GetAsync(new Uri(url, UriKind.Relative));
            await Assert.That(css.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(css.Content.Headers.ContentType?.MediaType).IsEqualTo("text/css");
            await Assert.That(css.Headers.CacheControl?.ToString()).IsEqualTo("public, max-age=31536000, immutable");
            await Assert.That(await css.Content.ReadAsStringAsync()).Contains("--color-accent-light: #0b6e4f;");

            using var stale = await client.GetAsync(new Uri("/theme.0000000000.css", UriKind.Relative));
            await Assert.That(stale.Headers.Location?.ToString()).IsEqualTo(url);
        }
        finally
        {
            Directory.Delete(content, recursive: true);
        }
    }

    private static string CopySeedWithTheme()
    {
        var target = Path.Combine(Path.GetTempPath(), "weesite-theme-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(SiteFactory.SeedPath, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(SiteFactory.SeedPath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var settingsFile = Path.Combine(target, "settings", "site.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsFile))!;
        settings["theme"] = new JsonObject { ["light"] = new JsonObject { ["accent"] = "#0b6e4f" } };
        File.WriteAllText(settingsFile, settings.ToJsonString());
        return target;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"(/theme\\.[0-9a-f]{10}\\.css)\"")]
    private static partial Regex ThemeLink();
}
