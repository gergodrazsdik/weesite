using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Azure.Data.Tables;

namespace Weesite.Web.Admin;

/// <summary>An admin action, for the audit log (brief, Editor and authentication).</summary>
/// <param name="At">When.</param>
/// <param name="Actor">Who: the editor's email address, or a description for sign-in attempts.</param>
/// <param name="Action">What, such as <c>sign-in</c> or <c>editor-added</c>.</param>
/// <param name="Target">What it was done to, if anything.</param>
internal sealed record AuditEntry(DateTimeOffset At, string Actor, string Action, string? Target = null);

/// <summary>Records admin actions and lists the latest.</summary>
internal interface IAuditLog
{
    /// <summary>Records an action. It has happened, so the entry is written even when the token is cancelled, and a failed write is logged rather than thrown (F-0115, F-0248).</summary>
    /// <param name="entry">The action.</param>
    /// <param name="cancellationToken">The caller's token; the table log doesn't stop for it.</param>
    /// <returns>A task.</returns>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>Lists the latest actions.</summary>
    /// <param name="count">How many.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<AuditEntry>> LatestAsync(int count, CancellationToken cancellationToken);

    /// <summary>Reads the actions in a time range, for a month's view and the export (T-0122).</summary>
    /// <param name="from">The start, included.</param>
    /// <param name="to">The end, left out.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>Newest first.</returns>
    IAsyncEnumerable<AuditEntry> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Deletes every entry: the reset, when asked (T-0167).</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>The audit log in memory: content from files (tests), where nothing persists.</summary>
internal sealed class MemoryAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditEntry>> LatestAsync(int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>([.. _entries.Reverse().Take(count)]);

    /// <inheritdoc />
    public IAsyncEnumerable<AuditEntry> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        _entries.Where(e => e.At >= from && e.At < to).OrderByDescending(e => e.At).ToAsyncEnumerable();
    /// <inheritdoc />
    public Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        var count = _entries.Count;
        _entries.Clear();
        return Task.FromResult(count);
    }
}

/// <summary>
/// The audit log in the <c>audit</c> table: one partition per month (<c>yyyy-MM</c>), row keys
/// that sort newest first (reverse ticks), so the latest entries are the first rows read.
/// </summary>
internal sealed partial class TableAuditLog(TableServiceClient tables, TimeProvider time, ILogger<TableAuditLog> logger) : IAuditLog
{
    private readonly TableClient _table = tables.GetTableClient("audit");
    private readonly TableCreation _creation = new();

    /// <inheritdoc />
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // What it records has happened, so a closed tab or a dropped request doesn't stop the
        // entry, nor turn the done action into an error (F-0248).
        cancellationToken = CancellationToken.None;
        var at = entry.At.ToUniversalTime();
        var rowKey = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks:D19}-{Guid.NewGuid():N}");
        var entity = new TableEntity(Partition(at), rowKey)
        {
            ["At"] = at,
            ["Actor"] = entry.Actor,
            ["Action"] = entry.Action,
            ["Target"] = entry.Target,
        };

        // Written after what it records has happened: a failed write is logged, with the entry,
        // rather than turning that success into an error (F-0115).
        try
        {
            await _creation.EnsureAsync(_table, cancellationToken);
            await _table.AddEntityAsync(entity, cancellationToken);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or OperationCanceledException)
        {
            LogNotWritten(entry.Actor, entry.Action, entry.Target, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit entry not written: {Actor} {Action} {Target}")]
    private partial void LogNotWritten(string actor, string action, string? target, Exception exception);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> LatestAsync(int count, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        var entries = new List<AuditEntry>();
        var month = time.GetUtcNow();

        // This month and the eleven before it, newest first, until there are enough.
        for (var i = 0; i < 12 && entries.Count < count; i++, month = month.AddMonths(-1))
        {
            var partition = Partition(month);
            await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, maxPerPage: count, cancellationToken: cancellationToken))
            {
                entries.Add(ToEntry(entity));
                if (entries.Count == count)
                {
                    break;
                }
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AuditEntry> ReadAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (to <= from)
        {
            yield break;
        }

        await _creation.EnsureAsync(_table, cancellationToken);

        // Month by month from the end, newest first, as the row keys sort.
        var last = to.ToUniversalTime().AddTicks(-1);
        var month = new DateTimeOffset(last.Year, last.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (; month.AddMonths(1) > from; month = month.AddMonths(-1))
        {
            var partition = Partition(month);
            await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, cancellationToken: cancellationToken))
            {
                var entry = ToEntry(entity);
                if (entry.At >= from && entry.At < to)
                {
                    yield return entry;
                }
            }
        }
    }

    private static AuditEntry ToEntry(TableEntity entity) => new(
        entity.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue,
        entity.GetString("Actor") ?? "",
        entity.GetString("Action") ?? "",
        entity.GetString("Target"));

    private static string Partition(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);

        // Page by page, as the query finds them: years of entries needn't be in memory at once (F-0221).
        return await TableRows.DeleteAllAsync(_table, null, cancellationToken);
    }
}
