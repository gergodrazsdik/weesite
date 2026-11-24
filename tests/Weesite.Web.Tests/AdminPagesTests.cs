using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Media;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The admin pages after the UI and UX review (T-0177): media, messages, the audit log, editors, analytics, backup and settings.</summary>
internal sealed class AdminPagesTests
{
    private const string Sample = "b368a21f9e93-sample.png";
    private const string Notes = "abcdef012345-notes.pdf";

    [Test]
    public async Task An_images_description_is_edited_on_its_card_without_a_new_file()
    {
        using var copy = MediaCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var author = Client(factory, EditorRole.Author);

        // U-47: admins get the form on an image's card; the PDF has none.
        var list = await admin.GetStringAsync(new Uri("/admin/media?q=sample", UriKind.Relative));
        await Assert.That(list).Contains("action=\"/admin/media/describe\"");
        await Assert.That(list).Contains($"id=\"describe-{Sample}\"");
        await Assert.That(list).Contains("keep the description they were written with");
        var authors = await author.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        await Assert.That(authors).DoesNotContain("action=\"/admin/media/describe\"");

        using var saved = await PostAsync(admin, "/admin/media/describe", list, ("name", Sample), ("alt", "  A blue square  "), ("back", "/admin/media?q=sample"));
        using var empty = await PostAsync(admin, "/admin/media/describe", list, ("name", Sample), ("alt", " "), ("back", "/admin/media"));
        using var refused = await PostAsync(author, "/admin/media/describe", list, ("name", Sample), ("alt", "Not theirs"), ("back", "/admin/media"));

        await Assert.That(saved.Headers.Location?.ToString()).IsEqualTo($"/admin/media?q=sample&described={Sample}");
        var after = await admin.GetStringAsync(saved.Headers.Location);
        await Assert.That(after).Contains($"<p class=\"notice success\" role=\"status\" data-focus>Saved the description of {Sample}.</p>");
        await Assert.That(after).Contains($">![A blue square](/media/{Sample})</code>");
        await Assert.That((await factory.Services.GetRequiredService<MediaLibrary>().ListAsync(CancellationToken.None)).Single(f => f.Name == Sample).Alt).IsEqualTo("A blue square");
        await Assert.That(empty.Headers.Location?.ToString()).StartsWith("/admin/media?error=");
        await Assert.That(refused.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");

        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        await Assert.That(audit.Count(e => e.Action == "media-described" && e.Target == Sample)).IsEqualTo(1);
    }

    [Test]
    public async Task Media_counts_agree_after_an_archive_and_follow_the_filter()
    {
        using var copy = MediaCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var page = await admin.GetStringAsync(new Uri("/admin/media", UriKind.Relative));

        using var archived = await PostAsync(admin, "/admin/media/archive", page, ("name", Notes), ("archived", "true"), ("back", "/admin/media"));
        var all = await admin.GetStringAsync(archived.Headers.Location);
        var filtered = await admin.GetStringAsync(new Uri("/admin/media?q=notes", UriKind.Relative));

        // U-48: the head counts what All counts; with a filter, the tabs count what matches.
        await Assert.That(all).Contains("<span class=\"count\">1 file</span>");
        await Assert.That(all).Contains("aria-current=\"page\">All <span><span class=\"visually-hidden\">, </span>1</span>");
        await Assert.That(all).Contains(">Archived <span><span class=\"visually-hidden\">, </span>1</span>");
        await Assert.That(filtered).Contains("aria-current=\"page\">All <span><span class=\"visually-hidden\">, </span>0</span>");
        await Assert.That(filtered).Contains(">In use <span><span class=\"visually-hidden\">, </span>0</span>");

        // U-31: the notice after the archive takes the focus (media-page.js, data-focus).
        await Assert.That(all).Contains($"<p class=\"notice success\" role=\"status\" data-focus>Archived {Notes}.</p>");

        // U-04, U-38: the upload says what's wrong itself; the filter has a button to see.
        await Assert.That(all).Contains("enctype=\"multipart/form-data\" novalidate");
        await Assert.That(all).Contains("<button type=\"submit\">Filter</button>");
    }

    [Test]
    public async Task Messages_count_one_say_unread_and_say_once_that_the_last_one_went()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var store = factory.Services.GetRequiredService<IMessageStore>();
        var now = DateTimeOffset.UtcNow;
        var message = new ContactMessage(ContactMessage.NewId(now), "Ada", "ada@example.com", "Hello there.", now, Read: false, Unverified: false);
        await store.AddAsync(message, CancellationToken.None);

        var page = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));

        // U-50: "1 message", and "unread" as the sidebar says it.
        await Assert.That(page).Contains("1 message, 1 unread</caption>");
        await Assert.That(page).Contains("<span class=\"pill new\">unread</span>");
        await Assert.That(page).Contains("data-confirm-title=\"Delete the message from Ada?\"");

        using var deleted = await PostAsync(admin, "/admin/messages/delete", page, ("id", message.Id));
        var after = WebUtility.HtmlDecode(await admin.GetStringAsync(deleted.Headers.Location));

        await Assert.That(after).Contains("Message deleted. No messages are left.");
        await Assert.That(after).DoesNotContain("No messages yet.");
    }

    [Test]
    public async Task The_audit_log_reads_in_words_and_asks_for_a_month()
    {
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "settings", "authors.json"),
            $"[{{ \"id\": \"{EditorRights.AuthorId("writer@example.com")}\", \"name\": \"Wendy Writer\" }}]");
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var log = factory.Services.GetRequiredService<IAuditLog>();
        var at = DateTimeOffset.UtcNow;
        await log.WriteAsync(new AuditEntry(at, "writer@example.com", "status-published", "posts/hello-world.md"), CancellationToken.None);
        await log.WriteAsync(new AuditEntry(at, "writer@example.com", "mcp-media-uploaded", "abc123def456-photo.png (via Claude)"), CancellationToken.None);

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/audit", UriKind.Relative)));
        var byName = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/audit?q=Wendy", UriKind.Relative)));
        var noMonth = await admin.GetStringAsync(new Uri("/admin/audit?month=", UriKind.Relative));

        // U-53: who by name with the address under it; the action in words; the post by its title.
        await Assert.That(page).Contains("<td class=\"who\"><span>Wendy Writer</span>");
        await Assert.That(page).Contains("<span class=\"muted\">writer@example.com</span></td>");
        await Assert.That(page).Contains("<td>Published</td>");
        await Assert.That(page).Contains("<a href=\"/admin/edit?path=posts%2Fhello-world.md\">“Hello, world”</a><span class=\"muted\">posts/hello-world.md</span>");
        await Assert.That(page).Contains("<td>Uploaded a file (through an app)</td>");
        await Assert.That(page).Contains("<span class=\"muted\">through Claude</span>");
        await Assert.That(page).Contains("actions, newest first</caption>");
        await Assert.That(byName).Contains("Uploaded a file");
        await Assert.That(noMonth).Contains("<p class=\"notice error\" role=\"alert\">Choose a month first, then Show the month.</p>");
    }

    [Test]
    [Arguments("media-uploaded", "Uploaded a file")]
    [Arguments("mcp-status-scheduled", "Scheduled (through an app)")]
    [Arguments("something-new", "Something new")]
    public async Task Audit_actions_read_as_words(string code, string words) =>
        await Assert.That(AuditText.Action(code)).IsEqualTo(words);

    [Test]
    public async Task Editors_add_first_say_where_admins_come_from_and_tell_apps_apart()
    {
        await using var site = new OAuthTests.OAuthSite();
        var store = site.Services.GetRequiredService<IOAuthStore>();
        var at = new DateTimeOffset(2026, 11, 20, 10, 11, 12, TimeSpan.Zero);
        await store.AddGrantAsync(new OAuthGrant("web123", "editor@example.com", "https://claude.ai/oauth/claude-code-client-metadata", "Claude", "http://localhost/mcp/editor", at, at.AddMinutes(5), "h", null, DateTimeOffset.UtcNow.AddDays(30)), CancellationToken.None);
        await store.AddGrantAsync(new OAuthGrant("cli123", "editor@example.com", "3f2a9c1b77e04d1f", "Claude", "http://localhost/mcp/editor", at, at, "h", null, DateTimeOffset.UtcNow.AddDays(30)), CancellationToken.None);
        using var admin = site.Client();

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/editors", UriKind.Relative)));

        // U-54: adding comes before the lists; no recipe names; apps by where they come from.
        await Assert.That(page.IndexOf("id=\"add-editor\"", StringComparison.Ordinal)).IsLessThan(page.IndexOf("id=\"configured-admins\"", StringComparison.Ordinal));
        await Assert.That(page).DoesNotContain("just site-admins");
        await Assert.That(page).Contains("Claude<span class=\"muted app-source\">from claude.ai</span>");
        await Assert.That(page).Contains("Claude<span class=\"muted app-source\">ID 3f2a9c1b…</span>");
        await Assert.That(page).Contains("<th scope=\"col\">Last used</th>");

        // U-05: an admin time with its seconds, as every recorded time.
        await Assert.That(page).Contains("2026-11-20 10:16:12 UTC");
        await Assert.That(page).Contains("data-confirm-title=\"Revoke Claude?\"");
    }

    [Test]
    public async Task Analytics_says_its_zone_and_what_reactions_count_and_backup_names_what_it_leaves_out()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var analytics = await admin.GetStringAsync(new Uri("/admin/analytics", UriKind.Relative));
        var report = await admin.GetStringAsync(new Uri("/admin/analytics/report", UriKind.Relative));
        var posts = await admin.GetStringAsync(new Uri("/admin/analytics/posts", UriKind.Relative));
        var backup = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/backup", UriKind.Relative)));
        var settings = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative)));

        // U-05, U-52: the update time in the site's zone and clock; a paused text to show.
        await Assert.That(analytics).Contains("data-zone=\"UTC\" data-clock=\"24h\"");
        await Assert.That(analytics).Contains("data-paused=\"Updates paused. Last updated at {0}\"");
        await Assert.That(report).Contains("<span class=\"column\" data-day=\"");

        // U-51: what each reactions number counts.
        await Assert.That(report).Contains("on every post, deleted ones too");
        await Assert.That(posts).Contains("there the tile also counts the reactions on posts deleted since");

        // U-55: comments, reactions and messages are named as left out.
        await Assert.That(backup).Contains("<li>Visitors' comments, reactions and contact messages</li>");

        // U-56: dates explained by example; an editor's own comment isn't held.
        await Assert.That(settings).Contains("d MMMM yyyy gives 5 March 2026");
        await Assert.That(settings).DoesNotContain(".NET date format");
        await Assert.That(settings).Contains("An editor's own comment on a post they moderate appears at once.");
    }

    private static SiteCopy MediaCopy()
    {
        var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", Sample), Path.Combine(media, Sample));
        File.WriteAllBytes(Path.Combine(media, Notes), "%PDF-1.4\n%%EOF\n"u8.ToArray());
        return copy;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    // Posts a form the way the page does: with the antiforgery token from the page's own form.
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string page, params (string Name, string Value)[] fields)
    {
        var token = Forms.Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }
}
