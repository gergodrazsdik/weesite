using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Media;

namespace Weesite.Content.Tests;

/// <summary>Uploads: checks, names, processing (brief, Images and attachments).</summary>
internal sealed class MediaLibraryTests
{
    private static readonly string SeedMedia = Path.Combine(FindRepoRoot(), "seed", "media");

    [Test]
    public async Task An_image_is_stored_under_its_hash_with_its_variants_and_indexed()
    {
        using var site = new Site();
        var photo = await File.ReadAllBytesAsync(Path.Combine(SeedMedia, "c0f10a37852f-gradient.jpg"));

        var outcome = await site.Library.UploadAsync("My Photo.JPEG", photo, CancellationToken.None);

        var item = ((MediaUploaded)outcome).Item;
        await Assert.That(item.Name).Matches("^[0-9a-f]{12}-my-photo\\.jpg$");
        await Assert.That(item.Path).IsEqualTo("/media/" + item.Name);
        await Assert.That((item.Width, item.Height)).IsEqualTo((1800, 1200));
        await Assert.That(File.Exists(Path.Combine(site.Root, "media", item.Name.Replace(".jpg", "-960w.webp", StringComparison.Ordinal)))).IsTrue();
        await Assert.That(site.Catalog.Media.Find(item.Name)).IsNotNull();
    }

    [Test]
    public async Task The_same_file_twice_is_stored_once()
    {
        using var site = new Site();
        var png = await File.ReadAllBytesAsync(Path.Combine(SeedMedia, "b368a21f9e93-sample.png"));

        var first = (MediaUploaded)await site.Library.UploadAsync("a.png", png, CancellationToken.None);
        var second = (MediaUploaded)await site.Library.UploadAsync("a.png", png, CancellationToken.None);

        await Assert.That(second.Item.Name).IsEqualTo(first.Item.Name);
        await Assert.That((await site.Library.ListAsync(CancellationToken.None)).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("tool.exe", "MZ", ".exe files aren't accepted")]
    [Arguments("fake.png", "not an image", "isn't a readable image")]
    [Arguments("paper.pdf", "<html>", "isn't a PDF")]
    [Arguments("drawing.svg", "<html><script>alert(1)</script>", "isn't an SVG image")]
    [Arguments("archive.zip", "hello", "isn't a ZIP archive")]
    [Arguments("notes.txt", "a\0b", "isn't plain text")]
    [Arguments("empty.txt", "", "between 1 byte")]
    public async Task Files_whose_contents_dont_match_are_refused(string name, string contents, string reason)
    {
        using var site = new Site();

        var outcome = await site.Library.UploadAsync(name, System.Text.Encoding.UTF8.GetBytes(contents), CancellationToken.None);

        await Assert.That(((MediaRejected)outcome).Reason).Contains(reason);
        await Assert.That(await site.Library.ListAsync(CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task Attachments_that_match_their_type_are_stored()
    {
        using var site = new Site();

        var pdf = await site.Library.UploadAsync("Report 2026.pdf", "%PDF-1.7\n..."u8.ToArray(), CancellationToken.None);
        var svg = await site.Library.UploadAsync("diagram.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), CancellationToken.None);

        await Assert.That(((MediaUploaded)pdf).Item.Name).EndsWith("-report-2026.pdf");
        await Assert.That(((MediaUploaded)svg).Item.IsImage).IsFalse();
    }

    [Test]
    [Arguments("Árvíztűrő Tükör.JPEG", "arvizturo-tukor.jpg")]
    [Arguments("../../etc/passwd.txt", "passwd.txt")]
    [Arguments("???.png", "file.png")]
    [Arguments("a  b__c.PNG", "a-b-c.png")]
    public async Task Names_are_made_safe(string uploaded, string safe) =>
        await Assert.That(MediaLibrary.SafeName(uploaded)).IsEqualTo(safe);

    [Test]
    public async Task Saving_refuses_images_without_alt_text()
    {
        using var site = new Site();
        var editor = site.Services.GetRequiredService<ContentEditor>();
        var draft = new ContentDraft { Title = "Pics", Slug = "pics", Date = new DateOnly(2026, 11, 9), Status = ContentStatus.Draft, Body = "![](/media/x.png)" };

        var outcome = await editor.SaveAsync(null, draft, null, CancellationToken.None);

        await Assert.That(((ContentInvalid)outcome).Message).Contains("needs a description");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weesite.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Weesite.slnx not found.");
    }

    // A site in a temporary directory.
    private sealed class Site : IDisposable
    {
        public Site()
        {
            Root = Directory.CreateTempSubdirectory("weesite-media-").FullName;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddContentFromFiles(Root);
            Services = services.BuildServiceProvider();
            Services.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public string Root { get; }

        public ServiceProvider Services { get; }

        public MediaLibrary Library => Services.GetRequiredService<MediaLibrary>();

        public ContentCatalog Catalog => Services.GetRequiredService<ContentCatalogProvider>().Current;

        public void Dispose()
        {
            Services.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
