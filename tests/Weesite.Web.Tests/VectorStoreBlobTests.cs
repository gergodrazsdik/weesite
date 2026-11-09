using Azure.Storage.Blobs;
using Weesite.Search;

namespace Weesite.Web.Tests;

internal sealed class VectorStoreBlobTests
{
    [Test]
    [Category("containers")]
    public async Task Blob_vectors_round_trip_per_model_and_item()
    {
        await using var azurite = new Testcontainers.Azurite.AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        var store = new BlobVectorStore(blobs);

        // Nothing stored yet, not even the container.
        await Assert.That(await store.LoadAsync("model-a", CancellationToken.None)).IsEmpty();

        float[] one = [0.6f, 0.8f], two = [1f, 0f];
        await store.SaveAsync("model-a", "post/first", new Dictionary<string, float[]> { ["h1"] = one, ["h2"] = two }, CancellationToken.None);
        await store.SaveAsync("model-a", "page/about", new Dictionary<string, float[]> { ["h3"] = two }, CancellationToken.None);
        await store.SaveAsync("model-b", "post/first", new Dictionary<string, float[]> { ["h1"] = [1f, 0f, 0f] }, CancellationToken.None);

        var loaded = await store.LoadAsync("model-a", CancellationToken.None);
        await Assert.That(loaded.Keys).IsEquivalentTo(["post/first", "page/about"]);
        await Assert.That(loaded["post/first"]["h1"]).IsEquivalentTo(one);
        await Assert.That(loaded["post/first"]["h2"]).IsEquivalentTo(two);
        await Assert.That((await blobs.GetBlobContainerClient("index").GetBlobClient("model-a/post/first.json").ExistsAsync()).Value).IsTrue();

        await store.DeleteAsync("model-a", "post/first", CancellationToken.None);
        await Assert.That((await store.LoadAsync("model-a", CancellationToken.None)).Keys).IsEquivalentTo(["page/about"]);
        await Assert.That((await store.LoadAsync("model-b", CancellationToken.None)).Keys).IsEquivalentTo(["post/first"]);
        await Assert.That(async () => await store.SaveAsync("model-a", "../evil", new Dictionary<string, float[]>(), CancellationToken.None)).Throws<ArgumentException>();
    }
}
