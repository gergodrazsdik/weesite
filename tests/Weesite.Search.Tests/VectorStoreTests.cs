using Microsoft.Extensions.Configuration;
using Weesite.Content;

namespace Weesite.Search.Tests;

internal sealed class VectorStoreTests
{
    private static ContentItem Post(string slug, string markdown) =>
        new(ContentKind.Post, slug, slug, "en", markdown, $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1) };

    private static ContentCatalog Catalog(params ContentItem[] items) =>
        ContentCatalog.Build(SiteSettings.Empty, items.Select(i => new ParseResult(i, null)));

    [Test]
    public async Task A_build_embeds_only_chunks_it_has_no_stored_vector_for()
    {
        var embedder = new CountingEmbedder();
        var catalog = Catalog(Post("a", "One.\n\n## Two\n\nTwo."), Post("b", "Three."));
        using var first = SearchIndex.Build(catalog, embedder);
        var stored = first.ItemVectors.Values.SelectMany(i => i).ToDictionary(v => v.Key, v => v.Value);

        using var second = SearchIndex.Build(Catalog(Post("a", "One.\n\n## Two\n\nTwo, changed."), Post("b", "Three.")), embedder, stored.GetValueOrDefault);

        await Assert.That(first.EmbeddedCount).IsEqualTo(3);
        await Assert.That(second.EmbeddedCount).IsEqualTo(1);
        await Assert.That(second.ItemVectors.Keys).IsEquivalentTo(["post/a", "post/b"]);
        await Assert.That(second.ItemVectors["post/a"].Count).IsEqualTo(2);
    }

    [Test]
    public async Task The_provider_stores_new_vectors_and_a_new_provider_reuses_them()
    {
        var store = new MemoryVectorStore();
        var content = new ContentCatalogProvider();
        content.Set(Catalog(Post("a", "One."), Post("b", "Two.")));
        var embedder = new CountingEmbedder();

        using (var first = new SearchIndexProvider(content, embedder, store: store))
        {
            await first.LoadVectorsAsync(CancellationToken.None);
            await Assert.That(first.Current.EmbeddedCount).IsEqualTo(2);
            await first.Saving;
        }

        using var second = new SearchIndexProvider(content, embedder, store: store);
        await second.LoadVectorsAsync(CancellationToken.None);

        await Assert.That(second.Current.EmbeddedCount).IsEqualTo(0);
        await Assert.That((await store.LoadAsync(embedder.ModelId, CancellationToken.None)).Keys).IsEquivalentTo(["post/a", "post/b"]);
    }

    [Test]
    public async Task Only_items_whose_chunks_changed_are_saved_again()
    {
        var store = new RecordingStore();
        var content = new ContentCatalogProvider();
        content.Set(Catalog(Post("a", "One."), Post("b", "Two.")));
        using var provider = new SearchIndexProvider(content, new CountingEmbedder(), store: store);
        _ = provider.Current;
        await provider.Saving;
        store.Saved.Clear();

        content.Set(Catalog(Post("a", "One, edited."), Post("b", "Two.")));
        await provider.RefreshAsync();
        await provider.Saving;

        await Assert.That(store.Saved).IsEquivalentTo(["post/a"]);
    }

    [Test]
    public async Task The_provider_keeps_only_the_vectors_of_the_current_passages()
    {
        // F-0064: an edited passage's old vector stayed until a restart, so memory grew with edits.
        var content = new ContentCatalogProvider();
        content.Set(Catalog(Post("a", "One."), Post("b", "Two.")));
        using var provider = new SearchIndexProvider(content, new CountingEmbedder(), store: new MemoryVectorStore());
        _ = provider.Current;
        await provider.Saving;
        var before = provider.KnownVectors;

        for (var i = 0; i < 5; i++)
        {
            content.Set(Catalog(Post("a", $"One, edit {i}."), Post("b", "Two.")));
            await provider.RefreshAsync();
            await provider.Saving;
        }

        await Assert.That(before).IsEqualTo(2);
        await Assert.That(provider.KnownVectors).IsEqualTo(2);
    }

    [Test]
    public async Task Vectors_of_another_model_are_not_used()
    {
        var store = new MemoryVectorStore();
        var content = new ContentCatalogProvider();
        content.Set(Catalog(Post("a", "One.")));
        using (var other = new SearchIndexProvider(content, new CountingEmbedder("other-model"), store: store))
        {
            _ = other.Current;
            await other.Saving;
        }

        using var provider = new SearchIndexProvider(content, new CountingEmbedder(), store: store);
        await provider.LoadVectorsAsync(CancellationToken.None);

        await Assert.That(provider.Current.EmbeddedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Vectors_read_after_a_build_began_are_used_by_the_next_one()
    {
        // F-0111: a request built the index between a reload and the vectors' load, and the
        // vectors the re-index job had stored were never used until the next edit.
        var store = new MemoryVectorStore();
        var content = new ContentCatalogProvider();
        content.Set(Catalog(Post("a", "One.")));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("Search:EmbedLimit", "0")]).Build();
        using var provider = new SearchIndexProvider(content, new CountingEmbedder(), store: store, configuration: configuration);
        await Assert.That(provider.Current.MissingCount).IsEqualTo(1);

        // The re-index job stores the vector; a build starts before this replica reads it.
        using (var job = new SearchIndexProvider(content, new CountingEmbedder(), store: store))
        {
            _ = job.Current;
            await job.Saving;
        }

        content.Set(Catalog(Post("a", "One.")));
        await provider.RefreshAsync();
        await provider.LoadVectorsAsync(CancellationToken.None);
        var index = await provider.RefreshAsync();

        await Assert.That(index.MissingCount).IsEqualTo(0);
        await Assert.That(index.HasVectors).IsTrue();
    }

    [Test]
    public async Task A_changed_catalog_is_built_in_the_background_while_the_previous_index_serves()
    {
        // F-0102: the build ran inside the request that first needed it, under a lock every other
        // request waited on.
        var embedder = new GatedEmbedder();
        var content = new ContentCatalogProvider();
        content.Set(Catalog(Post("a", "One.")));
        using var provider = new SearchIndexProvider(content, embedder);
        var first = provider.Current;

        embedder.Gate.Reset();
        content.Set(Catalog(Post("a", "One."), Post("b", "Two.")));
        var during = provider.Current;
        var again = provider.Current;
        embedder.Gate.Set();
        var built = await provider.RefreshAsync();

        await Assert.That(during).IsSameReferenceAs(first);
        await Assert.That(again).IsSameReferenceAs(first);
        await Assert.That(built).IsNotSameReferenceAs(first);
        await Assert.That(provider.Current).IsSameReferenceAs(built);
        await Assert.That(built.Search("two").Single().Item.Slug).IsEqualTo("b");
    }

    [Test]
    public async Task Item_keys_name_only_posts_and_pages_with_valid_slugs()
    {
        await Assert.That(VectorKeys.IsItem("post/hello-world")).IsTrue();
        await Assert.That(VectorKeys.IsItem("page/about")).IsTrue();
        await Assert.That(VectorKeys.IsItem("post/../x")).IsFalse();
        await Assert.That(VectorKeys.IsItem("post/")).IsFalse();
        await Assert.That(VectorKeys.IsItem("media/a")).IsFalse();
    }

    // Gives each text a different unit vector and counts the calls.
    private sealed class CountingEmbedder(string model = "test-model") : IEmbedder
    {
        public string ModelId => model;

        public int Dimensions => 4;

        public float[] EmbedPassage(string text)
        {
            var hash = (uint)string.GetHashCode(text, StringComparison.Ordinal);
            var vector = new[] { (hash & 0xff) + 1f, ((hash >> 8) & 0xff) + 1f, ((hash >> 16) & 0xff) + 1f, (hash >> 24) + 1f };
            Vectors.Normalize(vector);
            return vector;
        }

        public float[] EmbedQuery(string text) => EmbedPassage(text);
    }

    // Embeds only while its gate is open, so a test can hold a build.
    private sealed class GatedEmbedder : IEmbedder
    {
        private readonly CountingEmbedder _inner = new();

        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public string ModelId => _inner.ModelId;

        public int Dimensions => _inner.Dimensions;

        public float[] EmbedPassage(string text)
        {
            Gate.Wait(TimeSpan.FromSeconds(30));
            return _inner.EmbedPassage(text);
        }

        public float[] EmbedQuery(string text) => _inner.EmbedQuery(text);
    }

    private sealed class RecordingStore : IVectorStore
    {
        public List<string> Saved { get; } = [];

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>>> LoadAsync(string model, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>>>(new Dictionary<string, IReadOnlyDictionary<string, float[]>>());

        public Task SaveAsync(string model, string item, IReadOnlyDictionary<string, float[]> vectors, CancellationToken cancellationToken)
        {
            lock (Saved)
            {
                Saved.Add(item);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListAsync(string model, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteAsync(string model, string item, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
