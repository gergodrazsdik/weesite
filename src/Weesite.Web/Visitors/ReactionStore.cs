using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Visitors;

/// <summary>
/// Keeps the reactions to posts (ADR 0011): each post's totals, and every reaction given or taken
/// back as an event for the analytics. Posts are keyed by their source path, which a slug change
/// doesn't change. Nothing here says who reacted.
/// </summary>
internal interface IReactionStore
{
    /// <summary>Reads a post's counts.</summary>
    /// <param name="post">The post's source path.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The counts; zeros for a post without reactions.</returns>
    Task<ReactionCounts> GetAsync(string post, CancellationToken cancellationToken);

    /// <summary>Gives or takes back a reaction: changes the post's total and records the event.</summary>
    /// <param name="post">The post's source path.</param>
    /// <param name="kind">The reaction.</param>
    /// <param name="delta">+1 or -1.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The post's new counts.</returns>
    Task<ReactionCounts> AddAsync(string post, ReactionKind kind, int delta, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Reads the events of a range of days (UTC), oldest day first.</summary>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day, included.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The events.</returns>
    IAsyncEnumerable<ReactionEvent> ReadEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Deletes the events of days before one; the totals stay.</summary>
    /// <param name="from">The first day kept.</param>
    /// <param name="cancellationToken">Stops the clean-up.</param>
    /// <returns>How many events went.</returns>
    Task<int> CleanUpAsync(DateOnly from, CancellationToken cancellationToken);

    /// <summary>Deletes every post's counts and every event: for the factory reset (T-0166).</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many rows were deleted.</returns>
    Task<int> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Reactions in memory: content from files (tests).</summary>
internal sealed class MemoryReactionStore : IReactionStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ReactionCounts> _totals = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ReactionEvent> _events = new();

    /// <summary>Gets every event kept, oldest first.</summary>
    public IReadOnlyList<ReactionEvent> Events => [.. _events];

    /// <inheritdoc />
    public Task<ReactionCounts> GetAsync(string post, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_totals.GetValueOrDefault(post, ReactionCounts.None));
        }
    }

    /// <inheritdoc />
    public Task<ReactionCounts> AddAsync(string post, ReactionKind kind, int delta, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var counts = _totals.GetValueOrDefault(post, ReactionCounts.None).Add(kind, delta);
            _totals[post] = counts;
            _events.Enqueue(new ReactionEvent(at, post, kind, delta));
            return Task.FromResult(counts);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReactionEvent> ReadEventsAsync(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var e in _events.Where(e => DayOf(e.At) >= from && DayOf(e.At) <= to).OrderBy(e => e.At))
        {
            yield return e;
        }
    }

    /// <inheritdoc />
    public Task<int> CleanUpAsync(DateOnly from, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var kept = _events.Where(e => DayOf(e.At) >= from).ToList();
            var deleted = _events.Count - kept.Count;
            _events.Clear();
            foreach (var e in kept)
            {
                _events.Enqueue(e);
            }

            return Task.FromResult(deleted);
        }
    }

    /// <inheritdoc />
    public Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var deleted = _totals.Count + _events.Count;
            _totals.Clear();
            _events.Clear();
            return Task.FromResult(deleted);
        }
    }

    private static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);
}

/// <summary>
/// Reactions in Table Storage: the <c>reactions</c> table holds a row per post (partition
/// <c>post</c>) with a count per reaction, changed with the ETag so replicas don't lose a count;
/// <c>reactionevents</c> holds the events in day partitions (<c>yyyy-MM-dd</c>, UTC), like page
/// views, so a day reads as one query and old days delete by partition.
/// </summary>
/// <param name="tables">The table service.</param>
internal sealed class TableReactionStore(TableServiceClient tables) : IReactionStore
{
    private const string TotalsPartition = "post";
    // Each failed write means another one succeeded, so with n reactions to a post at once none
    // fails more than n - 1 times: 16 covers any burst a personal site sees.
    private const int Attempts = 16;
    private const int BatchSize = 100;
    private readonly TableClient _totals = tables.GetTableClient("reactions");
    private readonly TableClient _events = tables.GetTableClient("reactionevents");
    private int _created;

    /// <inheritdoc />
    public async Task<ReactionCounts> GetAsync(string post, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var row = await _totals.GetEntityIfExistsAsync<TableEntity>(TotalsPartition, VisitorKeys.Safe(post), cancellationToken: cancellationToken);
        return row.HasValue && row.Value is { } entity ? Read(entity) : ReactionCounts.None;
    }

    /// <inheritdoc />
    public async Task<ReactionCounts> AddAsync(string post, ReactionKind kind, int delta, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var key = VisitorKeys.Safe(post);
        ReactionCounts? counts = null;
        for (var attempt = 0; attempt < Attempts && counts is null; attempt++)
        {
            var existing = await _totals.GetEntityIfExistsAsync<TableEntity>(TotalsPartition, key, cancellationToken: cancellationToken);
            try
            {
                if (!existing.HasValue || existing.Value is null)
                {
                    var fresh = ReactionCounts.None.Add(kind, delta);
                    await _totals.AddEntityAsync(Write(new TableEntity(TotalsPartition, key) { ["Post"] = post }, fresh), cancellationToken);
                    counts = fresh;
                }
                else
                {
                    var entity = existing.Value;
                    var next = Read(entity).Add(kind, delta);
                    await _totals.UpdateEntityAsync(Write(entity, next), entity.ETag, TableUpdateMode.Replace, cancellationToken);
                    counts = next;
                }
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                // Another reaction to the post meanwhile: read again, after a short, random wait, so
                // the writers that collided don't collide again at once.
                await Task.Delay(System.Security.Cryptography.RandomNumberGenerator.GetInt32(5, 20 * (attempt + 1)), cancellationToken);
            }
        }

        if (counts is null)
        {
            throw new InvalidOperationException($"The reactions to {post} kept changing; the reaction wasn't counted.");
        }

        var utc = at.ToUniversalTime();
        await _events.AddEntityAsync(
            new TableEntity(Day(DateOnly.FromDateTime(utc.UtcDateTime)), string.Create(CultureInfo.InvariantCulture, $"{utc.UtcTicks:D19}-{Guid.NewGuid():N}"))
            {
                ["At"] = utc,
                ["Path"] = post,
                ["Kind"] = Reactions.Name(kind),
                ["Delta"] = delta,
            },
            cancellationToken);
        return counts;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReactionEvent> ReadEventsAsync(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var partition = Day(day);
            await foreach (var entity in _events.QueryAsync<TableEntity>(e => e.PartitionKey == partition, cancellationToken: cancellationToken))
            {
                if (Reactions.Parse(entity.GetString("Kind")) is { } kind && entity.GetString("Path") is { } path)
                {
                    yield return new ReactionEvent(entity.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue, path, kind, entity.GetInt32("Delta") ?? 0);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanUpAsync(DateOnly from, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var first = Day(from);
        var rows = new List<TableEntity>();
        await foreach (var entity in _events.QueryAsync<TableEntity>(e => e.PartitionKey.CompareTo(first) < 0, select: ["PartitionKey", "RowKey"], cancellationToken: cancellationToken))
        {
            rows.Add(entity);
        }

        var count = 0;
        foreach (var partition in rows.GroupBy(r => r.PartitionKey))
        {
            foreach (var chunk in partition.Chunk(BatchSize))
            {
                var actions = chunk.Select(r => new TableTransactionAction(TableTransactionActionType.Delete, r, ETag.All)).ToList();
                await _events.SubmitTransactionAsync(actions, cancellationToken);
                count += chunk.Length;
            }
        }

        return count;
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        return await DeleteAllAsync(_totals, cancellationToken) + await DeleteAllAsync(_events, cancellationToken);
    }

    // Every row of a table, a partition's batch at a time.
    private static async Task<int> DeleteAllAsync(TableClient table, CancellationToken cancellationToken)
    {
        var rows = new List<TableEntity>();
        await foreach (var entity in table.QueryAsync<TableEntity>(select: ["PartitionKey", "RowKey"], cancellationToken: cancellationToken))
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

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ReactionCounts Read(TableEntity entity) =>
        new(Count(entity, ReactionKind.Like), Count(entity, ReactionKind.Love), Count(entity, ReactionKind.Thanks), Count(entity, ReactionKind.Insightful));

    private static int Count(TableEntity entity, ReactionKind kind) => Math.Max(0, entity.GetInt32(kind.ToString()) ?? 0);

    private static TableEntity Write(TableEntity entity, ReactionCounts counts)
    {
        foreach (var kind in Reactions.All)
        {
            entity[kind.ToString()] = counts.Of(kind);
        }

        return entity;
    }

    private async Task EnsureTablesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await _totals.CreateIfNotExistsAsync(cancellationToken);
            await _events.CreateIfNotExistsAsync(cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}
