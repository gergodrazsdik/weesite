using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;
using TUnit.Playwright;
using Weesite.Web.Visitors;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Comments in a browser (ADR 0011): sent with a proof of work, waiting for moderation in this
/// browser only, edited and deleted with the token the browser kept, public once approved, and
/// readable without JavaScript, where there's no form. Each test comments on a post of its own.
/// </summary>
[ClassDataSource<CommentSite>(Shared = SharedType.PerClass)]
internal sealed class CommentTests(CommentSite site) : PageTest
{
    private static readonly AxeRunOptions WcagAa = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa", "best-practice"] },
    };

    [Test]
    public async Task A_comment_is_sent_waits_for_moderation_and_is_remembered()
    {
        await Page.WatchCspAsync();
        await Page.OpenAsync(site.At("/blog/hello-world"));
        var none = Page.GetByText("No comments yet.");
        await Expect(none).ToBeVisibleAsync();

        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Script Writer");
        await form.GetByLabel("Comment").FillAsync("Sent from a browser, with https://example.com/a in it.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();

        var status = Page.Locator("ws-comments [data-status]");
        await Expect(status).ToHaveTextAsync("Thank you: your comment waits for moderation, and shows here once it's approved.");
        var own = Page.Locator(".own-comments article");
        await Expect(own).ToContainTextAsync("Sent from a browser");
        await Expect(own).ToContainTextAsync("Waits for moderation");
        await Expect(form.GetByLabel("Comment")).ToHaveValueAsync("");

        // "No comments yet" gives way to the comment shown waiting.
        await Expect(none).ToBeHiddenAsync();

        // Stored with a proof of work, waiting, and still shown after a reload.
        var stored = site.Comments.Single(c => c.Name == "Script Writer");
        await Assert.That(stored.Status).IsEqualTo(CommentStatus.Pending);
        await Page.ReopenAsync();
        await Expect(Page.Locator(".own-comments article")).ToContainTextAsync("Sent from a browser");

        var axe = await Page.Locator("#comments").RunAxe(WcagAa);
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
    }

    [Test]
    public async Task Ten_comments_show_more_open_and_reply_quotes_and_dates_read_relative()
    {
        await Page.OpenAsync(site.At("/blog/why-every-repo-here-has-a-justfile"));
        var items = Page.Locator(".comment-list > li");
        var more = Page.GetByRole(AriaRole.Link, new() { Name = "Show more comments (2 more)" });

        await Expect(items.Locator("article:visible")).ToHaveCountAsync(10);
        await Expect(Page.Locator(".comment-list blockquote")).ToHaveTextAsync("Quoted line");
        await more.ClickAsync();
        await Expect(items.Locator("article:visible")).ToHaveCountAsync(12);
        await Expect(more).ToHaveCountAsync(0);
        await Expect(items.Nth(10).Locator("article")).ToBeFocusedAsync();

        // A date reads "… ago" and shows the exact time when clicked.
        var date = items.Nth(1).Locator("button.time-toggle");
        await Expect(date).ToContainTextAsync("ago");
        await date.ClickAsync();
        await Expect(date).Not.ToContainTextAsync("ago");
        await Expect(date).ToHaveAttributeAsync("aria-pressed", "true");

        // Reply quotes the comment into the form.
        await items.Nth(0).GetByRole(AriaRole.Button, new() { Name = "Reply to Reader 1" }).ClickAsync();
        var text = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" }).GetByLabel("Comment");
        await Expect(text).ToBeFocusedAsync();
        await Expect(text).ToHaveValueAsync(new Regex("^Reader 1 wrote on .+:\n> > Quoted line\n> My answer\\.\n\n$"));

        var axe = await Page.Locator("#comments").RunAxe(WcagAa);
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task A_reply_to_a_long_comment_with_links_quotes_part_of_it_and_is_taken()
    {
        // F-0250: Reply quoted all of it, so the reply was too long, or had too many links, for
        // text the visitor didn't write.
        await Page.OpenAsync(site.At("/blog/chiseled-net-images-in-practice"));
        await Page.Locator(".comment-list article").GetByRole(AriaRole.Button, new() { Name = "Reply to Long Writer" }).ClickAsync();
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        var text = form.GetByLabel("Comment");
        var quote = await text.InputValueAsync();

        await Assert.That(CommentSite.LongLinked.Length).IsGreaterThan(900);
        await Assert.That(quote.Length).IsLessThan(450);
        await Assert.That(quote).Contains("> Three to read: https://example.com/one https://example.com/two https://example.com/three\n").And.Contains(" …\n\n");

        await text.PressSequentiallyAsync("My answer, with https://example.com/four in it.");
        await form.GetByLabel("Your name").FillAsync("Replying Reader");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();

        await Expect(Page.Locator("ws-comments [data-status]")).ToHaveTextAsync("Thank you: your comment waits for moderation, and shows here once it's approved.");
        await Assert.That(site.Comments.Single(c => c.Name == "Replying Reader").Text).Contains("https://example.com/four");
    }

    [Test]
    public async Task Dates_read_in_the_site_language_on_a_post_in_another()
    {
        // The site is in English; the post is in Hungarian. The dates belong to the page around the
        // post, so they read in English, as its menus and buttons do (docs/languages.md).
        await Page.OpenAsync(site.At("/blog/egy-kagylo-ket-oldala"));
        var date = Page.Locator(".comment-list button.time-toggle");
        await Expect(date).ToHaveTextAsync("3 days ago");

        await Page.Locator(".comment-list article").GetByRole(AriaRole.Button, new() { Name = "Reply to Cili Example" }).ClickAsync();
        var text = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" }).GetByLabel("Comment");
        await Expect(text).ToHaveValueAsync(new Regex("^Cili Example wrote on [A-Z][a-z]+ [0-9]{1,2}, [0-9]{4}:\n"));
    }

    [Test]
    public async Task Show_more_counts_comments_not_the_markers_of_deleted_ones()
    {
        // F-0206: ten comments show, with the two markers among them; Show more opens the rest.
        await Page.OpenAsync(site.At("/blog/hybrid-search-without-a-search-service"));
        var items = Page.Locator(".comment-list > li");
        var more = Page.GetByRole(AriaRole.Link, new() { Name = "Show more comments (2 more)" });

        await Expect(items.Locator("article:not(.deleted):visible")).ToHaveCountAsync(10);
        await Expect(items.Locator("article.deleted:visible")).ToHaveCountAsync(2);
        await more.ClickAsync();
        await Expect(items.Locator("article:not(.deleted):visible")).ToHaveCountAsync(12);
        await Expect(more).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_link_to_a_comment_behind_show_more_shows_them_all()
    {
        // F-0206: after a comment is published or approved here, the page reloads at it.
        await Page.OpenAsync(site.At($"/blog/hybrid-search-without-a-search-service#comment-{site.LastPagedId}"));
        var last = Page.Locator($"#comment-{site.LastPagedId}");

        await Expect(last).ToBeVisibleAsync();
        await Expect(last).ToBeInViewportAsync();
        await Expect(Page.Locator(".comment-list > li[hidden]")).ToHaveCountAsync(0);
        await Expect(Page.Locator("a[data-show-more]")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_name_is_remembered_for_the_next_comment()
    {
        await Page.OpenAsync(site.At("/blog/a-weekend-with-haskell-type-classes"));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Remembered Writer");
        await form.GetByLabel("Comment").FillAsync("A first comment.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        await Expect(Page.Locator(".own-comments article")).ToContainTextAsync("A first comment.");

        // A waiting comment shows its quotes as the published ones do (T-0165).
        await form.GetByLabel("Comment").FillAsync("> Quoting someone\nMy reply.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        await Expect(Page.Locator(".own-comments article blockquote").First).ToContainTextAsync("Quoting someone");

        // A quote of a quote nests, as the page shows the published ones.
        await form.GetByLabel("Comment").FillAsync("> Bob wrote:\n> > inner\n> \n> outer\nMine.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        var nested = Page.Locator(".own-comments article").Filter(new() { HasText = "Mine." });
        await Expect(nested.Locator("blockquote blockquote")).ToHaveTextAsync("inner");
        await Expect(nested.Locator(".comment-text > blockquote > p")).ToHaveTextAsync(["Bob wrote:", "outer"]);

        await Page.ReopenAsync();
        await Expect(Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" }).GetByLabel("Your name")).ToHaveValueAsync("Remembered Writer");
    }

    [Test]
    public async Task A_moderator_approves_on_the_post()
    {
        await Page.OpenAsync(site.At("/blog/moving-from-bicep-to-opentofu"));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Waiting Writer");
        await form.GetByLabel("Comment").FillAsync("Approve me on the post.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        await Expect(Page.Locator(".own-comments article")).ToContainTextAsync("Approve me on the post.");

        var context = await Browser.NewContextAsync(new() { ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "Admin" } });
        await using (context)
        {
            var admin = await context.NewPageAsync();
            await admin.OpenAsync(site.At("/blog/moving-from-bicep-to-opentofu"));
            var waiting = admin.Locator(".own-comments article.waiting").Filter(new() { HasText = "Approve me on the post." });
            await Expect(waiting).ToContainTextAsync("Waiting for approval");
            await Expect(admin.GetByText("You're signed in: you comment as Test Editor.")).ToBeVisibleAsync();
            await Expect(admin.GetByLabel("Your name")).ToBeHiddenAsync();
            await waiting.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
            var published = admin.Locator(".comment-list article").Filter(new() { HasText = "Approve me on the post." });
            await Expect(published).ToBeVisibleAsync();
            await Expect(published.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToBeVisibleAsync();
        }
    }

    [Test]
    public async Task A_writer_edits_and_deletes_their_own_comment()
    {
        await Page.OpenAsync(site.At("/blog/notes-on-dialing-in-espresso"));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Editing Writer");
        await form.GetByLabel("Comment").FillAsync("First words.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        var own = Page.Locator(".own-comments article");
        await Expect(own).ToContainTextAsync("First words.");

        await own.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        var edit = Page.GetByLabel("Your comment", new() { Exact = true });
        await Expect(edit).ToBeFocusedAsync();

        // An edit's error shows beside it, and the field points at it (F-0213).
        await edit.FillAsync("<a href=\"https://example.com/\">Cheap words.</a>");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        var error = own.Locator(".own-edit .notice.error");
        await Expect(error).ToContainTextAsync("Write plain text");
        await Expect(edit).ToBeFocusedAsync();
        await Expect(edit).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(edit).ToHaveAttributeAsync("aria-describedby", (await error.GetAttributeAsync("id"))!);

        await edit.FillAsync("Better words.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(Page.Locator("ws-comments [data-status]")).ToHaveTextAsync("Saved. The change waits for moderation.");
        await Expect(own).ToContainTextAsync("Better words.");
        await Assert.That(site.Comments.Single(c => c.Name == "Editing Writer").Text).IsEqualTo("Better words.");

        // Saved, the focus is back on the comment's own buttons, not lost to the page (F-0213).
        await Expect(own.GetByRole(AriaRole.Button, new() { Name = "Edit" })).ToBeFocusedAsync();

        // Cancel puts Delete back, with the focus on it (F-0213).
        var delete = own.GetByRole(AriaRole.Button, new() { Name = "Delete" });
        await delete.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(delete).ToBeFocusedAsync();

        await delete.ClickAsync();
        var really = Page.GetByRole(AriaRole.Button, new() { Name = "Delete for good" });
        await Expect(really).ToBeFocusedAsync();
        await really.ClickAsync();
        await Expect(Page.Locator("ws-comments [data-status]")).ToHaveTextAsync("Your comment was deleted.");
        await Expect(own).ToHaveCountAsync(0);
        await Assert.That(site.Comments.Where(c => c.Name == "Editing Writer")).IsEmpty();
    }

    [Test]
    public async Task An_approved_comment_is_public_and_its_writer_can_still_change_it()
    {
        await Page.OpenAsync(site.At("/blog/walking-the-south-coast-of-crete"));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Approved Writer");
        await form.GetByLabel("Comment").FillAsync("Lovely photos.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        await Expect(Page.Locator(".own-comments article")).ToContainTextAsync("Lovely photos.");

        // An admin approves it in the admin.
        var context = await Browser.NewContextAsync(new() { ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "Admin" } });
        await using (context)
        {
            var admin = await context.NewPageAsync();
            await admin.OpenAsync(site.At("/admin/comments"));
            var card = admin.Locator(".comment-card").Filter(new() { HasText = "Lovely photos." });
            await card.GetByRole(AriaRole.Button, new() { Name = "Approve (Approved Writer)" }).ClickAsync();
            await Expect(admin.GetByText("Approved: it's on the post.")).ToBeVisibleAsync();
        }

        await Page.ReopenAsync();
        var published = Page.Locator(".comment-list article").Filter(new() { HasText = "Lovely photos." });
        await Expect(published).ToContainTextAsync("Approved Writer");
        await Expect(Page.Locator(".own-comments article")).ToHaveCountAsync(0);
        await Expect(published.GetByRole(AriaRole.Button, new() { Name = "Edit" })).ToBeVisibleAsync();

        // "your comment" is a mark like the others, and the time comes last (F-0204).
        await Expect(published.Locator(".comment-meta .comment-mark.own-label")).ToHaveTextAsync("your comment");
        await Expect(published.Locator(".comment-meta > :last-child")).ToHaveClassAsync("time-toggle");

        // Your own comment has no Reply (T-0165).
        await Expect(published.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Reply") })).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "1 comment: read it or write one" })).ToBeVisibleAsync();

        // An edit waits in the published comment for a moderator, not as a comment of its own (F-0200).
        await published.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Page.GetByLabel("Your comment", new() { Exact = true }).FillAsync("Lovely photos, and the map too.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(Page.Locator("ws-comments [data-status]")).ToContainTextAsync("Saved");

        var moderator = await Browser.NewContextAsync(new() { ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "Admin" } });
        await using (moderator)
        {
            var admin = await moderator.NewPageAsync();
            await admin.OpenAsync(site.At("/blog/walking-the-south-coast-of-crete"));
            var comment = admin.Locator(".comment-list article").Filter(new() { HasText = "Approved Writer" });
            var change = comment.Locator(".comment-edit");
            await Expect(change).ToContainTextAsync("Edit waiting for approval:");
            await Expect(change.Locator(".comment-text")).ToHaveTextAsync("Lovely photos, and the map too.");
            await Expect(comment.Locator(":scope > .comment-text")).ToHaveTextAsync("Lovely photos.");
            await Expect(admin.Locator(".own-comments article")).ToHaveCountAsync(0);

            await change.GetByRole(AriaRole.Button, new() { Name = "Reject the edit" }).ClickAsync();
            await Expect(admin.Locator("ws-comments [data-status]")).ToHaveTextAsync("Edit rejected: the published text stays.");
            await Expect(change).ToHaveCountAsync(0);
            await Expect(comment).ToContainTextAsync("Lovely photos.");
        }
    }

    [Test]
    public async Task A_deleted_comment_leaves_a_marker_where_a_reply_follows_it()
    {
        // F-0203: the writer takes back a published comment that a reply follows.
        const string Post = "/blog/testing-blazor-ssr-with-tunit-and-playwright";
        await Page.OpenAsync(site.At(Post));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Second Thoughts");
        await form.GetByLabel("Comment").FillAsync("I'll take this back.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        await Expect(Page.Locator(".own-comments article")).ToContainTextAsync("I'll take this back.");

        // An admin approves it on the post and replies, public at once.
        var context = await Browser.NewContextAsync(new() { ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "Admin" } });
        await using (context)
        {
            var admin = await context.NewPageAsync();
            await admin.OpenAsync(site.At(Post));
            var waiting = admin.Locator(".own-comments article.waiting").Filter(new() { HasText = "I'll take this back." });
            await waiting.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
            await Expect(admin.Locator(".comment-list article").Filter(new() { HasText = "I'll take this back." })).ToBeVisibleAsync();
            await admin.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" }).GetByLabel("Comment").FillAsync("A reply after it.");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
            var reply = admin.Locator(".comment-list article").Filter(new() { HasText = "A reply after it." });
            await Expect(reply).ToBeVisibleAsync();

            // A moderator's edit of their published comment applies at once, and marks it edited (F-0204).
            await reply.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
            await admin.GetByLabel("Your comment", new() { Exact = true }).FillAsync("A reply after it, edited.");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Expect(admin.Locator(".comment-list article").Filter(new() { HasText = "A reply after it, edited." })).ToBeVisibleAsync();
        }

        await Page.ReopenAsync();
        var edited = Page.Locator(".comment-list article").Filter(new() { HasText = "A reply after it, edited." });
        await Expect(edited.Locator(".comment-mark.edited")).ToHaveTextAsync("edited");
        await Expect(Page.Locator(".comment-list .comment-mark.edited")).ToHaveCountAsync(1);
        var mine = Page.Locator(".comment-list article.own");
        await Expect(mine).ToContainTextAsync("I'll take this back.");
        var id = await mine.GetAttributeAsync("id");
        await mine.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete for good" }).ClickAsync();
        await Expect(Page.Locator("ws-comments [data-status]")).ToHaveTextAsync("Your comment was deleted.");

        // In place, and after a reload: the marker, without the name or the text; the reply stays.
        var marker = Page.Locator($"#{id}");
        await Expect(marker).ToHaveClassAsync("comment deleted");
        await Expect(marker).ToContainTextAsync("This comment was deleted by its writer.");
        await Expect(marker).Not.ToContainTextAsync("Second Thoughts");
        await Page.ReopenAsync();
        await Expect(marker).ToContainTextAsync("This comment was deleted by its writer.");
        await Expect(marker.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Reply") })).ToHaveCountAsync(0);
        await Expect(edited).ToBeVisibleAsync();
        await Expect(Page.Locator(".own-comments article")).ToHaveCountAsync(0);
        var axe = await Page.Locator("#comments").RunAxe(WcagAa);
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task A_published_comment_a_moderator_removes_leaves_a_marker()
    {
        // T-0180: the admin removes a published comment on the post (F-0210); a reply follows it.
        var context = await Browser.NewContextAsync(new() { ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "Admin" } });
        await using (context)
        {
            var admin = await context.NewPageAsync();
            await admin.OpenAsync(site.At("/blog/forms-that-work-without-javascript"));
            var removable = admin.Locator(".comment-list article").Filter(new() { HasText = "Something to remove." });
            var id = await removable.GetAttributeAsync("id");
            await removable.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
            await Expect(admin.Locator("ws-comments [data-status]")).ToHaveTextAsync("Removed from the post.");

            var marker = admin.Locator($"#{id}");
            await Expect(marker).ToHaveClassAsync("comment deleted");
            await Expect(marker).ToHaveTextAsync(new Regex("^This comment was removed by a moderator\\."));
            await Expect(admin.Locator(".comment-list article").Filter(new() { HasText = "A reply that stays." })).ToBeVisibleAsync();
        }

        await Page.OpenAsync(site.At("/blog/forms-that-work-without-javascript"));
        await Expect(Page.Locator(".comment-list article.deleted")).ToContainTextAsync("This comment was removed by a moderator.");
        await Expect(Page.Locator(".comment-list")).Not.ToContainTextAsync("Removable Writer");
    }

    [Test]
    public async Task Without_javascript_comments_read_and_there_is_no_form()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.OpenAsync(site.At("/blog/notes-on-static-rendering"));

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Comments", Exact = true })).ToBeVisibleAsync();
        // No form without JavaScript, so the count offers only to read them (F-0246).
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "2 comments: read them", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator(".comment-list")).ToContainTextAsync("The search was the hard part.");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "https://example.com/static-notes" })).ToHaveAttributeAsync("rel", "ugc nofollow noopener");
        await Expect(page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" })).ToHaveCountAsync(0);
        await Expect(page.GetByLabel("Your name")).Not.ToBeVisibleAsync();
    }

    [Test]
    public async Task The_honeypot_is_out_of_the_tab_order_and_the_accessibility_tree()
    {
        await Page.OpenAsync(site.At("/blog/hello-world"));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await Expect(form).ToBeVisibleAsync();

        await Expect(Page.GetByLabel("Leave this empty")).Not.ToBeVisibleAsync();
        await Expect(form.GetByRole(AriaRole.Textbox)).ToHaveCountAsync(2);
    }

    [Test]
    public async Task A_moderators_own_delete_leaves_what_waits_for_them()
    {
        // F-0211: a visitor's comment and a published one's edit wait; the admin deletes their own.
        const string Post = "/blog/lighthouses-and-long-exposures";
        await Page.OpenAsync(site.At(Post));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Patient Visitor");
        await form.GetByLabel("Comment").FillAsync("Waiting while the admin tidies up.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        await Expect(Page.Locator(".own-comments article")).ToContainTextAsync("Waiting while the admin tidies up.");

        var context = await Browser.NewContextAsync(new() { ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "Admin" } });
        await using (context)
        {
            var admin = await context.NewPageAsync();
            await admin.OpenAsync(site.At(Post));
            await admin.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" }).GetByLabel("Comment").FillAsync("Mine, to delete.");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
            var mine = admin.Locator(".comment-list article.own").Filter(new() { HasText = "Mine, to delete." });
            await Expect(mine).ToBeVisibleAsync();
            var waiting = admin.Locator(".own-comments article.waiting").Filter(new() { HasText = "Waiting while the admin tidies up." });
            var change = admin.Locator(".comment-list .comment-edit").Filter(new() { HasText = "An edit for the admin." });
            await Expect(waiting).ToBeVisibleAsync();
            await Expect(change).ToBeVisibleAsync();

            await mine.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
            await admin.GetByRole(AriaRole.Button, new() { Name = "Delete for good" }).ClickAsync();
            await Expect(admin.Locator("ws-comments [data-status]")).ToHaveTextAsync("Your comment was deleted.");

            await Expect(waiting.GetByRole(AriaRole.Button, new() { Name = "Approve" })).ToBeVisibleAsync();
            await Expect(change.GetByRole(AriaRole.Button, new() { Name = "Approve the edit" })).ToBeVisibleAsync();
            await Expect(change.GetByRole(AriaRole.Button, new() { Name = "Reject the edit" })).ToBeVisibleAsync();
        }
    }

    [Test]
    public async Task Show_more_opens_a_marker_only_with_the_comment_after_it()
    {
        // F-0212: twenty-one comments, a deleted one's marker before the last; ten at a time.
        await Page.OpenAsync(site.At("/blog/small-things-the-audio-plug"));
        var items = Page.Locator(".comment-list > li");
        await Expect(items.Locator("article:not(.deleted):visible")).ToHaveCountAsync(10);

        await Page.GetByRole(AriaRole.Link, new() { Name = "Show more comments (11 more)" }).ClickAsync();
        await Expect(items.Locator("article:not(.deleted):visible")).ToHaveCountAsync(20);
        await Expect(items.Locator("article.deleted:visible")).ToHaveCountAsync(0);

        await Page.GetByRole(AriaRole.Link, new() { Name = "Show more comments (1 more)" }).ClickAsync();
        await Expect(items.Locator("article:not(.deleted):visible")).ToHaveCountAsync(21);
        await Expect(items.Locator("article.deleted:visible")).ToHaveCountAsync(1);
        await Expect(items.Nth(20).Locator("article")).ToBeFocusedAsync();
    }

    [Test]
    [NotInParallel]
    public async Task An_approved_comment_not_on_the_page_yet_says_it_shows_shortly()
    {
        // F-0213: approved elsewhere (another replica), before this one reloads its comments.
        await Page.OpenAsync(site.At("/blog/one-espresso-one-task"));
        var form = Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" });
        await form.GetByLabel("Your name").FillAsync("Soon Published");
        await form.GetByLabel("Comment").FillAsync("Approved on another server.");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send comment" }).ClickAsync();
        await Expect(Page.Locator(".own-comments article")).ToContainTextAsync("Approved on another server.");

        var sent = site.Comments.Single(c => c.Name == "Soon Published");
        await site.Store.SaveAsync(sent with { Status = CommentStatus.Approved, Moderated = DateTimeOffset.UtcNow }, CancellationToken.None);
        await Page.ReopenAsync();

        var own = Page.Locator(".own-comments article").Filter(new() { HasText = "Approved on another server." });
        await Expect(own).ToContainTextAsync("Published: it shows on the post shortly.");
        await Expect(own).Not.ToHaveClassAsync(new Regex("\\bwaiting\\b"));
        await Expect(own).Not.ToContainTextAsync("Waits for moderation");
    }

    [Test]
    public async Task A_malformed_address_leaves_the_comments_working()
    {
        // F-0213: a stray % after # can't be decoded; the form still shows.
        await Page.OpenAsync(site.At("/blog/hello-world#comment-%E0%A4%A"));
        await Expect(Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" })).ToBeVisibleAsync();
    }
}

/// <summary>The sample site with comments' minimum time off and high hourly and per-minute limits,
/// so a browser may send at once and often, and the tests together stay under them. Comments reload
/// only after this site's own changes, as if a change stored directly came from another replica.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class CommentSite : IAsyncInitializer, IAsyncDisposable
{
    private readonly SiteFactory _factory = new(new Dictionary<string, string>
    {
        ["Visitor:MinimumSeconds"] = "0",
        ["Visitor:CommentsPerHour"] = "100",
        ["RateLimits:VisitorPerMinute"] = "600",
        ["Content:ReloadSeconds"] = "0",
    });

    /// <summary>Gets the comments' store, for a change made as another replica would.</summary>
    public ICommentStore Store => _factory.Services.GetRequiredService<ICommentStore>();

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Gets the ID of the last comment on the paging post, behind Show more.</summary>
    public string LastPagedId { get; private set; } = "";

    /// <summary>Gets every comment kept so far.</summary>
    public IReadOnlyList<Comment> Comments =>
        _factory.Services.GetRequiredService<ICommentStore>().ListAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        BaseAddress = await _factory.StartAsync();

        // Two published comments to read, one of them with a link.
        var store = _factory.Services.GetRequiredService<ICommentStore>();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-2)), "posts/notes-on-static-rendering.md", "Ada Example", "Static pages are quick. The search was the hard part.", null, CommentStatus.Approved, now.AddDays(-2), null, now, "sample"), CancellationToken.None);
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-1)), "posts/notes-on-static-rendering.md", "Bob Example", "See https://example.com/static-notes for more.", null, CommentStatus.Approved, now.AddDays(-1), null, now, "sample"), CancellationToken.None);

        // Twelve comments and two markers of deleted ones on another post, for paging (F-0206).
        for (var i = 1; i <= 14; i++)
        {
            var at = now.AddDays(-20).AddHours(i);
            var deleted = i is 2 or 3;
            var id = Comment.NewId(at);
            await store.SaveAsync(new Comment(id, "posts/hybrid-search-without-a-search-service.md", deleted ? "" : $"Pager {i}", deleted ? "" : $"Paged comment {i}.", null, deleted ? CommentStatus.Deleted : CommentStatus.Approved, at, null, now, deleted ? "" : "sample"), CancellationToken.None);
            LastPagedId = id;
        }

        // Two on another post, for a moderator removing the first (T-0180).
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-2)), "posts/forms-that-work-without-javascript.md", "Removable Writer", "Something to remove.", null, CommentStatus.Approved, now.AddDays(-2), null, now, "sample"), CancellationToken.None);
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-1)), "posts/forms-that-work-without-javascript.md", "Later Reader", "A reply that stays.", null, CommentStatus.Approved, now.AddDays(-1), null, now, "sample"), CancellationToken.None);

        // One on a post in Hungarian, for the dates' language.
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-3)), "posts/egy-kagylo-ket-oldala.md", "Cili Example", "Szép kagyló.", null, CommentStatus.Approved, now.AddDays(-3), null, now, "sample"), CancellationToken.None);

        // Twelve on another post, for Show more, Reply and the dates (T-0161).
        for (var i = 1; i <= 12; i++)
        {
            var at = now.AddDays(-30).AddHours(i);
            var text = i == 1 ? "> Quoted line\nMy answer." : $"Comment number {i}.";
            await store.SaveAsync(new Comment(Comment.NewId(at), "posts/why-every-repo-here-has-a-justfile.md", $"Reader {i}", text, null, CommentStatus.Approved, at, null, now, "sample"), CancellationToken.None);
        }

        // A published one with an edit waiting, for a moderator's own changes beside it (F-0211).
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-1)), "posts/lighthouses-and-long-exposures.md", "Edit Writer", "Published words.", "An edit for the admin.", CommentStatus.Approved, now.AddDays(-1), now.AddHours(-1), now.AddDays(-1), "sample"), CancellationToken.None);

        // Twenty-one comments, and a deleted one's marker before the last, for Show more (F-0212).
        for (var i = 1; i <= 22; i++)
        {
            var at = now.AddDays(-40).AddHours(i);
            var deleted = i == 21;
            await store.SaveAsync(new Comment(Comment.NewId(at), "posts/small-things-the-audio-plug.md", deleted ? "" : $"Plug {i}", deleted ? "" : $"Plug comment {i}.", null, deleted ? CommentStatus.Deleted : CommentStatus.Approved, at, null, now, deleted ? "" : "sample"), CancellationToken.None);
        }

        // A long one with three links, for a reply quoting it (F-0250).
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-1)), "posts/chiseled-net-images-in-practice.md", "Long Writer", LongLinked, null, CommentStatus.Approved, now.AddDays(-1), null, now, "sample"), CancellationToken.None);

        await _factory.Services.GetRequiredService<CommentCatalog>().RefreshAsync(CancellationToken.None);
    }

    /// <summary>Gets a published comment of 900 characters or more, with three links in its first line.</summary>
    public static string LongLinked { get; } =
        "Three to read: https://example.com/one https://example.com/two https://example.com/three\n"
        + string.Join(' ', Enumerable.Repeat("Images without a shell are smaller and have less to patch.", 15));

    /// <summary>Gets the absolute address of a path on the site.</summary>
    /// <param name="path">A path such as <c>/blog/hello-world</c>.</param>
    /// <returns>The address.</returns>
    public string At(string path) => new Uri(BaseAddress, path).ToString();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
