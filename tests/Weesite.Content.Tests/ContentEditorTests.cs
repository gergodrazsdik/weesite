using System.Globalization;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Azurite;
using Weesite.Content.Sources;

namespace Weesite.Content.Tests;

/// <summary>Opening and saving content for the editor (ADR 0005).</summary>
internal sealed class ContentEditorTests
{
    private static readonly DateOnly Day = new(2026, 11, 9);

    private static ContentDraft Draft(string slug, ContentStatus status = ContentStatus.Published) => new()
    {
        Title = slug,
        Slug = slug,
        Date = Day,
        Status = status,
        Body = $"Body of {slug}.",
    };

    [Test]
    public async Task Drafts_round_trip_through_markdown()
    {
        var draft = new ContentDraft
        {
            Kind = ContentKind.Post,
            Title = "A title: with \"quotes\" and a # sign",
            Slug = "round-trip",
            Language = "en",
            Summary = "true",
            Date = Day,
            Updated = Day.AddDays(1),
            AuthorId = "example-author",
            Tags = ["web", "notes"],
            Status = ContentStatus.Scheduled,
            PublishAt = DateTimeOffset.Parse("2026-11-10T09:30:00+01:00", CultureInfo.InvariantCulture),
            Image = "/media/abc-x.png",
            ImageAlt = "A picture: of something",
            RedirectFrom = ["old-slug"],
            Body = "Text with `code`.\n\n## A heading\n\nMore.",
        };

        var item = ContentParser.Parse("posts/round-trip.md", draft.ToMarkdown()).Item!;
        var back = ContentDraft.From(item);

        await Assert.That(back with { Tags = [], RedirectFrom = [] }).IsEqualTo(draft with { Tags = [], RedirectFrom = [], PublishAt = draft.PublishAt!.Value.ToUniversalTime() });
        await Assert.That(back.Tags).IsEquivalentTo(["web", "notes"]);
        await Assert.That(back.RedirectFrom).IsEquivalentTo(["old-slug"]);
    }

    [Test]
    public async Task Pages_keep_their_kind_and_menu_flag()
    {
        var page = new ContentDraft { Kind = ContentKind.Page, Title = "Now", Slug = "now", ShowInMenu = true, Status = ContentStatus.Published, Body = "Now." };

        var item = ContentParser.Parse("pages/now.md", page.ToMarkdown()).Item!;

        await Assert.That(item.Kind).IsEqualTo(ContentKind.Page);
        await Assert.That(item.ShowInMenu).IsTrue();
        await Assert.That(page.ToMarkdown()).DoesNotContain("status:");
    }

    [Test]
    public async Task New_items_are_saved_and_show_up_at_once()
    {
        using var site = new TempSite();
        var editor = site.Editor;

        var outcome = await editor.SaveAsync(null, Draft("fresh", ContentStatus.Draft), null, CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<ContentSaved>();
        await Assert.That(((ContentSaved)outcome).Path).IsEqualTo("posts/fresh.md");
        await Assert.That(site.Catalog.Unpublished.Select(i => i.Slug)).Contains("fresh");
        await Assert.That(site.Catalog.FindPost("fresh")).IsNull();
    }

    [Test]
    public async Task A_slug_saved_a_moment_ago_on_another_server_is_refused()
    {
        // F-0119: the check read this replica's catalog, which hadn't loaded the other's new item
        // yet, so the second got posts/same-2.md with the same slug, and one of them vanished.
        using var site = new TempSite();
        Directory.CreateDirectory(Path.Combine(site.Root, "content", "posts"));
        await File.WriteAllTextAsync(Path.Combine(site.Root, "content", "posts", "same.md"), "---\ntitle: Other\nslug: same\ndate: 2026-11-09\n---\nText.\n");

        var outcome = await site.Editor.SaveAsync(null, Draft("same"), null, CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<ContentInvalid>();
        await Assert.That(((ContentInvalid)outcome).Message).Contains("another server");
        await Assert.That(File.Exists(Path.Combine(site.Root, "content", "posts", "same-2.md"))).IsFalse();
    }

    [Test]
    public async Task A_stale_save_is_a_conflict()
    {
        using var site = new TempSite();
        var saved = (ContentSaved)await site.Editor.SaveAsync(null, Draft("a"), null, CancellationToken.None);
        var opened = (await site.Editor.OpenAsync(saved.Path, CancellationToken.None))!;

        var first = await site.Editor.SaveAsync(saved.Path, opened.Draft with { Title = "First" }, opened.ETag, CancellationToken.None);
        var second = await site.Editor.SaveAsync(saved.Path, opened.Draft with { Title = "Second" }, opened.ETag, CancellationToken.None);

        await Assert.That(first).IsTypeOf<ContentSaved>();
        await Assert.That(second).IsTypeOf<ContentConflict>();
        await Assert.That(site.Catalog.FindPost("a")?.Title).IsEqualTo("First");
    }

    [Test]
    public async Task Renaming_a_public_item_leaves_a_redirect_and_a_draft_none()
    {
        using var site = new TempSite();
        var live = (ContentSaved)await site.Editor.SaveAsync(null, Draft("live"), null, CancellationToken.None);
        var draft = (ContentSaved)await site.Editor.SaveAsync(null, Draft("unseen", ContentStatus.Draft), null, CancellationToken.None);

        await site.Editor.SaveAsync(live.Path, Draft("live-renamed"), live.ETag, CancellationToken.None);
        await site.Editor.SaveAsync(draft.Path, Draft("unseen-renamed", ContentStatus.Draft), draft.ETag, CancellationToken.None);

        await Assert.That(site.Catalog.FindPostRedirect("live")).IsEqualTo("live-renamed");
        await Assert.That(site.Catalog.Unpublished.Single(i => i.Slug == "unseen-renamed").PreviousSlugs).IsEmpty();
        await Assert.That(site.Catalog.FindPost("live-renamed")?.SourcePath).IsEqualTo("posts/live.md");
    }

    [Test]
    public async Task Invalid_drafts_are_refused_with_a_reason()
    {
        using var site = new TempSite();
        await site.Editor.SaveAsync(null, Draft("taken", ContentStatus.Draft), null, CancellationToken.None);

        var duplicate = await site.Editor.SaveAsync(null, Draft("taken"), null, CancellationToken.None);
        var reserved = await site.Editor.SaveAsync(null, Draft("tags"), null, CancellationToken.None);
        var unscheduled = await site.Editor.SaveAsync(null, Draft("later", ContentStatus.Scheduled), null, CancellationToken.None);
        var badSlug = await site.Editor.SaveAsync(null, Draft("Not A Slug"), null, CancellationToken.None);

        await Assert.That(((ContentInvalid)duplicate).Message).Contains("already used by");
        await Assert.That(((ContentInvalid)reserved).Message).Contains("reserved");
        await Assert.That(((ContentInvalid)unscheduled).Message).Contains("needs a publish time (publish_at)");
        await Assert.That(((ContentInvalid)badSlug).Message).Contains("isn't lowercase letters");
    }

    [Test]
    public async Task A_new_item_gets_a_free_path_when_an_older_file_has_its_slug()
    {
        using var site = new TempSite();
        var first = (ContentSaved)await site.Editor.SaveAsync(null, Draft("a"), null, CancellationToken.None);
        await site.Editor.SaveAsync(first.Path, Draft("b"), first.ETag, CancellationToken.None);

        // "a" is free again (only a redirect uses it), but posts/a.md still holds the renamed item.
        var second = await site.Editor.SaveAsync(null, Draft("a"), null, CancellationToken.None);

        await Assert.That(((ContentSaved)second).Path).IsEqualTo("posts/a-2.md");
        await Assert.That(site.Catalog.FindPost("a")?.SourcePath).IsEqualTo("posts/a-2.md");
        await Assert.That(site.Catalog.FindPostRedirect("a")).IsNull();
    }

    [Test]
    [Category("containers")]
    public async Task Blob_store_writes_conditionally_and_bumps_the_version()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        var store = new BlobContentStore(blobs);
        var source = new BlobContentSource(blobs);

        var created = await store.WriteAsync("posts/x.md", "one", null, CancellationToken.None);
        var again = await store.WriteAsync("posts/x.md", "two", null, CancellationToken.None);
        var updated = await store.WriteAsync("posts/x.md", "three", created, CancellationToken.None);
        var stale = await store.WriteAsync("posts/x.md", "four", created, CancellationToken.None);
        var before = await source.ReadVersionAsync(CancellationToken.None);
        var bumped = await store.BumpVersionAsync(CancellationToken.None);

        await Assert.That(created).IsNotNull();
        await Assert.That(again).IsNull();
        await Assert.That(updated).IsNotNull();
        await Assert.That(stale).IsNull();
        await Assert.That((await store.ReadAsync("posts/x.md", CancellationToken.None))?.Text).IsEqualTo("three");
        await Assert.That(await source.ReadVersionAsync(CancellationToken.None)).IsEqualTo(bumped);
        await Assert.That(bumped).IsNotEqualTo(before);
        await Assert.That(await store.ListRevisionsAsync("posts/x.md", CancellationToken.None)).IsNotNull();
    }

    // A site in a temporary directory, so saves never touch the repository's seed.
    private sealed class TempSite : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("weesite-editor-").FullName;
        private readonly ServiceProvider _services;

        public TempSite()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddContentFromFiles(_root);
            _services = services.BuildServiceProvider();
            _services.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public string Root => _root;

        public ContentEditor Editor => _services.GetRequiredService<ContentEditor>();

        public ContentCatalog Catalog => _services.GetRequiredService<ContentCatalogProvider>().Current;

        public void Dispose()
        {
            _services.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
