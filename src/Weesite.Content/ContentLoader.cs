using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.Content.Markdown;
using Weesite.Content.Media;
using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary>
/// Loads the catalog when the app starts, before it serves requests (ADR 0002: content loads at
/// startup). With <c>Seed:Path</c> set, it first uploads that directory into Blob Storage.
/// </summary>
/// <param name="services">Resolves the source and, for seeding, the seeder.</param>
/// <param name="configuration">Reads <c>Seed:Path</c>.</param>
/// <param name="provider">Receives the catalog.</param>
/// <param name="renderer">Renders each item's Markdown once, at load.</param>
/// <param name="logger">Reports what was loaded and skipped.</param>
public sealed partial class ContentLoader(
    IServiceProvider services,
    IConfiguration configuration,
    ContentCatalogProvider provider,
    MarkdownRenderer renderer,
    ILogger<ContentLoader> logger) : IHostedService, IDisposable
{
    // The last media index: its sizes carry over to the next load (loads don't overlap).
    private MediaIndex? _media;
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedPath = configuration["Seed:Path"];
        if (!string.IsNullOrWhiteSpace(seedPath) && services.GetService<ContentSeeder>() is { } seeder)
        {
            await seeder.SeedAsync(seedPath, cancellationToken);
        }

        await LoadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _loading.Dispose();

    // One load at a time (F-0042): a load that started reading before a save and finished after
    // it would otherwise replace the newer catalog with an older one. In order, the later load
    // always finishes later.
    private readonly SemaphoreSlim _loading = new(1, 1);

    /// <summary>Reads the content and settings, builds the catalog and makes it current.</summary>
    /// <param name="cancellationToken">Stops the load.</param>
    /// <returns>A task.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _loading.WaitAsync(cancellationToken);
        try
        {
            await LoadNowAsync(cancellationToken);
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task LoadNowAsync(CancellationToken cancellationToken)
    {
        // Timed in parts (F-0107): the media index reads each new image once to measure it.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var media = await BuildMediaIndexAsync(cancellationToken);
        var measured = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var source = services.GetRequiredService<IContentSource>();
        var settingsJson = await source.ReadSettingsAsync(cancellationToken);
        var site = (settingsJson is null ? SiteSettings.Empty : SiteSettings.Parse(settingsJson))
            .WithProfiles(AuthorProfiles.Parse(await source.ReadSettingsFileAsync(ContentContainers.AuthorsFile, cancellationToken)));
        var files = await source.ReadContentAsync(cancellationToken);
        var read = System.Diagnostics.Stopwatch.GetElapsedTime(started) - measured;
        var now = services.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var catalog = ContentCatalog.Build(site, files.Select(f => Render(ContentParser.Parse(f.Path, f.Text, site.Language), media)), media, now);

        foreach (var problem in catalog.Problems)
        {
            LogSkipped(problem);
        }

        var total = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        LogLoaded(catalog.Posts.Count, catalog.Pages.Count, catalog.Problems.Count, (long)total.TotalMilliseconds, (long)measured.TotalMilliseconds, (long)read.TotalMilliseconds, (long)(total - measured - read).TotalMilliseconds);
        provider.Set(catalog);
    }

    private ParseResult Render(ParseResult result, MediaIndex media)
    {
        if (result.Item is not { } item)
        {
            return result;
        }

        var rendered = renderer.Render(item.Markdown, media);
        foreach (var problem in rendered.Problems)
        {
            LogSkipped($"{item.SourcePath}: {problem}");
        }

        return result with { Item = item with { Html = rendered.Html, PlainText = rendered.PlainText } };
    }

    // Sizes come from the originals' headers, read once per image; the variants are found by name.
    private async Task<MediaIndex> BuildMediaIndexAsync(CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IMediaStore>();
        var listed = await store.ListWithSizesAsync(cancellationToken);
        var sizes = listed.Where(f => f.Size is not null).ToDictionary(f => f.Name, f => f.Size, StringComparer.Ordinal);
        return _media = await MediaIndex.BuildAsync([.. listed.Select(f => f.Name)], async (name, ct) =>
        {
            // Kept with the image (F-0107); an image stored before that is read once, and its size
            // kept for the next load.
            if (sizes.TryGetValue(name, out var kept))
            {
                return kept;
            }

            var file = await store.OpenAsync(name, ct);
            if (file is null)
            {
                return null;
            }

            await using (file.Content)
            {
                using var buffer = new MemoryStream();
                await file.Content.CopyToAsync(buffer, ct);
                try
                {
                    var size = ImageProcessor.Measure(buffer.ToArray());
                    await KeepSizeAsync(store, name, size, ct);
                    return size;
                }
                catch (NetVips.VipsException ex)
                {
                    // A broken image renders without size or srcset; it doesn't stop the site.
                    LogSkipped($"media/{name}: unreadable image ({ex.Message})");
                    return null;
                }
            }
        }, cancellationToken, _media);
    }

    // Best effort: without it the next load measures the image again.
    private async Task KeepSizeAsync(IMediaStore store, string name, (int Width, int Height) size, CancellationToken cancellationToken)
    {
        try
        {
            await store.KeepSizeAsync(name, size.Width, size.Height, cancellationToken);
        }
        catch (Azure.RequestFailedException ex)
        {
            LogSizeNotKept(name, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The size of media/{Name} couldn't be kept with it; it's measured again next time")]
    private partial void LogSizeNotKept(string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped content: {Problem}")]
    private partial void LogSkipped(string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Posts} posts and {Pages} pages ({Skipped} skipped) in {Milliseconds} ms: media {Media} ms, reading {Reading} ms, parsing and rendering {Rendering} ms")]
    private partial void LogLoaded(int posts, int pages, int skipped, long milliseconds, long media, long reading, long rendering);
}
