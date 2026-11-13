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
        if (!_started)
        {
            (_version, _started) = (version, true);
        }

        var changed = !string.Equals(version, _version, StringComparison.Ordinal);
        var due = provider.Current.NextPublishAt is { } next && next <= time.GetUtcNow();
        if (!changed && !due)
        {
            return false;
        }

        LogReloading(changed ? "the content changed" : "a scheduled item is due");
        await loader.LoadAsync(cancellationToken);

        // Only once it loaded: a reload that failed is tried again on the next check (F-0108).
        _version = version;
        return true;
    }

    /// <summary>
    /// Makes every replica load a write: bumps the version marker, loads this replica now, then
    /// records the version as loaded. Not stopped with the request that wrote: once something is
    /// written, it must be loaded. Recorded only after the load, so a load that fails is tried
    /// again by the next check (F-0108, F-0120).
    /// </summary>
    /// <param name="store">The store that was written to.</param>
    /// <returns>A task.</returns>
    public async Task ReloadAfterWriteAsync(IContentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var version = await store.BumpVersionAsync(CancellationToken.None);
        await loader.LoadAsync(CancellationToken.None);
        Acknowledge(version);
    }

    /// <summary>
    /// Records a version this replica already loaded (its own save), so the next check doesn't
    /// reload again.
    /// </summary>
    /// <param name="version">The version marker after the save.</param>
    public void Acknowledge(string? version)
    {
        _version = version;
        _started = true;
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
