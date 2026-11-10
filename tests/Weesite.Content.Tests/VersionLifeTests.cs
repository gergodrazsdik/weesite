using Azure.Storage.Blobs;
using Testcontainers.Azurite;
using Weesite.Content.Sources;

namespace Weesite.Content.Tests;

/// <summary>Which versions the editor lists (T-0110, F-0054): those of the file's current life.</summary>
internal sealed class VersionLifeTests
{
    private static Revision At(int day, bool current = false) =>
        new($"v{day}", new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero), current);

    [Test]
    public async Task Versions_without_a_life_id_are_all_listed()
    {
        // F-0054: a filter by each version's own creation time left only the current one.
        var listed = BlobContentStore.OfCurrentLife([(At(1), null), (At(2), null), (At(3, current: true), null)]);

        await Assert.That(listed.Select(r => r.VersionId)).IsEquivalentTo(["v3", "v2", "v1"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_file_written_again_after_a_delete_lists_only_its_new_versions()
    {
        var listed = BlobContentStore.OfCurrentLife([(At(1), null), (At(2), "old"), (At(3), "old"), (At(4), "new"), (At(5, current: true), "new")]);

        await Assert.That(listed.Select(r => r.VersionId)).IsEquivalentTo(["v5", "v4"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_deleted_file_lists_nothing_as_current_and_keeps_its_versions_for_by_hand_restores()
    {
        var listed = BlobContentStore.OfCurrentLife([(At(1), "a"), (At(2), "a")]);

        await Assert.That(listed.Count).IsEqualTo(2);
    }

    [Test]
    [Category("containers")]
    public async Task A_new_file_gets_a_life_id_saves_keep_it_and_a_new_file_after_a_delete_gets_another()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        var store = new BlobContentStore(blobs);
        var blob = blobs.GetBlobContainerClient(ContentContainers.Content).GetBlobClient("posts/a.md");
        async Task<string?> LifeAsync() => (await blob.GetPropertiesAsync()).Value.Metadata.TryGetValue(BlobContentStore.LifeKey, out var life) ? life : null;

        var first = await store.WriteAsync("posts/a.md", "one", null, CancellationToken.None);
        var life = await LifeAsync();
        var second = await store.WriteAsync("posts/a.md", "two", first, CancellationToken.None);
        var kept = await LifeAsync();
        var stale = await store.WriteAsync("posts/a.md", "three", first, CancellationToken.None);
        await store.DeleteAsync("posts/a.md", second!, CancellationToken.None);
        await store.WriteAsync("posts/a.md", "again", null, CancellationToken.None);
        var renewed = await LifeAsync();

        await Assert.That(life).IsNotNull();
        await Assert.That(kept).IsEqualTo(life);
        await Assert.That(stale).IsNull();
        await Assert.That(renewed).IsNotNull();
        await Assert.That(renewed).IsNotEqualTo(life);
    }
}
