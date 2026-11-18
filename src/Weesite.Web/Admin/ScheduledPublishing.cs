using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>
/// Publishes scheduled posts and pages when their time comes (T-0147). The site already shows a
/// scheduled item from its time on (each replica reloads when one comes due), but its stored status
/// stayed "scheduled". Every <c>Content:ReloadSeconds</c> (30 by default; 0 turns it off, as it does
/// the reloads), each replica saves the due items as published, as <c>set_status</c> does, at the
/// version it read. Two replicas can try at once: the version check lets one write and refuses the
/// other, so no other coordination is needed. The save that wins is audited as the schedule's, and
/// it tells the content's listeners, IndexNow among them, once.
/// </summary>
internal sealed partial class ScheduledPublishing(
    ContentCatalogProvider content,
    ContentEditor editor,
    IAuditLog audit,
    TimeProvider time,
    IConfiguration configuration,
    ILogger<ScheduledPublishing> logger) : BackgroundService
{
    /// <summary>The actor of the audit entries.</summary>
    public const string Actor = "schedule";

    // Items the editor refused to publish (a check that fails now), said once each.
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

    /// <summary>Publishes every scheduled item whose time has come.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many this replica published.</returns>
    public async Task<int> PublishDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var catalog = content.Current;
        var due = catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished)
            .Where(i => i.Status == ContentStatus.Scheduled && i.PublishAt <= now)
            .Select(i => i.SourcePath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var published = 0;
        foreach (var path in due)
        {
            // As stored now: another replica or an editor may have changed it since the catalog loaded.
            if (await editor.OpenAsync(path, cancellationToken) is not { } opened
                || opened.Draft.Status != ContentStatus.Scheduled || opened.Draft.PublishAt > now)
            {
                continue;
            }

            switch (await editor.SaveAsync(path, opened.Draft with { Status = ContentStatus.Published, PublishAt = null }, opened.ETag, cancellationToken))
            {
                case ContentSaved { Unchanged: false } saved:
                    await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor, "status-published", saved.Path), CancellationToken.None);
                    LogPublished(logger, saved.Path);
                    published++;
                    break;
                case ContentInvalid invalid when _refused.Add(path):
                    LogRefused(logger, path, invalid.Message);
                    break;
                default:
                    // A conflict: saved meanwhile, by another replica or an editor.
                    break;
            }
        }

        return published;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Content:ReloadSeconds", 30);
        if (seconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PublishDueAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogFailed(logger, e);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled item published: {Path}")]
    private static partial void LogPublished(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled item {Path} is due but couldn't be published: {Reason}")]
    private static partial void LogRefused(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the scheduled items failed; trying again at the next check")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
