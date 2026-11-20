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
    private Snapshot _current = new(null, new Dictionary<string, IReadOnlyList<PublicComment>>(StringComparer.Ordinal));

    /// <summary>Gets a post's approved comments, oldest first, with the markers of deleted ones
    /// that a later comment follows (F-0203).</summary>
    /// <param name="post">The post's source path.</param>
    /// <returns>The comments; none before the first load.</returns>
    public IReadOnlyList<PublicComment> For(string post) =>
        Volatile.Read(ref _current).Comments.GetValueOrDefault(post) ?? [];

    /// <summary>Gets how many approved comments a post has, without the deleted ones' markers.</summary>
    /// <param name="post">The post's source path.</param>
    /// <returns>The count.</returns>
    public int CountFor(string post) => For(post).Count(c => !c.Deleted);

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
            var first = Volatile.Read(ref _current).Version is null;
            Volatile.Write(ref _current, new Snapshot(version, approved));
            if (!first)
            {
                await cache.EvictByTagAsync(Performance.ContentTag, CancellationToken.None);
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
                catch (Exception e) when (e is not OperationCanceledException)
                {
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

    private sealed record Snapshot(string? Version, IReadOnlyDictionary<string, IReadOnlyList<PublicComment>> Comments);
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

            if (comment.Status == CommentStatus.Rejected && await store.DeleteAsync(comment.Post, comment.Id, cancellationToken))
            {
                deleted++;
            }
            else if (comment.Status == CommentStatus.Removed && (comment.Name.Length > 0 || comment.Text.Length > 0))
            {
                await store.SaveAsync(comment with { Name = "", Text = "", PendingText = null, TokenHash = "", By = null }, cancellationToken);
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
                catch (Exception e) when (e is not OperationCanceledException)
                {
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
