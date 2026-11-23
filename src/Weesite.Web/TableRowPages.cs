using Azure;
using Azure.Data.Tables;

namespace Weesite.Web;

/// <summary>
/// Deleting many rows of a table page by page, as the query returns them (F-0221): the analytics'
/// and the audit log's tables can hold hundreds of thousands, and the keys of all of them needn't
/// be in memory at once. A row someone else deleted meanwhile (another replica's clean-up) isn't
/// an error: its batch is deleted row by row then, and the missing one is skipped.
/// </summary>
internal static class TableRowPages
{
    /// <summary>The rows a page of the query brings, at most (the service's own maximum).</summary>
    public const int PageSize = 1000;

    private const int BatchSize = 100;

    /// <summary>Deletes the rows a filter finds; every row without one.</summary>
    /// <param name="table">The table, which exists.</param>
    /// <param name="filter">An OData filter, or null for every row.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many rows were deleted.</returns>
    public static async Task<int> DeleteAsync(TableClient table, string? filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        var count = 0;
        await foreach (var page in table.QueryAsync<TableEntity>(filter, PageSize, ["PartitionKey", "RowKey"], cancellationToken).AsPages())
        {
            // A transaction holds up to 100 rows of one partition.
            foreach (var partition in page.Values.GroupBy(r => r.PartitionKey, StringComparer.Ordinal))
            {
                foreach (var chunk in partition.Chunk(BatchSize))
                {
                    count += await DeleteBatchAsync(table, chunk, cancellationToken);
                }
            }
        }

        return count;
    }

    private static async Task<int> DeleteBatchAsync(TableClient table, TableEntity[] rows, CancellationToken cancellationToken)
    {
        try
        {
            await table.SubmitTransactionAsync([.. rows.Select(r => new TableTransactionAction(TableTransactionActionType.Delete, r, ETag.All))], cancellationToken);
            return rows.Length;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // One of them went meanwhile, which fails the whole batch: the rest one at a time.
            var count = 0;
            foreach (var row in rows)
            {
                try
                {
                    using var response = await table.DeleteEntityAsync(row.PartitionKey, row.RowKey, ETag.All, cancellationToken);
                    if (response.Status != 404)
                    {
                        count++;
                    }
                }
                catch (RequestFailedException gone) when (gone.Status == 404)
                {
                    // Already gone.
                }
            }

            return count;
        }
    }
}
