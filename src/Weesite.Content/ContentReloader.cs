using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary>
/// Keeps every replica's catalog current (ADR 0005): every <c>Content:ReloadSeconds</c> (30 by
/// default; 0 turns it off) it reloads when the source's version marker changed (a save on any
/// replica) or when a scheduled item has come due.
/// </summary>
/// <param name="source">The content source, for its version marker.</param>
/// <param name="provider">The current catalog.</param>
/// <param name="loader">Loads a new catalog.</param>
/// <param name="time">The clock.</param>
/// <param name="configuration">Reads <c>Content:ReloadSeconds</c>.</param>
/// <param name="logger">Reports reloads and failures.</param>
public sealed partial class ContentReloader(
    IContentSource source,
    ContentCatalogProvider provider,
    ContentLoader loader,
    TimeProvider time,
    IConfiguration configuration,
    ILogger<ContentReloader> logger) : BackgroundService
{
    private string? _version;
    private bool _started;

    /// <summary>
    /// Checks once: reads the version marker and reloads when it changed since the last check, or
    /// when the next scheduled item is due. The first check only records the version.
    /// </summary>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>Whether it reloaded.</returns>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        var version = await source.ReadVersionAsync(cancellationToken);
        var changed = _started && !string.Equals(version, _version, StringComparison.Ordinal);
        _version = version;
        _started = true;

        var due = provider.Current.NextPublishAt is { } next && next <= time.GetUtcNow();
        if (!changed && !due)
        {
            return false;
        }

        LogReloading(changed ? "the content changed" : "a scheduled item is due");
        await loader.LoadAsync(cancellationToken);
        return true;
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
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed check (storage briefly unreachable) keeps the current catalog; the
                // next tick tries again.
                LogCheckFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reloading content: {Reason}")]
    private partial void LogReloading(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Content check failed; keeping the current content")]
    private partial void LogCheckFailed(Exception exception);
}
