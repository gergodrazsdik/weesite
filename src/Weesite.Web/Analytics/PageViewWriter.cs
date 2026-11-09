namespace Weesite.Web.Analytics;

/// <summary>
/// Writes queued page views to the store in batches, and once a day deletes views older than the
/// retention (13 months, maintainer 2026-11-10) and salts older than yesterday's, so old visitor
/// hashes can't be recomputed. A failed write is logged and its views are dropped: analytics never
/// gets in the way of serving pages.
/// </summary>
internal sealed partial class PageViewWriter(
    PageViewRecorder recorder, IPageViewStore store, TimeProvider time, ILogger<PageViewWriter> logger) : BackgroundService
{
    /// <summary>How many months of page views are kept.</summary>
    public const int RetentionMonths = 13;

    private const int BatchSize = 200;
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromHours(24);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanUp = time.GetUtcNow();
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

                if (time.GetUtcNow() >= nextCleanUp)
                {
                    await CleanUpAsync(stoppingToken);
                    nextCleanUp = time.GetUtcNow() + CleanUpEvery;
                }
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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogWriteFailed(logger, batch.Count, e);
        }
    }

    private async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        try
        {
            var deleted = await store.CleanUpAsync(today.AddMonths(-RetentionMonths), today.AddDays(-1), cancellationToken);
            LogCleanedUp(logger, deleted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogCleanUpFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not store {Count} page views; they are dropped")]
    private static partial void LogWriteFailed(ILogger logger, int count, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Page views: deleted {Count} older than the retention")]
    private static partial void LogCleanedUp(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Page views: the clean-up failed; it runs again tomorrow")]
    private static partial void LogCleanUpFailed(ILogger logger, Exception exception);
}
