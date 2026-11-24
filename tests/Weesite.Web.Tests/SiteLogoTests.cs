using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The site's own logo (T-0116).</summary>
// The drawn icons are a static cache: tests that draw them run one at a time.
[NotInParallel("site-logo-icons")]
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class SiteLogoTests(SharedSite shared)
{
    [Test]
    public async Task Without_a_logo_the_built_in_one_is_used()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(home).Contains("<link rel=\"icon\" href=\"/favicon.ico\"");
        await Assert.That(home).Contains("class=\"site-name\" href=\"/\"><img src=\"/icons/logo.");
        await Assert.That(home).DoesNotContain("/site-icon/");
    }

    [Test]
    public async Task A_logo_from_media_is_the_sites_mark_and_icons()
    {
        using var copy = Copy(out var settings);
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"privacyPage\"", "\"logo\": \"/media/0e3d1bf78702-diagram.svg\",\n  \"privacyPage\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var client = factory.CreateClient();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        using var icon = await client.GetAsync(new Uri("/site-icon/0e3d1bf78702/120.png", UriKind.Relative));
        var png = await icon.Content.ReadAsByteArrayAsync();
        using var wrongKey = await client.GetAsync(new Uri("/site-icon/ffffffffffff/120.png", UriKind.Relative));
        using var wrongSize = await client.GetAsync(new Uri("/site-icon/0e3d1bf78702/64.png", UriKind.Relative));
        var settingsPage = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));

        await Assert.That(home).Contains("<link rel=\"icon\" href=\"/media/0e3d1bf78702-diagram.svg\" type=\"image/svg&#x2B;xml\" />");
        await Assert.That(home).Contains("<link rel=\"apple-touch-icon\" href=\"/site-icon/0e3d1bf78702/180.png\" />");
        await Assert.That(home).Contains("class=\"site-name\" href=\"/\"><img src=\"/media/0e3d1bf78702-diagram.svg\"");
        await Assert.That(home).DoesNotContain("/favicon.ico");
        await Assert.That(icon.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
        await Assert.That(icon.Headers.CacheControl?.ToString()).Contains("immutable");
        using (var image = NetVips.Image.NewFromBuffer(png))
        {
            await Assert.That((image.Width, image.Height)).IsEqualTo((120, 120));
        }

        await Assert.That(wrongKey.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(wrongSize.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(settingsPage).Contains("href=\"/site-icon/0e3d1bf78702/120.png\" download=\"logo-120.png\"");
        await Assert.That(settingsPage).Contains("href=\"/site-icon/0e3d1bf78702/215.png\" download=\"logo-215.png\"");
    }

    [Test]
    public async Task A_new_logo_s_icons_replace_the_old_ones_in_the_cache()
    {
        // F-0064: the drawn icons of every logo the site ever had stayed until a restart.
        using var copy = Copy(out var settings);
        var original = await File.ReadAllTextAsync(settings);
        await File.WriteAllTextAsync(settings, original.Replace("\"privacyPage\"", "\"logo\": \"/media/0e3d1bf78702-diagram.svg\",\n  \"privacyPage\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();
        using (await client.GetAsync(new Uri("/site-icon/0e3d1bf78702/32.png", UriKind.Relative)))
        {
            await Assert.That(SiteLogo.DrawnNames).Contains("0e3d1bf78702-diagram.svg");
        }

        await File.WriteAllTextAsync(settings, original.Replace("\"privacyPage\"", "\"logo\": \"/media/b368a21f9e93-sample.png\",\n  \"privacyPage\"", StringComparison.Ordinal));
        await site.Services.GetRequiredService<Weesite.Content.ContentLoader>().LoadAsync(CancellationToken.None);
        using var icon = await client.GetAsync(new Uri("/site-icon/b368a21f9e93/32.png", UriKind.Relative));

        await Assert.That(icon.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(SiteLogo.DrawnNames).Contains("b368a21f9e93-sample.png");
        await Assert.That(SiteLogo.DrawnNames).DoesNotContain("0e3d1bf78702-diagram.svg");
    }

    [Test]
    public async Task A_favicon_replaces_the_logo_in_the_tab_only()
    {
        // T-0118: the logo stays in the header and for the sign-in screens.
        using var copy = Copy(out var settings);
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"privacyPage\"", "\"logo\": \"/media/0e3d1bf78702-diagram.svg\",\n  \"favicon\": \"/media/b368a21f9e93-sample.png\",\n  \"privacyPage\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        using var favicon = await client.GetAsync(new Uri("/site-icon/b368a21f9e93/32.png", UriKind.Relative));
        using var logo = await client.GetAsync(new Uri("/site-icon/0e3d1bf78702/215.png", UriKind.Relative));

        await Assert.That(home).Contains("<link rel=\"icon\" href=\"/site-icon/b368a21f9e93/32.png\" sizes=\"32x32\"");
        await Assert.That(home).Contains("<link rel=\"apple-touch-icon\" href=\"/site-icon/b368a21f9e93/180.png\" />");
        await Assert.That(home).DoesNotContain("<link rel=\"icon\" href=\"/media/0e3d1bf78702-diagram.svg\"");
        await Assert.That(home).Contains("class=\"site-name\" href=\"/\"><img src=\"/media/0e3d1bf78702-diagram.svg\"");
        await Assert.That(favicon.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(logo.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_logo_is_drawn_once_for_every_size_and_request()
    {
        // T-0177: each size was drawn by the anonymous request that asked first, and requests that
        // came at once each drew it.
        // A name of its own: the drawn icons are kept for the whole test run.
        using var copy = Copy(out var settings);
        var key = Guid.NewGuid().ToString("N");
        File.Copy(Path.Combine(copy.Root, "media", "0e3d1bf78702-diagram.svg"), Path.Combine(copy.Root, "media", $"{key}-diagram.svg"));
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"privacyPage\"", $"\"logo\": \"/media/{key}-diagram.svg\",\n  \"privacyPage\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();
        var before = SiteLogo.Drawings;

        var icons = await Task.WhenAll(SiteLogo.Sizes.Concat(SiteLogo.Sizes).Select(size => client.GetAsync(new Uri($"/site-icon/{key}/{size}.png", UriKind.Relative))));

        await Assert.That(icons.All(i => i.StatusCode == HttpStatusCode.OK)).IsTrue();
        await Assert.That(SiteLogo.Drawings - before).IsEqualTo(1);
        foreach (var icon in icons)
        {
            icon.Dispose();
        }
    }

    [Test]
    [Arguments("broken.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><unclosed")]
    [Arguments("drawing.png", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>")]
    [Arguments("picture.svg", "")]
    public async Task A_logo_that_isnt_the_image_its_name_says_is_read_once_and_gets_no_icons(string name, string contents)
    {
        // T-0177: a logo libvips couldn't draw was read and decoded again at every request; an SVG
        // named as a PNG went to the SVG decoder. The last one is a PNG named as an SVG.
        using var copy = Copy(out var settings);
        var key = Guid.NewGuid().ToString("N");
        name = $"{key}-{name}";
        var bytes = contents.Length > 0 ? System.Text.Encoding.UTF8.GetBytes(contents) : await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"));
        await File.WriteAllBytesAsync(Path.Combine(copy.Root, "media", name), bytes);
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"privacyPage\"", $"\"logo\": \"/media/{name}\",\n  \"privacyPage\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();
        var before = SiteLogo.Drawings;

        using var first = await client.GetAsync(new Uri($"/site-icon/{key}/32.png", UriKind.Relative));
        using var again = await client.GetAsync(new Uri($"/site-icon/{key}/192.png", UriKind.Relative));

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(SiteLogo.Drawings - before).IsEqualTo(1);
    }

    [Test]
    [Arguments("/media/0e3d1bf78702-diagram.svg", true)]
    [Arguments("/media/b368a21f9e93-sample.png", true)]
    [Arguments("/media/ffffffffffff-missing.svg", false)]
    [Arguments("/media/abcdef012345-notes.pdf", false)]
    [Arguments("https://example.com/logo.svg", false)]
    public async Task Only_a_stored_image_can_be_the_logo(string path, bool valid)
    {
        using var copy = Copy(out _);
        await using var site = new SiteFactory(copy.Root);
        var media = (site.Services.GetService(typeof(Weesite.Content.ContentCatalogProvider)) as Weesite.Content.ContentCatalogProvider)!.Current.Media;

        await Assert.That(SiteLogo.IsLogoFile(path, media)).IsEqualTo(valid);
    }

    // The sample site with a few media files: the copy has none of its own.
    private static SiteCopy Copy(out string settings)
    {
        var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        foreach (var name in new[] { "0e3d1bf78702-diagram.svg", "b368a21f9e93-sample.png" })
        {
            File.Copy(Path.Combine(SiteFactory.SeedPath, "media", name), Path.Combine(media, name));
        }

        File.WriteAllBytes(Path.Combine(media, "abcdef012345-notes.pdf"), "%PDF-1.4\n%%EOF\n"u8.ToArray());
        settings = Path.Combine(copy.Root, "settings", "site.json");
        return copy;
    }
}

/// <summary>The home page photo (T-0117).</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class HomePhotoTests(SharedSite shared)
{
    [Test]
    public async Task The_home_page_shows_the_photo_in_its_round_frame()
    {
        using var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"), Path.Combine(media, "b368a21f9e93-sample.png"));
        await File.WriteAllBytesAsync(Path.Combine(media, "b368a21f9e93-sample-480w.webp"), [1, 2, 3]);
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"privacyPage\"", "\"homePhoto\": \"/media/b368a21f9e93-sample.png\",\n  \"privacyPage\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(home).Contains("<span class=\"avatar photo\"><img src=\"/media/b368a21f9e93-sample-480w.webp\"");
    }

    [Test]
    public async Task Without_a_photo_the_frame_shows_the_logo()
    {
        var site = shared.Factory;
        using var client = site.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(home).Contains("<span class=\"avatar\"><img src=\"/icons/logo.");
    }
}
