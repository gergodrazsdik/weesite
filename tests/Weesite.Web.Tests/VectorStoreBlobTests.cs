using Azure.Storage.Blobs;
using Weesite.Search;
using Weesite.Tests;

namespace Weesite.Web.Tests;

internal sealed class VectorStoreBlobTests
{
    [Test]
    [Category("containers")]
    public async Task Blob_vectors_round_trip_per_model_and_item()
    {
        await using var azurite = TestAzurite.Create();
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

    [Test]
    [Category("containers")]
    public async Task A_load_skips_unreadable_files_and_reads_again_only_what_changed()
    {
        // F-0111: one bad file dropped every stored vector, and every reload downloaded them all.
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        var store = new BlobVectorStore(blobs);
        await store.SaveAsync("model-a", "post/first", new Dictionary<string, float[]> { ["h1"] = [1f, 0f] }, CancellationToken.None);
        await store.SaveAsync("model-a", "post/second", new Dictionary<string, float[]> { ["h2"] = [0f, 1f] }, CancellationToken.None);
        var container = blobs.GetBlobContainerClient("index");
        await container.GetBlobClient("model-a/post/broken.json").UploadAsync(BinaryData.FromString("{not json"));
        await container.GetBlobClient("model-a/post/empty.json").UploadAsync(BinaryData.FromString("""{"model":"model-a","dimensions":2}"""));
        await container.GetBlobClient("model-a/post/base64.json").UploadAsync(BinaryData.FromString("""{"model":"model-a","dimensions":2,"chunks":{"h":"%%"}}"""));

        var first = await store.LoadAsync("model-a", CancellationToken.None);
        await store.SaveAsync("model-a", "post/second", new Dictionary<string, float[]> { ["h3"] = [0.6f, 0.8f] }, CancellationToken.None);
        await store.DeleteAsync("model-a", "post/first", CancellationToken.None);
        var second = await store.LoadAsync("model-a", CancellationToken.None);

        await Assert.That(first["post/first"]["h1"]).IsEquivalentTo([1f, 0f]);
        await Assert.That(first["post/second"].Keys).IsEquivalentTo(["h2"]);
        await Assert.That(first.ContainsKey("post/broken")).IsFalse();
        await Assert.That(first.ContainsKey("post/base64")).IsFalse();
        await Assert.That(first["post/empty"]).IsEmpty();
        await Assert.That(second.ContainsKey("post/first")).IsFalse();
        await Assert.That(second["post/second"].Keys).IsEquivalentTo(["h3"]);
    }
}
