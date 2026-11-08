using Weesite.Content;

namespace Weesite.Search.Tests;

internal sealed class RelatedPostsTests
{
    private static readonly string ModelDirectory = Path.Combine(AppContext.BaseDirectory, "Models", OnnxEmbedder.ModelName);

    private static readonly Lazy<OnnxEmbedder> Model = new(() =>
        OnnxEmbedder.IsAvailable(ModelDirectory)
            ? new OnnxEmbedder(ModelDirectory)
            : throw new InvalidOperationException($"No embedding model in {ModelDirectory}: run `just model`."));

    private static ContentItem Post(string slug, string title, string markdown, params string[] tags) =>
        new(ContentKind.Post, slug, title, "en", markdown, $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1), Tags = tags };

    private static ContentCatalog Catalog() => ContentCatalog.Build(SiteSettings.Empty,
    [
        new ParseResult(Post("tofu", "OpenTofu plans in CI", "Every pull request runs a plan; policies check it before apply.", "infra"), null),
        new ParseResult(Post("terraform", "Reviewing infrastructure changes", "Plans of Terraform code show what an apply would change in the cloud.", "infra", "review"), null),
        new ParseResult(Post("cake", "Chocolate cake", "Melt the chocolate, fold in the eggs and bake for forty minutes.", "baking"), null),
        new ParseResult(new ContentItem(ContentKind.Page, "about", "About", "en", "About infrastructure plans.", "pages/about.md"), null),
    ]);

    [Test]
    public async Task Related_posts_come_from_meaning_and_leave_out_unrelated_ones()
    {
        var catalog = Catalog();
        using var index = SearchIndex.Build(catalog, Model.Value);

        var related = index.Related(catalog.FindPost("tofu")!);

        await Assert.That(related.Select(p => p.Slug)).IsEquivalentTo(["terraform"]);
        await Assert.That(index.Related(catalog.FindPost("cake")!)).IsEmpty();
    }

    [Test]
    public async Task Without_vectors_related_posts_share_tags()
    {
        var catalog = Catalog();
        using var index = SearchIndex.Build(catalog);

        await Assert.That(index.Related(catalog.FindPost("terraform")!).Select(p => p.Slug)).IsEquivalentTo(["tofu"]);
        await Assert.That(index.Related(catalog.FindPost("cake")!)).IsEmpty();
    }
}
