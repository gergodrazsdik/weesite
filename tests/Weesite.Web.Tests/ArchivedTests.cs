using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Archived posts, pages and media files (T-0111).</summary>
internal sealed partial class ArchivedTests
{
    [Test]
    public async Task An_archived_post_is_gone_from_the_site_and_under_archived_in_the_editor()
    {
        using var copy = new SiteCopy();
        var file = Path.Combine(copy.Root, "content", "posts", "hello-world.md");
        await File.WriteAllTextAsync(file, "---\nstatus: archived\n" + (await File.ReadAllTextAsync(file))["---\n".Length..]);
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var visitor = factory.CreateClient();
        using var admin = Client(factory);

        using var post = await visitor.GetAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var body = await post.Content.ReadAsStringAsync();
        var blog = await visitor.GetStringAsync(new Uri("/blog", UriKind.Relative));
        var feed = await visitor.GetStringAsync(new Uri("/feed.xml", UriKind.Relative));
        var sitemap = await visitor.GetStringAsync(new Uri("/sitemap.xml", UriKind.Relative));
        var search = await visitor.GetStringAsync(new Uri("/search?q=hello", UriKind.Relative));
        using var missing = await visitor.GetAsync(new Uri("/blog/no-such-post", UriKind.Relative));
        var all = await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));
        var archived = await admin.GetStringAsync(new Uri("/admin/posts?status=archived", UriKind.Relative));
        var editor = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fhello-world.md", UriKind.Relative));

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await Assert.That(body).Contains("<h1 id=\"gone-title\" class=\"page-title\">Removed</h1>");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(blog).DoesNotContain("/blog/hello-world\"");
        await Assert.That(feed).DoesNotContain(">Hello, world<");
        await Assert.That(sitemap).DoesNotContain("/blog/hello-world");
        await Assert.That(search).DoesNotContain("/blog/hello-world\"");
        await Assert.That(all).DoesNotContain(">Hello, world</a>");
        await Assert.That(all).Contains("Archived <span>1</span>");
        await Assert.That(archived).Contains(">Hello, world</a>");
        await Assert.That(archived).Contains("<span class=\"pill archived\">Archived</span>");
        await Assert.That(editor).Contains("value=\"archived\" checked");
    }

    [Test]
    public async Task An_archived_file_is_still_served_but_not_offered_and_comes_back()
    {
        using var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "0e3d1bf78702-diagram.svg"), Path.Combine(media, "0e3d1bf78702-diagram.svg"));
        await File.WriteAllBytesAsync(Path.Combine(media, "abcdef012345-notes.pdf"), "%PDF-1.4\n%%EOF\n"u8.ToArray());
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);
        using var visitor = factory.CreateClient();

        var warning = await admin.GetStringAsync(new Uri("/admin/media/archive?name=0e3d1bf78702-diagram.svg", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri("/admin/media/archive?name=abcdef012345-notes.pdf", UriKind.Relative));
        using var archive = await PostAsync(admin, "/admin/media/archive", page, ("name", "abcdef012345-notes.pdf"), ("archived", "true"));
        var picker = await admin.GetStringAsync(new Uri("/admin/media.json", UriKind.Relative));
        var listed = await admin.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        var tab = await admin.GetStringAsync(new Uri("/admin/media?show=archived", UriKind.Relative));
        using var served = await visitor.GetAsync(new Uri("/media/abcdef012345-notes.pdf", UriKind.Relative));
        var settings = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "media.json"));
        using var unarchive = await PostAsync(admin, "/admin/media/archive", tab, ("name", "abcdef012345-notes.pdf"), ("archived", "false"));
        var pickerAfter = await admin.GetStringAsync(new Uri("/admin/media.json", UriKind.Relative));

        await Assert.That(warning).Contains("Still used by these posts and pages (1)");
        await Assert.That(page).DoesNotContain("Still used by");
        await Assert.That(archive.Headers.Location?.ToString()).IsEqualTo("/admin/media?archived=abcdef012345-notes.pdf");
        await Assert.That(settings).Contains("\"abcdef012345-notes.pdf\"");
        await Assert.That(picker).DoesNotContain("abcdef012345-notes.pdf");
        await Assert.That(picker).Contains("0e3d1bf78702-diagram.svg");
        await Assert.That(listed).DoesNotContain(">abcdef012345-notes.pdf<");
        await Assert.That(tab).Contains(">abcdef012345-notes.pdf<");
        await Assert.That(tab).Contains("aria-label=\"Unarchive abcdef012345-notes.pdf\"");
        await Assert.That(served.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unarchive.Headers.Location?.ToString()).IsEqualTo("/admin/media?unarchived=abcdef012345-notes.pdf");
        await Assert.That(pickerAfter).Contains("abcdef012345-notes.pdf");
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string page, params (string Name, string Value)[] fields)
    {
        var token = Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
