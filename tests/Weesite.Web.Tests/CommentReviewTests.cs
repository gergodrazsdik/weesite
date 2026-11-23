using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Web.Admin;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The review of comments (F-0209 to F-0213, F-0225): versions, remove, the catalog.</summary>
internal sealed partial class CommentTests
{
    [Test]
    public async Task The_memory_store_saves_only_at_the_version_read()
    {
        var store = new MemoryCommentStore();
        var now = DateTimeOffset.UtcNow;
        var comment = new Comment(Comment.NewId(now), PostPath, "Ada", "One", null, CommentStatus.Pending, now, null, null, "x");

        var kept = await store.SaveAsync(comment, CancellationToken.None);
        var again = await store.SaveAsync(comment, CancellationToken.None);
        var changed = await store.SaveAsync(kept! with { Text = "Two" }, CancellationToken.None);
        var stale = await store.SaveAsync(kept with { Text = "Stale" }, CancellationToken.None);
        var staleDelete = await store.DeleteAsync(PostPath, comment.Id, CancellationToken.None, kept.Version);

        await Assert.That(kept.Version).IsNotNull();
        await Assert.That(kept.Stamp?.Length).IsEqualTo(16);
        await Assert.That(again).IsNull();
        await Assert.That(changed!.Version).IsNotEqualTo(kept.Version);
        await Assert.That(changed.Stamp).IsNotEqualTo(kept.Stamp);
        await Assert.That(stale).IsNull();
        await Assert.That(staleDelete).IsFalse();
        await Assert.That((await store.GetAsync(PostPath, comment.Id, CancellationToken.None))!.Text).IsEqualTo("Two");

        // Gone is gone: a change read before the delete doesn't bring it back.
        await Assert.That(await store.DeleteAsync(PostPath, comment.Id, CancellationToken.None, changed.Version)).IsTrue();
        await Assert.That(await store.SaveAsync(changed with { Status = CommentStatus.Approved }, CancellationToken.None)).IsNull();
        await Assert.That(await store.GetAsync(PostPath, comment.Id, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Approving_publishes_what_the_moderator_saw_and_refuses_a_comment_changed_since()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (id, token) = await SendAsync(client, site, "Ada", "What the moderator reads");
        var seen = VersionOn(await admin.GetStringAsync(new Uri("/admin/comments", UriKind.Relative)), id);
        await Edit(client, site, id, token, "Something else entirely");

        // From the Comments page, and on the post: refused, and nothing changes.
        using var stale = await Moderate(admin, "approve", id, version: seen);
        using var onPost = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id, decision = "approve", version = seen });
        using var withoutVersion = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id, decision = "approve" });
        var notice = await admin.GetStringAsync(new Uri(stale.Headers.Location!.ToString(), UriKind.Relative));
        var unchanged = Stored(site).Single();

        await Assert.That(seen).IsNotEmpty();
        await Assert.That(stale.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=changed");
        await Assert.That(notice).Contains("role=\"alert\">That comment changed since the page showed it");
        await Assert.That(onPost.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await Json(onPost)).GetProperty("error").GetString()).IsEqualTo("changed");
        await Assert.That(withoutVersion.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(unchanged.Status).IsEqualTo(CommentStatus.Pending);
        await Assert.That(unchanged.Text).IsEqualTo("Something else entirely");

        // Read again, the new text is approved.
        using var approved = await Moderate(admin, "approve", id);
        await Assert.That(approved.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=approved");
        await Assert.That(await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative))).Contains("Something else entirely");
    }

    [Test]
    public async Task A_comment_its_writer_deleted_isnt_brought_back_by_a_moderator()
    {
        await using var root = new SiteFactory();
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        // A published one with an edit waiting: deleted, it leaves its marker.
        var (published, publishedToken) = await SendAsync(client, site, "Ada", "Published, then taken back");
        await Moderate(admin, "approve", published);
        await Edit(client, site, published, publishedToken, "An edit to approve");
        var (waiting, waitingToken) = await SendAsync(client, site, "Bob", "Never published");
        var listed = await admin.GetFromJsonAsync<List<OwnComment>>(new Uri($"/visitor/comments/moderation?post={Post}", UriKind.Relative));
        using var deletedPublished = await Own(client, HttpMethod.Delete, published, publishedToken);
        using var deletedWaiting = await Own(client, HttpMethod.Delete, waiting, waitingToken);

        using var approveEdit = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = published, decision = "approve", version = listed!.Single(c => c.Id == published).Version });
        using var approveGone = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = waiting, decision = "approve", version = listed!.Single(c => c.Id == waiting).Version });

        await Assert.That(deletedPublished.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(deletedWaiting.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(approveEdit.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(approveGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var marker = Stored(site).Single();
        await Assert.That(marker.Id).IsEqualTo(published);
        await Assert.That(marker.Status).IsEqualTo(CommentStatus.Deleted);
        await Assert.That(marker.Text).IsEmpty();
        await Assert.That(marker.PendingText).IsNull();
    }

    [Test]
    public async Task A_change_meanwhile_is_read_again_and_the_decision_made_on_what_is_there()
    {
        var racing = new RacingStore(new MemoryCommentStore());
        await using var root = new SiteFactory();
        await using var site = Site(root, more: b => b.ConfigureTestServices(s => s.AddSingleton<ICommentStore>(racing)));
        using var client = site.CreateClient();
        using var admin = Admin(site);

        // A moderator approves it while its writer saves an edit: the edit waits beside it.
        var (id, token) = await SendAsync(client, site, "Ada", "Before");
        racing.Meanwhile = c => c with { Status = CommentStatus.Approved, Moderated = DateTimeOffset.UtcNow };
        var edited = await Json(await Edit(client, site, id, token, "After"));
        var afterEdit = Stored(site).Single();

        // Its writer edits it while a moderator approves what they read: refused.
        var version = (await admin.GetFromJsonAsync<List<OwnComment>>(new Uri($"/visitor/comments/moderation?post={Post}", UriKind.Relative)))!.Single().Version;
        racing.Meanwhile = c => c with { PendingText = "Swapped in meanwhile" };
        using var approve = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id, decision = "approve", version });
        var afterApprove = Stored(site).Single();

        // Rejecting without a version is decided again on what's there now.
        racing.Meanwhile = c => c with { PendingText = "Swapped once more" };
        using var reject = await admin.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id, decision = "reject" });
        var afterReject = Stored(site).Single();

        await Assert.That(edited.GetProperty("status").GetString()).IsEqualTo("approved");
        await Assert.That(afterEdit.Text).IsEqualTo("Before");
        await Assert.That(afterEdit.PendingText).IsEqualTo("After");
        await Assert.That(approve.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(afterApprove.Text).IsEqualTo("Before");
        await Assert.That(afterApprove.PendingText).IsEqualTo("Swapped in meanwhile");
        await Assert.That(reject.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterReject.Status).IsEqualTo(CommentStatus.Approved);
        await Assert.That(afterReject.PendingText).IsNull();
        await Assert.That(afterReject.Text).IsEqualTo("Before");
    }

    [Test]
    public async Task On_the_post_reject_drops_an_edit_and_remove_takes_a_published_comment_off()
    {
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "content", "posts", "mine.md"),
            $"---\ntitle: Mine\nslug: mine\ndate: 2026-11-01\nauthor: {EditorRights.AuthorId("editor@example.com")}\n---\n\nMy post.\n");
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);
        using var author = Client(site, EditorRole.Author);

        var (id, token) = await SendAsync(client, site, "Ada", "Published words");
        var (pending, _) = await SendAsync(client, site, "Bob", "Still waiting");
        var (onMine, _) = await SendAsync(client, site, "Cy", "On the author's post", post: "mine");
        await Moderate(admin, "approve", id);
        await Moderate(admin, "approve", onMine, post: "posts/mine.md");
        await Edit(client, site, id, token, "An edit");

        // F-0210: reject drops the edit, and the published text stays; nothing else waits then.
        using var rejectEdit = await Decide(admin, Post, id, "reject");
        var afterReject = Stored(site).Single(c => c.Id == id);
        using var rejectPublished = await Decide(admin, Post, id, "reject");

        // Remove takes it off, with an edit waiting or not; never one that isn't published.
        await Edit(client, site, id, token, "Another edit");
        using var removePending = await Decide(admin, Post, pending, "remove");
        using var byAuthor = await Decide(author, "mine", onMine, "remove");
        using var removed = await Decide(admin, Post, id, "remove");
        var afterRemove = Stored(site).Single(c => c.Id == id);

        await Assert.That(rejectEdit.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterReject.Status).IsEqualTo(CommentStatus.Approved);
        await Assert.That(afterReject.PendingText).IsNull();
        await Assert.That(afterReject.Text).IsEqualTo("Published words");
        await Assert.That(rejectPublished.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await Json(rejectPublished)).GetProperty("error").GetString()).IsEqualTo("nothing-waiting");
        await Assert.That(removePending.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await Json(removePending)).GetProperty("error").GetString()).IsEqualTo("not-published");
        await Assert.That(byAuthor.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await Json(byAuthor)).GetProperty("message").GetString()).IsEqualTo("Only an admin can remove a published comment.");
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterRemove.Status).IsEqualTo(CommentStatus.Removed);
        await Assert.That(afterRemove.PendingText).IsNull();
        await Assert.That(Stored(site).Single(c => c.Id == pending).Status).IsEqualTo(CommentStatus.Pending);
        await Assert.That(Stored(site).Single(c => c.Id == onMine).Status).IsEqualTo(CommentStatus.Approved);
    }

    [Test]
    public async Task The_moderation_answers_speak_the_sites_language()
    {
        using var copy = new SiteCopy();
        Rewrite(copy, json => json["language"] = "hu");
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var visitor = site.CreateClient();
        using var admin = Admin(site);

        using var forbidden = await visitor.GetAsync(new Uri($"/visitor/comments/moderation?post={Post}", UriKind.Relative));
        using var decided = await visitor.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post = Post, id = "x", decision = "approve" });
        var (id, _) = await SendAsync(visitor, site, "Ada", "Szia");
        using var nothing = await Decide(admin, Post, id, "remove");

        await Assert.That((await Json(forbidden)).GetProperty("message").GetString()).IsEqualTo("Jelentkezz be olyan szerkesztőként, aki moderálhatja ennek a bejegyzésnek a hozzászólásait.");
        await Assert.That((await Json(decided)).GetProperty("message").GetString()).StartsWith("Jelentkezz be");
        await Assert.That((await Json(nothing)).GetProperty("message").GetString()).IsEqualTo("Ez a hozzászólás nem jelent meg, így nincs mit eltávolítani.");
    }

    [Test]
    public async Task The_first_load_clears_the_cached_pages_and_a_waiting_comment_doesnt()
    {
        // F-0213: a post cached before the first load would show no comments for minutes.
        var store = new MemoryCommentStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Ada", "Shown", null, CommentStatus.Approved, now, null, now, "x"), CancellationToken.None);
        var cache = new CountingOutputCache();
        using var catalog = new CommentCatalog(store, cache, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<CommentCatalog>.Instance);

        await catalog.RefreshAsync(CancellationToken.None);
        var afterFirst = cache.Evicted;
        await store.SaveAsync(new Comment(Comment.NewId(now.AddSeconds(1)), PostPath, "Bob", "Waiting", null, CommentStatus.Pending, now, null, null, "y"), CancellationToken.None);
        var reloaded = await catalog.RefreshAsync(CancellationToken.None);

        await Assert.That(afterFirst).IsEqualTo(1);
        await Assert.That(reloaded).IsTrue();
        await Assert.That(cache.Evicted).IsEqualTo(1);
        await Assert.That(catalog.Waiting[PostPath]).IsEqualTo(1);
    }

    [Test]
    public async Task The_clean_up_leaves_a_comment_that_changed_since_it_listed_it()
    {
        var inner = new MemoryCommentStore();
        var racing = new RacingStore(inner);
        var clock = new OAuthTests.TestClock();
        var now = clock.GetUtcNow();
        var id = Comment.NewId(now);
        await inner.SaveAsync(new Comment(id, PostPath, "Ada", "Removed words", null, CommentStatus.Removed, now, null, now, "x"), CancellationToken.None);
        using var cleanUp = new CommentCleanUp(racing, clock, NullLogger<CommentCleanUp>.Instance);
        clock.Advance(TimeSpan.FromDays(CommentCleanUp.RejectedDays + 1));

        racing.Meanwhile = c => c with { Name = "Changed meanwhile" };
        var first = await cleanUp.CleanUpAsync(CancellationToken.None);
        var second = await cleanUp.CleanUpAsync(CancellationToken.None);

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(1);
        await Assert.That((await inner.GetAsync(PostPath, id, CancellationToken.None))!.Name).IsEmpty();
    }

    private static async Task<HttpResponseMessage> Decide(HttpClient editor, string post, string id, string decision, string? version = null) =>
        await editor.PostAsJsonAsync(new Uri("/visitor/comments/moderation", UriKind.Relative), new { post, id, decision, version });

    // Changes a comment once, just before the next save of one that was read, as another request
    // would between that read and that save.
    private sealed class RacingStore(MemoryCommentStore inner) : ICommentStore
    {
        private Func<Comment, Comment>? _meanwhile;

        public Func<Comment, Comment>? Meanwhile
        {
            get => Volatile.Read(ref _meanwhile);
            set => Volatile.Write(ref _meanwhile, value);
        }

        public async Task<Comment?> SaveAsync(Comment comment, CancellationToken cancellationToken)
        {
            if (comment.Version is not null && Interlocked.Exchange(ref _meanwhile, null) is { } change
                && await inner.GetAsync(comment.Post, comment.Id, cancellationToken) is { } current)
            {
                await inner.SaveAsync(change(current), cancellationToken);
            }

            return await inner.SaveAsync(comment, cancellationToken);
        }

        public Task<Comment?> GetAsync(string post, string id, CancellationToken cancellationToken) => inner.GetAsync(post, id, cancellationToken);

        public Task<bool> DeleteAsync(string post, string id, CancellationToken cancellationToken, string? version = null) => inner.DeleteAsync(post, id, cancellationToken, version);

        public Task<IReadOnlyList<Comment>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

        public Task<string> VersionAsync(CancellationToken cancellationToken) => inner.VersionAsync(cancellationToken);
    }

    // An output cache that counts its evictions.
    private sealed class CountingOutputCache : Microsoft.AspNetCore.OutputCaching.IOutputCacheStore
    {
        public int Evicted { get; private set; }

        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
        {
            Evicted++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
