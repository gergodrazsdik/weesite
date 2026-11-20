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
            await Expect(published.GetByRole(AriaRole.Button, new() { Name = "Reject" })).ToBeVisibleAsync();
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
        await edit.FillAsync("Better words.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(Page.Locator("ws-comments [data-status]")).ToHaveTextAsync("Saved. The change waits for moderation.");
        await Expect(own).ToContainTextAsync("Better words.");
        await Assert.That(site.Comments.Single(c => c.Name == "Editing Writer").Text).IsEqualTo("Better words.");

        await own.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
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
    public async Task Without_javascript_comments_read_and_there_is_no_form()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.OpenAsync(site.At("/blog/notes-on-static-rendering"));

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Comments", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "2 comments: read them or write one" })).ToBeVisibleAsync();
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
}

/// <summary>The sample site with comments' minimum time off and high hourly and per-minute limits,
/// so a browser may send at once and often, and the tests together stay under them.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class CommentSite : IAsyncInitializer, IAsyncDisposable
{
    private readonly SiteFactory _factory = new(new Dictionary<string, string>
    {
        ["Visitor:MinimumSeconds"] = "0",
        ["Visitor:CommentsPerHour"] = "100",
        ["RateLimits:VisitorPerMinute"] = "600",
    });

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

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

        // One on a post in Hungarian, for the dates' language.
        await store.SaveAsync(new Comment(Comment.NewId(now.AddDays(-3)), "posts/egy-kagylo-ket-oldala.md", "Cili Example", "Szép kagyló.", null, CommentStatus.Approved, now.AddDays(-3), null, now, "sample"), CancellationToken.None);

        // Twelve on another post, for Show more, Reply and the dates (T-0161).
        for (var i = 1; i <= 12; i++)
        {
            var at = now.AddDays(-30).AddHours(i);
            var text = i == 1 ? "> Quoted line\nMy answer." : $"Comment number {i}.";
            await store.SaveAsync(new Comment(Comment.NewId(at), "posts/why-every-repo-here-has-a-justfile.md", $"Reader {i}", text, null, CommentStatus.Approved, at, null, now, "sample"), CancellationToken.None);
        }

        await _factory.Services.GetRequiredService<CommentCatalog>().RefreshAsync(CancellationToken.None);
    }

    /// <summary>Gets the absolute address of a path on the site.</summary>
    /// <param name="path">A path such as <c>/blog/hello-world</c>.</param>
    /// <returns>The address.</returns>
    public string At(string path) => new Uri(BaseAddress, path).ToString();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
