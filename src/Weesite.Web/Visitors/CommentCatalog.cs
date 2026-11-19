using Microsoft.AspNetCore.OutputCaching;

namespace Weesite.Web.Visitors;

/// <summary>An approved comment, as its post shows it.</summary>
/// <param name="Id">Its ID.</param>
/// <param name="Name">Its writer's name.</param>
/// <param name="Text">The approved text.</param>
/// <param name="Created">When it was written.</param>
internal sealed record PublicComment(string Id, string Name, string Text, DateTimeOffset Created);

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

    /// <summary>Gets a post's approved comments, oldest first.</summary>
    /// <param name="post">The post's source path.</param>
    /// <returns>The comments; none before the first load.</returns>
    public IReadOnlyList<PublicComment> For(string post) =>
        Volatile.Read(ref _current).Comments.GetValueOrDefault(post) ?? [];

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
                .Where(c => c.Status == CommentStatus.Approved)
                .GroupBy(c => c.Post, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<PublicComment>)[.. g.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new PublicComment(c.Id, c.Name, c.Text, c.Created))],
                    StringComparer.Ordinal);
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

    /// <summary>Deletes the rejected comments whose time is up.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many went.</returns>
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow().AddDays(-RejectedDays);
        var deleted = 0;
        foreach (var comment in await store.ListAsync(cancellationToken))
        {
            if (comment.Status == CommentStatus.Rejected && (comment.Moderated ?? comment.Created) < before
                && await store.DeleteAsync(comment.Post, comment.Id, cancellationToken))
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
