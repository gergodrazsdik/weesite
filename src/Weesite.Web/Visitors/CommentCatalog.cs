using Microsoft.AspNetCore.OutputCaching;

namespace Weesite.Web.Visitors;

/// <summary>An approved comment, as its post shows it, or the marker of one its writer deleted.</summary>
/// <param name="Id">Its ID.</param>
/// <param name="Name">Its writer's name; empty for a deleted one.</param>
/// <param name="Text">The approved text; empty for a deleted one.</param>
/// <param name="Created">When it was written.</param>
/// <param name="By"><c>author</c> or <c>editor</c> when a signed-in editor wrote it (T-0161).</param>
/// <param name="Revised">When its text last changed after it was published (F-0204), if it did.</param>
/// <param name="Deleted">Whether it's gone, deleted by its writer or removed by a moderator: only its place shows (T-0172).</param>
/// <param name="Removed">Whether a moderator removed it (T-0180).</param>
internal sealed record PublicComment(string Id, string Name, string Text, DateTimeOffset Created, string? By = null, DateTimeOffset? Revised = null, bool Deleted = false, bool Removed = false);

/// <summary>
/// The approved comments of every post, in memory, so a post renders them without a storage call
/// (ADR 0011). This replica reloads them after its own changes at once, and every
/// <c>Content:ReloadSeconds</c> (30 by default; 0: only after its own) when the store's version
/// shows another replica changed something. A reload clears the output cache, as a content reload
/// does, so a cached post shows its new comments.
/// </summary>
/// <param name="store">The comments.</param>
/// <param name="cache">The output cache.</param>
/// <param name="configuration">The configuration, for the reload interval.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class CommentCatalog(
    ICommentStore store,
    IOutputCacheStore cache,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<CommentCatalog> logger) : BackgroundService
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private Snapshot _current = new(null, new Dictionary<string, IReadOnlyList<PublicComment>>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal), []);

    // How many times a reload changed what posts show and emptied the output cache.
    private long _evictions;

    /// <summary>Gets a post's approved comments, oldest first, with the markers of deleted ones
    /// that a later comment follows (F-0203).</summary>
    /// <param name="post">The post's source path.</param>
    /// <returns>The comments; none before the first load.</returns>
    public IReadOnlyList<PublicComment> For(string post) =>
        Volatile.Read(ref _current).Comments.GetValueOrDefault(post) ?? [];

    /// <summary>Gets how many comments wait for moderation on each post, by source path, as of the
    /// last load, so the admin's menu counts them without reading the store (F-0225).</summary>
    public IReadOnlyDictionary<string, int> Waiting => Volatile.Read(ref _current).Waiting;

    /// <summary>Gets every post's comments, as <see cref="For"/> gives them: for the analytics' counts, without reading the store (F-0222).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PublicComment>> All => Volatile.Read(ref _current).Comments;

    /// <summary>Gets how many approved comments a post has, without the deleted ones' markers.</summary>
    /// <param name="post">The post's source path.</param>
    /// <returns>The count.</returns>
    public int CountFor(string post) => For(post).Count(c => !c.Deleted);

    /// <summary>Gets the comments and edits waiting for moderation, oldest first, as of the last
    /// load: the digest reads them here rather than the whole table every minute (F-0250).</summary>
    public IReadOnlyList<Comment> WaitingComments => Volatile.Read(ref _current).WaitingComments;

    /// <summary>
    /// Keeps a page rendered from these comments out of the output cache when they're reloaded
    /// while it renders (F-0245). The reload empties the cache, but a page whose render read the
    /// comments before it and was stored after it would stay for its ten minutes; so when the
    /// request is done, after the cache stored its answer, the cache is emptied again if a reload
    /// changed the comments meanwhile. A component calls this as it reads them.
    /// </summary>
    /// <param name="context">The request, when there is one.</param>
    public void Guard(HttpContext? context)
    {
        if (context is null || !context.Items.TryAdd(typeof(CommentCatalog), null))
        {
            return;
        }

        var read = Interlocked.Read(ref _evictions);
        context.Response.OnCompleted(async () =>
        {
            if (Interlocked.Read(ref _evictions) != read)
            {
                await EvictAsync();
            }
        });
    }

    /// <summary>Reloads the approved comments if the store's version moved, and clears the output cache then.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>True when they were reloaded.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        await _loading.WaitAsync(cancellationToken);
        try
        {
            var version = await store.VersionAsync(cancellationToken);
            if (string.Equals(version, Volatile.Read(ref _current).Version, StringComparison.Ordinal))
            {
                return false;
            }

            var comments = await store.ListAsync(cancellationToken);
            var approved = comments
                .Where(c => c.Status is CommentStatus.Approved or CommentStatus.Deleted or CommentStatus.Removed)
                .GroupBy(c => c.Post, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Thread(g), StringComparer.Ordinal);
            var waiting = comments
                .Where(c => c.Waiting)
                .GroupBy(c => c.Post, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var waitingComments = comments.Where(c => c.Waiting).ToList();
            var before = Volatile.Read(ref _current);
            Volatile.Write(ref _current, new Snapshot(version, approved, waiting, waitingComments));

            // The cached pages go when what they show changed, also on the first load: a page
            // rendered before it has no comments (F-0213). Counted before the eviction, so a page
            // that read the old ones and is stored after this eviction is evicted again (Guard).
            if (!SameThreads(before.Comments, approved))
            {
                Interlocked.Increment(ref _evictions);
                await EvictAsync();
            }

            return true;
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Content:ReloadSeconds", 30);
        try
        {
            do
            {
                try
                {
                    await RefreshAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A storage call's timeout is a failure like any other; only shutting down stops the loop (F-0208).
                    LogReloadFailed(logger, e);
                }

                if (seconds <= 0)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(seconds), time, stoppingToken);
            }
            while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _loading.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comments: reloading the approved comments failed; trying again later")]
    private static partial void LogReloadFailed(ILogger logger, Exception exception);

    // A post's comments, oldest first. A deleted one's marker stays only where a later comment
    // follows it: there it keeps the thread readable; at the end it would only be noise (F-0203).
    private static IReadOnlyList<PublicComment> Thread(IEnumerable<Comment> comments)
    {
        var list = comments.OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Status switch
            {
                CommentStatus.Deleted => new PublicComment(c.Id, "", "", c.Created, Deleted: true),
                CommentStatus.Removed => new PublicComment(c.Id, "", "", c.Created, Deleted: true, Removed: true),
                _ => new PublicComment(c.Id, c.Name, c.Text, c.Created, c.By, c.Revised),
            })
            .ToList();
        var last = list.FindLastIndex(c => !c.Deleted);
        return list[..(last + 1)];
    }

    private static bool SameThreads(IReadOnlyDictionary<string, IReadOnlyList<PublicComment>> before, Dictionary<string, IReadOnlyList<PublicComment>> after) =>
        before.Count == after.Count
        && after.All(pair => before.TryGetValue(pair.Key, out var was) && was.SequenceEqual(pair.Value));

    private async Task EvictAsync()
    {
        try
        {
            await cache.EvictByTagAsync(Performance.ContentTag, CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LogEvictFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Comments: emptying the output cache after a reload failed")]
    private static partial void LogEvictFailed(ILogger logger, Exception exception);

    private sealed record Snapshot(string? Version, IReadOnlyDictionary<string, IReadOnlyList<PublicComment>> Comments, IReadOnlyDictionary<string, int> Waiting, IReadOnlyList<Comment> WaitingComments);
}

/// <summary>Deletes rejected comments <see cref="RejectedDays"/> days after their rejection, once a day.</summary>
/// <param name="store">The comments.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class CommentCleanUp(ICommentStore store, TimeProvider time, ILogger<CommentCleanUp> logger) : BackgroundService
{
    /// <summary>How many days a rejected comment is kept, in case it was a mistake.</summary>
    public const int RejectedDays = 30;

    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>Deletes the rejected comments whose time is up; a removed one keeps its marker,
    /// without its name and text (T-0180).</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many went.</returns>
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow().AddDays(-RejectedDays);
        var deleted = 0;
        foreach (var comment in await store.ListAsync(cancellationToken))
        {
            if ((comment.Moderated ?? comment.Created) >= before)
            {
                continue;
            }

            // Each at the version listed: one that changed meanwhile waits for the next run (F-0209).
            if (comment.Status == CommentStatus.Rejected && await store.DeleteAsync(comment.Post, comment.Id, cancellationToken, comment.Version))
            {
                deleted++;
            }
            else if (comment.Status == CommentStatus.Removed && (comment.Name.Length > 0 || comment.Text.Length > 0)
                && await store.SaveAsync(comment with { Name = "", Text = "", PendingText = null, TokenHash = "", By = null }, cancellationToken) is not null)
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(9), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await CleanUpAsync(stoppingToken);
                    LogCleanedUp(logger, deleted);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A storage call's timeout is a failure like any other; only shutting down stops the loop (F-0208).
                    LogCleanUpFailed(logger, e);
                }

                await Task.Delay(Every, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Comments: deleted {Count} rejected comments")]
    private static partial void LogCleanedUp(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comments: the clean-up failed; it runs again tomorrow")]
    private static partial void LogCleanUpFailed(ILogger logger, Exception exception);
}
