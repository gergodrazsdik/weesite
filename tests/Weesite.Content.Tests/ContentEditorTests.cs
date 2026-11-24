using System.Globalization;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Sources;
using Weesite.Tests;

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
    public async Task Text_over_the_limit_is_refused()
    {
        // T-0122: rendering it could take minutes, at every load.
        using var site = new TempSite();

        var outcome = await site.Editor.SaveAsync(null, Draft("long", ContentStatus.Draft) with { Body = new string('a', ContentDraft.MaxBodyLength + 1) }, null, CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<ContentInvalid>();
        await Assert.That(((ContentInvalid)outcome).Message).Contains("The text can be at most");
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
    public async Task Saving_what_is_stored_writes_nothing()
    {
        // F-0174: every save made a version, even with nothing changed.
        using var site = new TempSite();
        var created = (ContentSaved)await site.Editor.SaveAsync(null, Draft("a"), null, CancellationToken.None);
        var opened = (await site.Editor.OpenAsync(created.Path, CancellationToken.None))!;
        var file = Path.Combine(site.Root, "content", created.Path);
        var written = File.GetLastWriteTimeUtc(file);

        var outcome = (ContentSaved)await site.Editor.SaveAsync(created.Path, opened.Draft with { Tags = [.. opened.Draft.Tags, " "] }, opened.ETag, CancellationToken.None);

        await Assert.That(created.Unchanged).IsFalse();
        await Assert.That(outcome.Unchanged).IsTrue();
        await Assert.That(outcome.ETag).IsEqualTo(opened.ETag);
        await Assert.That(File.GetLastWriteTimeUtc(file)).IsEqualTo(written);
    }

    [Test]
    public async Task Only_the_formatting_of_a_file_is_no_change()
    {
        // F-0177: quotes, field order and the tag list's style, as a person or an app wrote them.
        using var site = new TempSite();
        var file = Path.Combine(site.Root, "content", "posts", "by-hand.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const string Text = "---\ntags: [one, two]\ntitle: \"By hand\"\nslug: by-hand\ndate: 2026-11-09\n---\n\nWritten by hand.\n";
        await File.WriteAllTextAsync(file, Text);
        var opened = (await site.Editor.OpenAsync("posts/by-hand.md", CancellationToken.None))!;

        var same = (ContentSaved)await site.Editor.SaveAsync("posts/by-hand.md", opened.Draft, opened.ETag, CancellationToken.None);
        var changed = (ContentSaved)await site.Editor.SaveAsync("posts/by-hand.md", opened.Draft with { Title = "Edited" }, opened.ETag, CancellationToken.None);

        await Assert.That(same.Unchanged).IsTrue();
        await Assert.That(changed.Unchanged).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(file)).Contains("title: Edited");
    }

    [Test]
    public async Task The_same_text_at_an_old_version_is_still_a_conflict()
    {
        using var site = new TempSite();
        var created = (ContentSaved)await site.Editor.SaveAsync(null, Draft("a"), null, CancellationToken.None);
        var opened = (await site.Editor.OpenAsync(created.Path, CancellationToken.None))!;
        await site.Editor.SaveAsync(created.Path, opened.Draft with { Title = "Same" }, opened.ETag, CancellationToken.None);

        var outcome = await site.Editor.SaveAsync(created.Path, opened.Draft with { Title = "Same" }, opened.ETag, CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<ContentConflict>();
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
        await using var azurite = TestAzurite.Create();
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

    [Test]
    public async Task An_unchanged_text_isnt_rendered_again_at_a_load_or_after_its_save()
    {
        // T-0177: every load rendered every file, drafts too, however many there were.
        using var site = new TempSite();
        await site.Editor.SaveAsync(null, Draft("first", ContentStatus.Draft), null, CancellationToken.None);
        await site.Editor.SaveAsync(null, Draft("second"), null, CancellationToken.None);
        var rendered = site.Renderer.Rendered;

        await site.LoadAsync();
        await site.LoadAsync();
        var afterLoads = site.Renderer.Rendered;
        await site.Editor.SaveAsync(null, Draft("third", ContentStatus.Draft), null, CancellationToken.None);
        var afterSave = site.Renderer.Rendered;
        var path = Path.Combine(site.Root, "content", "posts", "first.md");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("Body of first.", "Changed.", StringComparison.Ordinal));
        await site.LoadAsync();

        await Assert.That(afterLoads).IsEqualTo(rendered);
        await Assert.That(afterSave).IsEqualTo(rendered + 1);
        await Assert.That(site.Renderer.Rendered).IsEqualTo(rendered + 2);
        await Assert.That(site.Catalog.Unpublished.Single(i => i.Slug == "first").Html).Contains("Changed.");
        await Assert.That(site.Catalog.FindPost("second")!.Html).Contains("Body of second.");
    }

    [Test]
    public async Task A_save_takes_at_most_twenty_tags()
    {
        // T-0177. The limit is the editor's, so the admin form and connected apps both meet it.
        using var site = new TempSite();
        var tags = Enumerable.Range(1, ContentDraft.MaxTags + 1).Select(i => $"tag-{i}").ToList();

        var tooMany = await site.Editor.SaveAsync(null, Draft("many") with { Tags = tags }, null, CancellationToken.None);
        var enough = await site.Editor.SaveAsync(null, Draft("enough") with { Tags = tags[..ContentDraft.MaxTags] }, null, CancellationToken.None);

        await Assert.That(((ContentInvalid)tooMany).Message).IsEqualTo($"An item can have at most {ContentDraft.MaxTags} tags; this one has {ContentDraft.MaxTags + 1}. Keep the ones that say most about it.");
        await Assert.That(enough).IsTypeOf<ContentSaved>();
    }

    [Test]
    public async Task An_item_with_more_tags_from_before_keeps_them_but_gets_no_more()
    {
        using var site = new TempSite();
        var saved = (ContentSaved)await site.Editor.SaveAsync(null, Draft("older") with { Tags = ["a"] }, null, CancellationToken.None);
        var path = Path.Combine(site.Root, "content", "posts", "older.md");
        var tags = Enumerable.Range(1, 25).Select(i => $"tag-{i}").ToList();
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("- a\n", string.Concat(tags.Select(t => $"- {t}\n")), StringComparison.Ordinal));
        await site.LoadAsync();
        var opened = await site.Editor.OpenAsync(saved.Path, CancellationToken.None);

        var unpublished = await site.Editor.SaveAsync(saved.Path, opened!.Draft with { Status = ContentStatus.Draft }, opened.ETag, CancellationToken.None);
        var reopened = await site.Editor.OpenAsync(saved.Path, CancellationToken.None);
        var added = await site.Editor.SaveAsync(saved.Path, reopened!.Draft with { Tags = [.. tags, "one-more"] }, reopened.ETag, CancellationToken.None);

        await Assert.That(opened.Draft.Tags.Count).IsEqualTo(25);
        await Assert.That(unpublished).IsTypeOf<ContentSaved>();
        await Assert.That(added).IsTypeOf<ContentInvalid>();
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

        public Markdown.MarkdownRenderer Renderer => _services.GetRequiredService<Markdown.MarkdownRenderer>();

        public Task LoadAsync() => _services.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None);

        public ContentCatalog Catalog => _services.GetRequiredService<ContentCatalogProvider>().Current;

        public void Dispose()
        {
            _services.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
