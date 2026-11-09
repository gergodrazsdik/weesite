using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weesite.Content.Sources;

namespace Weesite.Content.Tests;

/// <summary>Drafts, scheduled items and reloading (ADR 0005).</summary>
internal sealed class PublishingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T12:00:00Z", CultureInfo.InvariantCulture);

    private static string Post(string slug, string extra = "", string date = "2026-01-01") =>
        $"---\ntitle: {slug}\nslug: {slug}\ndate: {date}\n{extra}---\nBody of {slug}.\n";

    private static ParseResult Parse(string slug, string extra = "") => ContentParser.Parse($"posts/{slug}.md", Post(slug, extra));

    [Test]
    [Arguments("", ContentStatus.Published)]
    [Arguments("status: published\n", ContentStatus.Published)]
    [Arguments("status: draft\n", ContentStatus.Draft)]
    [Arguments("status: scheduled\npublish_at: 2026-11-10T09:00:00Z\n", ContentStatus.Scheduled)]
    [Arguments("status: scheduled\npublish_at: 2026-11-10T09:00:00+01:00\n", ContentStatus.Scheduled)]
    public async Task Status_comes_from_front_matter(string extra, ContentStatus status) =>
        await Assert.That(Parse("a", extra).Item?.Status).IsEqualTo(status);

    [Test]
    [Arguments("status: secret\n", "status must be published, draft or scheduled")]
    [Arguments("status: scheduled\n", "a scheduled item needs publish_at")]
    [Arguments("status: scheduled\npublish_at: 2026-11-10T09:00:00\n", "isn't a time with an offset")]
    [Arguments("status: scheduled\npublish_at: tomorrow\n", "isn't a time with an offset")]
    public async Task Invalid_status_or_publish_time_is_reported(string extra, string message) =>
        await Assert.That(Parse("a", extra).Error).Contains(message);

    [Test]
    public async Task Only_published_and_due_items_are_public()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty,
        [
            Parse("live", "tags: [x]\n"),
            Parse("draft", "tags: [x]\nstatus: draft\nredirect_from: [old-draft]\n"),
            Parse("due", "status: scheduled\npublish_at: 2026-11-09T11:00:00Z\n"),
            Parse("later", "status: scheduled\npublish_at: 2026-11-09T13:00:00Z\n"),
            Parse("much-later", "status: scheduled\npublish_at: 2026-12-01T00:00:00Z\n"),
        ], now: Now);

        await Assert.That(catalog.Posts.Select(p => p.Slug)).IsEquivalentTo(["live", "due"]);
        await Assert.That(catalog.Unpublished.Select(p => p.Slug)).IsEquivalentTo(["draft", "later", "much-later"]);
        await Assert.That(catalog.FindPost("draft")).IsNull();
        await Assert.That(catalog.FindPostRedirect("old-draft")).IsNull();
        await Assert.That(catalog.Tags["x"].Select(p => p.Slug)).IsEquivalentTo(["live"]);
        await Assert.That(catalog.NextPublishAt).IsEqualTo(DateTimeOffset.Parse("2026-11-09T13:00:00Z", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task A_draft_cannot_take_a_published_items_slug()
    {
        var catalog = ContentCatalog.Build(SiteSettings.Empty,
        [
            ContentParser.Parse("posts/a.md", Post("same")),
            ContentParser.Parse("posts/b.md", Post("same", "status: draft\n")),
        ], now: Now);

        await Assert.That(catalog.Posts.Count).IsEqualTo(1);
        await Assert.That(catalog.Problems).Contains(p => p.Contains("'same' is already used", StringComparison.Ordinal));
    }

    [Test]
    public async Task Overlapping_loads_end_with_the_newest_content()
    {
        var source = new FakeSource();
        source.Files.Add(new SourceFile("posts/a.md", Post("a")));
        using var app = Services(source, new Clock(Now));
        var loader = app.GetRequiredService<ContentLoader>();
        var content = app.GetRequiredService<ContentCatalogProvider>();
        var gate = new TaskCompletionSource();
        source.Gate = gate.Task;

        // A slow load reads the old files, then a save adds one and loads again (F-0042).
        var slow = loader.LoadAsync(CancellationToken.None);
        await source.Reached.Task;
        source.Files.Add(new SourceFile("posts/b.md", Post("b")));
        var fresh = loader.LoadAsync(CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(slow, fresh);

        await Assert.That(content.Current.Posts.Select(p => p.Slug)).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Reloader_reloads_when_the_version_changes()
    {
        var source = new FakeSource { Version = "1" };
        source.Files.Add(new SourceFile("posts/a.md", Post("a")));
        using var app = Services(source, new Clock(Now));
        var reloader = app.GetRequiredService<ContentReloader>();
        var content = app.GetRequiredService<ContentCatalogProvider>();
        await app.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None);

        var first = await reloader.CheckAsync(CancellationToken.None);
        source.Files.Add(new SourceFile("posts/b.md", Post("b")));
        var unchanged = await reloader.CheckAsync(CancellationToken.None);
        source.Version = "2";
        var changed = await reloader.CheckAsync(CancellationToken.None);

        await Assert.That(first).IsFalse();
        await Assert.That(unchanged).IsFalse();
        await Assert.That(changed).IsTrue();
        await Assert.That(content.Current.Posts.Select(p => p.Slug)).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Reloader_publishes_scheduled_items_when_they_come_due()
    {
        var source = new FakeSource();
        source.Files.Add(new SourceFile("posts/soon.md", Post("soon", "status: scheduled\npublish_at: 2026-11-09T12:05:00Z\n")));
        var clock = new Clock(Now);
        using var app = Services(source, clock);
        var reloader = app.GetRequiredService<ContentReloader>();
        var content = app.GetRequiredService<ContentCatalogProvider>();
        await app.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None);

        var early = await reloader.CheckAsync(CancellationToken.None);
        clock.Now = Now.AddMinutes(6);
        var due = await reloader.CheckAsync(CancellationToken.None);

        await Assert.That(early).IsFalse();
        await Assert.That(due).IsTrue();
        await Assert.That(content.Current.FindPost("soon")).IsNotNull();
        await Assert.That(content.Current.NextPublishAt).IsNull();
    }

    private static ServiceProvider Services(FakeSource source, Clock clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(clock);
        services.AddContentFromFiles(Path.GetTempPath());
        services.Replace(ServiceDescriptor.Singleton<IContentSource>(source));
        return services.BuildServiceProvider();
    }

    private sealed class FakeSource : IContentSource
    {
        public List<SourceFile> Files { get; } = [];

        public string? Version { get; set; }

        // Set to hold the next read after it has taken its snapshot, until the task completes.
        public Task? Gate { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<SourceFile>> ReadContentAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<SourceFile> snapshot = [.. Files];
            if (Gate is { } gate)
            {
                Gate = null;
                Reached.TrySetResult();
                await gate;
            }

            return snapshot;
        }

        public Task<string?> ReadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ReadVersionAsync(CancellationToken cancellationToken) => Task.FromResult(Version);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
