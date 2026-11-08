using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace Weesite.Web.Tests;

/// <summary>Atom and JSON Feed, site-wide and per tag, and their discovery links.</summary>
internal sealed class FeedTests
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    // Every published post in the sample site (fewer than the feeds' cap of 50): the draft and the
    // scheduled post stay out.
    private static readonly int SeedPosts = Directory.GetFiles(Path.Combine(SiteFactory.SeedPath, "content", "posts"), "*.md").Length - 2;

    [Test]
    public async Task Atom_feed_has_the_posts_newest_first_with_absolute_links()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/feed.xml", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        var feed = XDocument.Parse(text).Root!;
        var entries = feed.Elements(Atom + "entry").ToList();

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/atom+xml");
        await Assert.That(text).StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        await Assert.That(feed.Element(Atom + "title")!.Value).IsEqualTo("Example Site");
        await Assert.That(feed.Element(Atom + "updated")!.Value).IsEqualTo("2026-10-12T00:00:00Z");
        await Assert.That(feed.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == "self").Attribute("href")!.Value)
            .IsEqualTo("http://localhost/feed.xml");
        await Assert.That(entries.Count).IsEqualTo(SeedPosts);
        await Assert.That(entries.Take(2).Select(e => e.Element(Atom + "title")!.Value)).IsEquivalentTo(
            ["Moving from Bicep to OpenTofu", "TOML, YAML and the configs in between"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        var notes = entries.Single(e => e.Element(Atom + "id")!.Value == "http://localhost/blog/notes-on-static-rendering");
        await Assert.That(notes.Element(Atom + "id")!.Value).IsEqualTo("http://localhost/blog/notes-on-static-rendering");
        await Assert.That(notes.Element(Atom + "published")!.Value).IsEqualTo("2026-02-03T00:00:00Z");
        await Assert.That(notes.Element(Atom + "author")!.Element(Atom + "name")!.Value).IsEqualTo("Example Author");
        await Assert.That(notes.Elements(Atom + "category").Select(c => c.Attribute("term")!.Value)).IsEquivalentTo(["web", "performance"]);
        var content = notes.Element(Atom + "content")!;
        await Assert.That(content.Attribute("type")!.Value).IsEqualTo("html");
        await Assert.That(content.Value).Contains("src=\"http://localhost/media/");
        await Assert.That(content.Value).DoesNotContain("=\"/");
    }

    [Test]
    public async Task Feed_html_gets_absolute_links_image_sources_and_srcsets()
    {
        const string Html = "<p><a href=\"/about\">a</a> <img src=\"/media/a.png\" srcset=\"/media/a-480w.webp 480w, /media/a-960w.webp 960w\" /> <a href=\"https://x.test/\">x</a></p>";

        var result = Seo.ContentUrls.AbsoluteHtml(Html, "https://example.com");

        await Assert.That(result).IsEqualTo("<p><a href=\"https://example.com/about\">a</a> <img src=\"https://example.com/media/a.png\" srcset=\"https://example.com/media/a-480w.webp 480w, https://example.com/media/a-960w.webp 960w\" /> <a href=\"https://x.test/\">x</a></p>");
    }

    [Test]
    public async Task Json_feed_follows_version_1_1()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/feed.json", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/feed+json");
        await Assert.That(root.GetProperty("version").GetString()).IsEqualTo("https://jsonfeed.org/version/1.1");
        await Assert.That(root.GetProperty("feed_url").GetString()).IsEqualTo("http://localhost/feed.json");
        await Assert.That(root.GetProperty("home_page_url").GetString()).IsEqualTo("http://localhost/");
        var notes = items.Single(i => i.GetProperty("id").GetString() == "http://localhost/blog/notes-on-static-rendering");
        var hello = items.Single(i => i.GetProperty("id").GetString() == "http://localhost/blog/hello-world");

        await Assert.That(items.Count).IsEqualTo(SeedPosts);
        await Assert.That(items[0].GetProperty("id").GetString()).IsEqualTo("http://localhost/blog/moving-from-bicep-to-opentofu");
        await Assert.That(notes.GetProperty("date_published").GetString()).IsEqualTo("2026-02-03T00:00:00Z");
        await Assert.That(notes.GetProperty("image").GetString()).IsEqualTo("http://localhost/media/c0f10a37852f-gradient.jpg");
        await Assert.That(notes.GetProperty("content_html").GetString()).Contains("http://localhost/media/");
        await Assert.That(hello.GetProperty("authors")[0].GetProperty("name").GetString()).IsEqualTo("Example Author");
    }

    [Test]
    [Arguments("/blog/tags/welcome/feed.xml")]
    [Arguments("/blog/tags/welcome/feed.json")]
    public async Task Tag_feeds_carry_only_that_tag(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var text = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        await Assert.That(text).Contains("Example Site · welcome");
        await Assert.That(text).Contains("http://localhost/blog/hello-world");
        await Assert.That(text).DoesNotContain("http://localhost/blog/notes-on-static-rendering");
        await Assert.That(text).Contains($"http://localhost{path}");
    }

    [Test]
    [Arguments("/blog/tags/no-such-tag/feed.xml")]
    [Arguments("/blog/tags/no-such-tag/feed.json")]
    public async Task Unknown_tag_feeds_are_not_found(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Pages_link_the_site_feeds_and_tag_pages_their_own()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var post = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var tag = await client.GetStringAsync(new Uri("/blog/tags/web", UriKind.Relative));

        await Assert.That(post).Contains("<link rel=\"alternate\" type=\"application/atom+xml\" title=\"Example Site\" href=\"http://localhost/feed.xml\" />");
        await Assert.That(post).Contains("<link rel=\"alternate\" type=\"application/feed+json\" title=\"Example Site\" href=\"http://localhost/feed.json\" />");
        await Assert.That(tag).Contains("href=\"http://localhost/blog/tags/web/feed.xml\"");
        await Assert.That(tag).Contains("href=\"/blog/tags/web/feed.json\"");
    }
}
