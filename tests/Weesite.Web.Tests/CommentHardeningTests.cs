using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>Limits on what comment writers send and how long comments wait (T-0177).</summary>
internal sealed partial class CommentTests
{
    [Test]
    public async Task A_comment_body_over_the_limit_is_refused_before_it_is_read()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        var text = new string('a', VisitorBody.MaxBytes);

        // The limit answers before the endpoint reads anything.
        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri("/visitor/comments", UriKind.Relative))
        {
            Content = JsonContent.Create(new { post = Post, name = "Ada", text }),
        };
        await post.Content.LoadIntoBufferAsync();
        using var posted = await client.SendAsync(post);
        using var edit = new HttpRequestMessage(HttpMethod.Put, new Uri($"/visitor/comments/0000000000000000000deadbeef?post={Post}", UriKind.Relative))
        {
            Content = new StringContent($"{{\"text\":\"{text}\"}}", Encoding.UTF8, "application/json"),
        };
        using var edited = await client.SendAsync(edit);
        using var react = new HttpRequestMessage(HttpMethod.Post, new Uri("/visitor/reactions", UriKind.Relative))
        {
            Content = new StringContent($"{{\"post\":\"{Post}\",\"kind\":\"like\",\"proof\":\"{text}\"}}", Encoding.UTF8, "application/json"),
        };
        using var reacted = await client.SendAsync(react);

        await Assert.That(posted.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(reacted.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(edited.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(Stored(site)).IsEmpty();
    }

    [Test]
    public async Task A_body_without_a_length_is_read_no_further_than_the_limit()
    {
        // A chunked request has no Content-Length: the endpoints' reader stops at the limit itself.
        var large = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"text\":\"{new string('a', VisitorBody.MaxBytes * 4)}\"}}"));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Body = large;
        var small = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        small.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"text\":\"Fine.\",\"proof\":\"1:x\"}"));
        var broken = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        broken.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"text\":"));
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var refused = await VisitorBody.ReadJsonAsync<CommentEdit>(context, options);
        var read = await VisitorBody.ReadJsonAsync<CommentEdit>(small, options);
        var bad = await VisitorBody.ReadJsonAsync<CommentEdit>(broken, options);

        await Assert.That(refused.TooLarge).IsTrue();
        await Assert.That(refused.Value).IsNull();
        await Assert.That(large.Position).IsLessThanOrEqualTo(VisitorBody.MaxBytes + 1);
        await Assert.That(read.Value).IsEqualTo(new CommentEdit("Fine.", "1:x"));
        await Assert.That((bad.Value, bad.TooLarge)).IsEqualTo(((CommentEdit?)null, false));
    }

    [Test]
    public async Task Text_far_over_the_limit_is_refused_before_it_is_normalized()
    {
        await Assert.That(VisitorText.IsFarTooLong(new string('a', CommentDesk.TextMax * VisitorText.SlackFactor), CommentDesk.TextMax)).IsFalse();
        await Assert.That(VisitorText.IsFarTooLong(new string('a', (CommentDesk.TextMax * VisitorText.SlackFactor) + 1), CommentDesk.TextMax)).IsTrue();
        await Assert.That(VisitorText.IsFarTooLong(null, CommentDesk.TextMax)).IsFalse();

        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var name = await Submit(client, site, new string('n', 1000), "Fine.");
        using var text = await Submit(client, site, "Ada", new string('t', 5000));

        await Assert.That((await Json(name)).GetProperty("error").GetString()).IsEqualTo("bad-name");
        await Assert.That((await Json(text)).GetProperty("error").GetString()).IsEqualTo("bad-text");
    }

    [Test]
    public async Task A_form_token_takes_one_comment()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        var token = (await Json(await client.GetAsync(new Uri($"/visitor/comments/form?post={Post}", UriKind.Relative)))).GetProperty("token").GetString();

        using var first = await Submit(client, site, "Ada", "One", token: token);
        using var again = await Submit(client, site, "Ada", "Two", token: token);
        using var fresh = await Submit(client, site, "Ada", "Three");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await Json(again)).GetProperty("error").GetString()).IsEqualTo("expired");
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Stored(site).Select(c => c.Text)).IsEquivalentTo(["One", "Three"]);
    }

    [Test]
    public async Task Edits_and_deletions_of_ones_own_comments_have_an_hourly_limit()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root, more: b => b.UseSetting("Visitor:CommentChangesPerHour", "2"));
        using var client = site.CreateClient();
        var (id, token) = await SendAsync(client, site, "Ada", "First words");
        var (other, otherToken) = await SendAsync(client, site, "Ada", "Other words");

        using var one = await Edit(client, site, id, token, "Second words");
        using var two = await Edit(client, site, id, token, "Third words");
        using var three = await Edit(client, site, id, token, "Fourth words");
        using var delete = await Own(client, HttpMethod.Delete, other, otherToken);

        await Assert.That(one.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(two.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(three.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That((await Json(three)).GetProperty("error").GetString()).IsEqualTo("too-many");
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(Stored(site).Select(c => c.Text)).IsEquivalentTo(["Third words", "Other words"]);
    }

    [Test]
    public async Task Reading_ones_own_comment_has_a_limit_of_its_own_and_a_malformed_token_is_refused()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root, more: b => b.UseSetting("RateLimits:VisitorReadPerMinute", "2"));
        using var client = site.CreateClient();
        var (id, token) = await SendAsync(client, site, "Ada", "Mine");

        using var first = await Own(client, HttpMethod.Get, id, token);
        using var malformed = await Own(client, HttpMethod.Get, id, "not-a-token");
        using var third = await Own(client, HttpMethod.Get, id, token);
        using var form = await client.GetAsync(new Uri($"/visitor/comments/form?post={Post}", UriKind.Relative));

        await Assert.That(Comment.IsToken(token)).IsTrue();
        await Assert.That(Comment.IsToken("not-a-token")).IsFalse();
        await Assert.That(Comment.IsToken(token[..42] + "!")).IsFalse();
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(form.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Comments_and_edits_that_wait_thirty_days_go()
    {
        var store = new MemoryCommentStore();
        var clock = new OAuthTests.TestClock();
        var now = clock.GetUtcNow();
        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Ada", "Never moderated", null, CommentStatus.Pending, now, null, null, "x"), CancellationToken.None);
        var edited = Comment.NewId(now.AddSeconds(1));
        await store.SaveAsync(new Comment(edited, PostPath, "Bob", "Published", "Edit never moderated", CommentStatus.Approved, now, now, now, "y"), CancellationToken.None);
        using var cleanUp = new CommentCleanUp(store, clock, NullLogger<CommentCleanUp>.Instance);

        clock.Advance(TimeSpan.FromDays(CommentCleanUp.PendingDays - 1));
        await store.SaveAsync(new Comment(Comment.NewId(clock.GetUtcNow()), PostPath, "Cy", "Recent", null, CommentStatus.Pending, clock.GetUtcNow(), null, null, "z"), CancellationToken.None);
        await Assert.That(await cleanUp.CleanUpAsync(CancellationToken.None)).IsEqualTo(0);
        clock.Advance(TimeSpan.FromDays(2));
        await Assert.That(await cleanUp.CleanUpAsync(CancellationToken.None)).IsEqualTo(2);

        var left = await store.ListAsync(CancellationToken.None);
        await Assert.That(left.Select(c => c.Text)).IsEquivalentTo(["Published", "Recent"]);
        await Assert.That(left.Single(c => c.Id == edited).PendingText).IsNull();
        await Assert.That(left.Single(c => c.Id == edited).Status).IsEqualTo(CommentStatus.Approved);
    }

    [Test]
    public async Task A_digest_lists_at_most_fifty_comments_and_counts_the_rest()
    {
        var now = DateTimeOffset.UtcNow;
        var waiting = Enumerable.Range(0, CommentsDigest.MaxListed + 7)
            .Select(i => new Comment(Comment.NewId(now.AddSeconds(i)), PostPath, "Ada", $"Comment number {i}.", null, CommentStatus.Pending, now.AddSeconds(i), null, null, "x"))
            .ToList();
        await using var root = new SiteFactory();
        var site = root.Services.GetRequiredService<ContentCatalogProvider>().Current.Site;

        var mail = CommentsDigest.Build(site, "boss@example.com", waiting, new Dictionary<string, ContentItem>(), link: null);

        await Assert.That(mail.Subject).IsEqualTo($"{CommentsDigest.MaxListed + 7} comments wait for moderation on Example Site");
        await Assert.That(mail.Text).Contains($"Comment number {CommentsDigest.MaxListed - 1}.").And.DoesNotContain($"Comment number {CommentsDigest.MaxListed}.");
        await Assert.That(mail.Text).Contains("7 more aren't listed here.");
        await Assert.That(mail.Html).Contains("7 more aren&#39;t listed here.");
    }

    [Test]
    public async Task Without_a_base_url_the_digest_leaves_its_link_out_whatever_host_the_comment_came_to()
    {
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = Site(root, clock: clock, more: b => b.UseSetting("Site:Admins", "boss@example.com"));
        using var client = site.CreateClient();
        client.DefaultRequestHeaders.Host = "elsewhere.example";
        await Assert.That(await client.GetStringAsync(new Uri("/robots.txt", UriKind.Relative))).Contains("http://elsewhere.example/sitemap.xml");
        await SendAsync(client, site, "Ada", "Sent to another host name.");
        clock.Advance(DigestSender.Settle + TimeSpan.FromMinutes(1));

        var mail = new RecordingSender();
        using var sender = new DigestSender(site.Services.GetServices<IDigest>(), new MemoryDigestStateStore(), mail, site.Services.GetRequiredService<ContentCatalogProvider>(), clock, NullLogger<DigestSender>.Instance);
        await Assert.That(await sender.RunOnceAsync(CancellationToken.None)).IsEqualTo(1);

        var sent = mail.Sent.Single();
        await Assert.That(sent.Text).Contains("Sent to another host name.").And.DoesNotContain("elsewhere.example").And.DoesNotContain("/admin/comments");
        await Assert.That(sent.Html).DoesNotContain("elsewhere.example").And.DoesNotContain("<a href");
    }
}
