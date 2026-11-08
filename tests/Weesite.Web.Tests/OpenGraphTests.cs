using System.Buffers.Binary;
using System.Net;
using System.Text.RegularExpressions;

namespace Weesite.Web.Tests;

/// <summary>Open Graph tags on every page, and the generated cards at /og/{key}.png.</summary>
internal sealed partial class OpenGraphTests
{
    [Test]
    public async Task Post_has_open_graph_and_twitter_tags_with_a_generated_card()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));

        await Assert.That(html).Contains("<meta property=\"og:type\" content=\"article\" />");
        await Assert.That(html).Contains("<meta property=\"og:title\" content=\"Hello, world\" />");
        await Assert.That(html).Contains("<meta property=\"og:site_name\" content=\"Example Site\" />");
        await Assert.That(html).Contains("<meta property=\"og:url\" content=\"http://localhost/blog/hello-world\" />");
        await Assert.That(html).Contains("<meta name=\"description\" content=\"The first post of the sample site.\" />");
        await Assert.That(html).Contains("<meta property=\"og:image:width\" content=\"1200\" />");
        await Assert.That(html).Contains("<meta property=\"article:published_time\" content=\"2026-01-15\" />");
        await Assert.That(html).Contains("<meta property=\"article:tag\" content=\"welcome\" />");
        await Assert.That(html).Contains("<meta name=\"twitter:card\" content=\"summary_large_image\"");
        await Assert.That(ImageUrl(html)).Matches("^http://localhost/og/[0-9a-f]{16}\\.png$");
    }

    [Test]
    public async Task Post_with_its_own_image_uses_it()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/blog/notes-on-static-rendering", UriKind.Relative));

        await Assert.That(ImageUrl(html)).IsEqualTo("http://localhost/media/c0f10a37852f-gradient.jpg");
        await Assert.That(html).Contains("<meta property=\"og:image:width\" content=\"1800\" />");
        await Assert.That(html).Contains("<meta property=\"og:image:alt\" content=\"A blue gradient, light at the top and dark at the bottom.\" />");
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog")]
    [Arguments("/blog/tags")]
    [Arguments("/blog/tags/web")]
    [Arguments("/about")]
    public async Task Every_page_has_an_open_graph_image(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        await Assert.That(ImageUrl(html)).StartsWith("http://localhost/");
        await Assert.That(html).Contains("<meta property=\"og:type\" content=\"website\" />");
    }

    [Test]
    public async Task Card_is_a_1200_by_630_png_cached_immutably()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));

        using var response = await client.GetAsync(new Uri(ImageUrl(html)));
        var png = await response.Content.ReadAsByteArrayAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
        await Assert.That(response.Headers.CacheControl?.ToString()).IsEqualTo("public, max-age=31536000, immutable");
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options")).Contains("nosniff");
        await Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4))).IsEqualTo(1200);
        await Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4))).IsEqualTo(630);
    }

    [Test]
    [Arguments("/og/0123456789abcdef.png")]
    [Arguments("/og/not-a-key.png")]
    [Arguments("/og/0123456789ABCDEF.png")]
    public async Task Unknown_cards_are_not_found(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Absolute_urls_follow_the_forwarded_scheme()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/blog/hello-world", UriKind.Relative));
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(ImageUrl(html)).StartsWith("https://localhost/og/");
        await Assert.That(html).Contains("<meta property=\"og:url\" content=\"https://localhost/blog/hello-world\" />");
    }

    private static string ImageUrl(string html) => OgImage().Match(html).Groups[1].Value;

    [GeneratedRegex("<meta property=\"og:image\" content=\"([^\"]+)\"")]
    private static partial Regex OgImage();
}
