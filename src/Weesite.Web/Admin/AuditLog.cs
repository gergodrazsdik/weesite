using System.Collections.Concurrent;
using System.Globalization;
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
    /// <summary>Records an action.</summary>
    /// <param name="entry">The action.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task.</returns>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>Lists the latest actions.</summary>
    /// <param name="count">How many.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<AuditEntry>> LatestAsync(int count, CancellationToken cancellationToken);
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
}

/// <summary>
/// The audit log in the <c>audit</c> table: one partition per month (<c>yyyy-MM</c>), row keys
/// that sort newest first (reverse ticks), so the latest entries are the first rows read.
/// </summary>
internal sealed class TableAuditLog(TableServiceClient tables, TimeProvider time) : IAuditLog
{
    private readonly TableClient _table = tables.GetTableClient("audit");
    private int _created;

    /// <inheritdoc />
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await EnsureTableAsync(cancellationToken);
        var at = entry.At.ToUniversalTime();
        var rowKey = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks:D19}-{Guid.NewGuid():N}");
        var entity = new TableEntity(Partition(at), rowKey)
        {
            ["At"] = at,
            ["Actor"] = entry.Actor,
            ["Action"] = entry.Action,
            ["Target"] = entry.Target,
        };
        await _table.AddEntityAsync(entity, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> LatestAsync(int count, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        var entries = new List<AuditEntry>();
        var month = time.GetUtcNow();

        // This month and the eleven before it, newest first, until there are enough.
        for (var i = 0; i < 12 && entries.Count < count; i++, month = month.AddMonths(-1))
        {
            var partition = Partition(month);
            await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, maxPerPage: count, cancellationToken: cancellationToken))
            {
                entries.Add(new AuditEntry(
                    entity.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue,
                    entity.GetString("Actor") ?? "",
                    entity.GetString("Action") ?? "",
                    entity.GetString("Target")));
                if (entries.Count == count)
                {
                    break;
                }
            }
        }

        return entries;
    }

    private static string Partition(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await _table.CreateIfNotExistsAsync(cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}
