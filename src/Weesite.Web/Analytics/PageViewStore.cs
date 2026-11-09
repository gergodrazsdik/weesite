using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Analytics;

/// <summary>Keeps page views, and the salt each day's visitor hashes use.</summary>
internal interface IPageViewStore
{
    /// <summary>Adds page views.</summary>
    /// <param name="views">The views.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task.</returns>
    Task AddAsync(IReadOnlyList<PageView> views, CancellationToken cancellationToken);

    /// <summary>Reads the views of a range of days (UTC), oldest day first.</summary>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day, included.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The views.</returns>
    IAsyncEnumerable<PageView> ReadAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>The day's salt for visitor hashes, made on first use; every replica gets the same one.</summary>
    /// <param name="day">The day (UTC).</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>32 random bytes.</returns>
    Task<byte[]> SaltAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Deletes views from before a day, and salts from before another.</summary>
    /// <param name="viewsFrom">The first day whose views are kept.</param>
    /// <param name="saltsFrom">The first day whose salt is kept.</param>
    /// <param name="cancellationToken">Stops the clean-up.</param>
    /// <returns>The number of views deleted.</returns>
    Task<int> CleanUpAsync(DateOnly viewsFrom, DateOnly saltsFrom, CancellationToken cancellationToken);
}

/// <summary>Page views in memory: content from files (tests), where nothing persists.</summary>
internal sealed class MemoryPageViewStore : IPageViewStore
{
    private readonly ConcurrentQueue<PageView> _views = new();
    private readonly ConcurrentDictionary<DateOnly, byte[]> _salts = new();

    /// <summary>Gets every view kept, oldest first.</summary>
    public IReadOnlyList<PageView> All => [.. _views];

    /// <inheritdoc />
    public Task AddAsync(IReadOnlyList<PageView> views, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(views);
        foreach (var view in views)
        {
            _views.Enqueue(view);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PageView> ReadAsync(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var view in _views.Where(v => DayOf(v.At) >= from && DayOf(v.At) <= to).OrderBy(v => v.At))
        {
            yield return view;
        }
    }

    /// <inheritdoc />
    public Task<byte[]> SaltAsync(DateOnly day, CancellationToken cancellationToken) =>
        Task.FromResult(_salts.GetOrAdd(day, _ => RandomNumberGenerator.GetBytes(32)));

    /// <inheritdoc />
    public Task<int> CleanUpAsync(DateOnly viewsFrom, DateOnly saltsFrom, CancellationToken cancellationToken)
    {
        var kept = _views.Where(v => DayOf(v.At) >= viewsFrom).ToList();
        var deleted = _views.Count - kept.Count;
        _views.Clear();
        foreach (var view in kept)
        {
            _views.Enqueue(view);
        }

        foreach (var day in _salts.Keys.Where(d => d < saltsFrom))
        {
            _salts.TryRemove(day, out _);
        }

        return Task.FromResult(deleted);
    }

    private static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);
}

/// <summary>
/// Page views in the <c>pageviews</c> table: one partition per day (<c>yyyy-MM-dd</c>, UTC), so
/// a day reads as one query and old days delete by partition; row keys in time order. The day's
/// salt is a row in the <c>salt</c> partition, created by whichever replica needs it first.
/// </summary>
internal sealed class TablePageViewStore(TableServiceClient tables) : IPageViewStore
{
    private const string SaltPartition = "salt";
    private const int BatchSize = 100;
    private readonly TableClient _table = tables.GetTableClient("pageviews");
    private int _created;

    /// <inheritdoc />
    public async Task AddAsync(IReadOnlyList<PageView> views, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(views);
        await EnsureTableAsync(cancellationToken);

        // A transaction holds up to 100 rows of one partition.
        foreach (var day in views.GroupBy(v => Partition(DayOf(v.At))))
        {
            foreach (var chunk in day.Chunk(BatchSize))
            {
                var actions = chunk.Select(v => new TableTransactionAction(TableTransactionActionType.Add, ToEntity(v))).ToList();
                await _table.SubmitTransactionAsync(actions, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PageView> ReadAsync(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var partition = Partition(day);
            await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, cancellationToken: cancellationToken))
            {
                yield return FromEntity(entity);
            }
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> SaltAsync(DateOnly day, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        var key = Partition(day);
        var existing = await _table.GetEntityIfExistsAsync<TableEntity>(SaltPartition, key, cancellationToken: cancellationToken);
        if (existing.HasValue && existing.Value?.GetBinary("Salt") is { Length: 32 } salt)
        {
            return salt;
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        try
        {
            await _table.AddEntityAsync(new TableEntity(SaltPartition, key) { ["Salt"] = fresh }, cancellationToken);
            return fresh;
        }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            // Another replica made it first: use theirs.
            var theirs = await _table.GetEntityAsync<TableEntity>(SaltPartition, key, cancellationToken: cancellationToken);
            return theirs.Value.GetBinary("Salt");
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanUpAsync(DateOnly viewsFrom, DateOnly saltsFrom, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        // Day partitions sort as dates; "salt" sorts after every date, so this filter skips it.
        var before = Partition(viewsFrom);
        var deleted = await DeleteAsync(e => e.PartitionKey.CompareTo(before) < 0, cancellationToken);
        var saltBefore = Partition(saltsFrom);
        await DeleteAsync(e => e.PartitionKey == SaltPartition && e.RowKey.CompareTo(saltBefore) < 0, cancellationToken);
        return deleted;
    }

    private static string Partition(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    private static TableEntity ToEntity(PageView view)
    {
        var at = view.At.ToUniversalTime();
        var rowKey = string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks:D19}-{Guid.NewGuid():N}");
        return new TableEntity(Partition(DayOf(at)), rowKey)
        {
            ["At"] = at,
            ["Path"] = view.Path,
            ["Referrer"] = view.ReferrerHost,
            ["Kind"] = view.Kind.ToString(),
            ["Client"] = view.Client,
            ["Visitor"] = view.Visitor,
        };
    }

    private static PageView FromEntity(TableEntity entity) => new(
        entity.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue,
        entity.GetString("Path") ?? "/",
        entity.GetString("Referrer"),
        Enum.TryParse<ClientKind>(entity.GetString("Kind"), out var kind) ? kind : ClientKind.Bot,
        entity.GetString("Client"),
        entity.GetString("Visitor") ?? "");

    private async Task<int> DeleteAsync(System.Linq.Expressions.Expression<Func<TableEntity, bool>> filter, CancellationToken cancellationToken)
    {
        var count = 0;
        var rows = new List<TableEntity>();
        await foreach (var entity in _table.QueryAsync(filter, select: ["PartitionKey", "RowKey"], cancellationToken: cancellationToken))
        {
            rows.Add(entity);
        }

        foreach (var partition in rows.GroupBy(r => r.PartitionKey))
        {
            foreach (var chunk in partition.Chunk(BatchSize))
            {
                var actions = chunk.Select(r => new TableTransactionAction(TableTransactionActionType.Delete, r, ETag.All)).ToList();
                await _table.SubmitTransactionAsync(actions, cancellationToken);
                count += chunk.Length;
            }
        }

        return count;
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await _table.CreateIfNotExistsAsync(cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}
