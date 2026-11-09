using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The posts list and the edit page (no JavaScript), against a copy of the sample site.</summary>
internal sealed partial class EditorPageTests
{
    [Test]
    public async Task Admins_see_every_post_with_its_status_and_authors_only_their_own()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var author = Client(factory, EditorRole.Author);

        var asAdmin = await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));
        var asAuthor = await author.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));

        await Assert.That(asAdmin).Contains("Notes for a future post</a></th>\n                        <td>Draft</td>");
        await Assert.That(asAdmin).Contains("Scheduled for 2030-01-01 08:00 UTC");
        await Assert.That(asAuthor).Contains("Nothing yet.");
    }

    [Test]
    public async Task Authors_cannot_open_pages_or_other_peoples_posts()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        using var pages = await author.GetAsync(new Uri("/admin/pages", UriKind.Relative));
        using var post = await author.GetAsync(new Uri("/admin/edit?path=posts%2Fhello-world.md", UriKind.Relative));

        await Assert.That(pages.Headers.Location?.AbsolutePath).IsEqualTo("/admin/denied");
        await Assert.That(post.Headers.Location?.AbsolutePath).IsEqualTo("/admin/denied");
    }

    [Test]
    public async Task An_author_writes_previews_and_publishes_a_post()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        var blank = await author.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var preview = await Post(author, "/admin/new/post", blank, "preview", ("Title", "My first post"), ("Slug", "my-first-post"), ("Status", "draft"), ("Body", "Hello **there**."));
        var previewHtml = await preview.Content.ReadAsStringAsync();
        using var save = await Post(author, "/admin/new/post", blank, "save", ("Title", "My first post"), ("Slug", "my-first-post"), ("Status", "published"), ("Date", "2026-11-09"), ("Body", "Hello **there**."));
        var mine = await author.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));
        using var publicPage = await factory.CreateClient().GetAsync(new Uri("/blog/my-first-post", UriKind.Relative));

        await Assert.That(previewHtml).Contains("<p>Hello <strong>there</strong>.</p>");
        await Assert.That(save.Headers.Location?.PathAndQuery).IsEqualTo("/admin/edit?path=posts%2Fmy-first-post.md&saved=1");
        await Assert.That(mine).Contains("My first post");
        await Assert.That(publicPage.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "my-first-post.md"))).Contains("author: editor-");
    }

    [Test]
    public async Task A_stale_save_shows_the_conflict_and_keeps_the_text()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var page = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fhello-world.md", UriKind.Relative));
        var fields = Fields(page);
        using var first = await Post(admin, "/admin/edit", page, "save", [.. fields, ("Title", "Hello, again")]);
        using var second = await Post(admin, "/admin/edit", page, "save", [.. fields, ("Title", "Hello, a third time"), ("Body", "My edit.")]);
        var html = await second.Content.ReadAsStringAsync();

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(html).Contains("Someone saved this since you opened it");
        await Assert.That(html).Contains("My edit.");
    }

    [Test]
    public async Task Invalid_input_is_reported_and_nothing_is_saved()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var blank = await admin.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var taken = await Post(admin, "/admin/new/post", blank, "save", ("Title", "Dup"), ("Slug", "hello-world"), ("Status", "draft"), ("Date", "2026-11-09"));
        using var badDate = await Post(admin, "/admin/new/post", blank, "save", ("Title", "X"), ("Slug", "x"), ("Status", "draft"), ("Date", "yesterday"));

        await Assert.That(await taken.Content.ReadAsStringAsync()).Contains("already used by");
        await Assert.That(await badDate.Content.ReadAsStringAsync()).Contains("isn&#x27;t a date");
        await Assert.That(File.Exists(Path.Combine(copy.Root, "content", "posts", "x.md"))).IsFalse();
    }

    [Test]
    public async Task Preview_endpoint_renders_markdown_for_editors_only()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var editor = Client(factory, EditorRole.Author);
        using var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await editor.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var form = new FormUrlEncodedContent([new("markdown", "Some **bold** <b>html</b>"), new("__RequestVerificationToken", Token().Match(page).Groups[1].Value)]);
        using var rendered = await editor.PostAsync(new Uri("/admin/preview", UriKind.Relative), form);
        using var noToken = new FormUrlEncodedContent([new("markdown", "x")]);
        using var refused = await editor.PostAsync(new Uri("/admin/preview", UriKind.Relative), noToken);
        using var anonymous = new FormUrlEncodedContent([new("markdown", "x")]);
        using var signedOut = await visitor.PostAsync(new Uri("/admin/preview", UriKind.Relative), anonymous);

        await Assert.That(await rendered.Content.ReadAsStringAsync()).IsEqualTo("<p>Some <strong>bold</strong> &lt;b&gt;html&lt;/b&gt;</p>\n");
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
    }

    [Test]
    public async Task Authors_upload_an_image_and_get_the_markdown_to_paste()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);
        var png = await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"));

        var page = await author.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        using var noAlt = await Upload(author, page, "square.png", png, alt: "");
        using var uploaded = await Upload(author, page, "square.png", png, alt: "A blue square");
        var after = await author.GetStringAsync(uploaded.Headers.Location!);
        using var fake = await Upload(author, page, "fake.png", "not an image"u8.ToArray(), alt: "x");
        using var noToken = await Upload(author, null, "square.png", png, alt: "x");

        await Assert.That(noAlt.Headers.Location?.OriginalString).Contains("error=");
        await Assert.That(uploaded.Headers.Location?.OriginalString).Contains("uploaded=");
        await Assert.That(after).Contains("![A blue square](/media/");
        await Assert.That(WebUtility.UrlDecode(fake.Headers.Location?.OriginalString)).Contains("isn't a readable image");
        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // Posts the media page's upload form (multipart), with the page's token unless page is null.
    private static async Task<HttpResponseMessage> Upload(HttpClient client, string? page, string name, byte[] data, string alt)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(data);
        form.Add(file, "file", name);
        using var altField = new StringContent(alt);
        form.Add(altField, "alt");
        using var token = new StringContent(page is null ? "" : Token().Match(page).Groups[1].Value);
        if (page is not null)
        {
            form.Add(token, "__RequestVerificationToken");
        }

        return await client.PostAsync(new Uri("/admin/media", UriKind.Relative), form);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    // Posts the editor form the way the browser does: its hidden fields and token, the given
    // fields (as Form.*), and the button pressed.
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string page, string action, params (string Name, string Value)[] fields)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "editor",
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value,
            ["Action"] = action,
        };
        foreach (Match hidden in Hidden().Matches(page))
        {
            values[hidden.Groups[1].Value] = WebUtility.HtmlDecode(hidden.Groups[2].Value);
        }

        foreach (var (name, value) in fields)
        {
            values["Form." + name] = value;
        }

        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    // The visible fields as the page filled them, to post an edit unchanged but for a few.
    private static (string Name, string Value)[] Fields(string page) =>
    [
        .. InputTag().Matches(page)
            .Select(m => m.Value)
            .Where(tag => !tag.Contains("type=\"hidden\"", StringComparison.Ordinal) && !tag.Contains("type=\"radio\"", StringComparison.Ordinal) && !tag.Contains("type=\"checkbox\"", StringComparison.Ordinal))
            .Select(tag => (Name: Attribute(tag, "name"), Value: Attribute(tag, "value")))
            .Where(f => f.Name.StartsWith("Form.", StringComparison.Ordinal))
            .Select(f => (f.Name["Form.".Length..], WebUtility.HtmlDecode(f.Value))),
        ("Body", WebUtility.HtmlDecode(Textarea().Match(page).Groups[1].Value)),
        ("Status", "published"),
    ];

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("<input type=\"hidden\" name=\"(Form\\.[A-Za-z]+)\" value=\"([^\"]*)\"")]
    private static partial Regex Hidden();

    private static string Attribute(string tag, string name) =>
        Regex.Match(tag, $" {name}=\"([^\"]*)\"").Groups[1].Value;

    [GeneratedRegex("<input [^>]*>")]
    private static partial Regex InputTag();

    [GeneratedRegex("<textarea[^>]*name=\"Form\\.Body\"[^>]*>([^<]*)</textarea>")]
    private static partial Regex Textarea();
}

/// <summary>A copy of the sample site's content and settings, so tests can save into it.</summary>
internal sealed class SiteCopy : IDisposable
{
    public SiteCopy()
    {
        Root = Directory.CreateTempSubdirectory("weesite-site-").FullName;
        foreach (var folder in new[] { "content", "settings" })
        {
            var source = Path.Combine(SiteFactory.SeedPath, folder);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(Root, folder, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }

    public string Root { get; }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
