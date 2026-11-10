using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The stylesheets and fonts (ADR 0006): public pages don't load the admin's styles.</summary>
internal sealed partial class StylesheetTests
{
    [Test]
    public async Task Public_pages_load_site_css_only_and_admin_pages_add_admin_css()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var visitor = factory.CreateClient();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var home = Stylesheets(await visitor.GetStringAsync(new Uri("/", UriKind.Relative)));
        var menu = Stylesheets(await admin.GetStringAsync(new Uri("/admin/menu", UriKind.Relative)));
        var css = await visitor.GetStringAsync(new Uri(home.Single(), UriKind.Relative));
        using var font = await visitor.GetAsync(new Uri("/fonts/plex-sans-1.1.0/IBMPlexSans-Regular-Latin1.woff2", UriKind.Relative));

        await Assert.That(home).Count().IsEqualTo(1);
        await Assert.That(home[0]).StartsWith("/site.");
        await Assert.That(menu).Count().IsEqualTo(2);
        await Assert.That(menu[1]).StartsWith("/admin.");
        await Assert.That(css).Contains("IBM Plex Sans");
        await Assert.That(font.IsSuccessStatusCode).IsTrue();

        // A printed page leaves out the software credit.
        await Assert.That(css).Contains("@media print");
        await Assert.That(css).Contains(".built-with");

        // T-0098: versioned folders, so a year of caching is safe; preloads match site.css.
        await Assert.That(font.Headers.CacheControl?.ToString()).IsEqualTo("public, max-age=31536000, immutable");
        foreach (var preload in System.Text.RegularExpressions.Regex.Matches(await visitor.GetStringAsync(new Uri("/", UriKind.Relative)), "rel=\"preload\" href=\"([^\"]+)\"").Select(m => m.Groups[1].Value))
        {
            await Assert.That(css).Contains($"url(\"{preload.TrimStart('/')}\")");
        }
    }

    private static List<string> Stylesheets(string html) =>
        [.. Stylesheet().Matches(html).Select(m => m.Groups[1].Value)];

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"([^\"]+)\"")]
    private static partial Regex Stylesheet();
}
