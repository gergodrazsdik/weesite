using Weesite.Content;

namespace Weesite.Search.Tests;

/// <summary>
/// The meaning leg's lead over the median (F-0056), with an embedder whose similarities are
/// exact: each post's vector is set by its slug, and every query embeds to (1, 0).
/// </summary>
internal sealed class MeaningLeadTests
{
    private static ContentItem Post(string slug) =>
        new(ContentKind.Post, slug, slug, "en", "Text.", $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1) };

    private static SearchIndex Index(params (string Slug, float Similarity)[] posts)
    {
        var embedder = new FixedEmbedder(posts.ToDictionary(p => p.Slug, p => p.Similarity));
        return SearchIndex.Build(ContentCatalog.Build(SiteSettings.Empty, posts.Select(p => new ParseResult(Post(p.Slug), null))), embedder);
    }

    [Test]
    public async Task Only_items_well_above_the_median_count()
    {
        // Median 0.62: "about" leads by 0.18, "close" by 0.10, the rest by less.
        using var index = Index(("about", 0.80f), ("close", 0.72f), ("a", 0.63f), ("b", 0.62f), ("c", 0.61f), ("d", 0.60f));

        var hits = index.SearchSimilar("anything", 10, minSimilarity: 0.6f, minLead: 0.115f);

        await Assert.That(hits.Select(h => h.Item.Slug)).IsEquivalentTo(["about"]);
    }

    [Test]
    public async Task Nothing_stands_out_so_nothing_is_found()
    {
        using var index = Index(("a", 0.66f), ("b", 0.65f), ("c", 0.64f), ("d", 0.62f));

        await Assert.That(index.SearchSimilar("anything", 10, 0.6f, 0.115f)).IsEmpty();
        await Assert.That(index.SearchSimilar("anything", 10, 0.6f, 0f).Count).IsEqualTo(4);
    }

    [Test]
    public async Task A_site_with_few_items_uses_the_floor_alone()
    {
        using var index = Index(("about", 0.80f), ("close", 0.70f), ("far", 0.40f));

        var hits = index.SearchSimilar("anything", 10, 0.6f, 0.115f);

        await Assert.That(hits.Select(h => h.Item.Slug)).IsEquivalentTo(["about", "close"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Unit vectors at the given cosine from (1, 0); the post's text carries its slug.
    private sealed class FixedEmbedder(Dictionary<string, float> similarities) : IEmbedder
    {
        public string ModelId => "fixed";

        public int Dimensions => 2;

        public float[] EmbedPassage(string text)
        {
            var cos = similarities.First(s => text.StartsWith(s.Key + ".", StringComparison.Ordinal) || text == s.Key || text.StartsWith(s.Key + " ", StringComparison.Ordinal)).Value;
            return [cos, MathF.Sqrt(1 - (cos * cos))];
        }

        public float[] EmbedQuery(string text) => [1f, 0f];
    }
}
