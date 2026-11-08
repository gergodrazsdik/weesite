using Microsoft.Extensions.Configuration;
using Weesite.Content;

namespace Weesite.Search.Tests;

internal sealed class HybridSearchTests
{
    private static readonly string ModelDirectory = Path.Combine(AppContext.BaseDirectory, "Models", OnnxEmbedder.ModelName);

    private static readonly Lazy<OnnxEmbedder> Model = new(() =>
        OnnxEmbedder.IsAvailable(ModelDirectory)
            ? new OnnxEmbedder(ModelDirectory)
            : throw new InvalidOperationException($"No embedding model in {ModelDirectory}: run `just model`."));

    private static ContentItem Post(string slug, string title, string markdown) =>
        new(ContentKind.Post, slug, title, "en", markdown, $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1) };

    private static SearchHit Hit(string slug, int rank, string snippet = "") => new(Post(slug, slug, ""), null, snippet, rank);

    private static TestSearch Searcher(IEmbedder embedder, TimeSpan? slotWait = null, string? minSimilarity = null)
    {
        var content = new ContentCatalogProvider();
        content.Set(ContentCatalog.Build(SiteSettings.Empty,
        [
            new ParseResult(Post("pets", "Caring for a puppy", "Feed it well, walk it daily and see the vet twice a year."), null),
            new ParseResult(Post("infra", "OpenTofu notes", "Plans run on every pull request; policies check them."), null),
            new ParseResult(Post("site", "Static rendering", "Every page is plain HTML rendered on the server."), null),
        ]));
        var settings = new Dictionary<string, string?> { ["Search:MinSimilarity"] = minSimilarity };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new TestSearch(new SearchIndexProvider(content, embedder), configuration, slotWait ?? TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Fusion_ranks_items_found_by_both_legs_first_and_keeps_keyword_snippets()
    {
        var keyword = new[] { Hit("a", 1, "<mark>a</mark>"), Hit("b", 2, "<mark>b</mark>") };
        var meaning = new[] { Hit("c", 1, "c text"), Hit("b", 2, "b text") };

        var fused = HybridSearch.Fuse(keyword, meaning, 10);

        await Assert.That(fused.Select(h => h.Item.Slug)).IsEquivalentTo(["b", "a", "c"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(fused[0].SnippetHtml).IsEqualTo("<mark>b</mark>");
        await Assert.That(fused.Select(h => h.Rank)).IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Finds_by_meaning_and_by_keyword()
    {
        using var search = Searcher(Model.Value);

        var meaning = search.Searcher.Search("dog health");
        var keyword = search.Searcher.Search("opentofu");

        await Assert.That(meaning.UsedMeaning).IsTrue();
        await Assert.That(meaning.Hits[0].Item.Slug).IsEqualTo("pets");
        await Assert.That(keyword.Hits[0].Item.Slug).IsEqualTo("infra");
    }

    [Test]
    public async Task Queries_unlike_anything_on_the_site_find_nothing()
    {
        using var search = Searcher(Model.Value);

        var results = search.Searcher.Search("chocolate cake recipe");

        await Assert.That(results.UsedMeaning).IsTrue();
        await Assert.That(results.Hits).IsEmpty();
    }

    [Test]
    public async Task Results_are_cached_per_query()
    {
        using var search = Searcher(Model.Value);

        var first = search.Searcher.Search("dog health");
        var second = search.Searcher.Search("  Dog   health ");

        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task Busy_embedding_slots_fall_back_to_keywords_without_caching_it()
    {
        using var search = Searcher(Model.Value, TimeSpan.FromMilliseconds(10));
        await search.Searcher.EmbeddingSlots.WaitAsync();
        await search.Searcher.EmbeddingSlots.WaitAsync();

        var busy = search.Searcher.Search("opentofu");
        search.Searcher.EmbeddingSlots.Release(2);
        var idle = search.Searcher.Search("opentofu");

        await Assert.That(busy.UsedMeaning).IsFalse();
        await Assert.That(busy.Hits[0].Item.Slug).IsEqualTo("infra");
        await Assert.That(idle.UsedMeaning).IsTrue();
    }

    [Test]
    public async Task Without_a_model_search_uses_keywords()
    {
        using var search = Searcher(new NoEmbedder());

        var results = search.Searcher.Search("puppy");

        await Assert.That(results.UsedMeaning).IsFalse();
        await Assert.That(results.Hits.Single().Item.Slug).IsEqualTo("pets");
    }

    [Test]
    public async Task Similarity_floor_is_configurable()
    {
        using var configured = Searcher(new NoEmbedder(), minSimilarity: "0.75");
        using var defaulted = Searcher(new NoEmbedder());

        await Assert.That(configured.Searcher.MinSimilarity).IsEqualTo(0.75f);
        await Assert.That(defaulted.Searcher.MinSimilarity).IsEqualTo(HybridSearch.DefaultMinSimilarity);
    }

    // The searcher and the index provider it uses, disposed together.
    private sealed class TestSearch(SearchIndexProvider provider, IConfiguration configuration, TimeSpan slotWait) : IDisposable
    {
        public HybridSearch Searcher { get; } = new(provider, configuration, slotWait);

        public void Dispose()
        {
            Searcher.Dispose();
            provider.Dispose();
        }
    }
}
