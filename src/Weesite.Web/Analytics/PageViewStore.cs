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

    /// <summary>Deletes the views that match, so a test sharing the app with others can take back what it added.</summary>
    /// <param name="match">Which views.</param>
    /// <returns>How many went.</returns>
    public int RemoveWhere(Func<PageView, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var kept = _views.Where(v => !match(v)).ToList();
        var removed = _views.Count - kept.Count;
        _views.Clear();
        foreach (var view in kept)
        {
            _views.Enqueue(view);
        }

        return removed;
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
/// <remarks>
/// The finished days are kept as the views themselves, so a report from them is the same as one
/// from the table, but with one instance of each string per day: a day's views share their
/// paths, visitor hashes, clients and countries (F-0222). A view then takes about
/// <see cref="ViewBytes"/> bytes, against some 300 with a string of its own for each field, and
/// the strings a day adds are counted on top. The <see cref="Budget"/> holds about 200,000 views:
/// 90 days of over 2,000 a day.
/// </remarks>
/// <param name="tables">The Table Storage account.</param>
/// <param name="time">The clock; the system's when null.</param>
internal sealed class TablePageViewStore(TableServiceClient tables, TimeProvider? time = null) : IPageViewStore
{
    /// <summary>The bytes the finished days kept in memory may take, as estimated (F-0222).</summary>
    public const long Budget = 24L * 1024 * 1024;

    /// <summary>
    /// The bytes a kept view takes besides its strings: the record (headers 16, the time 16, five
    /// references 40, the kind 4, rounded to 80) and its place in the day's array (8).
    /// </summary>
    public const long ViewBytes = 88;

    private const string SaltPartition = "salt";
    private const int BatchSize = 100;

    // Days asked for at once.
    private const int Parallel = 8;
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(5);

    private readonly TableClient _table = tables.GetTableClient("pageviews");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _created;

    /// <summary>Gets the finished days kept in memory, for tests.</summary>
    internal DayCache<PageView[]> Days { get; } = new(time ?? TimeProvider.System, Budget, KeptFor);

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
        if (finished && Days.TryGet(day, out var kept))
        {
            return kept;
        }

        var generation = Days.Generation;
        var partition = Partition(day);
        var strings = new MemorySize.Strings();
        var views = new List<PageView>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, cancellationToken: cancellationToken))
        {
            views.Add(FromEntity(entity, strings));
        }

        var array = views.ToArray();
        if (finished)
        {
            Days.Set(day, array, 24 + (array.Length * ViewBytes) + strings.Bytes, generation);
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
        try
        {
            // Day partitions sort as dates; "salt" sorts after every date, so this filter skips it.
            // Page by page, as the query finds them: the reset deletes every view (F-0221).
            var before = Partition(viewsFrom);
            var deleted = await TableRowPages.DeleteAsync(_table, TableClient.CreateQueryFilter<TableEntity>(e => e.PartitionKey.CompareTo(before) < 0), cancellationToken);
            var saltBefore = Partition(saltsFrom);
            await TableRowPages.DeleteAsync(_table, TableClient.CreateQueryFilter<TableEntity>(e => e.PartitionKey == SaltPartition && e.RowKey.CompareTo(saltBefore) < 0), cancellationToken);
            return deleted;
        }
        finally
        {
            // Also when it stopped partway: some days are gone.
            Days.Clear();
        }
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

    private static PageView FromEntity(TableEntity entity, MemorySize.Strings strings) => new(
        entity.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue,
        strings.Share(entity.GetString("Path") ?? "/"),
        strings.Share(entity.GetString("Referrer")),
        Enum.TryParse<ClientKind>(entity.GetString("Kind"), out var kind) ? kind : ClientKind.Bot,
        strings.Share(entity.GetString("Client")),
        strings.Share(entity.GetString("Visitor") ?? ""),
        strings.Share(CountryLookup.Normalize(entity.GetString("Country"))));

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await _table.CreateIfNotExistsAsync(cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}
