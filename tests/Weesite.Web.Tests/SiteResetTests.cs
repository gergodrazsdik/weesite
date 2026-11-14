using System.Net;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Sources;
using Weesite.Tests;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The factory reset in site settings' danger zone (T-0100).</summary>
internal sealed partial class SiteResetTests
{
    [Test]
    public async Task Typing_the_sites_name_deletes_all_content_and_settings_and_is_audited()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var wrong = await PostAsync(admin, page, "example site");
        var stillThere = Directory.EnumerateFiles(Path.Combine(copy.Root, "content"), "*", SearchOption.AllDirectories).Count();
        using var reset = await PostAsync(admin, page, "Example Site");
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        using var post = await visitor.GetAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        var settings = await admin.GetStringAsync(new Uri("/admin/settings?reset=1", UriKind.Relative));

        await Assert.That(page).Contains("Danger zone");
        await Assert.That(page).Contains("name, Example Site, to confirm");
        await Assert.That(wrong.Headers.Location?.OriginalString).IsEqualTo("/admin/settings?resetError=1#danger-zone");
        await Assert.That(stillThere).IsGreaterThan(20);
        await Assert.That(reset.Headers.Location?.OriginalString).IsEqualTo("/admin/settings?reset=1#danger-zone");
        await Assert.That(Directory.EnumerateFiles(copy.Root, "*", SearchOption.AllDirectories)).IsEmpty();
        await Assert.That(home).DoesNotContain("Example Site");
        await Assert.That(home).DoesNotContain("Hello, world");
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(audit.Select(e => e.Action)).Contains("site-reset");
        await Assert.That(settings).Contains("Everything was deleted.");
    }

    [Test]
    public async Task Only_admins_with_the_form_token_can_reset()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var author = Client(factory, EditorRole.Author);

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var asAuthor = await PostAsync(author, page, "Example Site");
        using var noToken = new FormUrlEncodedContent([new("confirm", "Example Site")]);
        using var withoutToken = await admin.PostAsync(new Uri("/admin/settings/reset", UriKind.Relative), noToken);

        await Assert.That(asAuthor.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
        await Assert.That(withoutToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(File.Exists(Path.Combine(copy.Root, "settings", "site.json"))).IsTrue();
    }

    [Test]
    public async Task Files_inside_a_git_working_tree_are_never_deleted()
    {
        // The repository's own sample site: a test pointing the reset at it must fail, not empty it.
        var files = new FileSiteFiles(SiteFactory.SeedPath);

        await Assert.That(async () => await files.DeleteAllAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(File.Exists(Path.Combine(SiteFactory.SeedPath, "settings", "site.json"))).IsTrue();
    }

    [Test]
    [Category("containers")]
    public async Task Blob_reset_empties_the_sites_containers_but_keeps_the_version_marker()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        foreach (var (container, name) in new[] { ("content", "posts/a.md"), ("media", "abc-x.png"), ("settings", "site.json"), ("settings", "version"), ("index", "model/post/a.json"), ("dataprotection", "keys.xml") })
        {
            var client = blobs.GetBlobContainerClient(container);
            await client.CreateIfNotExistsAsync();
            await client.UploadBlobAsync(name, BinaryData.FromString("x"));
        }

        var deleted = await new BlobSiteFiles(blobs).DeleteAllAsync(CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(4);
        await Assert.That((await blobs.GetBlobContainerClient("settings").GetBlobClient(ContentContainers.VersionFile).ExistsAsync()).Value).IsTrue();
        await Assert.That((await blobs.GetBlobContainerClient("dataprotection").GetBlobClient("keys.xml").ExistsAsync()).Value).IsTrue();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string page, string confirm)
    {
        using var form = new FormUrlEncodedContent([new("confirm", confirm), new("__RequestVerificationToken", Token().Match(page).Groups[1].Value)]);
        return await client.PostAsync(new Uri("/admin/settings/reset", UriKind.Relative), form);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    [GeneratedRegex("""action="/admin/settings/reset">\s*<input type="hidden" name="__RequestVerificationToken" value="([^"]+)" """)]
    private static partial Regex Token();
}
