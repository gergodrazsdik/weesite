using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary>
/// Loads the catalog when the app starts, before it serves requests (ADR 0002: content loads at
/// startup). With <c>Seed:Path</c> set, it first uploads that directory into Blob Storage.
/// </summary>
/// <param name="services">Resolves the source and, for seeding, the seeder.</param>
/// <param name="configuration">Reads <c>Seed:Path</c>.</param>
/// <param name="provider">Receives the catalog.</param>
/// <param name="logger">Reports what was loaded and skipped.</param>
public sealed partial class ContentLoader(
    IServiceProvider services,
    IConfiguration configuration,
    ContentCatalogProvider provider,
    ILogger<ContentLoader> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedPath = configuration["Seed:Path"];
        if (!string.IsNullOrWhiteSpace(seedPath) && services.GetService<ContentSeeder>() is { } seeder)
        {
            await seeder.SeedAsync(seedPath, cancellationToken);
        }

        var source = services.GetRequiredService<IContentSource>();
        var settingsJson = await source.ReadSettingsAsync(cancellationToken);
        var site = settingsJson is null ? SiteSettings.Empty : SiteSettings.Parse(settingsJson);
        var files = await source.ReadContentAsync(cancellationToken);
        var catalog = ContentCatalog.Build(site, files.Select(f => ContentParser.Parse(f.Path, f.Text, site.Language)));

        foreach (var problem in catalog.Problems)
        {
            LogSkipped(problem);
        }

        LogLoaded(catalog.Posts.Count, catalog.Pages.Count, catalog.Problems.Count);
        provider.Set(catalog);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped content: {Problem}")]
    private partial void LogSkipped(string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Posts} posts and {Pages} pages ({Skipped} skipped)")]
    private partial void LogLoaded(int posts, int pages, int skipped);
}
