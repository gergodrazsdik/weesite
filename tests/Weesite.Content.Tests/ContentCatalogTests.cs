namespace Weesite.Content.Tests;

internal sealed class ContentCatalogTests
{
    private static ParseResult Post(string slug, string date, params string[] tags) =>
        ContentParser.Parse($"posts/{slug}.md", $"---\ntitle: {slug}\nslug: {slug}\ndate: {date}\ntags: [{string.Join(", ", tags)}]\n---\nBody\n");

    private static ParseResult Page(string slug, string? redirectFrom = null) =>
        ContentParser.Parse(
            $"pages/{slug}.md",
            $"---\nkind: page\ntitle: {slug}\nslug: {slug}\n{(redirectFrom is null ? "" : $"redirect_from: [{redirectFrom}]\n")}---\nBody\n");

    [Test]
    public async Task Orders_posts_newest_first_and_groups_tags()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [Post("old", "2025-01-01", "a"), Post("new", "2026-01-01", "a", "b")]);

        await Assert.That(catalog.Posts.Select(p => p.Slug)).IsEquivalentTo(["new", "old"]);
        await Assert.That(catalog.Tags["a"].Select(p => p.Slug)).IsEquivalentTo(["new", "old"]);
        await Assert.That(catalog.Tags["b"].Count).IsEqualTo(1);
    }

    [Test]
    public async Task Skips_duplicate_slugs_and_reserved_page_slugs()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [Post("x", "2026-01-01"), Post("x", "2026-01-02"), Page("blog"), Page("about")]);

        await Assert.That(catalog.Posts.Count).IsEqualTo(1);
        await Assert.That(catalog.Pages.Select(p => p.Slug)).IsEquivalentTo(["about"]);
        await Assert.That(catalog.Problems.Count).IsEqualTo(2);
        await Assert.That(catalog.Problems.Any(p => p.Contains("reserved", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Finds_items_and_redirects_from_earlier_slugs()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [Page("about", redirectFrom: "about-me"), Page("contact", redirectFrom: "about")]);

        await Assert.That(catalog.FindPage("about")?.Title).IsEqualTo("about");
        await Assert.That(catalog.FindPageRedirect("about-me")).IsEqualTo("about");
        // A slug in current use never redirects.
        await Assert.That(catalog.FindPageRedirect("about")).IsNull();
        await Assert.That(catalog.FindPost("about")).IsNull();
    }
}
