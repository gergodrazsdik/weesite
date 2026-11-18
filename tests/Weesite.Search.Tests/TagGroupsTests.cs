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
    }

    [Test]
    public async Task The_sample_sites_tags_group_by_topic()
    {
        var source = new FileContentSource(Weesite.Tests.RepoPaths.Seed);
        var site = SiteSettings.Parse((await source.ReadSettingsAsync(CancellationToken.None))!);
        var files = await source.ReadContentAsync(CancellationToken.None);
        var catalog = ContentCatalog.Build(site, files.Select(f => ContentParser.Parse(f.Path, f.Text, site.Language)));
        var directory = Path.Combine(AppContext.BaseDirectory, "Models", OnnxEmbedder.ModelName);
        if (!OnnxEmbedder.IsAvailable(directory))
        {
            throw new InvalidOperationException($"No embedding model in {directory}: run `just model`.");
        }

        using var model = new OnnxEmbedder(directory);
        using var index = SearchIndex.Build(catalog, model);

        var groups = index.GroupTags(catalog.Tags, TagDetails.Colours.Count);
        bool Together(string a, string b) => groups.Any(g => g.Contains(a) && g.Contains(b));

        await Assert.That(groups.Count).IsBetween(3, TagDetails.Colours.Count);
        await Assert.That(Together("azure", "opentofu")).IsTrue();
        await Assert.That(Together("travel", "photography")).IsTrue();
        await Assert.That(Together("web", "css")).IsTrue();
        await Assert.That(Together("web", "azure")).IsFalse();
        await Assert.That(Together("azure", "travel")).IsFalse();
    }
}
