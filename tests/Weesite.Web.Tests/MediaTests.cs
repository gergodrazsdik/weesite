using System.Net;
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weesite.Content.Sources;
using Weesite.Tests;

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

        // Images stay indexable, for image search (F-0260).
        await Assert.That(response.Headers.Contains("X-Robots-Tag")).IsFalse();
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

        // A download isn't a search result of its own (F-0260).
        await Assert.That(response.Headers.GetValues("X-Robots-Tag").Single()).IsEqualTo("noindex");
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

    [Test]
    public async Task The_file_store_answers_ranges_with_the_length()
    {
        using var client = shared.Factory.CreateClient();

        await CheckRangesAsync(client, await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", Sample)));
    }

    [Test]
    [Category("containers")]
    public async Task The_blob_store_answers_ranges_and_downloads_nothing_for_head_or_304()
    {
        // F-0236: the blob's download stream couldn't seek, so Azure answered every range with
        // the whole file and no length, and HEAD and 304 started a download they threw away.
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var downloads = new Downloads(Sample);
        var options = new BlobClientOptions();
        options.AddPolicy(downloads, HttpPipelinePosition.PerCall);
        var store = new BlobMediaStore(new BlobServiceClient(azurite.GetConnectionString(), options));
        var data = await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", Sample));
        await store.WriteAsync(Sample, data, CancellationToken.None);
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMediaStore>();
            services.AddSingleton<IMediaStore>(store);
        }));
        using var client = factory.CreateClient();

        var etag = await CheckRangesAsync(client, data);
        var before = downloads.Count;
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, MediaPath);
        using var head = await client.SendAsync(headRequest);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, MediaPath);
        conditional.Headers.IfNoneMatch.Add(etag);
        using var notModified = await client.SendAsync(conditional);

        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo(data.Length);
        await Assert.That(notModified.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(downloads.Count).IsEqualTo(before);
    }

    private const string Sample = "b368a21f9e93-sample.png";

    private static readonly Uri MediaPath = new($"/media/{Sample}", UriKind.Relative);

    // The whole file with its length, a range, and a suffix range; returns the tag.
    private static async Task<EntityTagHeaderValue> CheckRangesAsync(HttpClient client, byte[] data)
    {
        using var whole = await client.GetAsync(MediaPath);
        using var middle = new HttpRequestMessage(HttpMethod.Get, MediaPath) { Headers = { Range = new RangeHeaderValue(10, 19) } };
        using var part = await client.SendAsync(middle);
        using var last = new HttpRequestMessage(HttpMethod.Get, MediaPath) { Headers = { Range = new RangeHeaderValue(null, 5) } };
        using var tail = await client.SendAsync(last);

        await Assert.That(whole.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(whole.Headers.AcceptRanges).Contains("bytes");
        await Assert.That(await whole.Content.ReadAsByteArrayAsync()).IsEquivalentTo(data);
        await Assert.That(part.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
        await Assert.That(part.Content.Headers.ContentRange?.ToString()).IsEqualTo($"bytes 10-19/{data.Length}");
        await Assert.That(await part.Content.ReadAsByteArrayAsync()).IsEquivalentTo(data[10..20]);
        await Assert.That(tail.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
        await Assert.That(await tail.Content.ReadAsByteArrayAsync()).IsEquivalentTo(data[^5..]);
        return whole.Headers.ETag!;
    }

    // Counts the downloads (GETs) of one blob.
    private sealed class Downloads(string name) : HttpPipelineSynchronousPolicy
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Method == RequestMethod.Get && message.Request.Uri.Path.EndsWith("/" + name, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
