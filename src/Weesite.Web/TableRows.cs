using Azure;
using Azure.Data.Tables;

namespace Weesite.Web;

/// <summary>Deleting a table's rows, for the reset (T-0167): a partition's batch at a time.</summary>
internal static class TableRows
{
    private const int BatchSize = 100;

    /// <summary>Deletes the rows a filter finds; every row without one.</summary>
    /// <param name="table">The table, which exists.</param>
    /// <param name="filter">An OData filter, or null for every row.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many rows were deleted.</returns>
    public static async Task<int> DeleteAllAsync(TableClient table, string? filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        var rows = new List<TableEntity>();
        await foreach (var entity in table.QueryAsync<TableEntity>(filter, select: ["PartitionKey", "RowKey"], cancellationToken: cancellationToken))
        {
            rows.Add(entity);
        }

        var count = 0;
        foreach (var partition in rows.GroupBy(r => r.PartitionKey))
        {
            foreach (var chunk in partition.Chunk(BatchSize))
            {
                await table.SubmitTransactionAsync([.. chunk.Select(r => new TableTransactionAction(TableTransactionActionType.Delete, r, ETag.All))], cancellationToken);
                count += chunk.Length;
            }
        }

        return count;
    }
}
