using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Sources;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>"Export everything" (brief, Backups and export; T-0051).</summary>
internal sealed class BackupTests
{
    [Test]
    public async Task Admins_download_the_content_and_settings_as_a_zip_and_it_is_audited()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        using var response = await admin.GetAsync(new Uri("/admin/backup/file", UriKind.Relative));
        await using var body = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        using var manifest = JsonDocument.Parse(await ReadAsync(zip, SiteBackup.ManifestName));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/zip");
        await Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar).Matches(@"^weesite-backup-\d{4}-\d{2}-\d{2}\.zip$");
        await Assert.That(manifest.RootElement.GetProperty("format").GetInt32()).IsEqualTo(1);
        await Assert.That(manifest.RootElement.GetProperty("site").GetString()).IsEqualTo("Example Site");
        await Assert.That(names).Contains("content/posts/hello-world.md");
        await Assert.That(names).Contains("content/posts/notes-for-a-future-post.md");
        await Assert.That(names).Contains("content/pages/about.md");
        await Assert.That(names).Contains("settings/site.json");
        await Assert.That(await ReadAsync(zip, "content/posts/hello-world.md"))
            .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(SiteFactory.SeedPath, "content", "posts", "hello-world.md")));
        await Assert.That(audit.Select(e => e.Action)).Contains("backup-downloaded");
    }

    [Test]
    public async Task The_backup_holds_the_media_files_as_they_are()
    {
        var media = new FileMediaStore(Path.Combine(SiteFactory.SeedPath, "media"));
        var source = new FileContentSource(SiteFactory.SeedPath);
        using var buffer = new MemoryStream();

        await SiteBackup.WriteAsync(buffer, source, media, "Example Site", DateTimeOffset.UtcNow, CancellationToken.None);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entry = zip.GetEntry("media/b368a21f9e93-sample.png")!;
        await using var stream = await entry.OpenAsync();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        await Assert.That(zip.Entries.Count(e => e.FullName.StartsWith("media/", StringComparison.Ordinal))).IsEqualTo(Directory.GetFiles(Path.Combine(SiteFactory.SeedPath, "media")).Length);
        await Assert.That(copy.ToArray()).IsEquivalentTo(await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png")));
    }

    [Test]
    public async Task Only_admins_can_download_it()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);
        using var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var asAuthor = await author.GetAsync(new Uri("/admin/backup/file", UriKind.Relative));
        using var signedOut = await visitor.GetAsync(new Uri("/admin/backup/file", UriKind.Relative));

        await Assert.That(asAuthor.Headers.Location?.AbsolutePath).IsEqualTo("/admin/denied");
        await Assert.That(signedOut.Headers.Location?.AbsolutePath).IsEqualTo("/admin/sign-in");
        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
    }

    private static async Task<string> ReadAsync(ZipArchive zip, string name)
    {
        await using var stream = await zip.GetEntry(name)!.OpenAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }
}
