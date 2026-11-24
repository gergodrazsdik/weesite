using Weesite.Content;
using Weesite.Content.Sources;

namespace Weesite.Search.Tests;

/// <summary>Grouping tags for suggested colours (T-0142).</summary>
internal sealed class TagGroupsTests
{
    // Points near the corners of a simplex: tags in a cluster are close, clusters far apart.
    private static double Similarity(int[] clusterOf, int a, int b) => clusterOf[a] == clusterOf[b] ? 0.9 - (0.01 * Math.Abs(a - b)) : 0.1;

    [Test]
    public async Task Clear_clusters_become_the_groups_largest_first()
    {
        int[] clusterOf = [0, 0, 0, 1, 1, 2, 2, 2, 2];

        var groups = TagGroups.Group(clusterOf.Length, (a, b) => Similarity(clusterOf, a, b), maxGroups: 10);

        await Assert.That(groups.Select(g => string.Join(",", g))).IsEquivalentTo(["5,6,7,8", "0,1,2", "3,4"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task There_are_never_more_groups_than_colours()
    {
        int[] clusterOf = [0, 0, 1, 1, 2, 2, 3, 3, 4, 4];

        var groups = TagGroups.Group(clusterOf.Length, (a, b) => Similarity(clusterOf, a, b), maxGroups: 3);

        await Assert.That(groups.Count).IsLessThanOrEqualTo(3);
        await Assert.That(groups.SelectMany(g => g).Order()).IsEquivalentTo(Enumerable.Range(0, 10));
    }

    [Test]
    public async Task A_tag_unlike_the_rest_joins_the_group_closest_to_it()
    {
        // T-0144: a fine cut leaves outliers alone; a colour each would waste the palette.
        int[] clusterOf = [0, 0, 0, 1, 1, 1, 2];
        double Similarity(int a, int b) =>
            (a, b) is (6, 0) or (0, 6) ? 0.4 : clusterOf[a] == clusterOf[b] ? 0.9 : 0.1;

        var groups = TagGroups.Group(clusterOf.Length, Similarity, maxGroups: 10);

        await Assert.That(groups.Select(g => string.Join(",", g))).IsEquivalentTo(["0,1,2,6", "3,4,5"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task One_or_two_tags_get_a_group_each()
    {
        await Assert.That(TagGroups.Group(0, (_, _) => 0, 10)).IsEmpty();
        await Assert.That(TagGroups.Group(2, (_, _) => 0.5, 10).Count).IsEqualTo(2);
        await Assert.That(TagGroups.Group(5, (_, _) => 0.5, 1).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Without_vectors_tags_sharing_posts_group_together()
    {
        static ContentItem Post(string slug, params string[] tags) =>
            new(ContentKind.Post, slug, slug, "en", "Text.", $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1), Tags = tags };
        var catalog = ContentCatalog.Build(SiteSettings.Empty,
        [
            new ParseResult(Post("a", "azure", "opentofu"), null),
            new ParseResult(Post("b", "azure", "opentofu", "ci"), null),
            new ParseResult(Post("c", "opentofu", "ci"), null),
            new ParseResult(Post("d", "baking", "cake"), null),
            new ParseResult(Post("e", "baking", "cake", "bread"), null),
            new ParseResult(Post("f", "bread", "baking"), null),
            new ParseResult(Post("g", "roses", "soil"), null),
            new ParseResult(Post("h", "roses", "soil", "tulips"), null),
            new ParseResult(Post("i", "tulips", "roses"), null),
        ]);
        using var index = SearchIndex.Build(catalog);

        var groups = index.GroupTags(catalog.Tags, 10);

        await Assert.That(groups.Select(g => string.Join(",", g.Order(StringComparer.Ordinal))))
            .IsEquivalentTo(["azure,ci,opentofu", "baking,bread,cake", "roses,soil,tulips"]);

        // F-0225: the same catalog's tags aren't grouped again; other tags or limits are.
        var again = index.GroupTags(catalog.Tags, 10);
        var fewer = index.GroupTags(catalog.Tags, 2);
        var copied = index.GroupTags(catalog.Tags.ToDictionary(), 10);
        await Assert.That(ReferenceEquals(again, groups)).IsTrue();
        await Assert.That(fewer.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(copied, groups)).IsFalse();
        await Assert.That(copied.Select(g => string.Join(",", g.Order(StringComparer.Ordinal))))
            .IsEquivalentTo(groups.Select(g => string.Join(",", g.Order(StringComparer.Ordinal))));
    }

    [Test]
    public async Task Only_the_most_used_tags_are_grouped_so_many_tags_stay_quick()
    {
        // T-0177: grouping takes time in the cube of the tags; thousands took minutes and gigabytes.
        static ContentItem Post(string slug, IReadOnlyList<string> tags) =>
            new(ContentKind.Post, slug, slug, "en", "Text.", $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1), Tags = tags };
        var posts = Enumerable.Range(0, 200).Select(p => new ParseResult(Post($"p{p}", [.. Enumerable.Range(0, 20).Select(t => $"t{(p * 20) + t}"), "common"]), null));
        var catalog = ContentCatalog.Build(SiteSettings.Empty, posts);
        using var index = SearchIndex.Build(catalog);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var groups = index.GroupTags(catalog.Tags, 10);

        await Assert.That(catalog.Tags.Count).IsEqualTo(4_001);
        await Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(groups.Sum(g => g.Count)).IsEqualTo(SearchIndex.MaxGroupedTags);
        await Assert.That(groups.SelectMany(g => g)).Contains("common");
    }

    [Test]
    [Arguments("bge-small-en-v1.5")]
    [Arguments("multilingual-e5-small")]
    public async Task The_sample_sites_tags_group_by_topic(string name)
    {
        // T-0144: with e5, as on test, the groups were technical, personal and the site's own.
        var model = EmbeddingModel.Find(name)!;
        var source = new FileContentSource(Weesite.Tests.RepoPaths.Seed);
        var site = SiteSettings.Parse((await source.ReadSettingsAsync(CancellationToken.None))!);
        var files = await source.ReadContentAsync(CancellationToken.None);
        var catalog = ContentCatalog.Build(site, files.Select(f => ContentParser.Parse(f.Path, f.Text, site.Language)));
        var directory = Path.Combine(AppContext.BaseDirectory, "Models", model.Name);
        if (!OnnxEmbedder.IsAvailable(directory, model))
        {
            throw new InvalidOperationException($"No embedding model in {directory}: run `just model`.");
        }

        using var embedder = new OnnxEmbedder(directory, model);
        using var index = SearchIndex.Build(catalog, embedder);

        var groups = index.GroupTags(catalog.Tags, TagDetails.Colours.Count);
        bool Together(string a, string b) => groups.Any(g => g.Contains(a) && g.Contains(b));

        await Assert.That(groups.Count).IsBetween(5, TagDetails.Colours.Count);
        await Assert.That(groups.All(g => g.Count >= 2)).IsTrue();
        await Assert.That(Together("azure", "opentofu")).IsTrue();
        await Assert.That(Together("web", "css")).IsTrue();
        await Assert.That(Together("architecture", "travel")).IsTrue();
        await Assert.That(Together("web", "azure")).IsFalse();
        await Assert.That(Together("travel", "nature")).IsFalse();
        await Assert.That(Together("azure", "travel")).IsFalse();
    }
}
