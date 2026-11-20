using System.Net;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Sources;
using Weesite.Tests;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The reset on its own page (T-0100, T-0167).</summary>
internal sealed partial class SiteResetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Test]
    public async Task The_page_ticks_what_the_samples_put_back_and_settings_links_to_it()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var author = Client(factory, EditorRole.Author);

        var page = await admin.GetStringAsync(new Uri("/admin/reset", UriKind.Relative));
        var settings = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var asAuthor = await author.GetAsync(new Uri("/admin/reset", UriKind.Relative));

        await Assert.That(Ticked(page)).IsEquivalentTo(["content", "media", "settings", "search-index"]);
        await Assert.That(page).Contains("name=\"part\" value=\"comments\"");
        await Assert.That(page).Contains("name=\"part\" value=\"audit-log\"");
        await Assert.That(page).Contains("name, Example Site, to confirm");
        await Assert.That(settings).Contains("Danger zone");
        await Assert.That(settings).Contains("href=\"/admin/reset\"");
        await Assert.That(settings).DoesNotContain("name=\"confirm\"");
        await Assert.That(asAuthor.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
    }

    [Test]
    public async Task The_default_reset_deletes_content_and_settings_keeps_what_visitors_sent_and_signs_out()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();
        var comments = factory.Services.GetRequiredService<ICommentStore>();
        await comments.SaveAsync(OldComment(), CancellationToken.None);

        var page = await admin.GetStringAsync(new Uri("/admin/reset", UriKind.Relative));
        using var reset = await PostAsync(admin, page, "Example Site", [.. Ticked(page)]);
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        using var post = await visitor.GetAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        var signIn = await visitor.GetStringAsync(new Uri("/admin/sign-in?reset=1", UriKind.Relative));

        await Assert.That(reset.Headers.Location?.OriginalString).IsEqualTo("/admin/sign-in?reset=1");
        await Assert.That(reset.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(Directory.EnumerateFiles(copy.Root, "*", SearchOption.AllDirectories)).IsEmpty();
        await Assert.That(home).DoesNotContain("Example Site");
        await Assert.That(home).DoesNotContain("Hello, world");
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await comments.ListAsync(CancellationToken.None)).HasSingleItem();
        await Assert.That(audit[0].Action).IsEqualTo("site-reset");
        await Assert.That(audit[0].Target).Contains("content ");
        await Assert.That(signIn).Contains("The site was reset and you were signed out.");
    }

    [Test]
    public async Task Ticked_parts_clear_visitors_records_and_added_editors_but_not_the_deployments_admins()
    {
        // T-0166: a post seeded again under the same file mustn't get its old comments back.
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn().WithWebHostBuilder(b => b.UseSetting("Site:Admins", "boss@example.com"));
        using var admin = Client(factory, EditorRole.Admin);
        var services = factory.Services;
        var comments = services.GetRequiredService<ICommentStore>();
        var reactions = services.GetRequiredService<IReactionStore>();
        var messages = services.GetRequiredService<IMessageStore>();
        var views = services.GetRequiredService<IPageViewStore>();
        var editors = services.GetRequiredService<IEditorStore>();
        var oauth = services.GetRequiredService<IOAuthStore>();
        var audit = services.GetRequiredService<IAuditLog>();
        await comments.SaveAsync(OldComment(), CancellationToken.None);
        await reactions.AddAsync("posts/hello-world.md", ReactionKind.Like, 1, Now, CancellationToken.None);
        await messages.AddAsync(new ContactMessage(ContactMessage.NewId(Now), "Bob", "bob@example.org", "Hi.", Now, Read: false, Unverified: false), CancellationToken.None);
        await views.AddAsync([new PageView(Now, "/", null, ClientKind.Browser, null, "0123456789abcdef")], CancellationToken.None);
        await editors.SetAsync(new Editor("author@example.com", EditorRole.Author, "x", Now), CancellationToken.None);
        await oauth.SaveClientAsync(new OAuthClient("client1", "App", ["https://app.example/cb"], FromDocument: false), CancellationToken.None);
        await oauth.AddGrantAsync(new OAuthGrant("grant123", "author@example.com", "client1", "App", "http://localhost/mcp/editor", Now, Now, "h", null, Now.AddDays(30)), CancellationToken.None);
        await audit.WriteAsync(new AuditEntry(Now, "someone@example.com", "publish", "posts/a.md"), CancellationToken.None);
        string[] parts = ["comments", "reactions", "messages", "analytics", "editors", "connected-apps", "audit-log", "spam-counters", "imports"];

        var page = await admin.GetStringAsync(new Uri("/admin/reset", UriKind.Relative));
        using var reset = await PostAsync(admin, page, "Example Site", parts);
        using var adminAfter = Client(factory, EditorRole.Admin);
        var editorsPage = await adminAfter.GetStringAsync(new Uri("/admin/editors", UriKind.Relative));
        var log = await audit.LatestAsync(10, CancellationToken.None);

        await Assert.That(reset.Headers.Location?.OriginalString).IsEqualTo("/admin/sign-in?reset=1");
        await Assert.That(File.Exists(Path.Combine(copy.Root, "settings", "site.json"))).IsTrue();
        await Assert.That(await comments.ListAsync(CancellationToken.None)).IsEmpty();
        await Assert.That((await reactions.GetAsync("posts/hello-world.md", CancellationToken.None)).Total).IsEqualTo(0);
        await Assert.That(((MemoryReactionStore)reactions).Events).IsEmpty();
        await Assert.That((await messages.ListAsync(0, 10, CancellationToken.None)).Total).IsEqualTo(0);
        await Assert.That(((MemoryPageViewStore)views).All).IsEmpty();
        await Assert.That(await editors.ListAsync(CancellationToken.None)).IsEmpty();
        await Assert.That(await oauth.ListClientsAsync(CancellationToken.None)).IsEmpty();
        await Assert.That(await oauth.ListGrantsAsync(null, CancellationToken.None)).IsEmpty();
        await Assert.That(log).HasSingleItem();
        await Assert.That(log[0].Action).IsEqualTo("site-reset");
        await Assert.That(log[0].Target).Contains("comments 1");
        await Assert.That(editorsPage).Contains("boss@example.com");
    }

    [Test]
    public async Task A_wrong_name_or_nothing_ticked_deletes_nothing_and_keeps_the_ticks()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var before = Directory.EnumerateFiles(copy.Root, "*", SearchOption.AllDirectories).Count();

        var page = await admin.GetStringAsync(new Uri("/admin/reset", UriKind.Relative));
        using var wrong = await PostAsync(admin, page, "example site", ["media", "comments"]);
        using var nothing = await PostAsync(admin, page, "Example Site", []);
        var again = await admin.GetStringAsync(new Uri(wrong.Headers.Location!.OriginalString, UriKind.Relative));

        await Assert.That(wrong.Headers.Location?.OriginalString).IsEqualTo("/admin/reset?error=confirm&part=media&part=comments");
        await Assert.That(nothing.Headers.Location?.OriginalString).IsEqualTo("/admin/reset?error=nothing");
        await Assert.That(Directory.EnumerateFiles(copy.Root, "*", SearchOption.AllDirectories).Count()).IsEqualTo(before);
        await Assert.That(Ticked(again)).IsEquivalentTo(["media", "comments"]);
        await Assert.That(again).Contains("the name you typed didn&#x27;t match");
    }

    [Test]
    public async Task Only_admins_with_the_form_token_can_reset()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var author = Client(factory, EditorRole.Author);

        var page = await admin.GetStringAsync(new Uri("/admin/reset", UriKind.Relative));
        using var asAuthor = await PostAsync(author, page, "Example Site", ["settings"]);
        using var noToken = new FormUrlEncodedContent([new("confirm", "Example Site"), new("part", "settings")]);
        using var withoutToken = await admin.PostAsync(new Uri("/admin/reset", UriKind.Relative), noToken);

        await Assert.That(asAuthor.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
        await Assert.That(withoutToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(File.Exists(Path.Combine(copy.Root, "settings", "site.json"))).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Files_inside_a_git_working_tree_are_never_deleted(bool gitFolder)
    {
        // A sample site below a working tree's root, which has a .git folder (a clone) or a .git file (a worktree or
        // submodule): a test pointing the reset at it must fail, not empty it. Not the repository's own seed: run
        // outside a working tree, from a source archive, the guard rightly let the reset through and emptied it (F-0173).
        var tree = Directory.CreateTempSubdirectory("weesite-tree-");
        try
        {
            var git = Path.Combine(tree.FullName, ".git");
            if (gitFolder)
            {
                Directory.CreateDirectory(git);
            }
            else
            {
                await File.WriteAllTextAsync(git, "gitdir: /elsewhere/.git/worktrees/tree\n");
            }

            var site = Path.Combine(tree.FullName, "seed");
            var settings = Path.Combine(site, "settings", "site.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            await File.WriteAllTextAsync(settings, "{}");
            var files = new FileSiteFiles(site);

            await Assert.That(async () => await files.DeleteAsync(["settings"], CancellationToken.None)).Throws<InvalidOperationException>();
            await Assert.That(File.Exists(settings)).IsTrue();
        }
        finally
        {
            tree.Delete(recursive: true);
        }
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

        var deleted = await new BlobSiteFiles(blobs).DeleteAsync(["content", "media", "settings", "index"], CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(4);
        await Assert.That((await blobs.GetBlobContainerClient("settings").GetBlobClient(ContentContainers.VersionFile).ExistsAsync()).Value).IsTrue();
        await Assert.That((await blobs.GetBlobContainerClient("dataprotection").GetBlobClient("keys.xml").ExistsAsync()).Value).IsTrue();
    }

    private static Comment OldComment() =>
        new(Comment.NewId(Now), "posts/hello-world.md", "Ada", "Old words.", null, CommentStatus.Approved, Now, null, Now, "x");

    private static IEnumerable<string> Ticked(string page) => TickedPart().Matches(page).Select(m => m.Groups[1].Value);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string page, string confirm, string[] parts)
    {
        using var form = new FormUrlEncodedContent([new("confirm", confirm), new("__RequestVerificationToken", Token().Match(page).Groups[1].Value), .. parts.Select(p => new KeyValuePair<string, string>("part", p))]);
        return await client.PostAsync(new Uri("/admin/reset", UriKind.Relative), form);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    [GeneratedRegex("""action="/admin/reset">\s*<input type="hidden" name="__RequestVerificationToken" value="([^"]+)" """)]
    private static partial Regex Token();

    [GeneratedRegex("""name="part" value="([a-z-]+)" checked""")]
    private static partial Regex TickedPart();
}
