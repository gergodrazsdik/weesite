using Azure;
using Azure.Data.Tables;

namespace Weesite.Web;

/// <summary>
/// Table Storage helpers: deleting the rows a filter finds, a page and a partition's batch at a
/// time (the clean-ups and the resets, T-0167, F-0221), and marking a row with the writes that changed it, so a
/// write that's sent again isn't applied twice (F-0215).
/// </summary>
internal static class TableRows
{
    /// <summary>The rows a page of a query brings, at most (the service's own maximum).</summary>
    public const int PageSize = 1000;

    /// <summary>The property holding a row's last write IDs.</summary>
    public const string WritesProperty = "Writes";

    // A transaction holds at most 100 actions, all in one partition.
    private const int BatchSize = 100;

    // How many write IDs a row keeps: more than can change one row between a write and the read
    // that checks it.
    private const int WritesKept = 32;

    /// <summary>Deletes the rows a filter finds; every row without one.</summary>
    /// <param name="table">The table, which exists.</param>
    /// <param name="filter">An OData filter, or null for every row.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many rows were deleted.</returns>
    /// <remarks>
    /// Reads only the keys, a page at a time, and deletes each page before reading the next, so
    /// a large table never sits in memory. A row someone else deleted meanwhile isn't an error:
    /// its batch is deleted again row by row, and it isn't counted.
    /// </remarks>
    public static async Task<int> DeleteAllAsync(TableClient table, string? filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        var count = 0;
        await foreach (var page in table.QueryAsync<TableEntity>(filter, maxPerPage: PageSize, select: ["PartitionKey", "RowKey"], cancellationToken: cancellationToken).AsPages())
        {
            count += await DeleteAsync(table, page.Values, cancellationToken);
        }

        return count;
    }

    /// <summary>Deletes rows by their keys, a partition's batch at a time; rows already gone are skipped.</summary>
    /// <param name="table">The table, which exists.</param>
    /// <param name="rows">The rows' keys.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many rows were deleted.</returns>
    public static async Task<int> DeleteAsync(TableClient table, IEnumerable<ITableEntity> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);
        var count = 0;
        foreach (var partition in rows.GroupBy(r => r.PartitionKey, StringComparer.Ordinal))
        {
            foreach (var chunk in partition.Chunk(BatchSize))
            {
                count += await DeleteBatchAsync(table, chunk, cancellationToken);
            }
        }

        return count;
    }

    /// <summary>Whether a write with this ID has changed the row, as one of its last writes.</summary>
    /// <param name="entity">The row as read.</param>
    /// <param name="id">The write's ID.</param>
    /// <returns>True when the row holds the ID.</returns>
    public static bool HasWrite(TableEntity entity, string id)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return entity.GetString(WritesProperty) is { } writes && writes.Split(' ').Contains(id, StringComparer.Ordinal);
    }

    /// <summary>Marks the row with a write's ID, keeping the last few others.</summary>
    /// <param name="entity">The row to write.</param>
    /// <param name="id">The write's ID: unique, without spaces.</param>
    /// <returns>The row.</returns>
    public static TableEntity MarkWrite(TableEntity entity, string id)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var earlier = entity.GetString(WritesProperty)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        entity[WritesProperty] = string.Join(' ', earlier.TakeLast(WritesKept - 1).Append(id));
        return entity;
    }

    /// <summary>
    /// Whether a write that threw may have landed all the same: a failure the SDK retried until it
    /// gave up (its answers lost, a timeout, the service busy), or the request cancelled while it
    /// was on its way. Its write ID then tells (F-0215, F-0232).
    /// </summary>
    /// <param name="exception">What the write threw.</param>
    /// <returns>True when it may have landed.</returns>
    public static bool MayHaveLanded(Exception exception) =>
        exception is RequestFailedException or OperationCanceledException or IOException or AggregateException;

    /// <summary>Makes an ID for a write.</summary>
    /// <returns>32 hex digits.</returns>
    public static string NewWriteId() => Guid.NewGuid().ToString("N");

    private static async Task<int> DeleteBatchAsync(TableClient table, ITableEntity[] rows, CancellationToken cancellationToken)
    {
        try
        {
            await table.SubmitTransactionAsync([.. rows.Select(r => new TableTransactionAction(TableTransactionActionType.Delete, r, ETag.All))], cancellationToken);
            return rows.Length;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // A row went meanwhile, and the whole batch with it: one at a time, a missing row answers 404.
            var deleted = 0;
            foreach (var row in rows)
            {
                using var response = await table.DeleteEntityAsync(row.PartitionKey, row.RowKey, ETag.All, cancellationToken);
                deleted += response.Status == 404 ? 0 : 1;
            }

            return deleted;
        }
    }
}

/// <summary>
/// Creates a store's tables once, on first use: a store keeps one and calls it before each use
/// (F-0227). Two callers at once may both create them, which the service allows.
/// </summary>
internal sealed class TableCreation
{
    private int _created;

    /// <summary>Creates the table unless this did already.</summary>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>A task.</returns>
    public Task EnsureAsync(TableClient table, CancellationToken cancellationToken) => EnsureAsync(cancellationToken, table);

    /// <summary>Creates the tables unless this did already.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <param name="tables">The tables.</param>
    /// <returns>A task.</returns>
    public async Task EnsureAsync(CancellationToken cancellationToken, params TableClient[] tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (Volatile.Read(ref _created) == 0)
        {
            foreach (var table in tables)
            {
                await table.CreateIfNotExistsAsync(cancellationToken);
            }

            Volatile.Write(ref _created, 1);
        }
    }
}
