using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content;

namespace Weesite.Search.Tests;

/// <summary>
/// The search index while the embedding model is still loading (ADR 0012, T-0191): the first index
/// is on keywords, so the replica can report ready, and the embeddings follow once the model is in.
/// </summary>
internal sealed class LoadingModelTests
{
    private static ContentItem Post(string slug, string markdown) =>
        new(ContentKind.Post, slug, slug, "en", markdown, $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1) };

    private static ContentCatalogProvider Content()
    {
        var content = new ContentCatalogProvider();
        content.Set(ContentCatalog.Build(SiteSettings.Empty,
        [
            new ParseResult(Post("ferries", "Ferries cross the bay every hour."), null),
            new ParseResult(Post("trails", "Mountain trails close in winter."), null),
        ]));
        return content;
    }

    [Test]
    public async Task While_the_model_loads_the_first_index_is_on_keywords_and_the_embeddings_follow()
    {
        using var gate = new ManualResetEventSlim();
        using var embedder = new LoadingEmbedder(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return new FixedEmbedder();
        });
        var store = new MemoryVectorStore();
        using var provider = new SearchIndexProvider(Content(), embedder, NullLogger<SearchIndexProvider>.Instance, store);

        // At startup: the vectors can't be read without the model, and the build doesn't wait for it.
        await provider.LoadVectorsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var first = await provider.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(provider.IsBuilt).IsTrue();
        await Assert.That(first.HasVectors).IsFalse();
        await Assert.That(first.Search("ferries").Select(h => h.Item.Slug)).IsEquivalentTo(["ferries"]);
        await Assert.That(await store.ListAsync("fixed", CancellationToken.None)).IsEmpty();
        await Assert.That(provider.WhenComplete.IsCompleted).IsFalse();

        var swapped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Swapped += (_, _) => swapped.TrySetResult();
        gate.Set();
        await swapped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await provider.WhenComplete.WaitAsync(TimeSpan.FromSeconds(10));
        var second = provider.Current;
        await Assert.That(second.HasVectors).IsTrue();
        await Assert.That(second.EmbeddedCount).IsEqualTo(2);
        await provider.Saving;
        await Assert.That(await store.ListAsync("fixed", CancellationToken.None)).IsEquivalentTo(["post/ferries", "post/trails"]);
    }

    [Test]
    public async Task Once_the_model_is_in_the_build_uses_the_stored_vectors()
    {
        var content = Content();
        var store = new MemoryVectorStore();
        using (var earlier = new SearchIndexProvider(content, new FixedEmbedder(), store: store))
        {
            _ = earlier.Current;
            await earlier.Saving;
        }

        using var gate = new ManualResetEventSlim();
        using var embedder = new LoadingEmbedder(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return new FixedEmbedder();
        });
        using var provider = new SearchIndexProvider(content, embedder, store: store);
        await provider.LoadVectorsAsync(CancellationToken.None);
        await provider.RefreshAsync();

        var swapped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Swapped += (_, _) => swapped.TrySetResult();
        gate.Set();
        await swapped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(provider.Current.HasVectors).IsTrue();
        await Assert.That(provider.Current.EmbeddedCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_model_loaded_before_the_first_build_gives_the_whole_index_at_once()
    {
        // Prod: its model loads in about a second, before the content.
        using var embedder = new LoadingEmbedder(() => new FixedEmbedder());
        await embedder.WhenLoaded;
        using var provider = new SearchIndexProvider(Content(), embedder);

        var index = await provider.RefreshAsync();

        await Assert.That(index.HasVectors).IsTrue();
    }

    [Test]
    public async Task Searching_while_the_model_loads_finds_by_keywords_without_waiting()
    {
        using var gate = new ManualResetEventSlim();
        using var embedder = new LoadingEmbedder(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return new FixedEmbedder();
        });
        using var provider = new SearchIndexProvider(Content(), embedder);
        using var search = new HybridSearch(provider, new ConfigurationBuilder().Build(), embedder);

        try
        {
            var results = await Task.Run(() => search.Search("trails")).WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(results.Hits.Select(h => h.Item.Slug)).IsEquivalentTo(["trails"]);
            await Assert.That(embedder.IsLoaded).IsFalse();
        }
        finally
        {
            gate.Set();
        }
    }

    [Test]
    public async Task Without_a_known_model_or_its_files_search_stays_on_keywords()
    {
        var unknown = EmbedderStart.Begin(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Search:EmbeddingModel"] = "no-such-model" }).Build());
        var missing = EmbedderStart.Begin(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Search:ModelPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }).Build());

        await Assert.That(unknown.Embedder).IsTypeOf<NoEmbedder>();
        await Assert.That(missing.Embedder).IsTypeOf<NoEmbedder>();
    }

    // The same unit vector for every text: enough to tell an index with vectors from one without.
    private sealed class FixedEmbedder : IEmbedder
    {
        public string ModelId => "fixed";

        public int Dimensions => 2;

        public float[] EmbedPassage(string text) => [0.6f, 0.8f];

        public float[] EmbedQuery(string text) => [0.6f, 0.8f];
    }
}
