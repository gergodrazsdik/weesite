using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Importing a backup zip (T-0051): reviewed first, new files added, overwrites by choice, nothing deleted.</summary>
internal sealed partial class ImportTests
{
    private const string Manifest = """{"format":1,"site":"Other Site","exportedAt":"2026-11-10T09:00:00.0000000+00:00"}""";

    [Test]
    public async Task An_import_adds_new_files_and_overwrites_only_what_is_ticked()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);
        var seedPost = await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "notes-on-static-rendering.md"));
        var siteJson = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));
        var zip = Zip(
            ("weesite-backup.json", Manifest),
            ("content/posts/imported-post.md", Post("Imported post", "imported-post")),
            ("content/posts/hello-world.md", Post("Hello again", "hello-world")),
            ("content/posts/notes-on-static-rendering.md", seedPost),
            ("content/posts/taken-address.md", Post("Taken", "notes-on-static-rendering")),
            ("content/posts/broken.md", "no front matter at all"),
            ("content/posts/notes.txt", "not Markdown"),
            ("settings/site.json", siteJson.Replace("Example Site", "Other Site", StringComparison.Ordinal)),
            ("media/0123456789ab-imported.png", Png),
            ("media/abcdef012345-fake.png", "not an image"),
            ("../escape.md", "nope"));

        using var uploaded = await UploadAsync(admin, zip);
        var review = WebUtility.HtmlDecode(await admin.GetStringAsync(uploaded.Headers.Location));
        var id = Regex.Match(review, "name=\"id\" value=\"([0-9a-f]{32})\"").Groups[1].Value;

        await Assert.That(uploaded.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(review).Contains("From Other Site, exported 2026-11-10T09:00:00.0000000+00:00.");
        await Assert.That(review).Contains("<div><dt>New</dt><dd>2</dd></div>");
        await Assert.That(review).Contains("<div><dt>In both, different</dt><dd>2</dd></div>");
        await Assert.That(review).Contains("<div><dt>The same</dt><dd>1</dd></div>");
        await Assert.That(review).Contains("<div><dt>Can't import</dt><dd>5</dd></div>");
        await Assert.That(review).Contains("media/abcdef012345-fake.png</span>: The file isn't a readable image.");
        await Assert.That(review).Contains("Its address is already used by posts/notes-on-static-rendering.md.");
        await Assert.That(review).Contains("name=\"overwrite\" value=\"content/posts/hello-world.md\"");
        await Assert.That(review).Contains("name=\"overwrite\" value=\"settings/site.json\"");

        using var applied = await PostFormAsync(admin, "/admin/backup/import/apply", review,
            ("id", id), ("action", "apply"), ("overwrite", "content/posts/hello-world.md"));
        var result = WebUtility.HtmlDecode(await admin.GetStringAsync(applied.Headers.Location));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);

        await Assert.That(result).Contains("Imported: 2 added, 1 overwritten, 1 kept as they were.");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "imported-post.md"))).Contains("Imported post");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "hello-world.md"))).Contains("Hello again");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).IsEqualTo(siteJson);
        await Assert.That(File.Exists(Path.Combine(copy.Root, "media", "0123456789ab-imported.png"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(copy.Root, "content", "posts", "broken.md"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(copy.Root, "media", "abcdef012345-fake.png"))).IsFalse();
        await Assert.That(await factory.CreateClient().GetStringAsync(new Uri("/blog/imported-post", UriKind.Relative))).Contains("Imported post");
        await Assert.That(audit.Select(e => e.Action)).Contains("backup-imported");
    }

    [Test]
    public async Task A_zip_that_is_not_a_backup_is_refused_and_a_cancelled_import_is_gone()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);

        using var notBackup = await UploadAsync(admin, Zip(("content/posts/a.md", Post("A", "a"))));
        var refused = WebUtility.HtmlDecode(await admin.GetStringAsync(notBackup.Headers.Location));
        using var backup = await UploadAsync(admin, Zip(("weesite-backup.json", Manifest), ("content/posts/later.md", Post("Later", "later"))));
        var review = await admin.GetStringAsync(backup.Headers.Location);
        var id = Regex.Match(review, "name=\"id\" value=\"([0-9a-f]{32})\"").Groups[1].Value;
        using var cancelled = await PostFormAsync(admin, "/admin/backup/import/apply", review, ("id", id), ("action", "cancel"));
        using var again = await PostFormAsync(admin, "/admin/backup/import/apply", review, ("id", id), ("action", "apply"));

        await Assert.That(refused).Contains("That isn't a Weesite backup zip");
        await Assert.That(cancelled.Headers.Location?.OriginalString).IsEqualTo("/admin/backup?importCancelled=1");
        await Assert.That(again.Headers.Location?.OriginalString).Contains("importError=");
        await Assert.That(File.Exists(Path.Combine(copy.Root, "content", "posts", "later.md"))).IsFalse();
    }

    [Test]
    public async Task Uploads_need_the_antiforgery_token_first_and_an_admin()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);
        using var author = Client(factory, EditorRole.Author);

        using var file = new ByteArrayContent(Zip(("weesite-backup.json", Manifest)));
        using var noToken = new MultipartFormDataContent();
        noToken.Add(file, "file", "backup.zip");
        using var withoutToken = await admin.PostAsync(new Uri("/admin/backup/import", UriKind.Relative), noToken);
        using var asAuthor = await author.GetAsync(new Uri("/admin/backup", UriKind.Relative));

        await Assert.That(withoutToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(asAuthor.Headers.Location?.AbsolutePath).IsEqualTo("/admin/denied");
    }

    [Test]
    [Category("containers")]
    public async Task Blob_staging_keeps_a_zip_until_it_is_deleted()
    {
        await using var azurite = new Testcontainers.Azurite.AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var staging = new BlobImportStaging(new Azure.Storage.Blobs.BlobServiceClient(azurite.GetConnectionString()), TimeProvider.System);
        var zip = Zip(("weesite-backup.json", Manifest));

        var id = await staging.SaveAsync(new MemoryStream(zip), CancellationToken.None);
        await using (var stream = await staging.OpenAsync(id, CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);
            await Assert.That(stream.CanSeek).IsTrue();
            await Assert.That(copy.ToArray()).IsEquivalentTo(zip);
        }

        await staging.DeleteAsync(id, CancellationToken.None);
        await Assert.That(await staging.OpenAsync(id, CancellationToken.None)).IsNull();
        await Assert.That(await staging.OpenAsync("../not-an-id", CancellationToken.None)).IsNull();
    }

    private static string Post(string title, string slug) =>
        $"---\ntitle: \"{title}\"\nslug: {slug}\ndate: 2026-11-01\n---\n\nText of {title}.\n";

    // The sample image from the seed, under another name.
    private static readonly byte[] Png = File.ReadAllBytes(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"));

    private static byte[] Zip(params (string Name, object Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content as byte[] ?? Encoding.UTF8.GetBytes((string)content));
            }
        }

        return buffer.ToArray();
    }

    // Uploads the way the page's form does: the antiforgery token first, then the file.
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] zip)
    {
        var page = await client.GetStringAsync(new Uri("/admin/backup", UriKind.Relative));
        using var token = new StringContent(Token().Match(page).Groups[1].Value);
        using var file = new ByteArrayContent(zip);
        using var form = new MultipartFormDataContent
        {
            { token, "__RequestVerificationToken" },
            { file, "file", "backup.zip" },
        };
        return await client.PostAsync(new Uri("/admin/backup/import", UriKind.Relative), form);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string page, params (string Name, string Value)[] fields)
    {
        var token = Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role = EditorRole.Admin)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
