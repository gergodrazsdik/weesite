using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content.Sources;
using Weesite.Tests;

namespace Weesite.Content.Tests;

/// <summary>The repo's seed/ through both sources: the directory, and Azurite after seeding.</summary>
internal sealed class SourceTests
{
    private static readonly string SeedPath = Weesite.Tests.RepoPaths.Seed;

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
        await Assert.That(catalog.Unpublished.Select(i => i.Slug)).IsEquivalentTo([
            "a-post-from-the-future", "notes-for-a-future-post", "allee-des-philosophes-at-chantilly",
            "red-anchors-at-chacachacare", "a-keyboard-for-writing-prose", "a-1952-hudson-hornet-in-quebec-city",
        ]);
        await Assert.That(catalog.NextPublishAt).IsEqualTo(DateTimeOffset.Parse("2099-01-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(catalog.FindPage("about")).IsNotNull();
        await Assert.That(site.FindAuthor("example-author")?.Name).IsEqualTo("Example Author");
    }

    [Test]
    [Category("containers")]
    public async Task Blob_source_reads_what_the_seeder_uploaded()
    {
        await using var azurite = TestAzurite.Create();
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

    [Test]
    [Category("containers")]
    public async Task Image_sizes_are_kept_with_the_images_so_a_load_needn_t_read_them()
    {
        // F-0107: each start downloaded every original image to measure it.
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        await new ContentSeeder(blobs, NullLogger<ContentSeeder>.Instance).SeedAsync(SeedPath, CancellationToken.None);
        IMediaStore store = new BlobMediaStore(blobs);
        const string Name = "c0f10a37852f-gradient.jpg";
        var data = await File.ReadAllBytesAsync(Path.Combine(SeedPath, "media", Name));
        var size = Media.ImageProcessor.Measure(data);
        var services = new ServiceCollection().AddSingleton(blobs).AddLogging()
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddContentFromBlobs();
        await using var provider = services.BuildServiceProvider();

        var seeded = (await store.ListWithSizesAsync(CancellationToken.None)).Single(f => f.Name == Name);
        await provider.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None);
        var loaded = (await store.ListWithSizesAsync(CancellationToken.None)).Single(f => f.Name == Name);
        await store.WriteAsync("0123456789ab-copy.jpg", data, CancellationToken.None);
        var uploaded = (await store.ListWithSizesAsync(CancellationToken.None)).Single(f => f.Name == "0123456789ab-copy.jpg");
        var variant = (await store.ListWithSizesAsync(CancellationToken.None)).Single(f => f.Name == "c0f10a37852f-gradient-480w.webp");

        await Assert.That(seeded.Size).IsNull();
        await Assert.That(loaded.Size).IsEqualTo(size);
        await Assert.That(uploaded.Size).IsEqualTo(size);
        await Assert.That(variant.Size).IsNull();
    }

    [Test]
    [Category("containers")]
    public async Task Seeding_again_writes_only_changed_files_and_keeps_edits()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        var seed = Directory.CreateTempSubdirectory("weesite-seed-");
        try
        {
            Directory.CreateDirectory(Path.Combine(seed.FullName, "content", "posts"));
            var untouched = Path.Combine(seed.FullName, "content", "posts", "untouched.md");
            var changed = Path.Combine(seed.FullName, "content", "posts", "changed.md");
            var edited = Path.Combine(seed.FullName, "content", "posts", "edited.md");
            foreach (var file in new[] { untouched, changed, edited })
            {
                await File.WriteAllTextAsync(file, "first\n");
            }

            var seeder = new ContentSeeder(blobs, NullLogger<ContentSeeder>.Instance);
            var content = blobs.GetBlobContainerClient("content");
            await seeder.SeedAsync(seed.FullName, CancellationToken.None);
            async Task<ETag> Tag(string name) => (await content.GetBlobClient(name).GetPropertiesAsync()).Value.ETag;
            var untouchedBefore = await Tag("posts/untouched.md");
            await content.GetBlobClient("posts/edited.md").UploadAsync(BinaryData.FromString("edited in the editor\n"), overwrite: true);
            await File.WriteAllTextAsync(changed, "second\n");
            await File.WriteAllTextAsync(edited, "second\n");

            await seeder.SeedAsync(seed.FullName, CancellationToken.None);

            async Task<string> Text(string name) => (await content.GetBlobClient(name).DownloadContentAsync()).Value.Content.ToString();
            await Assert.That(await Tag("posts/untouched.md")).IsEqualTo(untouchedBefore);
            await Assert.That(await Text("posts/changed.md")).IsEqualTo("second\n");
            await Assert.That(await Text("posts/edited.md")).IsEqualTo("edited in the editor\n");
        }
        finally
        {
            seed.Delete(recursive: true);
        }
    }

}
