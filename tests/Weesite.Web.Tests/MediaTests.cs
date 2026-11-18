using System.Net;

namespace Weesite.Web.Tests;

/// <summary>/media/{hash}-{name}: headers, inline only for raster images, names checked.</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class MediaTests(SharedSite shared)
{
    [Test]
    public async Task Raster_image_is_served_inline_and_cached_immutably()
    {
        var factory = shared.Factory;
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
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/media/0e3d1bf78702-diagram.svg", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("attachment");
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options")).Contains("nosniff");
    }

    [Test]
    [Arguments("0123456789ab-data.json", "application/json")]
    [Arguments("0123456789ab-config.yaml", "application/yaml")]
    [Arguments("0123456789ab-config.yml", "application/yaml")]
    [Arguments("0123456789ab-cargo.toml", "application/toml")]
    [Arguments("0123456789ab-notes.md", "text/markdown")]
    public async Task Data_files_get_their_registered_types(string name, string type) =>
        await Assert.That(MediaEndpoint.ContentTypeOf(name)).IsEqualTo(type);

    [Test]
    [Arguments("/media/nohash.png")]
    [Arguments("/media/B368A21F9E93-sample.png")]
    [Arguments("/media/b368a21f9e93-..%2F..%2Fsettings%2Fsite.json")]
    [Arguments("/media/b368a21f9e93-missing.png")]
    public async Task Other_names_are_not_found(string path)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
