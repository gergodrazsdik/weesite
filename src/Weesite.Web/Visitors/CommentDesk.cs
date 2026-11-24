using System.Security.Claims;
using Weesite.Content;
using Weesite.Web.Admin;

namespace Weesite.Web.Visitors;

/// <summary>A comment as its writer's browser sends it.</summary>
/// <param name="Post">The post's slug.</param>
/// <param name="Name">The writer's name.</param>
/// <param name="Text">The comment.</param>
/// <param name="FormToken">The form token, from <c>/visitor/comments/form</c>.</param>
/// <param name="Website">The honeypot: people leave it empty.</param>
/// <param name="Proof">The proof-of-work answer for <c>comment</c>.</param>
internal sealed record CommentSubmission(string? Post, string? Name, string? Text, string? FormToken, string? Website, string? Proof);

/// <summary>A writer's edit of their comment.</summary>
/// <param name="Text">The new text.</param>
/// <param name="Proof">The proof-of-work answer for <c>comment</c>.</param>
internal sealed record CommentEdit(string? Text, string? Proof);

/// <summary>What became of something a visitor did with a comment.</summary>
internal enum CommentOutcome
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>A bot filled the honeypot: it's told the comment was sent, and nothing is kept.</summary>
    Pretended,

    /// <summary>The post isn't published, or takes no comments.</summary>
    Closed,

    /// <summary>Sent sooner after the form was shown than a person types.</summary>
    TooFast,

    /// <summary>The form was shown too long ago.</summary>
    Expired,

    /// <summary>No form token, or one from elsewhere.</summary>
    Invalid,

    /// <summary>No valid proof of work: the page's script didn't send it.</summary>
    NoProof,

    /// <summary>Over the hourly limit for the address.</summary>
    TooMany,

    /// <summary>Over the hourly limit on edits and deletions for the address.</summary>
    TooManyChanges,

    /// <summary>The name is missing, too long, or has a link or markup.</summary>
    BadName,

    /// <summary>The text is missing, too long, or breaks the text rules.</summary>
    BadText,

    /// <summary>No such comment on that post.</summary>
    NotFound,

    /// <summary>The token isn't the comment's.</summary>
    WrongToken,

    /// <summary>A rejected comment can't be edited.</summary>
    NotEditable,

    /// <summary>It kept changing while this was saved (F-0209): read it again.</summary>
    Changed,
}

/// <summary>What became of something a visitor did with a comment.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Problem">What's wrong with the name or text, when that was it.</param>
/// <param name="Comment">The comment, when there is one to show.</param>
/// <param name="Token">The new comment's token: given once, never again.</param>
internal sealed record CommentResult(CommentOutcome Outcome, TextProblem Problem = TextProblem.None, Comment? Comment = null, string? Token = null);

/// <summary>What became of a moderation.</summary>
internal enum ModerationOutcome
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>No such comment.</summary>
    NotFound,

    /// <summary>The editor may not moderate the comments of that post.</summary>
    Forbidden,

    /// <summary>Nothing waits: a published comment without an edit, or a rejected one.</summary>
    NothingWaiting,

    /// <summary>
    /// The comment changed since the moderator read it (F-0209): its writer edited or deleted it, or
    /// another moderator decided first. Nothing was done; read it again.
    /// </summary>
    Changed,

    /// <summary>Only admins may remove a published comment (T-0180).</summary>
    AdminsOnly,

    /// <summary>There's nothing published to remove: it waits, or it's rejected, deleted or removed already.</summary>
    NotPublished,
}

/// <summary>
/// Comments (ADR 0011): what visitors send, through every layer, and what editors do with them.
/// A new comment passes the post check, the honeypot, the form token, the text rules, the proof of
/// work (required: the form only exists with JavaScript) and the hourly limit per address hash, and
/// then waits for moderation. Its writer's browser gets a token once, and with it can read, edit and
/// delete the comment; an edit of an approved comment waits beside the approved text, which stays
/// public until an editor approves the edit. Admins moderate every post's comments, authors their
/// own posts'. Nothing about the writer beyond what they typed is kept or logged.
/// </summary>
/// <param name="store">The comments.</param>
/// <param name="catalog">The approved comments in memory, refreshed after each change.</param>
/// <param name="content">The site and its posts.</param>
/// <param name="guard">The form token and the honeypot.</param>
/// <param name="proof">The proof of work.</param>
/// <param name="hashes">Address hashes.</param>
/// <param name="limits">The counters.</param>
/// <param name="options">The limits.</param>
/// <param name="audit">The audit log, for moderation.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class CommentDesk(
    ICommentStore store,
    CommentCatalog catalog,
    ContentCatalogProvider content,
    FormGuard guard,
    ProofOfWork proof,
    ClientHashes hashes,
    IVisitorStore limits,
    VisitorOptions options,
    IAuditLog audit,
    TimeProvider time,
    ILogger<CommentDesk> logger)
{
    /// <summary>The form's purpose, for its token and its proof of work.</summary>
    public const string Purpose = "comment";

    /// <summary>The longest name.</summary>
    public const int NameMax = 60;

    /// <summary>The longest comment.</summary>
    public const int TextMax = 1000;

    /// <summary>What the hourly limit on a writer's edits and deletions counts, and its hash's purpose.</summary>
    public const string ChangePurpose = "comment-change";

    // How many times a change is read and tried again when the comment changed meanwhile (F-0209).
    private const int Attempts = 3;

    /// <summary>Gets a published post by slug, or null.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>The post.</returns>
    public ContentItem? Published(string? slug) =>
        string.IsNullOrEmpty(slug) ? null : content.Current.FindPost(slug);

    /// <summary>Gets whether a post takes comments now: published, comments on for the site and not closed in its front matter.</summary>
    /// <param name="post">The post.</param>
    /// <returns>True when it does.</returns>
    public bool IsOpen(ContentItem? post) =>
        post is { Kind: ContentKind.Post, CommentsClosed: false } && content.Current.Site.Comments;

    /// <summary>Makes the token for a comment form being shown now.</summary>
    /// <returns>The token.</returns>
    public string FormToken() => guard.Issue(Purpose);

    /// <summary>Takes a new comment.</summary>
    /// <param name="context">The request.</param>
    /// <param name="sent">What the browser sent.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What became of it; for a kept comment, the comment and its token.</returns>
    public async Task<CommentResult> SubmitAsync(HttpContext context, CommentSubmission sent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sent);
        var post = Published(sent.Post);
        if (!IsOpen(post))
        {
            return new CommentResult(CommentOutcome.Closed);
        }

        var now = time.GetUtcNow();

        // A signed-in editor comments under their own name, marked (T-0161): no honeypot, proof or
        // limit; public at once from an admin or the post's author, who'd approve it anyway.
        if (Signed(context.User) is { } editor)
        {
            if (VisitorText.IsFarTooLong(sent.Text, TextMax))
            {
                return new CommentResult(CommentOutcome.BadText, TextProblem.TooLong);
            }

            var editorText = VisitorText.Normalize(sent.Text);
            if (VisitorText.Check(editorText, TextMax, quotable: Quotable(post!.SourcePath)) is not TextProblem.None and var editorProblem)
            {
                return new CommentResult(CommentOutcome.BadText, editorProblem);
            }

            var by = MarkOf(post!.AuthorId, EditorRights.AuthorId(context.User));
            var status = CanModerate(context.User, post.SourcePath) ? CommentStatus.Approved : CommentStatus.Pending;
            var editorToken = Comment.NewToken();
            var written = new Comment(Comment.NewId(now), post.SourcePath, editor, editorText, null, status, now, null, status == CommentStatus.Approved ? now : null, Comment.HashOf(editorToken)) { By = by };
            if (await store.SaveAsync(written, cancellationToken) is not { } kept)
            {
                return new CommentResult(CommentOutcome.Changed);
            }

            await catalog.RefreshAsync(cancellationToken);
            return new CommentResult(CommentOutcome.Done, Comment: kept, Token: editorToken);
        }

        if (FormGuard.IsHoneypotFilled(sent.Website))
        {
            LogHoneypot(logger);
            var pretend = new Comment(Comment.NewId(now), post!.SourcePath, VisitorText.NormalizeLine(Cut(sent.Name, NameMax)), VisitorText.Normalize(Cut(sent.Text, TextMax)), null, CommentStatus.Pending, now, null, null, "");
            return new CommentResult(CommentOutcome.Pretended, Comment: pretend, Token: Comment.NewToken());
        }

        switch (guard.Check(Purpose, sent.FormToken))
        {
            case FormCheck.TooFast:
                return new CommentResult(CommentOutcome.TooFast);
            case FormCheck.Expired:
                return new CommentResult(CommentOutcome.Expired);
            case FormCheck.Invalid:
                return new CommentResult(CommentOutcome.Invalid);
        }

        // Far over the limits: refused before the text is normalized, which copies it several times.
        if (VisitorText.IsFarTooLong(sent.Name, NameMax))
        {
            return new CommentResult(CommentOutcome.BadName, TextProblem.TooLong);
        }

        var name = VisitorText.NormalizeLine(sent.Name);
        if (VisitorText.Check(name, NameMax, maxLinks: 0) is not TextProblem.None and var nameProblem)
        {
            return new CommentResult(CommentOutcome.BadName, nameProblem);
        }

        if (VisitorText.IsFarTooLong(sent.Text, TextMax))
        {
            return new CommentResult(CommentOutcome.BadText, TextProblem.TooLong);
        }

        var text = VisitorText.Normalize(sent.Text);
        if (VisitorText.Check(text, TextMax, quotable: Quotable(post!.SourcePath)) is not TextProblem.None and var problem)
        {
            return new CommentResult(CommentOutcome.BadText, problem);
        }

        if (!await proof.VerifyAsync(Purpose, sent.Proof, cancellationToken))
        {
            return new CommentResult(CommentOutcome.NoProof);
        }

        // A form token takes one comment; the form asks for a new one when told it expired.
        if (!await guard.TryUseAsync(sent.FormToken!, cancellationToken))
        {
            return new CommentResult(CommentOutcome.Expired);
        }

        var hash = await hashes.OfAddressAsync(context, Purpose, cancellationToken);
        if (!await limits.TryCountAsync(Purpose, hash, now, CountWindow.Hour, options.CommentsPerHour, cancellationToken))
        {
            LogTooMany(logger);
            return new CommentResult(CommentOutcome.TooMany);
        }

        // Its time is taken just before the save, after the proof and the limit's storage calls, so a
        // slow call before it can't put it in a range a digest has already covered (F-0250).
        var token = Comment.NewToken();
        var created = time.GetUtcNow();
        var comment = new Comment(Comment.NewId(created), post!.SourcePath, name, text, null, CommentStatus.Pending, created, null, null, Comment.HashOf(token));
        if (await store.SaveAsync(comment, cancellationToken) is not { } saved)
        {
            // Another comment with its ID, a chance in billions: sent again, it gets another.
            return new CommentResult(CommentOutcome.Changed);
        }

        // Not on the post yet, but counted in the admin's menu (F-0225).
        await catalog.RefreshAsync(cancellationToken);
        LogReceived(logger);
        return new CommentResult(CommentOutcome.Done, Comment: saved, Token: token);
    }

    /// <summary>Gets a writer's own comment, with what waits.</summary>
    /// <param name="slug">The post's slug.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="token">The token its writer got.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The comment, or why not.</returns>
    public async Task<CommentResult> OwnAsync(string? slug, string? id, string? token, CancellationToken cancellationToken)
    {
        var (comment, outcome) = await FindOwnAsync(slug, id, token, cancellationToken);
        return new CommentResult(outcome, Comment: comment);
    }

    /// <summary>
    /// Edits a writer's own comment: a pending one is replaced; an approved one's edit waits beside
    /// it. Edits and deletions count against <see cref="VisitorOptions.CommentChangesPerHour"/> per
    /// address hash, except a signed-in editor's.
    /// </summary>
    /// <param name="context">The request: its address for the limit, and its user: a moderator of
    /// the post edits without a proof, at once.</param>
    /// <param name="slug">The post's slug.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="token">The token its writer got.</param>
    /// <param name="edit">The new text and a proof of work.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The comment as it is now, or why not.</returns>
    public async Task<CommentResult> EditAsync(HttpContext context, string? slug, string? id, string? token, CommentEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(edit);
        var user = context.User;
        var proven = false;
        var counted = false;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (comment, outcome) = await FindOwnAsync(slug, id, token, cancellationToken);
            if (comment is null)
            {
                return new CommentResult(outcome);
            }

            if (!IsOpen(Published(slug)))
            {
                return new CommentResult(CommentOutcome.Closed);
            }

            if (comment.Status is CommentStatus.Rejected or CommentStatus.Removed)
            {
                return new CommentResult(CommentOutcome.NotEditable);
            }

            if (VisitorText.IsFarTooLong(edit.Text, TextMax))
            {
                return new CommentResult(CommentOutcome.BadText, TextProblem.TooLong);
            }

            var text = VisitorText.Normalize(edit.Text);
            if (VisitorText.Check(text, TextMax, quotable: Quotable(comment.Post)) is not TextProblem.None and var problem)
            {
                return new CommentResult(CommentOutcome.BadText, problem);
            }

            // A moderator's own comment needs no proof, and its edit applies at once (T-0161). A
            // proof counts once: a second try, after a change meanwhile, doesn't ask for it again.
            var moderator = Signed(user) is not null && CanModerate(user, comment.Post);
            if (!moderator && !proven)
            {
                if (!await proof.VerifyAsync(Purpose, edit.Proof, cancellationToken))
                {
                    return new CommentResult(CommentOutcome.NoProof);
                }

                proven = true;
            }

            // Each change is a save and a reload of the comments: counted once, not per try.
            if (!counted)
            {
                if (!await TryCountChangeAsync(context, cancellationToken))
                {
                    return new CommentResult(CommentOutcome.TooManyChanges);
                }

                counted = true;
            }

            // Decided on the comment as read now, and saved only at that version (F-0209): if a
            // moderator approved it meanwhile, the next try makes the edit wait beside it.
            var now = time.GetUtcNow();
            var revised = comment.Status == CommentStatus.Approved && !string.Equals(text, comment.Text, StringComparison.Ordinal) ? now : comment.Revised;
            var edited = moderator
                ? comment with { Text = text, PendingText = null, Edited = now, Revised = revised }
                : comment.Status == CommentStatus.Pending
                ? comment with { Text = text, Edited = now }
                : string.Equals(text, comment.Text, StringComparison.Ordinal)
                    ? comment with { PendingText = null }
                    : comment with { PendingText = text, Edited = now };
            if (await store.SaveAsync(edited, cancellationToken) is { } saved)
            {
                await catalog.RefreshAsync(cancellationToken);
                return new CommentResult(CommentOutcome.Done, Comment: saved);
            }
        }

        return new CommentResult(CommentOutcome.Changed);
    }

    /// <summary>
    /// Deletes a writer's own comment, whatever its status. A published one leaves a marker in its
    /// place, without its name, text or token, so the thread still reads (F-0203); one never
    /// published goes without a trace.
    /// </summary>
    /// <param name="context">The request: its address for the hourly limit on changes.</param>
    /// <param name="slug">The post's slug.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="token">The token its writer got.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>Done, or why not.</returns>
    public async Task<CommentOutcome> DeleteOwnAsync(HttpContext context, string? slug, string? id, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var counted = false;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (comment, outcome) = await FindOwnAsync(slug, id, token, cancellationToken);
            if (comment is null)
            {
                return outcome;
            }

            // A moderator removed it from the post: its marker stays, its writer can't take it (T-0180).
            if (comment.Status == CommentStatus.Removed)
            {
                return CommentOutcome.NotEditable;
            }

            if (!counted)
            {
                if (!await TryCountChangeAsync(context, cancellationToken))
                {
                    return CommentOutcome.TooManyChanges;
                }

                counted = true;
            }

            // At the version read (F-0209): a moderator's decision meanwhile, such as approving it,
            // is read again, so an approved one leaves its marker rather than going without a trace.
            var done = comment.Status == CommentStatus.Approved
                ? await store.SaveAsync(comment with { Status = CommentStatus.Deleted, Name = "", Text = "", PendingText = null, TokenHash = "", By = null, Edited = time.GetUtcNow() }, cancellationToken) is not null
                : await store.DeleteAsync(comment.Post, comment.Id, cancellationToken, comment.Version);
            if (done)
            {
                await catalog.RefreshAsync(cancellationToken);
                return CommentOutcome.Done;
            }
        }

        return CommentOutcome.Changed;
    }

    /// <summary>
    /// The mark a signed-in editor's comment gets: <c>author</c> when they're the post's author,
    /// <c>editor</c> otherwise, also when the post or the editor has no author profile (F-0205).
    /// </summary>
    /// <param name="postAuthor">The post's author ID, if it has one.</param>
    /// <param name="writerAuthor">The writer's author ID, if they have one.</param>
    /// <returns><c>author</c> or <c>editor</c>.</returns>
    public static string MarkOf(string? postAuthor, string? writerAuthor) =>
        !string.IsNullOrEmpty(writerAuthor) && string.Equals(postAuthor, writerAuthor, StringComparison.Ordinal) ? "author" : "editor";

    /// <summary>Gets whether an editor may moderate the comments of a post: admins every post's, authors their own.</summary>
    /// <param name="user">The editor.</param>
    /// <param name="post">The post's source path.</param>
    /// <returns>True when they may.</returns>
    public bool CanModerate(ClaimsPrincipal user, string post)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsInRole(nameof(EditorRole.Admin)) || CanModerate(user, FindPost(post));
    }

    /// <summary>Lists the comments an editor may moderate, oldest first.</summary>
    /// <param name="user">The editor.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The comments.</returns>
    public async Task<IReadOnlyList<Comment>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var all = await store.ListAsync(cancellationToken);
        var posts = Posts();
        return [.. all.Where(c => CanModerate(user, posts.GetValueOrDefault(c.Post)))];
    }

    /// <summary>
    /// Counts the comments waiting for an editor, from the catalog's last load rather than the store,
    /// so every admin page can show it (F-0225). It can lag another replica's change by the reload
    /// interval; this replica's own changes count at once.
    /// </summary>
    /// <param name="user">The editor.</param>
    /// <returns>How many wait on the posts they may moderate.</returns>
    public int WaitingCount(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var posts = Posts();
        return catalog.Waiting.Where(pair => CanModerate(user, posts.GetValueOrDefault(pair.Key))).Sum(pair => pair.Value);
    }

    /// <summary>Gets every post, published or not, by its source path: one lookup for a list of comments (F-0225).</summary>
    /// <returns>The posts.</returns>
    public IReadOnlyDictionary<string, ContentItem> Posts()
    {
        var catalogNow = content.Current;
        return catalogNow.Posts.Concat(catalogNow.Unpublished)
            .Where(i => i.Kind == ContentKind.Post)
            .GroupBy(i => i.SourcePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>Finds a post, published or not, by its source path.</summary>
    /// <param name="post">The source path.</param>
    /// <returns>The post, or null when it's gone.</returns>
    public ContentItem? FindPost(string post)
    {
        var catalogNow = content.Current;
        return catalogNow.Posts.Concat(catalogNow.Unpublished).FirstOrDefault(i => i.Kind == ContentKind.Post && string.Equals(i.SourcePath, post, StringComparison.Ordinal));
    }

    /// <summary>
    /// Approves a comment, or the edit waiting beside it, as the moderator read it (F-0209): with the
    /// <see cref="Comment.Stamp"/> they read, and refused as <see cref="ModerationOutcome.Changed"/>
    /// when its text changed since, or when there's no stamp, so what's published is what they saw.
    /// </summary>
    /// <param name="user">The editor.</param>
    /// <param name="post">The post's source path.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="seen">The stamp of the comment as the moderator read it.</param>
    /// <param name="via">What the audit entry adds, such as the app that did it.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What became of it.</returns>
    public Task<ModerationOutcome> ApproveAsync(ClaimsPrincipal user, string post, string id, string? seen, string? via, CancellationToken cancellationToken) =>
        ModerateAsync(user, post, id, "comment-approved", via, seen ?? "", c => c.Status switch
        {
            // A rejected one too, until the clean-up deletes it: a rejection can be taken back (U-49).
            CommentStatus.Pending or CommentStatus.Rejected => c with { Status = CommentStatus.Approved },
            CommentStatus.Approved when c.PendingText is { } edit => c with { Text = edit, PendingText = null, Revised = time.GetUtcNow() },
            _ => null,
        }, cancellationToken);

    /// <summary>
    /// Rejects a comment that waits, or the edit waiting beside a published one (the published text
    /// stays). A published comment without an edit has nothing waiting: <see cref="RemoveAsync"/>
    /// takes one off its post (F-0210).
    /// </summary>
    /// <param name="user">The editor.</param>
    /// <param name="post">The post's source path.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="seen">The stamp of the comment as the moderator read it, when they send one: then a comment changed since is left alone.</param>
    /// <param name="via">What the audit entry adds.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What became of it.</returns>
    public Task<ModerationOutcome> RejectAsync(ClaimsPrincipal user, string post, string id, string? seen, string? via, CancellationToken cancellationToken) =>
        ModerateAsync(user, post, id, "comment-rejected", via, seen, c => c.Status switch
        {
            CommentStatus.Pending => c with { Status = CommentStatus.Rejected },
            CommentStatus.Approved when c.PendingText is not null => c with { PendingText = null },
            _ => null,
        }, cancellationToken);

    /// <summary>
    /// Takes a published comment off its post, with an edit waiting beside it or not: its place stays
    /// marked as removed by a moderator (T-0161, T-0180, F-0210). Admins only.
    /// </summary>
    /// <param name="user">The admin.</param>
    /// <param name="post">The post's source path.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="seen">The stamp of the comment as the admin read it, when they send one.</param>
    /// <param name="via">What the audit entry adds.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What became of it.</returns>
    public async Task<ModerationOutcome> RemoveAsync(ClaimsPrincipal user, string post, string id, string? seen, string? via, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var outcome = await ModerateAsync(user, post, id, "comment-removed", via, seen, c =>
            !user.IsInRole(nameof(EditorRole.Admin)) || c.Status != CommentStatus.Approved ? null : c with { Status = CommentStatus.Removed, PendingText = null }, cancellationToken);
        return outcome != ModerationOutcome.NothingWaiting ? outcome
            : user.IsInRole(nameof(EditorRole.Admin)) ? ModerationOutcome.NotPublished
            : ModerationOutcome.AdminsOnly;
    }

    /// <summary>The name a signed-in editor comments under: their author profile's name, else the
    /// name their sign-in gave, else their address's local part; null for a visitor (T-0161).</summary>
    /// <param name="user">The request's user.</param>
    /// <returns>The name, or null.</returns>
    public string? Signed(ClaimsPrincipal? user)
    {
        if (user is not { Identity.IsAuthenticated: true } || !(user.IsInRole(nameof(EditorRole.Admin)) || user.IsInRole(nameof(EditorRole.Author))))
        {
            return null;
        }

        var author = EditorRights.AuthorId(user) is { } id ? content.Current.Site.FindAuthor(id)?.Name : null;
        var email = EditorClaims.Email(user);
        var name = author ?? EditorClaims.ProvidedName(user) ?? email.Split('@')[0];
        return VisitorText.NormalizeLine(name) is { Length: > 0 } clean ? clean[..Math.Min(clean.Length, NameMax)] : null;
    }

    /// <summary>The comments of a post waiting for a moderator, oldest first (T-0161).</summary>
    /// <param name="user">The moderator.</param>
    /// <param name="post">The post.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The comments, or empty when the user can't moderate this post.</returns>
    public async Task<IReadOnlyList<Comment>> WaitingOnAsync(ClaimsPrincipal user, ContentItem post, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (Signed(user) is null || !CanModerate(user, post.SourcePath))
        {
            return [];
        }

        var all = await store.ListAsync(cancellationToken);
        return [.. all.Where(c => c.Post == post.SourcePath && c.Waiting).OrderBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>Deletes a comment.</summary>
    /// <param name="user">The editor.</param>
    /// <param name="post">The post's source path.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="via">What the audit entry adds.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What became of it.</returns>
    public async Task<ModerationOutcome> DeleteAsync(ClaimsPrincipal user, string post, string id, string? via, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await store.GetAsync(post ?? "", id ?? "", cancellationToken) is not { } comment)
        {
            return ModerationOutcome.NotFound;
        }

        if (!CanModerate(user, comment.Post))
        {
            return ModerationOutcome.Forbidden;
        }

        await store.DeleteAsync(comment.Post, comment.Id, cancellationToken);
        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(user), "comment-deleted", Target(comment, via)), CancellationToken.None);
        await catalog.RefreshAsync(cancellationToken);
        return ModerationOutcome.Done;
    }

    private static string Target(Comment comment, string? via) =>
        via is null ? $"{comment.Post}#{comment.Id}" : $"{comment.Post}#{comment.Id} ({via})";

    // The links a reply may quote without their counting against its limit: those of the post's
    // published comments, which Reply quotes (F-0250).
    private HashSet<string> Quotable(string post) =>
        new(catalog.For(post).SelectMany(c => VisitorText.Links(c.Text)).Select(l => l.Url), StringComparer.Ordinal);

    private static bool CanModerate(ClaimsPrincipal user, ContentItem? item) =>
        user.IsInRole(nameof(EditorRole.Admin))
        || (user.IsInRole(nameof(EditorRole.Author)) && item is not null
            && EditorRights.AuthorId(user) is { } author && string.Equals(item.AuthorId, author, StringComparison.Ordinal));

    // Reads the comment, decides on it as read, and saves at that version (F-0209). When it changed
    // meanwhile, it's read and decided again, unless the moderator sent the stamp they read: then
    // their decision was about something else, and it's left to them.
    private async Task<ModerationOutcome> ModerateAsync(ClaimsPrincipal user, string post, string id, string action, string? via, string? seen, Func<Comment, Comment?> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await store.GetAsync(post ?? "", id ?? "", cancellationToken) is not { } comment)
            {
                return ModerationOutcome.NotFound;
            }

            if (!CanModerate(user, comment.Post))
            {
                return ModerationOutcome.Forbidden;
            }

            if (seen is not null && !string.Equals(seen, comment.Stamp, StringComparison.Ordinal))
            {
                return ModerationOutcome.Changed;
            }

            if (change(comment) is not { } changed)
            {
                return ModerationOutcome.NothingWaiting;
            }

            if (await store.SaveAsync(changed with { Moderated = time.GetUtcNow() }, cancellationToken) is not null)
            {
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(user), action, Target(comment, via)), CancellationToken.None);
                await catalog.RefreshAsync(cancellationToken);
                return ModerationOutcome.Done;
            }
        }

        return ModerationOutcome.Changed;
    }

    // Cuts what a bot sent to a few times a field's limit, for the comment it's told was kept.
    private static string? Cut(string? text, int maxLength) =>
        VisitorText.IsFarTooLong(text, maxLength) ? text![..(maxLength * VisitorText.SlackFactor)] : text;

    // Counts a writer's edit or deletion against the hourly limit for their address; a signed-in
    // editor's isn't counted, as their comments aren't.
    private async Task<bool> TryCountChangeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (Signed(context.User) is not null)
        {
            return true;
        }

        var hash = await hashes.OfAddressAsync(context, ChangePurpose, cancellationToken);
        if (await limits.TryCountAsync(ChangePurpose, hash, time.GetUtcNow(), CountWindow.Hour, options.CommentChangesPerHour, cancellationToken))
        {
            return true;
        }

        LogTooManyChanges(logger);
        return false;
    }

    private async Task<(Comment? Comment, CommentOutcome Outcome)> FindOwnAsync(string? slug, string? id, string? token, CancellationToken cancellationToken)
    {
        if (Published(slug) is not { } post || !Comment.IsId(id))
        {
            return (null, CommentOutcome.NotFound);
        }

        // A token that can't be one is refused before the comment is read.
        if (!Comment.IsToken(token))
        {
            return (null, CommentOutcome.WrongToken);
        }

        // A deleted one's marker is nobody's (F-0203): to its writer's browser, it's gone.
        if (await store.GetAsync(post.SourcePath, id!, cancellationToken) is not { } comment || comment.Status == CommentStatus.Deleted)
        {
            return (null, CommentOutcome.NotFound);
        }

        return comment.HasToken(token) ? (comment, CommentOutcome.Done) : (null, CommentOutcome.WrongToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Comments: a comment arrived; it waits for moderation")]
    private static partial void LogReceived(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Comments: the honeypot was filled; nothing kept")]
    private static partial void LogHoneypot(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Comments: over the hourly limit for an address")]
    private static partial void LogTooMany(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Comments: over the hourly limit on edits and deletions for an address")]
    private static partial void LogTooManyChanges(ILogger logger);
}
