using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Azurite;
using Weesite.Content.Sources;

namespace Weesite.Content.Tests;

/// <summary>The repo's seed/ through both sources: the directory, and Azurite after seeding.</summary>
internal sealed class SourceTests
{
    private static readonly string SeedPath = Path.Combine(FindRepoRoot(), "seed");

    [Test]
    public async Task File_source_reads_the_sample_site()
    {
        var source = new FileContentSource(SeedPath);

        var files = await source.ReadContentAsync(CancellationToken.None);
        var settings = await source.ReadSettingsAsync(CancellationToken.None);

        await Assert.That(files.Select(f => f.Path)).Contains("posts/hello-world.md");
        await Assert.That(SiteSettings.Parse(settings!).Name).IsEqualTo("Example Site");
    }

    [Test]
    public async Task Sample_site_builds_without_problems()
    {
        var source = new FileContentSource(SeedPath);
        var site = SiteSettings.Parse((await source.ReadSettingsAsync(CancellationToken.None))!);
        var files = await source.ReadContentAsync(CancellationToken.None);

        var catalog = ContentCatalog.Build(site, files.Select(f => ContentParser.Parse(f.Path, f.Text)));

        await Assert.That(catalog.Problems).IsEmpty();
        await Assert.That(catalog.Posts.Count).IsEqualTo(2);
        await Assert.That(catalog.FindPage("about")).IsNotNull();
        await Assert.That(site.FindAuthor("example-author")?.Name).IsEqualTo("Example Author");
    }

    [Test]
    [Category("containers")]
    public async Task Blob_source_reads_what_the_seeder_uploaded()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());

        await new ContentSeeder(blobs, NullLogger<ContentSeeder>.Instance).SeedAsync(SeedPath, CancellationToken.None);
        var source = new BlobContentSource(blobs);
        var files = await source.ReadContentAsync(CancellationToken.None);
        var settings = await source.ReadSettingsAsync(CancellationToken.None);

        await Assert.That(files.Select(f => f.Path)).IsEquivalentTo(
            ["pages/about.md", "posts/hello-world.md", "posts/notes-on-static-rendering.md"]);
        await Assert.That(SiteSettings.Parse(settings!).Name).IsEqualTo("Example Site");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weesite.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Weesite.slnx not found above the test binaries.");
    }
}
