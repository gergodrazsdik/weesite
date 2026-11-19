namespace Weesite.Content.Tests;

/// <summary>Special pages (T-0159): pages without a slug, which Site settings place.</summary>
internal sealed class SpecialPageTests
{
    private const string Special = "---\nkind: page\ntitle: Privacy policy\n---\n\nWhat this site keeps.\n";

    [Test]
    public async Task A_page_may_have_no_slug_and_a_post_may_not()
    {
        var page = ContentParser.Parse("pages/privacy.md", Special);
        var post = ContentParser.Parse("posts/x.md", "---\ntitle: X\ndate: 2026-11-19\n---\n\nText.\n");

        await Assert.That(page.Item?.IsSpecial).IsTrue();
        await Assert.That(page.Item?.Slug).IsEqualTo("");
        await Assert.That(post.Item).IsNull();
        await Assert.That(post.Error).Contains("slug");
    }

    [Test]
    public async Task Special_pages_stay_out_of_the_pages_and_get_the_address_settings_give()
    {
        var site = SiteSettings.Parse("{\"name\":\"Example\",\"privacyPage\":\"pages/privacy.md\"}");
        var catalog = ContentCatalog.Build(site, [
            ContentParser.Parse("pages/privacy.md", Special),
            ContentParser.Parse("pages/unplaced.md", "---\nkind: page\ntitle: Unplaced\n---\n\nText.\n"),
            ContentParser.Parse("pages/draft.md", "---\nkind: page\ntitle: Draft\nstatus: draft\n---\n\nText.\n"),
            ContentParser.Parse("pages/about.md", "---\nkind: page\ntitle: About\nslug: about\n---\n\nText.\n"),
        ]);

        await Assert.That(catalog.Pages.Select(p => p.Slug)).IsEquivalentTo(["about"]);
        await Assert.That(catalog.SpecialPages.Select(p => p.SourcePath)).IsEquivalentTo(["pages/privacy.md", "pages/unplaced.md"]);
        await Assert.That(catalog.AllSpecialPages.Select(p => p.Title)).IsEquivalentTo(["Draft", "Privacy policy", "Unplaced"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(catalog.FindSpecialPage("pages/privacy.md")?.SpecialAddress).IsEqualTo("privacy");
        await Assert.That(catalog.FindSpecialPage("pages/unplaced.md")?.SpecialAddress).IsNull();
        await Assert.That(catalog.FindSpecialPage("pages/draft.md")).IsNull();
        await Assert.That(catalog.Problems).IsEmpty();
    }

    [Test]
    public async Task A_page_that_kept_a_special_address_as_its_slug_still_loads()
    {
        // Pages called privacy, terms or contact from before special pages: an editor must be able
        // to find them, to take their slug away.
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [
            ContentParser.Parse("pages/privacy.md", "---\nkind: page\ntitle: Privacy\nslug: privacy\n---\n\nText.\n"),
            ContentParser.Parse("pages/admin.md", "---\nkind: page\ntitle: Admin\nslug: admin\n---\n\nText.\n"),
        ]);

        await Assert.That(catalog.FindPage("privacy")).IsNotNull();
        await Assert.That(catalog.FindPage("admin")).IsNull();
        await Assert.That(catalog.Problems.Single()).Contains("reserved");
    }

    [Test]
    public async Task A_special_page_is_written_without_a_slug_and_named_after_its_title()
    {
        var draft = new ContentDraft { Kind = ContentKind.Page, Title = "Adatvédelmi tájékoztató", Slug = "", Body = "Text." };

        await Assert.That(draft.ToMarkdown()).DoesNotContain("slug:");
        await Assert.That(Slugs.FromTitle(draft.Title, "page")).IsEqualTo("adatvédelmi-tájékoztató");
        await Assert.That(Slugs.FromTitle("!!!", "page")).IsEqualTo("page");
        await Assert.That(Slugs.Reserved).Contains("privacy").And.Contains("terms").And.Contains("contact");
    }
}
