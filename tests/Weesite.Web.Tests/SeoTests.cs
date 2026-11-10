using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content;
using Weesite.Web.Seo;

namespace Weesite.Web.Tests;

/// <summary>Canonical links, JSON-LD, robots.txt and the sitemap.</summary>
internal sealed partial class SeoTests
{
    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private static readonly string[] SitePages = ["http://localhost/", "http://localhost/blog", "http://localhost/blog/tags"];

    [Test]
    [Arguments("/", "http://localhost/")]
    [Arguments("/blog", "http://localhost/blog")]
    [Arguments("/blog/hello-world?utm_source=x", "http://localhost/blog/hello-world")]
    [Arguments("/blog/tags/web", "http://localhost/blog/tags/web")]
    [Arguments("/about", "http://localhost/about")]
    public async Task Pages_have_a_canonical_link(string path, string canonical)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        await Assert.That(html).Contains($"<link rel=\"canonical\" href=\"{canonical}\" />");
    }

    [Test]
    public async Task Base_url_setting_makes_every_absolute_url_canonical()
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b.UseSetting("Site:BaseUrl", "https://example.com/"));
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var robots = await client.GetStringAsync(new Uri("/robots.txt", UriKind.Relative));

        await Assert.That(html).Contains("<link rel=\"canonical\" href=\"https://example.com/blog/hello-world\" />");
        await Assert.That(html).Contains("<meta property=\"og:image\" content=\"https://example.com/og/");
        await Assert.That(html).Contains("href=\"https://example.com/feed.xml\"");
        await Assert.That(robots).Contains("Sitemap: https://example.com/sitemap.xml");
    }

    [Test]
    [Arguments("GET", "/blog/hello-world?page=2", HttpStatusCode.MovedPermanently)]
    [Arguments("GET", "/", HttpStatusCode.MovedPermanently)]
    [Arguments("POST", "/search", HttpStatusCode.PermanentRedirect)]
    public async Task Www_redirects_to_the_canonical_domain(string method, string path, HttpStatusCode status)
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b.UseSetting("Site:BaseUrl", "https://example.com"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://www.example.com") });

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("https://example.com" + path);
    }

    [Test]
    [Arguments("", "example.com")]
    [Arguments("https://example.com", "ca-weesite-web.example.azurecontainerapps.io")]
    [Arguments("https://example.com", "example.com")]
    [Arguments("https://www.example.com", "www.example.com")]
    public async Task Other_hosts_are_served_as_they_are(string baseUrl, string host)
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b.UseSetting("Site:BaseUrl", baseUrl));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://" + host) });

        using var response = await client.GetAsync(new Uri("/blog/hello-world", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("example.com")]
    [Arguments("ftp://example.com")]
    [Arguments("https://example.com/?a=b")]
    public async Task Base_url_must_be_an_absolute_http_url(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("Site:BaseUrl", value)]).Build();

        await Assert.That(() => new SiteUrls(configuration)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_post_without_a_summary_is_described_by_the_start_of_its_text()
    {
        // T-0119.
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "no-summary.md"), "---\ntitle: No summary\nslug: no-summary\ndate: 2026-01-20\n---\nA post that says what it's about in its **first** sentence.\n");
        await using var factory = new SiteFactory(copy.Root);
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/no-summary", UriKind.Relative));

        await Assert.That(html).Contains("<meta name=\"description\" content=\"A post that says what it&#x27;s about in its first sentence.\" />");
        await Assert.That(html).Contains("\"description\":\"A post that says what it");
    }

    [Test]
    public async Task Post_has_blog_posting_json_ld_with_its_author()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        using var json = JsonDocument.Parse(JsonLdOf(html));
        var root = json.RootElement;

        await Assert.That(root.GetProperty("@type").GetString()).IsEqualTo("BlogPosting");
        await Assert.That(root.GetProperty("headline").GetString()).IsEqualTo("Hello, world");
        await Assert.That(root.GetProperty("url").GetString()).IsEqualTo("http://localhost/blog/hello-world");
        await Assert.That(root.GetProperty("datePublished").GetString()).IsEqualTo("2026-01-15T00:00:00Z");
        await Assert.That(root.GetProperty("author").GetProperty("@type").GetString()).IsEqualTo("Person");
        await Assert.That(root.GetProperty("author").GetProperty("name").GetString()).IsEqualTo("Example Author");
        await Assert.That(root.GetProperty("author").GetProperty("url").GetString()).IsEqualTo("https://example.com/");
        await Assert.That(root.GetProperty("keywords").EnumerateArray().Select(k => k.GetString() ?? "")).IsEquivalentTo(["welcome", "weesite"]);
        await Assert.That(root.GetProperty("image").GetString()).StartsWith("http://localhost/og/");
    }

    [Test]
    [Arguments("/", "WebSite")]
    [Arguments("/about", "WebPage")]
    public async Task Home_and_pages_have_json_ld(string path, string type)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));
        using var json = JsonDocument.Parse(JsonLdOf(html));

        await Assert.That(json.RootElement.GetProperty("@type").GetString()).IsEqualTo(type);
    }

    [Test]
    public async Task Json_ld_text_cannot_close_the_script_element()
    {
        var item = new ContentItem(ContentKind.Page, "x", "</script><script>alert(1)</script>", "en", "", "x.md");

        var json = JsonLd.For(SiteSettings.Empty, item, false, "https://example.com/x", "https://example.com", "https://example.com/og/x.png")!;

        await Assert.That(json).DoesNotContain("<");
        await Assert.That(JsonElement.Parse(json).GetProperty("name").GetString()).IsEqualTo(item.Title);
    }

    [Test]
    public async Task Robots_allows_every_crawler_and_points_at_the_sitemap()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/robots.txt", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        await Assert.That(text).IsEqualTo("User-agent: *\nAllow: /\nDisallow: /admin\n\nSitemap: http://localhost/sitemap.xml\n");
    }

    [Test]
    public async Task Robots_disallows_everything_with_noindex()
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b.UseSetting("Site:NoIndex", "true"));
        using var client = factory.CreateClient();

        var text = await client.GetStringAsync(new Uri("/robots.txt", UriKind.Relative));

        await Assert.That(text).IsEqualTo("User-agent: *\nDisallow: /\n");
    }

    [Test]
    public async Task Sitemap_lists_home_blog_posts_pages_and_tags()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/sitemap.xml", UriKind.Relative));
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var urls = xml.Root!.Elements(SitemapNs + "url")
            .ToDictionary(u => u.Element(SitemapNs + "loc")!.Value, u => u.Element(SitemapNs + "lastmod")?.Value);

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/xml");
        // Every post, page and tag of the sample site, plus home, blog and the tag index.
        var catalog = factory.Services.GetRequiredService<ContentCatalogProvider>().Current;
        var expected = SitePages
            .Concat(catalog.Posts.Select(p => $"http://localhost/blog/{p.Slug}"))
            .Concat(catalog.Pages.Select(p => $"http://localhost/{p.Slug}"))
            .Concat(catalog.Tags.Keys.Select(t => $"http://localhost/blog/tags/{t}"));
        await Assert.That(urls.Keys).IsEquivalentTo(expected);
        await Assert.That(urls["http://localhost/blog/hello-world"]).IsEqualTo("2026-01-15");
        await Assert.That(urls["http://localhost/blog/encrypting-opentofu-state-with-key-vault"]).IsEqualTo("2026-05-01");
        await Assert.That(urls["http://localhost/"]).IsEqualTo("2026-10-12");
        await Assert.That(urls["http://localhost/about"]).IsNull();
    }

    [Test]
    public async Task Not_found_is_still_a_404()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/sitemap.xml/extra", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static string JsonLdOf(string html) => JsonLdScript().Match(html).Groups[1].Value;

    [GeneratedRegex("<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex JsonLdScript();
}
