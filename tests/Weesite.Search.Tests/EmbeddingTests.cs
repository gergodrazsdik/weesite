using Weesite.Content;

namespace Weesite.Search.Tests;

/// <summary>The real model (run `just model` first; CI downloads it).</summary>
internal sealed class EmbeddingTests
{
    private static readonly string ModelDirectory = Path.Combine(AppContext.BaseDirectory, "Models", OnnxEmbedder.ModelName);

    private static readonly Lazy<OnnxEmbedder> Model = new(() =>
        OnnxEmbedder.IsAvailable(ModelDirectory)
            ? new OnnxEmbedder(ModelDirectory)
            : throw new InvalidOperationException($"No embedding model in {ModelDirectory}: run `just model`."));

    private static ContentItem Post(string slug, string title, string markdown) =>
        new(ContentKind.Post, slug, title, "en", markdown, $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1) };

    [Test]
    public async Task Vectors_have_384_dimensions_and_unit_length()
    {
        var vector = Model.Value.EmbedPassage("Server-side rendering keeps pages fast.");

        await Assert.That(Model.Value.Dimensions).IsEqualTo(384);
        await Assert.That(vector.Length).IsEqualTo(384);
        await Assert.That(Math.Sqrt(vector.Sum(v => (double)v * v))).IsEqualTo(1.0).Within(1e-4);
    }

    [Test]
    public async Task Related_texts_are_more_similar_than_unrelated_ones()
    {
        var query = Model.Value.EmbedQuery("how do I keep my dog healthy");
        var related = Model.Value.EmbedPassage("Feeding puppies well and taking them to the vet keeps them fit.");
        var unrelated = Model.Value.EmbedPassage("Kubernetes schedules containers onto nodes in a cluster.");

        await Assert.That(Vectors.Similarity(query, related)).IsGreaterThan(Vectors.Similarity(query, unrelated));
    }

    [Test]
    public async Task Search_by_meaning_finds_posts_without_shared_words()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty,
        [
            new ParseResult(Post("pets", "Caring for a puppy", "Feed it well, walk it daily and see the vet twice a year."), null),
            new ParseResult(Post("k8s", "Cluster notes", "Kubernetes schedules containers onto nodes."), null),
        ]);
        using var index = SearchIndex.Build(catalog, Model.Value);

        var hits = index.SearchSimilar("dog health");

        await Assert.That(index.HasVectors).IsTrue();
        await Assert.That(index.Search("dog health")).IsEmpty();
        await Assert.That(hits[0].Item.Slug).IsEqualTo("pets");
        await Assert.That(hits.Select(h => h.Rank)).IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Without_a_model_search_by_meaning_is_empty()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [new ParseResult(Post("a", "A", "Text."), null)]);
        using var index = SearchIndex.Build(catalog);

        await Assert.That(index.HasVectors).IsFalse();
        await Assert.That(index.SearchSimilar("text")).IsEmpty();
    }
}
