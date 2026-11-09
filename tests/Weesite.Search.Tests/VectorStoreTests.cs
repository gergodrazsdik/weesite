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
        _ = provider.Current;
        await provider.Saving;

        await Assert.That(store.Saved).IsEquivalentTo(["post/a"]);
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
