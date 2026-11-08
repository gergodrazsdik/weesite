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
        var postFiles = Directory.GetFiles(Path.Combine(SeedPath, "content", "posts"), "*.md").Length;
        await Assert.That(catalog.Posts.Count + catalog.Unpublished.Count).IsEqualTo(postFiles);
        await Assert.That(catalog.Unpublished.Select(i => i.Slug)).IsEquivalentTo(["a-post-from-the-future", "notes-for-a-future-post"]);
        await Assert.That(catalog.NextPublishAt).IsEqualTo(DateTimeOffset.Parse("2030-01-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
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

        var expected = Directory.EnumerateFiles(Path.Combine(SeedPath, "content"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(SeedPath, "content"), f).Replace('\\', '/'));
        await Assert.That(files.Select(f => f.Path)).IsEquivalentTo(expected);
        await Assert.That(SiteSettings.Parse(settings!).Name).IsEqualTo("Example Site");

        // Seeding processed the photo: its WebP variants are stored beside it.
        var media = await new BlobMediaStore(blobs).ListAsync(CancellationToken.None);
        await Assert.That(media).Contains("c0f10a37852f-gradient-480w.webp");
        await Assert.That(media).Contains("c0f10a37852f-gradient-1600w.webp");

        // Seeding again leaves the stored images alone: their names carry their hash (F-0033).
        var image = blobs.GetBlobContainerClient("media").GetBlobClient("c0f10a37852f-gradient.jpg");
        var before = (await image.GetPropertiesAsync()).Value.ETag;
        await new ContentSeeder(blobs, NullLogger<ContentSeeder>.Instance).SeedAsync(SeedPath, CancellationToken.None);
        await Assert.That((await image.GetPropertiesAsync()).Value.ETag).IsEqualTo(before);
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
