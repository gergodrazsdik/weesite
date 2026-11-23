using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Weesite.Content;
using Weesite.Web.Email;

namespace Weesite.Web.Visitors;

/// <summary>A mail a digest sends: to one of the site's own people, never to a visitor.</summary>
/// <param name="To">The recipient.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Text">The plain text body.</param>
/// <param name="Html">The HTML body.</param>
internal sealed record DigestMail(string To, string Subject, string Text, string Html);

/// <summary>
/// An hourly digest (ADR 0011): what arrived in a time range, as mails. <see cref="DigestSender"/>
/// asks each registered digest once an hour at most, and sends what it builds.
/// </summary>
internal interface IDigest
{
    /// <summary>Gets the digest's name, which keys its state: such as <c>messages</c>.</summary>
    string Name { get; }

    /// <summary>Builds the mails for what arrived in a time range; none when nothing did.</summary>
    /// <param name="after">The start, left out: where the last digest ended.</param>
    /// <param name="until">The end, included.</param>
    /// <param name="cancellationToken">Stops the build.</param>
    /// <returns>The mails.</returns>
    Task<IReadOnlyList<DigestMail>> BuildAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken);
}

/// <summary>Where a digest got to.</summary>
/// <param name="Until">The end of the range the last digest covered.</param>
/// <param name="SentAt">When the last digest was sent.</param>
/// <param name="Version">The state's version, for the conditional write; null when there's none yet.</param>
internal sealed record DigestState(DateTimeOffset Until, DateTimeOffset SentAt, string? Version);

/// <summary>
/// Keeps each digest's state, written only at the version read, so of two replicas only one
/// claims a digest: the one whose write succeeds sends it.
/// </summary>
internal interface IDigestStateStore
{
    /// <summary>Reads a digest's state.</summary>
    /// <param name="name">The digest.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The state, or null when it has none yet.</returns>
    Task<DigestState?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Writes a digest's state, if it's still at the version read.</summary>
    /// <param name="name">The digest.</param>
    /// <param name="state">The new state; its version is the one read (null: none existed).</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The new version, or null when someone wrote first.</returns>
    Task<string?> TrySetAsync(string name, DigestState state, CancellationToken cancellationToken);
}

/// <summary>Digest state in memory: content from files (tests).</summary>
internal sealed class MemoryDigestStateStore : IDigestStateStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DigestState> _states = new(StringComparer.Ordinal);
    private int _version;

    /// <inheritdoc />
    public Task<DigestState?> GetAsync(string name, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_states.GetValueOrDefault(name));
        }
    }

    /// <inheritdoc />
    public Task<string?> TrySetAsync(string name, DigestState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (_states.GetValueOrDefault(name)?.Version != state.Version)
            {
                return Task.FromResult<string?>(null);
            }

            var version = (++_version).ToString(CultureInfo.InvariantCulture);
            _states[name] = state with { Version = version };
            return Task.FromResult<string?>(version);
        }
    }
}

/// <summary>
/// Digest state in the <c>visitorlimits</c> table, partition <c>state</c>: a name, not a date,
/// so the daily clean-up of counters passes it by. Written with the ETag.
/// </summary>
/// <param name="tables">The table service.</param>
internal sealed class TableDigestStateStore(TableServiceClient tables) : IDigestStateStore
{
    private const string Partition = "state";
    private readonly TableClient _table = tables.GetTableClient("visitorlimits");
    private int _created;

    /// <inheritdoc />
    public async Task<DigestState?> GetAsync(string name, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        var entity = await _table.GetEntityIfExistsAsync<TableEntity>(Partition, Row(name), cancellationToken: cancellationToken);
        return entity.HasValue && entity.Value is { } found
            ? new DigestState(found.GetDateTimeOffset("Until") ?? DateTimeOffset.MinValue, found.GetDateTimeOffset("SentAt") ?? DateTimeOffset.MinValue, found.ETag.ToString())
            : null;
    }

    /// <inheritdoc />
    public async Task<string?> TrySetAsync(string name, DigestState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await EnsureTableAsync(cancellationToken);
        var entity = new TableEntity(Partition, Row(name)) { ["Until"] = state.Until.ToUniversalTime(), ["SentAt"] = state.SentAt.ToUniversalTime() };
        try
        {
            var response = state.Version is null
                ? await _table.AddEntityAsync(entity, cancellationToken)
                : await _table.UpdateEntityAsync(entity, new ETag(state.Version), TableUpdateMode.Replace, cancellationToken);
            return response.Headers.ETag?.ToString();
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            return null;
        }
    }

    private static string Row(string name) => VisitorKeys.Safe("digest|" + name);

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await _table.CreateIfNotExistsAsync(cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}

/// <summary>
/// Sends the hourly digests (ADR 0011). Every minute, each replica looks at each digest: when the
/// last one went out an hour ago or more and something arrived since, it claims the digest by
/// writing its state at the version read (only one replica's write succeeds), then sends. So the
/// first thing after a quiet hour goes out within a minute, and later ones wait for the hour. A
/// failed send puts the state back, and the next minute tries again. Without SMTP, nothing is
/// sent and the state stays, so what's waiting goes once mail is set up. Test scales to zero: a
/// waiting digest goes when the app next wakes.
/// </summary>
/// <param name="digests">The digests.</param>
/// <param name="states">Their state.</param>
/// <param name="sender">The mail sender.</param>
/// <param name="content">The site, for the sender's name.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class DigestSender(
    IEnumerable<IDigest> digests,
    IDigestStateStore states,
    IEmailSender sender,
    ContentCatalogProvider content,
    TimeProvider time,
    ILogger<DigestSender> logger) : BackgroundService
{
    /// <summary>How often a digest goes at most.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>How often each replica looks.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far behind now a digest ends: an item written a moment after its time was taken would
    /// otherwise fall before the range the next digest covers.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>How far back the first digest looks, when there's no state yet.</summary>
    public static readonly TimeSpan FirstLook = TimeSpan.FromDays(1);

    /// <summary>Looks at every digest once, and sends those that are due.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many mails this replica sent.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!sender.IsConfigured)
        {
            return 0;
        }

        var sent = 0;
        foreach (var digest in digests)
        {
            try
            {
                sent += await RunAsync(digest, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(logger, digest.Name, e);
            }
        }

        return sent;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Tick, time);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task<int> RunAsync(IDigest digest, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var state = await states.GetAsync(digest.Name, cancellationToken);
        if (state is not null && now - state.SentAt < Interval)
        {
            return 0;
        }

        var after = state?.Until ?? now - FirstLook;
        var until = now - Settle;
        if (until <= after)
        {
            return 0;
        }

        var mails = await digest.BuildAsync(after, until, cancellationToken);
        if (mails.Count == 0)
        {
            return 0;
        }

        // Claimed: another replica's write now fails, and it waits the hour.
        if (await states.TrySetAsync(digest.Name, new DigestState(until, now, state?.Version), cancellationToken) is not { } claimed)
        {
            return 0;
        }

        var sent = 0;
        try
        {
            foreach (var mail in mails)
            {
                await sender.SendAsync(new EmailMessage(mail.To, mail.Subject, mail.Text, mail.Html, content.Current.Site.Name), cancellationToken);
                sent++;
            }

            LogSent(logger, digest.Name, sent);
            return sent;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Back as it was, so the next minute tries again; mails already sent may go twice.
            LogSendFailed(logger, digest.Name, e);
            var back = state is null ? new DigestState(after, DateTimeOffset.MinValue, claimed) : state with { Version = claimed };
            await states.TrySetAsync(digest.Name, back, CancellationToken.None);
            return sent;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Digest {Name}: sent {Count} mails")]
    private static partial void LogSent(ILogger logger, string name, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Digest {Name}: sending failed; trying again in a minute")]
    private static partial void LogSendFailed(ILogger logger, string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Digest {Name}: building it failed; trying again in a minute")]
    private static partial void LogFailed(ILogger logger, string name, Exception exception);
}
