namespace Weesite.Content.Tests;

/// <summary>The archived status (T-0111).</summary>
internal sealed class ArchivedTests
{
    private const string Archived = "---\ntitle: Old news\nslug: old-news\ndate: 2025-01-01\nstatus: archived\nimage: /media/0123456789ab-cover.jpg\n---\nGone.\n";

    [Test]
    public async Task An_archived_item_parses_isnt_public_and_round_trips()
    {
        var item = ContentParser.Parse("posts/old-news.md", Archived).Item!;

        await Assert.That(item.Status).IsEqualTo(ContentStatus.Archived);
        await Assert.That(item.IsPublicAt(DateTimeOffset.UtcNow)).IsFalse();
        await Assert.That(ContentDraft.From(item).ToMarkdown()).Contains("status: archived");
    }

    [Test]
    public async Task The_catalog_keeps_it_for_editors_and_leaves_it_out_of_media_warnings()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty, [
            ContentParser.Parse("posts/old-news.md", Archived),
            ContentParser.Parse("posts/live.md", "---\ntitle: Live\nslug: live\ndate: 2026-01-01\n---\nText.\n"),
        ]);

        await Assert.That(catalog.Posts.Select(p => p.Slug)).IsEquivalentTo(["live"]);
        await Assert.That(catalog.FindPost("old-news")).IsNull();
        await Assert.That(catalog.IsArchived(ContentKind.Post, "old-news")).IsTrue();
        await Assert.That(catalog.IsArchived(ContentKind.Page, "old-news")).IsFalse();
        await Assert.That(catalog.IsArchived(ContentKind.Post, "live")).IsFalse();
        await Assert.That(catalog.UsesOf("0123456789ab-cover.jpg").Count).IsEqualTo(1);
        await Assert.That(catalog.ActiveUsesOf("0123456789ab-cover.jpg")).IsEmpty();
    }

    [Test]
    public async Task An_unknown_status_names_archived_among_the_choices()
    {
        var result = ContentParser.Parse("posts/x.md", "---\ntitle: X\nslug: x\ndate: 2026-01-01\nstatus: hidden\n---\n");

        await Assert.That(result.Error).Contains("published, draft, scheduled or archived");
    }
}
