using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The unlisted preview of unpublished items (T-0048): signed-in editors only.</summary>
internal sealed class PreviewTests
{
    [Test]
    public async Task Editors_preview_a_draft_as_its_public_page()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        using var response = await admin.GetAsync(new Uri("/admin/preview/post/notes-for-a-future-post", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(response.Headers.GetValues("X-Robots-Tag").Single()).IsEqualTo("noindex, nofollow");
        await Assert.That(html).Contains("<h1 id=\"post-title\" class=\"page-title\">Notes for a future post</h1>");
        await Assert.That(html).Contains("Preview of a draft, as last saved.");
        await Assert.That(html).Contains("<a href=\"/admin/edit?path=posts%2Fnotes-for-a-future-post.md\">Back to the editor</a>");
        await Assert.That(html).DoesNotContain("/admin.");
        await Assert.That(html).Contains("<meta name=\"robots\" content=\"noindex\">");
    }

    [Test]
    public async Task A_scheduled_post_says_when_it_goes_public()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var html = await admin.GetStringAsync(new Uri("/admin/preview/post/a-post-from-the-future", UriKind.Relative));

        await Assert.That(html).Contains("Preview, as last saved: public from 2030-01-01 08:00 UTC.");
    }

    [Test]
    public async Task Visitors_must_sign_in_and_authors_see_only_their_own_drafts()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var author = Client(factory, EditorRole.Author);

        using var signedOut = await visitor.GetAsync(new Uri("/admin/preview/post/notes-for-a-future-post", UriKind.Relative));
        using var notTheirs = await author.GetAsync(new Uri("/admin/preview/post/notes-for-a-future-post", UriKind.Relative));

        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(signedOut.Headers.Location?.AbsolutePath).IsEqualTo("/admin/sign-in");
        await Assert.That(notTheirs.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("/admin/preview/post/no-such-post")]
    [Arguments("/admin/preview/page/notes-for-a-future-post")]
    [Arguments("/admin/preview/other/notes-for-a-future-post")]
    public async Task Unknown_items_and_kinds_are_not_found(string path)
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        using var response = await admin.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_public_item_goes_to_its_address()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        using var post = await admin.GetAsync(new Uri("/admin/preview/post/hello-world", UriKind.Relative));
        using var page = await admin.GetAsync(new Uri("/admin/preview/page/about", UriKind.Relative));

        await Assert.That(post.Headers.Location?.AbsolutePath).IsEqualTo("/blog/hello-world");
        await Assert.That(page.Headers.Location?.AbsolutePath).IsEqualTo("/about");
    }

    [Test]
    public async Task The_editor_links_an_unpublished_item_to_its_preview()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var draft = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fnotes-for-a-future-post.md", UriKind.Relative));
        var published = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fhello-world.md", UriKind.Relative));

        await Assert.That(draft).Contains("<a class=\"btn\" href=\"/admin/preview/post/notes-for-a-future-post\">Preview page <span aria-hidden=\"true\">↗</span></a>");
        await Assert.That(published).DoesNotContain("/admin/preview/post/");
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }
}
