using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content;
using Weesite.Tests;
using Weesite.Web.Admin;
using Weesite.Web.Email;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>Comments on posts (ADR 0011): what visitors send, their own comments, moderation, the digest.</summary>
internal sealed partial class CommentTests
{
    private const string Post = "hello-world";
    private const string PostPath = "posts/hello-world.md";
    private const string Address = "203.0.113.41";

    [Test]
    public async Task The_form_endpoint_says_whether_a_post_takes_comments()
    {
        using var copy = new SiteCopy();
        Close(copy, "posts/notes-on-dialing-in-espresso.md");
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();

        var open = await Json(await client.GetAsync(new Uri($"/visitor/comments/form?post={Post}", UriKind.Relative)));
        var closed = await Json(await client.GetAsync(new Uri("/visitor/comments/form?post=notes-on-dialing-in-espresso", UriKind.Relative)));
        var draft = await Json(await client.GetAsync(new Uri("/visitor/comments/form?post=a-long-draft-of-walks-and-scripts", UriKind.Relative)));
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));
        var closedPage = await client.GetStringAsync(new Uri("/blog/notes-on-dialing-in-espresso", UriKind.Relative));

        await Assert.That(open.GetProperty("open").GetBoolean()).IsTrue();
        await Assert.That(open.GetProperty("token").GetString()).IsNotNull().And.IsNotEmpty();
        await Assert.That(closed.GetProperty("open").GetBoolean()).IsFalse();
        await Assert.That(draft.GetProperty("open").GetBoolean()).IsFalse();
        await Assert.That(page).Contains("<ws-comments hidden data-post=\"hello-world\" data-key=\"posts/hello-world.md\"");
        await Assert.That(page).Contains("src=\"/comments.");
        await Assert.That(page).Contains("No comments yet.");
        await Assert.That(closedPage).DoesNotContain("<ws-comments");
        await Assert.That(closedPage).DoesNotContain("src=\"/comments.");
    }

    [Test]
    public async Task A_comment_waits_for_moderation_and_only_its_token_reads_it()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var sent = await Submit(client, site, "Ada", "A fine post.");
        var mine = await Json(sent);
        var id = mine.GetProperty("id").GetString()!;
        var token = mine.GetProperty("token").GetString()!;
        var own = await Json(await Own(client, HttpMethod.Get, id, token));
        using var wrong = await Own(client, HttpMethod.Get, id, Comment.NewToken());
        using var none = await Own(client, HttpMethod.Get, id, null);
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(mine.GetProperty("status").GetString()).IsEqualTo("pending");
        await Assert.That(token.Length).IsGreaterThanOrEqualTo(43);
        await Assert.That(own.GetProperty("text").GetString()).IsEqualTo("A fine post.");
        await Assert.That(own.TryGetProperty("token", out var again) && again.ValueKind == JsonValueKind.String).IsFalse();
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(page).DoesNotContain("A fine post.");

        // Only the token's hash is kept.
        var stored = Stored(site).Single();
        await Assert.That(stored.TokenHash).IsEqualTo(Comment.HashOf(token));
        await Assert.That(stored.TokenHash).DoesNotContain(token);
    }

    [Test]
    public async Task Without_a_proof_of_work_nothing_is_kept()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var none = await Submit(client, site, "Ada", "Hello", proof: "");
        using var forged = await Submit(client, site, "Ada", "Hello", proof: "12:forged");
        var error = await Json(none);

        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("no-proof");
        await Assert.That(error.GetProperty("message").GetString()).Contains("check didn't finish");
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(Stored(site)).IsEmpty();
    }

    [Test]
    public async Task A_filled_honeypot_looks_like_success_and_keeps_nothing()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var sent = await Submit(client, site, "Bot", "Buy now", website: "https://spam.example");
        var answer = await Json(sent);

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(answer.GetProperty("token").GetString()).IsNotNull();
        await Assert.That(Stored(site)).IsEmpty();
    }

    [Test]
    public async Task A_comment_sent_too_soon_is_refused_with_the_text_left_to_send_again()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root, minimumSeconds: 3);
        using var client = site.CreateClient();

        using var sent = await Submit(client, site, "Ada", "Quick");
        var error = await Json(sent);

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("too-fast");
        await Assert.That(Stored(site)).IsEmpty();
    }

    [Test]
    [Arguments("Ada", "<a href=\"https://spam.example\">x</a>", "bad-text", "HTML links")]
    [Arguments("Ada", "[url=https://spam.example]x[/url]", "bad-text", "HTML links")]
    [Arguments("Ada", "https://a.example https://b.example https://c.example https://d.example", "bad-text", "at most 3 links")]
    [Arguments("Ada", "", "bad-text", "Write a comment")]
    [Arguments("https://spam.example", "Hi", "bad-name", "without links")]
    [Arguments("", "Hi", "bad-name", "Enter your name")]
    public async Task Names_and_text_follow_the_plain_text_rules(string name, string text, string code, string message)
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var sent = await Submit(client, site, name, text);
        var error = await Json(sent);

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(error.GetProperty("error").GetString()).IsEqualTo(code);
        await Assert.That(error.GetProperty("message").GetString()).Contains(message);
    }

    [Test]
    public async Task Long_text_is_refused_with_the_limit()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var tooLong = await Submit(client, site, "Ada", new string('a', CommentDesk.TextMax + 1));
        using var longest = await Submit(client, site, "Ada", new string('a', CommentDesk.TextMax));

        await Assert.That((await Json(tooLong)).GetProperty("message").GetString()).Contains("1000");
        await Assert.That(longest.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task An_address_has_an_hourly_limit()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root, perHour: 1);
        using var client = site.CreateClient();

        using var first = await Submit(client, site, "Ada", "One");
        using var second = await Submit(client, site, "Ada", "Two");
        using var otherAddress = await Submit(client, site, "Bob", "Three", address: "203.0.113.42");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That((await Json(second)).GetProperty("error").GetString()).IsEqualTo("too-many");
        await Assert.That(otherAddress.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task What_every_post_page_reads_is_left_out_of_the_per_minute_limit()
    {
        // Each post page asks for a form token and the reader's own comments; sending stays limited.
        await using var root = new SiteFactory();
        await using var site = Site(root, more: b => b.UseSetting("RateLimits:VisitorPerMinute", "1"));
        using var client = site.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", Address);

        var reads = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var form = await client.GetAsync(new Uri($"/visitor/comments/form?post={Post}", UriKind.Relative));
            using var own = await Own(client, HttpMethod.Get, "0000000000000000000deadbeef", Comment.NewToken());
            reads.Add(form.StatusCode);
            reads.Add(own.StatusCode);
        }

        using var first = await Submit(client, site, "Ada", "One", token: "x", proof: "1:x");
        using var second = await Submit(client, site, "Ada", "Two", token: "x", proof: "1:x");

        await Assert.That(reads).DoesNotContain(HttpStatusCode.TooManyRequests);
        await Assert.That(first.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Closed_posts_and_a_site_without_comments_take_none()
    {
        using var copy = new SiteCopy();
        Close(copy, PostPath);
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();

        using var closed = await Submit(client, site, "Ada", "Hello", token: "x");

        using var off = new SiteCopy();
        Rewrite(off, json => json["comments"] = false);
        await using var offRoot = new SiteFactory(off.Root);
        await using var offSite = Site(offRoot);
        using var offClient = offSite.CreateClient();
        using var siteOff = await Submit(offClient, offSite, "Ada", "Hello", token: "x");
        var offPage = await offClient.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(closed.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await Json(closed)).GetProperty("error").GetString()).IsEqualTo("closed");
        await Assert.That(siteOff.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(offPage).DoesNotContain("id=\"comments\"");
    }

    [Test]
    public async Task An_edit_of_an_approved_comment_waits_while_the_approved_text_stays_public()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (id, token) = await SendAsync(client, site, "Ada", "First words");

        // A pending comment's edit replaces it.
        var replaced = await Json(await Edit(client, site, id, token, "First words, fixed"));
        await Assert.That(replaced.GetProperty("text").GetString()).IsEqualTo("First words, fixed");
        await Assert.That(replaced.GetProperty("pendingText").ValueKind).IsEqualTo(JsonValueKind.Null);

        await Moderate(admin, "approve", id);
        await Assert.That(await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative))).Contains("First words, fixed");

        // An approved one's edit waits beside it.
        var waiting = await Json(await Edit(client, site, id, token, "Second thoughts"));
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));
        await Assert.That(waiting.GetProperty("text").GetString()).IsEqualTo("First words, fixed");
        await Assert.That(waiting.GetProperty("pendingText").GetString()).IsEqualTo("Second thoughts");
        await Assert.That(page).Contains("First words, fixed");
        await Assert.That(page).DoesNotContain("Second thoughts");

        // Rejecting the edit keeps the approved text; approving the next one shows it.
        await Moderate(admin, "reject", id);
        var afterReject = Stored(site).Single();
        await Assert.That(afterReject.Status).IsEqualTo(CommentStatus.Approved);
        await Assert.That(afterReject.PendingText).IsNull();
        await Assert.That(afterReject.Text).IsEqualTo("First words, fixed");

        await Edit(client, site, id, token, "Third time");
        await Moderate(admin, "approve", id);
        page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));
        await Assert.That(page).Contains("Third time");
        await Assert.That(page).DoesNotContain("First words, fixed");
        await Assert.That(page).Contains("1 comment: read it or write one");
    }

    [Test]
    public async Task A_writer_deletes_their_comment_at_once_and_a_rejected_one_cant_be_edited()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (approved, approvedToken) = await SendAsync(client, site, "Ada", "Keep me for a while");
        var (rejected, rejectedToken) = await SendAsync(client, site, "Bob", "Not this one");
        await Moderate(admin, "approve", approved);
        await Moderate(admin, "reject", rejected);

        using var notEditable = await Edit(client, site, rejected, rejectedToken, "Again");
        using var wrongToken = await Own(client, HttpMethod.Delete, approved, rejectedToken);
        using var deleted = await Own(client, HttpMethod.Delete, approved, approvedToken);
        using var gone = await Own(client, HttpMethod.Get, approved, approvedToken);
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(notEditable.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(wrongToken.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(page).DoesNotContain("Keep me for a while");

        // A published one leaves a marker, without its name or text; with no comment after it,
        // the post doesn't show it (F-0203).
        var marker = Stored(site).Single(c => c.Id == approved);
        await Assert.That(marker.Status).IsEqualTo(CommentStatus.Deleted);
        await Assert.That(marker.Name).IsEmpty();
        await Assert.That(marker.Text).IsEmpty();
        await Assert.That(marker.TokenHash).IsEmpty();
        await Assert.That(page).DoesNotContain("class=\"comment deleted\"");
        await Assert.That(Stored(site).Select(c => c.Id)).IsEquivalentTo([approved, rejected]);
    }

    [Test]
    public async Task A_deleted_comment_keeps_its_place_before_a_later_one_and_a_waiting_one_goes()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (first, firstToken) = await SendAsync(client, site, "Ada", "The first word");
        var (reply, _) = await SendAsync(client, site, "Bob", "> The first word\nAnd mine.");
        var (waiting, waitingToken) = await SendAsync(client, site, "Cy", "Never published");
        await Moderate(admin, "approve", first);
        await Moderate(admin, "approve", reply);

        using var deleted = await Own(client, HttpMethod.Delete, first, firstToken);
        using var deletedWaiting = await Own(client, HttpMethod.Delete, waiting, waitingToken);
        using var again = await Own(client, HttpMethod.Delete, first, firstToken);
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(page).Contains($"<article class=\"comment deleted\" id=\"comment-{first}\" data-comment-id=\"{first}\" data-deleted=\"writer\" data-count=\"1\">");
        await Assert.That(page).Contains("<span class=\"comment-deleted\">This comment was deleted by its writer.</span>");
        await Assert.That(page).DoesNotContain("<span class=\"comment-name\">Ada</span>");
        await Assert.That(page).Contains($"id=\"comment-{reply}\"");
        await Assert.That(page).Contains("1 comment: read it or write one");
        await Assert.That(Stored(site).Select(c => c.Id)).IsEquivalentTo([first, reply]);
    }

    [Test]
    public async Task An_approved_edit_marks_the_comment_edited_and_one_before_publishing_doesnt()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (early, earlyToken) = await SendAsync(client, site, "Ada", "Draft");
        var (later, laterToken) = await SendAsync(client, site, "Bob", "Before");
        var (rejectedEdit, rejectedToken) = await SendAsync(client, site, "Cy", "Stays as it was");

        // Edited before it was published: not marked. Edited after, approved: marked (F-0204).
        await Edit(client, site, early, earlyToken, "Final");
        await Moderate(admin, "approve", early);
        await Moderate(admin, "approve", later);
        await Moderate(admin, "approve", rejectedEdit);
        await Edit(client, site, later, laterToken, "After");
        await Moderate(admin, "approve", later);
        await Edit(client, site, rejectedEdit, rejectedToken, "Changed");
        await Moderate(admin, "reject", rejectedEdit);
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(Stored(site).Single(c => c.Id == early).Revised).IsNull();
        await Assert.That(Stored(site).Single(c => c.Id == later).Revised).IsNotNull();
        await Assert.That(Stored(site).Single(c => c.Id == rejectedEdit).Revised).IsNull();
        await Assert.That(System.Text.RegularExpressions.Regex.Count(page, "<span class=\"comment-mark edited\" title=\"Edited ")).IsEqualTo(1);
        await Assert.That(page[page.IndexOf($"id=\"comment-{later}\"", StringComparison.Ordinal)..]).Contains(">edited</span>");
    }

    [Test]
    public async Task The_post_shows_approved_comments_as_escaped_text_with_ugc_links()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (id, _) = await SendAsync(client, site, "Ada & Co", "If x < y & z, see https://example.com/a_(b).\nSecond line.");
        await Moderate(admin, "approve", id);
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(page).Contains("<h2 id=\"comments-heading\">Comments</h2>");
        await Assert.That(page).Contains($"<article class=\"comment\" id=\"comment-{id}\"");
        await Assert.That(page).Contains("Ada &amp; Co");
        await Assert.That(page).Contains("If x &lt; y &amp; z, see <a href=\"https://example.com/a_(b)\" rel=\"ugc nofollow noopener\">https://example.com/a_(b)</a>.&#xA;Second line.");
    }

    [Test]
    public async Task Admins_moderate_every_post_and_authors_their_own()
    {
        using var copy = new SiteCopy();
        var own = Path.Combine(copy.Root, "content", "posts", "notes-on-dialing-in-espresso.md");
        await File.WriteAllTextAsync(own, (await File.ReadAllTextAsync(own)).Replace("author: example-author", "author: " + EditorRights.AuthorId("editor@example.com"), StringComparison.Ordinal));
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var author = Client(site, EditorRole.Author);
        using var admin = Admin(site);

        var (others, _) = await SendAsync(client, site, "Ada", "On someone else's post");
        var (theirs, _) = await SendAsync(client, site, "Bob", "On the author's post", post: "notes-on-dialing-in-espresso");

        var authorPage = await author.GetStringAsync(new Uri("/admin/comments", UriKind.Relative));
        var adminPage = await admin.GetStringAsync(new Uri("/admin/comments", UriKind.Relative));
        using var forbidden = await Moderate(author, "approve", others);
        using var allowed = await Moderate(author, "approve", theirs, post: "posts/notes-on-dialing-in-espresso.md");

        await Assert.That(authorPage).Contains("On the author&#x27;s post").Or.Contains("On the author's post");
        await Assert.That(authorPage).DoesNotContain("On someone else");
        await Assert.That(adminPage).Contains("On someone else");

        // The menu counts what waits for each, from the comments in memory (F-0225).
        await Assert.That(authorPage).Contains("Comments<span class=\"badge\"><span class=\"visually-hidden\">, </span>1<span");
        await Assert.That(adminPage).Contains("Comments<span class=\"badge\"><span class=\"visually-hidden\">, </span>2<span");
        await Assert.That(forbidden.Headers.Location?.ToString()).IsEqualTo("/admin/forbidden");
        await Assert.That(allowed.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=approved");
        await Assert.That(Stored(site).Single(c => c.Id == others).Status).IsEqualTo(CommentStatus.Pending);
        await Assert.That(Stored(site).Single(c => c.Id == theirs).Status).IsEqualTo(CommentStatus.Approved);

        // The audit says who and which, never what was written.
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        var entry = audit.First(e => e.Action == "comment-approved");
        await Assert.That(entry.Target).IsEqualTo($"posts/notes-on-dialing-in-espresso.md#{theirs}");
        await Assert.That(audit.Any(e => (e.Target ?? "").Contains("author's post", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task The_admin_lists_waiting_comments_and_edits_and_counts_them_in_the_menu()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (approved, token) = await SendAsync(client, site, "Ada", "Published words");
        await Moderate(admin, "approve", approved);
        await Edit(client, site, approved, token, "Edited words");
        await SendAsync(client, site, "Bob", "A new one");

        var page = await admin.GetStringAsync(new Uri("/admin/comments", UriKind.Relative));

        await Assert.That(page).Contains("Published text");
        await Assert.That(page).Contains("Edited words");
        await Assert.That(page).Contains("A new one");
        await Assert.That(page).Contains("Approve the edit");
        await Assert.That(page).Contains("href=\"/admin/comments\" aria-current=\"page\">Comments<span class=\"badge\"><span class=\"visually-hidden\">, </span>2<span class=\"visually-hidden\"> waiting</span>");

        // No published tab (T-0161): published comments are moderated on their post.
        await Assert.That(page).DoesNotContain("tab=approved");
        await Assert.That(page).Contains("class=\"btn refresh\" href=\"/admin/comments?tab=waiting\"");
    }

    [Test]
    public async Task The_digest_goes_to_each_moderator_with_what_waits_for_them()
    {
        using var copy = new SiteCopy();
        var own = Path.Combine(copy.Root, "content", "posts", "notes-on-dialing-in-espresso.md");
        await File.WriteAllTextAsync(own, (await File.ReadAllTextAsync(own)).Replace("author: example-author", "author: " + EditorRights.AuthorId("writer@example.com"), StringComparison.Ordinal));
        await using var root = new SiteFactory(copy.Root);
        var clock = new OAuthTests.TestClock();
        await using var site = Site(root, clock: clock, more: b => b.UseSetting("Site:Admins", "boss@example.com").UseSetting("Site:BaseUrl", "https://site.example"));
        using var client = site.CreateClient();
        await site.Services.GetRequiredService<IEditorStore>().SetAsync(new Editor("writer@example.com", EditorRole.Author, "boss@example.com", clock.GetUtcNow()), CancellationToken.None);

        await SendAsync(client, site, "Ada", "On the sample post, if x < y?");
        await SendAsync(client, site, "Bob", "On the writer's post", post: "notes-on-dialing-in-espresso");
        clock.Advance(DigestSender.Settle + TimeSpan.FromMinutes(1));

        var mail = new RecordingSender();
        using var sender = new DigestSender(site.Services.GetServices<IDigest>(), new MemoryDigestStateStore(), mail, site.Services.GetRequiredService<ContentCatalogProvider>(), clock, NullLogger<DigestSender>.Instance);
        var sent = await sender.RunOnceAsync(CancellationToken.None);

        await Assert.That(sent).IsEqualTo(2);
        var boss = mail.Sent.Single(m => m.To == "boss@example.com");
        var writer = mail.Sent.Single(m => m.To == "writer@example.com");
        await Assert.That(boss.Subject).IsEqualTo("2 comments wait for moderation on Example Site");
        await Assert.That(boss.Text).Contains("On the sample post").And.Contains("On the writer's post").And.Contains("https://site.example/admin/comments");
        await Assert.That(boss.Html).Contains("if x &lt; y?").And.DoesNotContain("x < y");
        await Assert.That(writer.Subject).IsEqualTo("1 comment waits for moderation on Example Site");
        await Assert.That(writer.Text).Contains("On the writer's post").And.DoesNotContain("On the sample post");

        // Within the hour, nothing more.
        await SendAsync(client, site, "Cy", "A later one");
        clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(await sender.RunOnceAsync(CancellationToken.None)).IsEqualTo(0);
    }

    [Test]
    public async Task Another_replica_sees_a_change_when_the_version_moves()
    {
        var store = new MemoryCommentStore();
        using var one = Catalog(store);
        using var two = Catalog(store);
        await one.RefreshAsync(CancellationToken.None);
        await two.RefreshAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Ada", "Shown", null, CommentStatus.Approved, now, null, now, "x"), CancellationToken.None);
        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Bob", "Waiting", null, CommentStatus.Pending, now, null, null, "y"), CancellationToken.None);

        await Assert.That(two.For(PostPath)).IsEmpty();
        await Assert.That(await two.RefreshAsync(CancellationToken.None)).IsTrue();
        await Assert.That(two.For(PostPath).Select(c => c.Text)).IsEquivalentTo(["Shown"]);
        await Assert.That(await two.RefreshAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    [Arguments("example-author", "example-author", "author")]
    [Arguments("example-author", "someone-else", "editor")]
    [Arguments("example-author", null, "editor")]
    [Arguments(null, null, "editor")]
    [Arguments("", "", "editor")]
    [Arguments(null, "example-author", "editor")]
    public async Task Only_the_posts_own_author_gets_the_author_mark(string? postAuthor, string? writerAuthor, string mark) =>
        // F-0205: a post without an author and an editor without a profile don't make an author.
        await Assert.That(CommentDesk.MarkOf(postAuthor, writerAuthor)).IsEqualTo(mark);

    [Test]
    public async Task Rejected_comments_go_after_thirty_days()
    {
        var store = new MemoryCommentStore();
        var clock = new OAuthTests.TestClock();
        var now = clock.GetUtcNow();
        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Ada", "Old reject", null, CommentStatus.Rejected, now, null, now, "x"), CancellationToken.None);
        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Bob", "Approved", null, CommentStatus.Approved, now, null, now, "y"), CancellationToken.None);
        using var cleanUp = new CommentCleanUp(store, clock, NullLogger<CommentCleanUp>.Instance);

        await Assert.That(await cleanUp.CleanUpAsync(CancellationToken.None)).IsEqualTo(0);
        clock.Advance(TimeSpan.FromDays(CommentCleanUp.RejectedDays + 1));
        await Assert.That(await cleanUp.CleanUpAsync(CancellationToken.None)).IsEqualTo(1);
        await Assert.That((await store.ListAsync(CancellationToken.None)).Select(c => c.Text)).IsEquivalentTo(["Approved"]);
    }

    [Test]
    public async Task A_removed_comment_loses_its_text_after_thirty_days_and_keeps_its_marker()
    {
        // T-0180: a moderator removed it once published; its place stays.
        var store = new MemoryCommentStore();
        var clock = new OAuthTests.TestClock();
        var now = clock.GetUtcNow();
        var id = Comment.NewId(now);
        await store.SaveAsync(new Comment(id, PostPath, "Ada", "Removed words", null, CommentStatus.Removed, now, null, now, "x"), CancellationToken.None);
        using var cleanUp = new CommentCleanUp(store, clock, NullLogger<CommentCleanUp>.Instance);

        await Assert.That(await cleanUp.CleanUpAsync(CancellationToken.None)).IsEqualTo(0);
        clock.Advance(TimeSpan.FromDays(CommentCleanUp.RejectedDays + 1));
        await Assert.That(await cleanUp.CleanUpAsync(CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await cleanUp.CleanUpAsync(CancellationToken.None)).IsEqualTo(0);

        var kept = await store.GetAsync(PostPath, id, CancellationToken.None);
        await Assert.That(kept!.Status).IsEqualTo(CommentStatus.Removed);
        await Assert.That(kept.Name).IsEmpty();
        await Assert.That(kept.Text).IsEmpty();
        await Assert.That(kept.TokenHash).IsEmpty();
    }

    [Test]
    public async Task A_published_comment_an_admin_removes_leaves_a_marker_its_writer_cant_change()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (first, firstToken) = await SendAsync(client, site, "Ada", "Something to remove");
        var (reply, _) = await SendAsync(client, site, "Bob", "> Something to remove\nI saw it.");
        await Moderate(admin, "approve", first);
        await Moderate(admin, "approve", reply);
        using var removed = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = first, decision = "remove" });
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var read = await Own(client, HttpMethod.Get, first, firstToken);
        using var delete = await Own(client, HttpMethod.Delete, first, firstToken);
        using var edit = await Edit(client, site, first, firstToken, "Let me change it");
        var page = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        // T-0180: marked as removed by a moderator where a later comment follows; its writer still
        // sees it, but can neither delete nor edit it.
        await Assert.That(Stored(site).Single(c => c.Id == first).Status).IsEqualTo(CommentStatus.Removed);
        await Assert.That(page).Contains("<span class=\"comment-deleted\">This comment was removed by a moderator.</span>");
        await Assert.That(page).DoesNotContain("data-text=\"Something to remove\"");
        await Assert.That(page).Contains("1 comment: read it or write one");
        await Assert.That(await read.Content.ReadAsStringAsync()).Contains("\"status\":\"removed\"");
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(edit.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Stored(site).Single(c => c.Id == first).Text).IsEqualTo("Something to remove");
    }

    [Test]
    public async Task No_address_reaches_the_logs_or_the_stored_comments()
    {
        using var log = new ReactionTests.LogLines();
        await using var root = new SiteFactory();
        await using var site = Site(root, log: log);
        using var client = site.CreateClient();

        await SendAsync(client, site, "Ada", "Hello there");
        using var refused = await Submit(client, site, "Ada", "<a href=\"https://spam.example\">x</a>");

        await Assert.That(log.Lines.Where(l => l.Contains(Address, StringComparison.Ordinal))).IsEmpty();
        await Assert.That(log.Lines.Any(l => l.Contains("a comment arrived", StringComparison.Ordinal))).IsTrue();
        var stored = Stored(site).Single();
        await Assert.That($"{stored.Name}|{stored.Text}|{stored.Post}|{stored.TokenHash}").DoesNotContain(Address);
        await Assert.That(site.Services.GetRequiredService<MemoryVisitorStore>().Keys.Any(k => k.Contains(Address, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task The_editor_form_closes_a_posts_comments_in_its_front_matter()
    {
        var form = EditorForm.New(ContentKind.Post, new DateOnly(2026, 11, 19));
        form.Title = "Quiet";
        form.Slug = "quiet";
        form.Body = "Text.";
        form.CloseComments = true;

        var draft = form.ToDraft(out var error);
        var markdown = draft!.ToMarkdown();
        var parsed = ContentParser.Parse("posts/quiet.md", markdown, "en").Item!;
        var reopened = parsed with { CommentsClosed = false };

        await Assert.That(error).IsNull();
        await Assert.That(markdown).Contains("comments: false");
        await Assert.That(parsed.CommentsClosed).IsTrue();
        await Assert.That(ContentDraft.From(reopened).ToMarkdown()).DoesNotContain("comments:");
        await Assert.That(EditorForm.From(ContentDraft.From(parsed), "posts/quiet.md", "v1").CloseComments).IsTrue();
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_keeps_comments_per_post_and_moves_its_version()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var store = new TableCommentStore(new TableServiceClient(azurite.GetConnectionString()));
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var first = new Comment(Comment.NewId(now), PostPath, "Ada", "One", null, CommentStatus.Pending, now, null, null, "hash1");
        var second = new Comment(Comment.NewId(now.AddMinutes(1)), "posts/other.md", "Bob", "Two", "Edit", CommentStatus.Approved, now.AddMinutes(1), now.AddMinutes(2), now.AddMinutes(3), "hash2");

        var empty = await store.VersionAsync(CancellationToken.None);
        await store.SaveAsync(first, CancellationToken.None);
        var afterOne = await store.VersionAsync(CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);
        var all = await store.ListAsync(CancellationToken.None);
        var read = await store.GetAsync("posts/other.md", second.Id, CancellationToken.None);

        await Assert.That(afterOne).IsNotEqualTo(empty);
        await Assert.That(all.Select(c => c.Id)).IsEquivalentTo([first.Id, second.Id], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(read! with { Version = null }).IsEqualTo(second);
        await Assert.That(read.Version).IsNotNull().And.IsNotEmpty();

        // Saved only at the version read (F-0209): a stale copy, a second add or a delete at an old
        // version changes nothing.
        var kept = await store.GetAsync(PostPath, first.Id, CancellationToken.None);
        var approved = await store.SaveAsync(kept! with { Status = CommentStatus.Approved }, CancellationToken.None);
        await Assert.That(approved?.Version).IsNotNull().And.IsNotEqualTo(kept.Version);
        await Assert.That(await store.SaveAsync(kept with { Text = "Stale" }, CancellationToken.None)).IsNull();
        await Assert.That(await store.SaveAsync(first, CancellationToken.None)).IsNull();
        await Assert.That(await store.DeleteAsync(PostPath, first.Id, CancellationToken.None, kept.Version)).IsFalse();
        await Assert.That((await store.GetAsync(PostPath, first.Id, CancellationToken.None))!.Text).IsEqualTo("One");
        await Assert.That(await store.DeleteAsync(PostPath, first.Id, CancellationToken.None, approved!.Version)).IsTrue();
        await Assert.That(await store.DeleteAsync(PostPath, first.Id, CancellationToken.None)).IsFalse();
        await Assert.That(await store.SaveAsync(approved with { Text = "Back" }, CancellationToken.None)).IsNull();
        await Assert.That(await store.VersionAsync(CancellationToken.None)).IsNotEqualTo(afterOne);
        await Assert.That((await store.ListAsync(CancellationToken.None)).Count).IsEqualTo(1);
    }

    // The site with the test sign-in, a quick proof of work, no minimum time unless asked, and the given changes.
    private static WebApplicationFactory<Program> Site(SiteFactory root, TimeProvider? clock = null, int? perHour = null, int minimumSeconds = 0, ReactionTests.LogLines? log = null, Action<IWebHostBuilder>? more = null) =>
        root.WithTestSignIn().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Visitor:ProofMaxNumber", "500");
            builder.UseSetting("Visitor:MinimumSeconds", minimumSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (perHour is { } limit)
            {
                builder.UseSetting("Visitor:CommentsPerHour", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (log is not null)
            {
                builder.ConfigureLogging(l => l.AddProvider(log));
            }

            if (clock is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock));
            }

            more?.Invoke(builder);
        });

    private static CommentCatalog Catalog(ICommentStore store) => new(
        store,
        new NoOutputCache(),
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        TimeProvider.System,
        NullLogger<CommentCatalog>.Instance);

    private static HttpClient Admin(WebApplicationFactory<Program> site) => Client(site, EditorRole.Admin);

    private static HttpClient Client(WebApplicationFactory<Program> site, EditorRole role)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    private static IReadOnlyList<Comment> Stored(WebApplicationFactory<Program> site) =>
        site.Services.GetRequiredService<ICommentStore>().ListAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<(string Id, string Token)> SendAsync(HttpClient client, WebApplicationFactory<Program> site, string name, string text, string post = Post)
    {
        var answer = await Json(await Submit(client, site, name, text, post: post));
        return (answer.GetProperty("id").GetString()!, answer.GetProperty("token").GetString()!);
    }

    private static async Task<HttpResponseMessage> Submit(
        HttpClient client,
        WebApplicationFactory<Program> site,
        string name,
        string text,
        string post = Post,
        string? proof = null,
        string website = "",
        string? token = null,
        string address = Address)
    {
        token ??= (await Json(await client.GetAsync(new Uri($"/visitor/comments/form?post={post}", UriKind.Relative)))).GetProperty("token").GetString();
        proof ??= ProofOfWork.Solve(site.Services.GetRequiredService<ProofOfWork>().Create("comment"));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/visitor/comments", UriKind.Relative))
        {
            Content = JsonContent.Create(new { post, name, text, formToken = token, website, proof }),
        };
        request.Headers.Add("X-Forwarded-For", address);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Own(HttpClient client, HttpMethod method, string id, string? token)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/visitor/comments/{id}?post={Post}", UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(CommentEndpoints.Scheme, token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Edit(HttpClient client, WebApplicationFactory<Program> site, string id, string token, string text)
    {
        var proof = ProofOfWork.Solve(site.Services.GetRequiredService<ProofOfWork>().Create("comment"));
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/visitor/comments/{id}?post={Post}", UriKind.Relative))
        {
            Content = JsonContent.Create(new { text, proof }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(CommentEndpoints.Scheme, token);
        return await client.SendAsync(request);
    }

    // A decision from the admin's Comments page, with the version it shows for the comment (F-0209),
    // or the one given.
    private static async Task<HttpResponseMessage> Moderate(HttpClient admin, string action, string id, string post = PostPath, string? version = null)
    {
        var page = await admin.GetStringAsync(new Uri("/admin/comments", UriKind.Relative));
        var token = Forms.Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([new("post", post), new("id", id), new("version", version ?? VersionOn(page, id)), new("tab", "waiting"), new("__RequestVerificationToken", token)]);
        return await admin.PostAsync(new Uri($"/admin/comments/{action}", UriKind.Relative), form);
    }

    // The version the admin's Comments page shows for a comment; empty when it isn't there.
    private static string VersionOn(string page, string id)
    {
        var at = page.IndexOf($"id=\"comment-{id}\"", StringComparison.Ordinal);
        return at < 0 ? "" : System.Text.RegularExpressions.Regex.Match(page[at..], "name=\"version\" value=\"([^\"]*)\"").Groups[1].Value;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using (response)
        {
            return JsonElement.Parse(await response.Content.ReadAsStringAsync());
        }
    }

    private static void Close(SiteCopy copy, string post)
    {
        var path = Path.Combine(copy.Root, "content", post);
        var text = File.ReadAllText(path);
        var end = text.IndexOf("\n---\n", StringComparison.Ordinal);
        File.WriteAllText(path, text[..end] + "\ncomments: false" + text[end..]);
    }

    private static void Rewrite(SiteCopy copy, Action<JsonObject> change)
    {
        var path = Path.Combine(copy.Root, "settings", "site.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    // Records what it sends.
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public bool IsConfigured => true;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // An output cache that keeps nothing, for catalogs outside an app.
    private sealed class NoOutputCache : Microsoft.AspNetCore.OutputCaching.IOutputCacheStore
    {
        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
