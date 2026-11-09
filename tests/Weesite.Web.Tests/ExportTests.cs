using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The HTML export page and file (brief, HTML export; T-0050).</summary>
internal sealed class ExportTests
{
    private const string Path = "/admin/export?path=posts%2Fnotes-on-static-rendering.md";

    [Test]
    public async Task The_export_page_offers_title_cover_and_clean_html()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var html = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri(Path, UriKind.Relative)));
        var withFooter = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri(Path + "&footer=1", UriKind.Relative)));

        await Assert.That(html).Contains("<input id=\"export-title\" value=\"Notes on static rendering\" readonly />");
        await Assert.That(html).Contains("<input id=\"export-cover\" class=\"mono-input\" value=\"http://localhost/media/c0f10a37852f-gradient.jpg\" readonly />");
        await Assert.That(html).Contains("<img src=\"http://localhost/media/b368a21f9e93-sample.png\" alt=\"A blue square, the sample image\">");
        await Assert.That(html).Contains("<h2>A list</h2>");
        await Assert.That(html).Contains("href=\"/admin/export/file?path=posts%2Fnotes-on-static-rendering.md\"");
        await Assert.That(html).DoesNotContain("Originally published at <a");
        await Assert.That(withFooter).Contains("Originally published at <a href=\"http://localhost/blog/notes-on-static-rendering\">");
        await Assert.That(withFooter).Contains("href=\"/admin/export/file?path=posts%2Fnotes-on-static-rendering.md&footer=1\"");
    }

    [Test]
    public async Task The_file_downloads_as_a_standalone_document()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        using var response = await admin.GetAsync(new Uri("/admin/export/file?path=posts%2Fnotes-on-static-rendering.md&footer=1", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar).IsEqualTo("notes-on-static-rendering.html");
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        await Assert.That(body).StartsWith("<!doctype html>\n<html lang=\"en\">");
        await Assert.That(body).Contains("<title>Notes on static rendering</title>");
        await Assert.That(body).Contains("<h1>Notes on static rendering</h1>\n<p>Every page is rendered on the server");
        await Assert.That(body).Contains("Originally published at");
    }

    [Test]
    public async Task The_bundle_holds_the_html_and_the_posts_images()
    {
        using var copy = new SiteCopy();
        Directory.CreateDirectory(System.IO.Path.Combine(copy.Root, "media"));
        foreach (var name in new[] { "b368a21f9e93-sample.png", "c0f10a37852f-gradient.jpg" })
        {
            File.Copy(System.IO.Path.Combine(SiteFactory.SeedPath, "media", name), System.IO.Path.Combine(copy.Root, "media", name));
        }

        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        using var response = await admin.GetAsync(new Uri("/admin/export/bundle?path=posts%2Fnotes-on-static-rendering.md", UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        await using var htmlStream = await zip.GetEntry("notes-on-static-rendering.html")!.OpenAsync();
        using var reader = new StreamReader(htmlStream);
        var html = await reader.ReadToEndAsync();
        var page = await admin.GetStringAsync(new Uri("/admin/export?path=posts%2Fnotes-on-static-rendering.md", UriKind.Relative));

        await Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar).IsEqualTo("notes-on-static-rendering.zip");
        await Assert.That(zip.Entries.Select(e => e.FullName)).IsEquivalentTo(["notes-on-static-rendering.html", "images/b368a21f9e93-sample.png", "images/c0f10a37852f-gradient.jpg"]);
        await Assert.That(html).Contains("<img src=\"images/b368a21f9e93-sample.png\" alt=\"A blue square, the sample image\">");
        await Assert.That(zip.GetEntry("images/b368a21f9e93-sample.png")!.Length).IsEqualTo(new FileInfo(System.IO.Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png")).Length);
        await Assert.That(page).Contains("href=\"/admin/export/bundle?path=posts%2Fnotes-on-static-rendering.md\"");
    }

    [Test]
    public async Task Only_editors_who_may_edit_the_item_can_export_it()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var author = Client(factory, EditorRole.Author);
        using var admin = Client(factory, EditorRole.Admin);

        using var signedOut = await visitor.GetAsync(new Uri("/admin/export/file?path=posts%2Fhello-world.md", UriKind.Relative));
        using var notTheirs = await author.GetAsync(new Uri("/admin/export/file?path=posts%2Fhello-world.md", UriKind.Relative));
        using var notTheirPage = await author.GetAsync(new Uri("/admin/export?path=posts%2Fhello-world.md", UriKind.Relative));
        using var unknown = await admin.GetAsync(new Uri("/admin/export?path=posts%2Fno-such-post.md", UriKind.Relative));

        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(notTheirs.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(notTheirPage.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task The_editor_links_a_saved_item_to_its_export()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var saved = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fhello-world.md", UriKind.Relative));
        var fresh = await admin.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));

        await Assert.That(saved).Contains("<a class=\"btn\" href=\"/admin/export?path=posts%2Fhello-world.md\">Export</a>");
        await Assert.That(fresh).DoesNotContain("/admin/export?");
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }
}
