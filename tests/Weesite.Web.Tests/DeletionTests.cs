using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Deleting posts, pages and media files for good (T-0110).</summary>
internal sealed partial class DeletionTests
{
    private const string HelloWorld = "/admin/delete?path=posts%2Fhello-world.md";

    [Test]
    public async Task An_admin_deletes_a_post_after_typing_its_slug()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var file = Path.Combine(copy.Root, "content", "posts", "hello-world.md");

        var editor = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fhello-world.md", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri(HelloWorld, UriKind.Relative));
        using var wrong = await PostAsync(admin, "/admin/delete", page, ("path", "posts/hello-world.md"), ("etag", Field(page, "etag")), ("confirm", "Hello, world"));
        using var stale = await PostAsync(admin, "/admin/delete", page, ("path", "posts/hello-world.md"), ("etag", "not-the-etag"), ("confirm", "hello-world"));
        var keptAfterMistakes = File.Exists(file);
        using var delete = await PostAsync(admin, "/admin/delete", page, ("path", "posts/hello-world.md"), ("etag", Field(page, "etag")), ("confirm", "hello-world"));
        using var visitor = factory.CreateClient();
        using var gone = await visitor.GetAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var list = await admin.GetStringAsync(delete.Headers.Location!);
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);

        await Assert.That(editor).Contains("<a href=\"/admin/delete?path=posts%2Fhello-world.md\">Delete permanently…</a>");
        await Assert.That(page).Contains("Type the slug, hello-world, to confirm");
        await Assert.That(page).Contains("It&#x27;s public now at /blog/hello-world");
        await Assert.That(wrong.Headers.Location?.ToString()).IsEqualTo(HelloWorld + "&error=confirm");
        await Assert.That(stale.Headers.Location?.ToString()).IsEqualTo(HelloWorld + "&error=changed");
        await Assert.That(keptAfterMistakes).IsTrue();
        await Assert.That(delete.Headers.Location?.ToString()).IsEqualTo("/admin/posts?deleted=Hello%2C%20world");
        await Assert.That(File.Exists(file)).IsFalse();
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(list).Contains("Deleted “Hello, world”.");
        await Assert.That(audit.Any(e => e.Action == "content-deleted" && e.Target == "posts/hello-world.md")).IsTrue();
    }

    [Test]
    public async Task Deleting_a_page_takes_its_menu_item_too()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var before = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri("/admin/delete?path=pages%2Fabout.md", UriKind.Relative));
        using var delete = await PostAsync(admin, "/admin/delete", page, ("path", "pages/about.md"), ("etag", Field(page, "etag")), ("confirm", "about"));
        var after = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var settings = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);

        await Assert.That(before).Contains("<a href=\"/about\"");
        await Assert.That(page).Contains("The menu links to this page (About): that item goes too.");
        await Assert.That(delete.Headers.Location?.ToString()).StartsWith("/admin/pages?deleted=");
        await Assert.That(after).DoesNotContain("<a href=\"/about\"");
        await Assert.That(after).Contains("<a href=\"/blog\"");
        await Assert.That(settings).DoesNotContain("\"about\"");
        await Assert.That(audit.Any(e => e.Action == "menu-changed" && e.Target == "site.json (removed /about)")).IsTrue();
    }

    [Test]
    public async Task A_menu_item_for_a_page_thats_gone_is_left_out_and_kept_in_the_menu_editor()
    {
        using var copy = new SiteCopy();
        File.Delete(Path.Combine(copy.Root, "content", "pages", "about.md"));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var menu = await admin.GetStringAsync(new Uri("/admin/menu", UriKind.Relative));

        await Assert.That(home).DoesNotContain("<a href=\"/about\"");
        await Assert.That(menu).Contains("<option value=\"page:about\" selected>/about (not published: choose another target or clear the row)</option>");
    }

    [Test]
    [Arguments("../settings/site.json")]
    [Arguments("posts/../../settings/site.json")]
    [Arguments("posts/no-such-post.md")]
    public async Task Only_a_post_or_page_the_site_knows_can_be_deleted(string path)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var page = await admin.GetStringAsync(new Uri(HelloWorld, UriKind.Relative));

        using var delete = await PostAsync(admin, "/admin/delete", page, ("path", path), ("etag", Field(page, "etag")), ("confirm", "hello-world"));

        await Assert.That(delete.Headers.Location?.ToString()).EndsWith("&error=changed");
        await Assert.That(File.Exists(Path.Combine(copy.Root, "settings", "site.json"))).IsTrue();
    }

    [Test]
    public async Task An_author_cant_delete_someone_elses_post()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var author = Client(factory, EditorRole.Author);
        var page = await admin.GetStringAsync(new Uri(HelloWorld, UriKind.Relative));

        using var get = await author.GetAsync(new Uri(HelloWorld, UriKind.Relative));
        var authorsForm = await author.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var post = await PostAsync(author, "/admin/delete", authorsForm, ("path", "posts/hello-world.md"), ("etag", Field(page, "etag")), ("confirm", "hello-world"));

        await Assert.That(get.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
        await Assert.That(post.Headers.Location?.ToString()).IsEqualTo("/admin/forbidden");
        await Assert.That(File.Exists(Path.Combine(copy.Root, "content", "posts", "hello-world.md"))).IsTrue();
    }

    [Test]
    public async Task Deleting_a_used_file_warns_and_takes_its_resized_copies_too()
    {
        using var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "0e3d1bf78702-diagram.svg"), Path.Combine(media, "0e3d1bf78702-diagram.svg"));
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"), Path.Combine(media, "b368a21f9e93-sample.png"));
        await File.WriteAllBytesAsync(Path.Combine(media, "b368a21f9e93-sample-480w.webp"), [1, 2, 3]);
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var used = await admin.GetStringAsync(new Uri("/admin/media/delete?name=0e3d1bf78702-diagram.svg", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri("/admin/media/delete?name=b368a21f9e93-sample.png", UriKind.Relative));
        using var delete = await PostAsync(admin, "/admin/media/delete", page, ("name", "b368a21f9e93-sample.png"), ("confirm", "b368a21f9e93-sample.png"));
        var list = await admin.GetStringAsync(delete.Headers.Location!);

        await Assert.That(used).Contains("Still used by these posts and pages (1)");
        await Assert.That(used).Contains("<a href=\"/admin/edit?path=posts%2Fnotes-on-static-rendering.md\">Notes on static rendering</a>");
        await Assert.That(delete.Headers.Location?.ToString()).IsEqualTo("/admin/media?deleted=b368a21f9e93-sample.png");
        await Assert.That(File.Exists(Path.Combine(media, "b368a21f9e93-sample.png"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(media, "b368a21f9e93-sample-480w.webp"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(media, "0e3d1bf78702-diagram.svg"))).IsTrue();
        await Assert.That(list).Contains("Deleted b368a21f9e93-sample.png.");
        await Assert.That(list).Contains("aria-label=\"Delete 0e3d1bf78702-diagram.svg\"");
    }

    [Test]
    public async Task Authors_cant_delete_files()
    {
        using var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"), Path.Combine(media, "b368a21f9e93-sample.png"));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        var list = await author.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        using var page = await author.GetAsync(new Uri("/admin/media/delete?name=b368a21f9e93-sample.png", UriKind.Relative));
        var form = await author.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var post = await PostAsync(author, "/admin/media/delete", form, ("name", "b368a21f9e93-sample.png"), ("confirm", "b368a21f9e93-sample.png"));

        await Assert.That(list).DoesNotContain("Delete…");
        await Assert.That(page.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(post.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
        await Assert.That(File.Exists(Path.Combine(media, "b368a21f9e93-sample.png"))).IsTrue();
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    private static string Field(string page, string name) =>
        Regex.Match(page, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;

    // Posts a form the way the page does: with the antiforgery token from the page's own form.
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string page, params (string Name, string Value)[] fields)
    {
        var token = Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
