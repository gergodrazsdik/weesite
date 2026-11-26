using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Weesite.Web.Tests;

/// <summary>Atom and JSON Feed, site-wide and per tag, and their discovery links.</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class FeedTests(SharedSite shared)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    [Test]
    [Arguments("xml")]
    [Arguments("json")]
    public async Task Feeds_ask_not_to_be_listed_in_search(string extension)
    {
        // F-0260: Search Console reported every feed as "crawled, not indexed".
        var factory = shared.Factory;
        using var client = factory.CreateClient();
        var tag = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current.Tags.Keys.First();

        using var site = await client.GetAsync(new Uri($"/feed.{extension}", UriKind.Relative));
        using var perTag = await client.GetAsync(new Uri($"/blog/tags/{Uri.EscapeDataString(tag)}/feed.{extension}", UriKind.Relative));

        await Assert.That(site.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(perTag.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(site.Headers.GetValues("X-Robots-Tag").Single()).IsEqualTo("noindex");
        await Assert.That(perTag.Headers.GetValues("X-Robots-Tag").Single()).IsEqualTo("noindex");
    }

    [Test]
    public async Task A_site_kept_out_of_search_keeps_nofollow_on_its_feeds()
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(builder => builder.UseSetting("Site:NoIndex", "true"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/feed.xml", UriKind.Relative));

        await Assert.That(response.Headers.GetValues("X-Robots-Tag").Single()).IsEqualTo("noindex, nofollow");
    }

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
        var catalog = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current;
        var ids = entries.Select(e => e.Element(Atom + "id")!.Value).ToList();
        await Assert.That(ids).IsEquivalentTo(Newest(catalog), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Unpublished(catalog)).IsNotEmpty();
        await Assert.That(ids).DoesNotContain(id => Unpublished(catalog).Contains(id));
        var newest = catalog.Posts.Take(2).Select(p => p.Title).ToList();
        await Assert.That(entries.Take(2).Select(e => e.Element(Atom + "title")!.Value)).IsEquivalentTo(newest, TUnit.Assertions.Enums.CollectionOrdering.Matching);

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

        var result = Seo.ContentUrls.AbsoluteHtml(Html, "https://example.com/blog/a-post");

        await Assert.That(result).IsEqualTo("<p><a href=\"https://example.com/about\">a</a> <img src=\"https://example.com/media/a.png\" srcset=\"https://example.com/media/a-480w.webp 480w, https://example.com/media/a-960w.webp 960w\" /> <a href=\"https://x.test/\">x</a></p>");

        // F-0116: protocol-relative and relative links, and fragments, resolve against the post.
        var other = Seo.ContentUrls.AbsoluteHtml("<a href=\"//cdn.test/x\">1</a><a href=\"notes\">2</a><a href=\"#part\">3</a><a href=\"mailto:a@example.com\">4</a><a href=\"/a?x=1&amp;y=2\">5</a>", "https://example.com/blog/a-post");
        await Assert.That(other).IsEqualTo("<a href=\"https://cdn.test/x\">1</a><a href=\"https://example.com/blog/notes\">2</a><a href=\"https://example.com/blog/a-post#part\">3</a><a href=\"mailto:a@example.com\">4</a><a href=\"https://example.com/a?x=1&amp;y=2\">5</a>");
    }

    [Test]
    public async Task Json_feed_follows_version_1_1()
    {
        var factory = shared.Factory;
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

        var catalog = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current;
        var ids = items.Select(i => i.GetProperty("id").GetString()!).ToList();
        await Assert.That(ids).IsEquivalentTo(Newest(catalog), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(ids).DoesNotContain(id => Unpublished(catalog).Contains(id));
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
        var factory = shared.Factory;
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
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Characters_xml_cannot_carry_are_left_out_of_the_atom_feed()
    {
        // Terminal output pasted with its colour codes, a form feed in the title (F-0234).
        const string Post = "---\ntitle: \"Build\\fnotes\"\nslug: build-notes\ndate: 2026-10-20\nsummary: \"Colours\\e[0m\"\ntags: [web]\n---\n\n```text\n\u001b[32mok\u001b[0m\n```\n";
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "build-notes.md"), Post);
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        using var response = await client.GetAsync(new Uri("/feed.xml", UriKind.Relative));
        using var tag = await client.GetAsync(new Uri("/blog/tags/web/feed.xml", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(tag.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var entry = XDocument.Parse(text).Root!.Elements(Atom + "entry").Single(e => e.Element(Atom + "id")!.Value == "http://localhost/blog/build-notes");
        await Assert.That(entry.Element(Atom + "title")!.Value).IsEqualTo("Buildnotes");
        await Assert.That(entry.Element(Atom + "summary")!.Value).IsEqualTo("Colours[0m");
        await Assert.That(entry.Element(Atom + "content")!.Value).Contains("[32mok[0m");
    }

    [Test]
    public async Task Text_xml_cannot_carry_is_removed_and_the_rest_kept()
    {
        await Assert.That(Seo.XmlResults.Safe("a\u0000b\u001bc\td\ne\uFFFEf")).IsEqualTo("abc\td\nef");
        await Assert.That(Seo.XmlResults.Safe("emoji 🎉 lone \uD800 end")).IsEqualTo("emoji 🎉 lone  end");
        const string Clean = "plain text";
        await Assert.That(Seo.XmlResults.Safe(Clean)).IsSameReferenceAs(Clean);
    }

    [Test]
    public async Task Pages_link_the_site_feeds_and_tag_pages_their_own()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var post = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var tag = await client.GetStringAsync(new Uri("/blog/tags/web", UriKind.Relative));

        await Assert.That(post).Contains("<link rel=\"alternate\" type=\"application/atom+xml\" title=\"Example Site\" href=\"http://localhost/feed.xml\" />");
        await Assert.That(post).Contains("<link rel=\"alternate\" type=\"application/feed+json\" title=\"Example Site\" href=\"http://localhost/feed.json\" />");
        await Assert.That(tag).Contains("href=\"http://localhost/blog/tags/web/feed.xml\"");
        await Assert.That(tag).Contains("href=\"/blog/tags/web/feed.json\"");
    }

    // The feed's addresses: the newest published posts, as many as a feed holds.
    private static List<string> Newest(Weesite.Content.ContentCatalog catalog) =>
        [.. catalog.Posts.Take(Weesite.Web.Seo.FeedEndpoints.MaxItems).Select(p => "http://localhost" + Weesite.Web.Seo.ContentUrls.PathOf(p))];

    // The drafts' and scheduled posts' addresses, which a feed never carries.
    private static HashSet<string> Unpublished(Weesite.Content.ContentCatalog catalog) =>
        [.. catalog.Unpublished.Where(p => p.Kind == Weesite.Content.ContentKind.Post).Select(p => "http://localhost" + Weesite.Web.Seo.ContentUrls.PathOf(p))];
}
