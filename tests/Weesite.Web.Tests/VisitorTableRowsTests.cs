using Azure.Data.Tables;
using Weesite.Tests;

namespace Weesite.Web.Tests;

/// <summary>The Table Storage helpers the visitor stores share (F-0215, F-0227): batch deletes and write marks.</summary>
internal sealed class VisitorTableRowsTests
{
    [Test]
    public async Task A_row_remembers_its_last_writes()
    {
        var row = new TableEntity("p", "r");
        var first = TableRows.NewWriteId();
        TableRows.MarkWrite(row, first);
        var marked = TableRows.HasWrite(row, first);
        for (var i = 0; i < 40; i++)
        {
            TableRows.MarkWrite(row, TableRows.NewWriteId());
        }

        var last = TableRows.NewWriteId();
        TableRows.MarkWrite(row, last);

        await Assert.That(marked).IsTrue();
        await Assert.That(TableRows.HasWrite(row, last)).IsTrue();

        // Only the last writes are kept, so the row stays small.
        await Assert.That(TableRows.HasWrite(row, first)).IsFalse();
        await Assert.That(row.GetString(TableRows.WritesProperty)!.Split(' ').Length).IsEqualTo(32);
        await Assert.That(TableRows.HasWrite(new TableEntity("p", "r"), last)).IsFalse();
    }

    [Test]
    [Category("containers")]
    public async Task Deletes_go_page_by_page_and_skip_rows_already_gone()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var table = new TableServiceClient(azurite.GetConnectionString()).GetTableClient("rows");
        await table.CreateAsync();

        // More rows than a page of the query holds, and a partition the filter leaves alone.
        foreach (var partition in new[] { "2026-11-01", "2026-11-02", "state" })
        {
            var rows = Enumerable.Range(0, partition == "2026-11-01" ? 1050 : 5).Select(i => new TableEntity(partition, i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)));
            foreach (var chunk in rows.Chunk(100))
            {
                await table.SubmitTransactionAsync([.. chunk.Select(r => new TableTransactionAction(TableTransactionActionType.Add, r))]);
            }
        }

        // A batch with a row someone deleted meanwhile still deletes the others (F-0227).
        var mixed = await TableRows.DeleteAsync(table, [new TableEntity("2026-11-02", "00000"), new TableEntity("2026-11-02", "gone"), new TableEntity("2026-11-02", "00001")], CancellationToken.None);
        var deleted = await TableRows.DeleteAllAsync(table, TableClient.CreateQueryFilter($"PartitionKey lt {"state"}"), CancellationToken.None);
        var left = new List<string>();
        await foreach (var row in table.QueryAsync<TableEntity>())
        {
            left.Add(row.PartitionKey);
        }

        await Assert.That(mixed).IsEqualTo(2);
        await Assert.That(deleted).IsEqualTo(1053);
        await Assert.That(left).IsEquivalentTo(Enumerable.Repeat("state", 5));
    }
}
