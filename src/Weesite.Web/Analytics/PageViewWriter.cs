namespace Weesite.Web.Analytics;

/// <summary>
/// Writes queued page views to the store in batches. A failed write is logged and its views are
/// dropped: analytics never gets in the way of serving pages.
/// </summary>
internal sealed partial class PageViewWriter(
    PageViewRecorder recorder, IPageViewStore store, ILogger<PageViewWriter> logger) : BackgroundService
{
    private const int BatchSize = 200;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<PageView>(BatchSize);
        try
        {
            while (await recorder.Queue.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < BatchSize && recorder.Queue.TryRead(out var view))
                {
                    batch.Add(view);
                }

                await WriteAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; views still queued are lost, which is acceptable for analytics.
        }
    }

    private async Task WriteAsync(List<PageView> batch, CancellationToken cancellationToken)
    {
        try
        {
            await store.AddAsync(batch, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogWriteFailed(logger, batch.Count, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not store {Count} page views; they are dropped")]
    private static partial void LogWriteFailed(ILogger logger, int count, Exception exception);
}

/// <summary>
/// Once a day, deletes page views older than the retention (13 months, maintainer 2026-11-10) and
/// salts older than yesterday's, so old visitor hashes can't be recomputed. On its own timer, not
/// after views are written (F-0250): with <c>Analytics:Enabled</c> false, or on a quiet site, no
/// views arrive, and the old ones and the salts the visitor limits still make must go all the same.
/// </summary>
/// <param name="store">The page views and salts.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class PageViewCleanUp(IPageViewStore store, TimeProvider time, ILogger<PageViewCleanUp> logger) : BackgroundService
{
    /// <summary>How many months of page views are kept.</summary>
    public const int RetentionMonths = 13;

    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>Deletes the views older than the retention and the salts older than yesterday's.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many rows went.</returns>
    public Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        return store.CleanUpAsync(today.AddMonths(-RetentionMonths), today.AddDays(-1), cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not at startup: the app has more urgent things to do then.
            await Task.Delay(TimeSpan.FromMinutes(7), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await CleanUpAsync(stoppingToken);
                    LogCleanedUp(logger, deleted);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Page views: deleted {Count} older than the retention")]
    private static partial void LogCleanedUp(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Page views: the clean-up failed; it runs again tomorrow")]
    private static partial void LogCleanUpFailed(ILogger logger, Exception exception);
}
