using System.Net;

namespace Weesite.Web.Tests;

/// <summary>/media/{hash}-{name}: headers, inline only for raster images, names checked.</summary>
internal sealed class MediaTests
{
    [Test]
    public async Task Raster_image_is_served_inline_and_cached_immutably()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/media/b368a21f9e93-sample.png", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options")).Contains("nosniff");
        await Assert.That(response.Headers.CacheControl?.ToString()).IsEqualTo("public, max-age=31536000, immutable");
        await Assert.That(response.Content.Headers.ContentDisposition).IsNull();
        await Assert.That(response.Headers.ETag).IsNotNull();
    }

    [Test]
    public async Task Svg_downloads_instead_of_rendering()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/media/0e3d1bf78702-diagram.svg", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("attachment");
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options")).Contains("nosniff");
    }

    [Test]
    [Arguments("/media/nohash.png")]
    [Arguments("/media/B368A21F9E93-sample.png")]
    [Arguments("/media/b368a21f9e93-..%2F..%2Fsettings%2Fsite.json")]
    [Arguments("/media/b368a21f9e93-missing.png")]
    public async Task Other_names_are_not_found(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
