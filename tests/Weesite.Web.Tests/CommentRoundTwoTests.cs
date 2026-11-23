using System.Net;
using Weesite.Web.Admin;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The second round of comments (T-0161, T-0155): editors' names, quotes, ten at a time, moderating on the post.</summary>
internal sealed partial class CommentTests
{
    [Test]
    public async Task A_signed_in_admin_comments_under_their_name_published_at_once()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var admin = Admin(site);

        using var sent = await admin.PostAsJsonAsync(new Uri("/visitor/comments", UriKind.Relative), new { post = Post, name = "Someone else", text = "From the editor." });
        var answer = await Json(sent);
        var page = await site.CreateClient().GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(answer.GetProperty("name").GetString()).IsEqualTo("Test Editor");
        await Assert.That(answer.GetProperty("status").GetString()).IsEqualTo("approved");
        await Assert.That(answer.GetProperty("by").GetString()).IsEqualTo("editor");
        await Assert.That(page).Contains("class=\"comment by-editor\"");
        await Assert.That(page).Contains("<span class=\"comment-name\">Test Editor</span>");
        await Assert.That(page).Contains("<span class=\"comment-mark\">editor</span>");
        await Assert.That(page).Contains("From the editor.");
    }

    [Test]
    public async Task An_author_comments_at_once_on_their_post_and_waits_on_another()
    {
        using var copy = new SiteCopy();
        var author = EditorRights.AuthorId("editor@example.com");
        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "content", "posts", "mine.md"),
            $"---\ntitle: Mine\nslug: mine\ndate: 2026-11-01\nauthor: {author}\n---\n\nMy post.\n");
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var writer = Client(site, EditorRole.Author);

        var own = await Json(await writer.PostAsJsonAsync(new Uri("/visitor/comments", UriKind.Relative), new { post = "mine", text = "On my post." }));
        var other = await Json(await writer.PostAsJsonAsync(new Uri("/visitor/comments", UriKind.Relative), new { post = Post, text = "On someone else's." }));
        var me = await writer.GetFromJsonAsync<CommentViewer>(new Uri("/visitor/comments/me?post=mine", UriKind.Relative));
        var notMine = await writer.GetFromJsonAsync<CommentViewer>(new Uri($"/visitor/comments/me?post={Post}", UriKind.Relative));
        var visitor = await site.CreateClient().GetFromJsonAsync<CommentViewer>(new Uri($"/visitor/comments/me?post={Post}", UriKind.Relative));

        await Assert.That(own.GetProperty("status").GetString()).IsEqualTo("approved");
        await Assert.That(own.GetProperty("by").GetString()).IsEqualTo("author");
        await Assert.That(other.GetProperty("status").GetString()).IsEqualTo("pending");
        await Assert.That(other.GetProperty("by").GetString()).IsEqualTo("editor");
        await Assert.That(me).IsEqualTo(new CommentViewer(true, "Test Editor", "author", true, false));
        await Assert.That(notMine).IsEqualTo(new CommentViewer(true, "Test Editor", "editor", false, false));
        await Assert.That(visitor).IsEqualTo(new CommentViewer(false, null, null, false, false));
    }

    [Test]
    public async Task Moderators_approve_and_reject_on_the_post_and_admins_reject_published_ones()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var visitor = site.CreateClient();
        using var admin = Admin(site);
        using var author = Client(site, EditorRole.Author);

        var (first, _) = await SendAsync(visitor, site, "Ada", "First waiting.");
        var (second, _) = await SendAsync(visitor, site, "Bob", "Second waiting.");
        using var forVisitor = await visitor.GetAsync(new Uri($"/visitor/comments/moderation?post={Post}", UriKind.Relative));
        var waiting = await admin.GetFromJsonAsync<List<OwnComment>>(new Uri($"/visitor/comments/moderation?post={Post}", UriKind.Relative));
        using var byVisitor = await visitor.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = first, decision = "approve" });
        using var approved = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = first, decision = "approve" });
        using var rejected = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = second, decision = "reject" });
        var page = await visitor.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        // An author can't take back a published comment on someone else's post; an admin can.
        using var byAuthor = await author.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = first, decision = "reject" });
        using var takenBack = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = first, decision = "reject" });
        var after = await visitor.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(forVisitor.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(waiting!.Select(c => c.Id)).IsEquivalentTo([first, second], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(byVisitor.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(page).Contains("First waiting.");
        await Assert.That(page).DoesNotContain("Second waiting.");
        await Assert.That(byAuthor.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(takenBack.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after).DoesNotContain("First waiting.");
    }

    [Test]
    public async Task A_quote_of_a_quote_nests_four_deep_at_most()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var visitor = site.CreateClient();
        using var admin = Admin(site);

        // A reply to a reply, as Reply quotes it (the maintainer's UAT, 2026-11-20), and a thread five deep.
        var (reply, _) = await SendAsync(visitor, site, "Ada", "Bob wrote on 20 November 2026:\n> Ada wrote on 20 November 2026:\n> > hi there!!\n> \n> Hi\n\nwhatsup");
        var (deep, _) = await SendAsync(visitor, site, "Cy", "> > > > > five\nmine");
        await Moderate(admin, "approve", reply);
        await Moderate(admin, "approve", deep);
        var page = await visitor.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(page).Contains("<p>Bob wrote on 20 November 2026:</p><blockquote><p>Ada wrote on 20 November 2026:</p><blockquote><p>hi there!!</p></blockquote><p>Hi</p></blockquote><p>whatsup</p>");
        await Assert.That(page).Contains("<blockquote><blockquote><blockquote><blockquote><p>&gt; five</p></blockquote></blockquote></blockquote></blockquote><p>mine</p>");
    }

    [Test]
    public async Task Quotes_show_as_quotes_and_ten_comments_show_before_show_more()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root, perHour: 50);
        using var visitor = site.CreateClient();
        using var admin = Admin(site);

        var (quoted, _) = await SendAsync(visitor, site, "Ada", "> You wrote this\n| and this\nI agree.\n\nAlso, this.");
        await Moderate(admin, "approve", quoted);
        for (var i = 1; i <= 11; i++)
        {
            var (id, _) = await SendAsync(visitor, site, $"Visitor {i}", $"Comment number {i}.");
            await Moderate(admin, "approve", id);
        }

        var page = await visitor.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));
        var all = await visitor.GetStringAsync(new Uri($"/blog/{Post}?comments=all", UriKind.Relative));

        await Assert.That(page).Contains("<blockquote><p>You wrote this&#xA;and this</p></blockquote><p>I agree.</p><p>Also, this.</p>");
        await Assert.That(page).Contains("data-text=\"&gt; You wrote this");
        await Assert.That(System.Text.RegularExpressions.Regex.Count(page, "<li hidden><article")).IsEqualTo(2);
        await Assert.That(page).Contains("<a href=\"?comments=all#comments\" data-show-more data-step=\"10\"");
        await Assert.That(page).Contains("Show more comments (2 more)");
        await Assert.That(all).DoesNotContain("<li hidden><article");
        await Assert.That(all).DoesNotContain("data-show-more");
    }

    [Test]
    public async Task The_markers_of_deleted_comments_dont_count_in_the_ten()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root, perHour: 50);
        using var visitor = site.CreateClient();
        using var admin = Admin(site);

        // Thirteen comments; their writers delete the first two, which leave markers (F-0206).
        var sent = new List<(string Id, string Token)>();
        for (var i = 1; i <= 13; i++)
        {
            var comment = await SendAsync(visitor, site, $"Visitor {i}", $"Comment number {i}.");
            await Moderate(admin, "approve", comment.Id);
            sent.Add(comment);
        }

        foreach (var (id, token) in sent.Take(2))
        {
            using var deleted = await Own(visitor, HttpMethod.Delete, id, token);
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        var page = await visitor.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        // Ten comments and the two markers before them show; the eleventh waits.
        await Assert.That(System.Text.RegularExpressions.Regex.Count(page, "<article class=\"comment deleted\"")).IsEqualTo(2);
        await Assert.That(System.Text.RegularExpressions.Regex.Count(page, "<li hidden><article")).IsEqualTo(1);
        await Assert.That(page).Contains("Show more comments (1 more)");
        await Assert.That(page[page.IndexOf("<li hidden><article", StringComparison.Ordinal)..]).Contains("Comment number 13.");
    }

    [Test]
    public async Task The_form_says_less_and_takes_a_thousand_characters()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);

        var page = await site.CreateClient().GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(page).Contains("maxlength=\"1000\"");
        await Assert.That(page).DoesNotContain("Shown with your comment.");
        await Assert.That(page).DoesNotContain("Only this browser can edit or delete your comment");
    }
}
