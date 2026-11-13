using System.Net;

namespace Weesite.Web.Tests;

/// <summary>A post and a tag whose slugs aren't ASCII (F-0091 L1): served, linked and percent-encoded where URLs must be ASCII.</summary>
internal sealed class UnicodeSlugTests
{
    [Test]
    public async Task A_post_and_a_tag_in_another_script_are_served_and_linked()
    {
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "content", "posts", "első-bejegyzés.md"),
            "---\ntitle: Első bejegyzés\nslug: első-bejegyzés\ndate: 2026-11-01\nlang: hu\ntags: [ékezet]\nredirect_from: [régi-cím]\n---\n\nSzöveg.\n");
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        using var post = await client.GetAsync(new Uri("/blog/első-bejegyzés", UriKind.Relative));
        using var tag = await client.GetAsync(new Uri("/blog/tags/ékezet", UriKind.Relative));
        var html = await post.Content.ReadAsStringAsync();
        var sitemap = await client.GetStringAsync(new Uri("/sitemap.xml", UriKind.Relative));
        var feed = await client.GetStringAsync(new Uri("/feed.xml", UriKind.Relative));

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(tag.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("Első bejegyzés");
        await Assert.That(html).Contains("/blog/els%C5%91-bejegyz%C3%A9s\" />");
        await Assert.That(sitemap).Contains("/blog/els%C5%91-bejegyz%C3%A9s</loc>");
        await Assert.That(sitemap).Contains("/blog/tags/%C3%A9kezet</loc>");
        await Assert.That(feed).Contains("/blog/els%C5%91-bejegyz%C3%A9s");
        await Assert.That(feed).DoesNotContain("/blog/első-bejegyzés");

        // A renamed post's old address redirects with an ASCII Location (F-0113): Kestrel refuses
        // any other, though the test server doesn't check.
        using var redirected = await site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .GetAsync(new Uri("/blog/régi-cím", UriKind.Relative));
        await Assert.That(redirected.Headers.Location?.OriginalString).IsEqualTo("/blog/els%C5%91-bejegyz%C3%A9s");

        // The Markdown versions, by suffix and by negotiation.
        var markdown = await client.GetStringAsync(new Uri("/blog/első-bejegyzés.md", UriKind.Relative));
        using var negotiated = new HttpRequestMessage(HttpMethod.Get, new Uri("/blog/első-bejegyzés", UriKind.Relative));
        negotiated.Headers.Accept.ParseAdd("text/markdown");
        using var negotiatedResponse = await client.SendAsync(negotiated);
        await Assert.That(markdown).Contains("# Első bejegyzés");
        await Assert.That(negotiatedResponse.Content.Headers.ContentType?.MediaType).IsEqualTo("text/markdown");
    }
}
