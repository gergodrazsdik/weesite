using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

        await Assert.That(asAdmin).Contains("<span class=\"slug\">/blog/notes-for-a-future-post</span></th>\n                        <td><span class=\"pill draft\">Draft</span></td>");
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

        await Assert.That(pages.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
        await Assert.That(post.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");

        // F-0087: the refusal says it's about the role, inside the admin, with a way back.
        using var forbidden = await author.GetAsync(new Uri("/admin/forbidden", UriKind.Relative));
        var html = WebUtility.HtmlDecode(await forbidden.Content.ReadAsStringAsync());
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(html).Contains("<h1>For admins only</h1>");
        await Assert.That(html).Contains("Your role can't open this page or make this change.");
        await Assert.That(html).Contains("<a class=\"btn\" href=\"/admin/posts\">Back to your posts</a>");
        await Assert.That(html).Contains("<nav aria-label=\"Editor\">");
        await Assert.That(html).DoesNotContain("list of editors");
    }

    [Test]
    public async Task With_a_menu_of_its_own_a_page_says_so_instead_of_offering_the_menu_box()
    {
        using var copy = new SiteCopy();
        var about = Path.Combine(copy.Root, "content", "pages", "about.md");
        await File.WriteAllTextAsync(about, "---\nmenu: true\n" + (await File.ReadAllTextAsync(about))["---\n".Length..]);
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var page = await admin.GetStringAsync(new Uri("/admin/edit?path=pages%2Fabout.md", UriKind.Relative));
        using var saved = await Post(admin, "/admin/edit", page, "save", Fields(page));

        // The seed's site.json has a menu: the box would do nothing (F-0083).
        await Assert.That(page).DoesNotContain(">Show in the menu</label>");
        await Assert.That(page).Contains("The site has its own menu, so pages don&#x27;t add themselves to it.");
        await Assert.That(page).Contains("<a href=\"/admin/menu\">Menu</a>");
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(await File.ReadAllTextAsync(about)).Contains("menu: true");
    }

    [Test]
    public async Task The_bar_shows_whats_stored_and_save_is_at_both_ends()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        // A save that fails with a changed status and slug (F-0091 L6): the bar keeps the stored ones.
        var page = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fhello-world.md", UriKind.Relative));
        using var failed = await Post(admin, "/admin/edit", page, "save", [.. Fields(page), ("Status", "scheduled"), ("Slug", "renamed"), ("PublishAt", "")]);
        var html = await failed.Content.ReadAsStringAsync();

        await Assert.That(html).Contains("role=\"alert\"");
        await Assert.That(html).Contains("<span aria-current=\"page\">hello-world</span>");
        await Assert.That(html).Contains("<span class=\"pill published\">Published</span>");

        // No server preview (F-0093), no draft help (F-0099), and Save at the start and the end (L26).
        await Assert.That(page).DoesNotContain("value=\"preview\"");
        await Assert.That(page).DoesNotContain("Only editors see it.");
        await Assert.That(System.Text.RegularExpressions.Regex.Count(page, "<button class=\"btn primary\" type=\"submit\" name=\"Action\" value=\"save\">Save</button>")).IsEqualTo(2);
        await Assert.That(page).Contains("<ws-media-picker for=\"image\" alt-for=\"image-alt\" mode=\"field\"");
    }

    [Test]
    public async Task A_scheduled_item_whose_time_has_come_shows_as_published_and_archived_ones_arent_counted()
    {
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "due.md"), "---\ntitle: Due\nslug: due\ndate: 2026-01-01\nstatus: scheduled\npublish_at: 2026-01-01T09:00:00Z\n---\nText.\n");
        var hello = Path.Combine(copy.Root, "content", "posts", "hello-world.md");
        await File.WriteAllTextAsync(hello, "---\nstatus: archived\n" + (await File.ReadAllTextAsync(hello))["---\n".Length..]);
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var editor = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fdue.md", UriKind.Relative));
        var list = await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));

        // F-0091 L5: public since its time, so the bar says Published, as the list does.
        await Assert.That(editor).Contains("<span class=\"pill published\">Published</span>");

        // F-0091 L9: the sidebar counts posts as the All tab does, without the archived one.
        var all = System.Text.RegularExpressions.Regex.Match(list, "All <span>(\\d+)</span>").Groups[1].Value;
        await Assert.That(list).Contains($"<a href=\"/admin/posts\" aria-current=\"page\">Posts<span>{all}</span></a>");
    }

    [Test]
    public async Task A_refused_save_keeps_the_text_and_shows_nothing_of_the_item()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);
        var file = Path.Combine(copy.Root, "content", "posts", "hello-world.md");
        var before = await File.ReadAllTextAsync(file);

        // A role changed mid-edit, or a forged post: the item is someone else's (F-0087).
        var blank = await author.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var refused = await Post(author, "/admin/edit", blank, "save",
            ("Path", "posts/hello-world.md"), ("ETag", "x"), ("Title", "Mine"), ("Slug", "hello-world"), ("Status", "draft"), ("Date", "2026-11-09"), ("Body", "My long text."));
        var html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(html).Contains("Your role can't save this item, so nothing was saved. Your text is still below");
        await Assert.That(html).Contains("My long text.");
        await Assert.That(html).DoesNotContain("revisions-heading");
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo(before);
    }

    [Test]
    public async Task An_editors_first_save_gives_them_a_profile_when_they_have_none()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        // Signed in before profiles came from the sign-in (F-0088): the first save makes one.
        var blank = await author.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var saved = await Post(author, "/admin/new/post", blank, "save", ("Title", "First"), ("Slug", "first"), ("Status", "published"), ("Date", "2026-11-09"), ("Body", "Text."));
        var post = await factory.CreateClient().GetStringAsync(new Uri("/blog/first", UriKind.Relative));

        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "authors.json"))).Contains("\"name\": \"Test Editor\"");
        await Assert.That(post).Contains("by Test Editor");
    }

    [Test]
    public async Task An_error_is_marked_on_its_field_and_links_to_it()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var blank = await admin.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var taken = await Post(admin, "/admin/new/post", blank, "save", ("Title", "Dup"), ("Slug", "hello-world"), ("Status", "draft"), ("Date", "2026-11-09"));
        var html = await taken.Content.ReadAsStringAsync();

        await Assert.That(html).Contains("<p class=\"notice\" role=\"alert\">The slug &#x27;hello-world&#x27; is already used by");
        await Assert.That(html).Contains("&quot;. <a href=\"#slug\">Go to the field</a></p>");
        await Assert.That(html).Matches("<input(?=[^>]*\\bid=\"slug\")(?=[^>]*\\baria-invalid=\"true\")[^>]*>");
        await Assert.That(html).DoesNotMatch("<input(?=[^>]*\\bid=\"title\")(?=[^>]*\\baria-invalid)[^>]*>");
        await Assert.That(html).Contains("admin/notice-focus");
    }

    [Test]
    [Arguments("The title is missing.", "title")]
    [Arguments("The slug 'x' is reserved for the site.", "slug")]
    [Arguments("The update date isn't a date such as 2026-11-09.", "updated")]
    [Arguments("The date isn't a date such as 2026-11-09.", "date")]
    [Arguments("A scheduled item needs a publish time (publish_at).", "publish-at")]
    [Arguments("The language must be a code such as en, en-GB or hu.", "language")]
    [Arguments("The tag 'A b' isn't lowercase letters, digits and single hyphens.", "tags")]
    [Arguments("Every image needs a description (alt text) in its brackets", "body")]
    [Arguments("Someone saved this since you opened it.", null)]
    public async Task Errors_name_their_field(string message, string? field) =>
        await Assert.That(EditorErrors.FieldOf(message)).IsEqualTo(field);

    [Test]
    public async Task An_author_writes_and_publishes_a_post()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        var blank = await author.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var save = await Post(author, "/admin/new/post", blank, "save", ("Title", "My first post"), ("Slug", "my-first-post"), ("Status", "published"), ("Date", "2026-11-09"), ("Body", "Hello **there**."));
        var mine = await author.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));
        using var publicPage = await factory.CreateClient().GetAsync(new Uri("/blog/my-first-post", UriKind.Relative));

        await Assert.That(save.Headers.Location?.PathAndQuery).IsEqualTo("/admin/edit?path=posts%2Fmy-first-post.md&saved=1&created=1");
        await Assert.That(mine).Contains("My first post");
        await Assert.That(publicPage.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "my-first-post.md"))).Contains("author: editor-");
    }

    [Test]
    public async Task Saves_in_the_editor_are_audited_with_what_changed()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        const string Hello = "/admin/edit?path=posts%2Fhello-world.md";

        var page = await admin.GetStringAsync(new Uri(Hello, UriKind.Relative));
        using var saved = await Post(admin, "/admin/edit", page, "save", [.. Fields(page), ("Title", "Hello, again")]);
        page = await admin.GetStringAsync(new Uri(Hello, UriKind.Relative));
        using var archived = await Post(admin, "/admin/edit", page, "save", [.. Fields(page), ("Status", "archived")]);
        var blank = await admin.GetStringAsync(new Uri("/admin/new/post", UriKind.Relative));
        using var created = await Post(admin, "/admin/new/post", blank, "save", ("Title", "New"), ("Slug", "new"), ("Status", "draft"), ("Date", "2026-11-09"), ("Body", "Text."));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);

        var actions = audit.Select(e => $"{e.Actor} {e.Action} {e.Target}").ToList();

        await Assert.That(actions.Count).IsEqualTo(3);
        await Assert.That(actions[0]).IsEqualTo("editor@example.com item-created posts/new.md");
        await Assert.That(actions[1]).IsEqualTo("editor@example.com status-archived posts/hello-world.md");
        await Assert.That(actions[2]).IsEqualTo("editor@example.com item-saved posts/hello-world.md");
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
        using var longSlug = await Post(admin, "/admin/new/post", blank, "save", ("Title", "X"), ("Slug", string.Join('-', Enumerable.Repeat("word", 21))), ("Status", "draft"), ("Date", "2026-11-09"));

        await Assert.That(await taken.Content.ReadAsStringAsync()).Contains("already used by");
        await Assert.That(await badDate.Content.ReadAsStringAsync()).Contains("isn&#x27;t a date");
        await Assert.That(await longSlug.Content.ReadAsStringAsync()).Contains("The slug can be at most 100 characters long; this one has 104.");
        await Assert.That(blank).Contains("maxlength=\"100\"");
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
        using var empty = await Upload(author, page, "empty.txt", [], alt: "");

        await Assert.That(noAlt.Headers.Location?.OriginalString).Contains("error=");
        await Assert.That(uploaded.Headers.Location?.OriginalString).Contains("uploaded=");
        await Assert.That(after).Contains("![A blue square](/media/");
        await Assert.That(WebUtility.UrlDecode(fake.Headers.Location?.OriginalString)).Contains("isn't a readable image");
        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // F-0091 L11: an empty file is told apart from no file.
        await Assert.That(WebUtility.UrlDecode(empty.Headers.Location?.OriginalString)).Contains("The file is empty.");
    }

    [Test]
    public async Task The_media_picker_lists_files_and_uploads_as_json()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
        var png = await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"));
        author.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        using var signedOut = await anonymous.GetAsync(new Uri("/admin/media.json", UriKind.Relative));
        var page = await author.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        using var uploaded = await Upload(author, page, "Blue Square.png", png, alt: "A [blue] square");
        using var refused = await Upload(author, page, "square.png", png, alt: " ");
        var list = await author.GetStringAsync(new Uri("/admin/media.json", UriKind.Relative));

        using var files = System.Text.Json.JsonDocument.Parse(list);
        var paths = files.RootElement.EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        var sample = files.RootElement.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "b368a21f9e93-blue-square.png");
        await Assert.That(paths).Contains("/media/b368a21f9e93-blue-square.png");
        await Assert.That(paths).DoesNotContain(p => p!.EndsWith("w.webp", StringComparison.Ordinal));
        await Assert.That(sample.GetProperty("isImage").GetBoolean()).IsTrue();
        await Assert.That(sample.GetProperty("thumbnail").GetString()).IsNotNull();
        await Assert.That(signedOut.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(uploaded.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await uploaded.Content.ReadAsStringAsync()).Contains("![A blue square](/media/b368a21f9e93-blue-square.png)");
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("\"error\"");
    }

    [Test]
    public async Task The_media_page_lists_files_as_cards_and_filters_them_by_name()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);
        var png = await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"));

        var page = await author.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        using var image = await Upload(author, page, "square.png", png, alt: "A blue square");
        using var text = await Upload(author, page, "notes.txt", "Some notes."u8.ToArray(), alt: "");
        var all = WebUtility.HtmlDecode(await author.GetStringAsync(new Uri("/admin/media", UriKind.Relative)));
        var filtered = await author.GetStringAsync(new Uri("/admin/media?q=SQUARE", UriKind.Relative));

        await Assert.That(all).Contains("<span class=\"count\">2 files</span>");
        await Assert.That(all).Contains("96 × 96");
        await Assert.That(all).Contains("TXT · download");
        await Assert.That(all).Contains("[notes.txt](/media/");
        await Assert.That(filtered).Contains("square.png");
        await Assert.That(filtered).DoesNotContain("notes.txt</a>");
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

    [Test]
    public async Task The_posts_list_filters_by_text_and_status()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var drafts = await admin.GetStringAsync(new Uri("/admin/posts?status=draft", UriKind.Relative));
        var tagged = await admin.GetStringAsync(new Uri("/admin/posts?q=%23haskell", UriKind.Relative));
        var none = await admin.GetStringAsync(new Uri("/admin/posts?q=zzzqqq", UriKind.Relative));
        var home = await admin.GetStringAsync(new Uri("/admin", UriKind.Relative));

        await Assert.That(drafts).Contains("Notes for a future post");
        await Assert.That(drafts).DoesNotContain("Moving from Bicep to OpenTofu");
        await Assert.That(drafts).Contains("<a href=\"/admin/posts?status=draft\" aria-current=\"page\">Drafts <span>1</span></a>");
        await Assert.That(tagged).Contains("A weekend with Haskell");
        await Assert.That(tagged).DoesNotContain("Moving from Bicep to OpenTofu");
        await Assert.That(tagged).Contains("<a href=\"/admin/posts?q=%23haskell&amp;status=draft\">Drafts <span>0</span></a>");
        await Assert.That(none).Contains("Nothing matches.");
        await Assert.That(home).Contains("<h1>Posts</h1>");
    }

    [Test]
    public async Task The_lists_refresh_with_their_filter_and_tab_but_without_a_notice()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var posts = await admin.GetStringAsync(new Uri("/admin/posts?q=%23haskell&status=draft&deleted=Old", UriKind.Relative));
        var pages = await admin.GetStringAsync(new Uri("/admin/pages", UriKind.Relative));
        var media = await admin.GetStringAsync(new Uri("/admin/media?show=unused&deleted=old.png", UriKind.Relative));
        var odd = await admin.GetStringAsync(new Uri("/admin/posts?status=a%26b", UriKind.Relative));

        await Assert.That(posts).Contains("<a class=\"btn refresh\" href=\"/admin/posts?q=%23haskell&amp;status=draft\">");
        await Assert.That(pages).Contains("<a class=\"btn refresh\" href=\"/admin/pages\">");
        await Assert.That(media).Contains("<a class=\"btn refresh\" href=\"/admin/media?show=unused\">");
        await Assert.That(odd).Contains("<a class=\"btn refresh\" href=\"/admin/posts\">");
        await Assert.That(posts).Contains("Refresh</a>");
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
