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
    public async Task Media_uses_count_text_and_image_references_drafts_included()
    {
        // T-0112: which posts and pages use each stored file.
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [
            ContentParser.Parse("posts/a.md", "---\ntitle: A\nslug: a\ndate: 2026-01-01\nimage: /media/0123456789ab-cover.jpg\n---\n![x](/media/0123456789ab-cover.jpg) and [file](/media/abcdef012345-notes.pdf).\n"),
            ContentParser.Parse("posts/b.md", "---\ntitle: B\nslug: b\ndate: 2026-01-02\nstatus: draft\n---\nSee /media/abcdef012345-notes.pdf?download.\n"),
            ContentParser.Parse("pages/c.md", "---\nkind: page\ntitle: C\nslug: c\n---\nNo files, just /media/ and /media/not-a-name.\n"),
        ]);

        await Assert.That(catalog.UsesOf("0123456789ab-cover.jpg").Select(i => i.Slug)).IsEquivalentTo(["a"]);
        await Assert.That(catalog.UsesOf("abcdef012345-notes.pdf").Select(i => i.Slug)).IsEquivalentTo(["a", "b"]);
        await Assert.That(catalog.UsesOf("ffffffffffff-unused.png")).IsEmpty();
        await Assert.That(catalog.MediaUses.Count).IsEqualTo(2);
    }

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
    public async Task Skips_posts_with_slugs_the_blog_routes_use()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [Post("tags", "2026-01-01"), Page("tags")]);

        await Assert.That(catalog.FindPost("tags")).IsNull();
        await Assert.That(catalog.FindPage("tags")).IsNotNull();
        await Assert.That(catalog.Problems).Contains(p => p.Contains("'tags' is reserved", StringComparison.Ordinal));
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(220, 1)]
    [Arguments(221, 2)]
    [Arguments(1000, 5)]
    public async Task Reading_time_rounds_up_at_220_words_a_minute(int words, int minutes)
    {
        var item = new ContentItem(ContentKind.Post, "x", "X", "en", "", "x.md")
        {
            PlainText = string.Join(" \n", Enumerable.Repeat("word", words)),
        };

        await Assert.That(item.ReadingMinutes).IsEqualTo(minutes);
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
