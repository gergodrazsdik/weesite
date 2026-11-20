using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;

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
/// A read asks for its days in parallel, and keeps the finished ones (before yesterday, UTC) in
/// memory for a few minutes, so the analytics' refresh every 30 s reads only today and yesterday
/// (F-0202). A clean-up forgets them; another replica's forget after the few minutes.
/// </summary>
/// <param name="tables">The Table Storage account.</param>
/// <param name="time">The clock; the system's when null.</param>
internal sealed class TablePageViewStore(TableServiceClient tables, TimeProvider? time = null) : IPageViewStore, IDisposable
{
    private const string SaltPartition = "salt";
    private const int BatchSize = 100;

    // Days asked for at once, and how many views the finished days kept in memory may hold.
    private const int Parallel = 8;
    private const long KeptViews = 250_000;
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(5);

    private readonly TableClient _table = tables.GetTableClient("pageviews");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly MemoryCache _days = new(new MemoryCacheOptions { SizeLimit = KeptViews });
    private int _created;

    /// <inheritdoc />
    public void Dispose() => _days.Dispose();

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
        var days = new List<DateOnly>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            days.Add(day);
        }

        var finished = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(-1);
        var read = new PageView[days.Count][];
        await System.Threading.Tasks.Parallel.ForEachAsync(
            Enumerable.Range(0, days.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancellationToken },
            async (index, cancel) => read[index] = await ReadDayAsync(days[index], days[index] < finished, cancel));
        foreach (var day in read)
        {
            foreach (var view in day)
            {
                yield return view;
            }
        }
    }

    // One day's views, from memory when it's finished and was read in the last few minutes.
    private async Task<PageView[]> ReadDayAsync(DateOnly day, bool finished, CancellationToken cancellationToken)
    {
        if (finished && _days.TryGetValue(day, out PageView[]? kept) && kept is not null)
        {
            return kept;
        }

        var partition = Partition(day);
        var views = new List<PageView>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, cancellationToken: cancellationToken))
        {
            views.Add(FromEntity(entity));
        }

        var array = views.ToArray();
        if (finished)
        {
            _days.Set(day, array, new MemoryCacheEntryOptions { Size = Math.Max(1, array.Length), AbsoluteExpirationRelativeToNow = KeptFor });
        }

        return array;
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
        _days.Clear();
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
            ["Country"] = view.Country,
        };
    }

    private static PageView FromEntity(TableEntity entity) => new(
        entity.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue,
        entity.GetString("Path") ?? "/",
        entity.GetString("Referrer"),
        Enum.TryParse<ClientKind>(entity.GetString("Kind"), out var kind) ? kind : ClientKind.Bot,
        entity.GetString("Client"),
        entity.GetString("Visitor") ?? "",
        CountryLookup.Normalize(entity.GetString("Country")));

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
