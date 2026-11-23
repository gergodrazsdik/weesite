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

    /// <summary>
    /// Gives or takes back a reaction: changes the post's total and records the event. Taking back
    /// a reaction the post has none of changes nothing and records nothing, so the analytics never
    /// go below zero (F-0196).
    /// </summary>
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
            var current = _totals.GetValueOrDefault(post, ReactionCounts.None);
            if (current.Of(kind) + delta < 0)
            {
                return Task.FromResult(current);
            }

            var counts = current.Add(kind, delta);
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
    /// <remarks>
    /// The event is written first and deleted again when the total doesn't change, so when this
    /// throws the total is as it was (F-0215): the endpoint can put its counters back. The worst
    /// case left is an event whose delete failed too, which the analytics count once too often;
    /// the other order could change a total and lose its event, and the endpoint would then put
    /// back counters the total no longer agrees with. Each write of the total marks the row with
    /// its own ID, so a write the SDK sent again after it had succeeded (answered 409 or 412) is
    /// recognized rather than applied twice.
    /// </remarks>
    public async Task<ReactionCounts> AddAsync(string post, ReactionKind kind, int delta, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var key = VisitorKeys.Safe(post);
        var existing = await _totals.GetEntityIfExistsAsync<TableEntity>(TotalsPartition, key, cancellationToken: cancellationToken);
        var before = existing.HasValue && existing.Value is { } first ? Read(first) : ReactionCounts.None;
        if (before.Of(kind) + delta < 0)
        {
            // Nothing to take back (the counts were deleted meanwhile, F-0196): no change, no event.
            return before;
        }

        var utc = at.ToUniversalTime();
        var change = new TableEntity(VisitorKeys.Day(utc), string.Create(CultureInfo.InvariantCulture, $"{utc.UtcTicks:D19}-{Guid.NewGuid():N}"))
        {
            ["At"] = utc,
            ["Path"] = post,
            ["Kind"] = Reactions.Name(kind),
            ["Delta"] = delta,
        };
        try
        {
            await _events.AddEntityAsync(change, cancellationToken);
        }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            // The key is new, so a conflict is this add sent again after it had succeeded.
        }

        var write = TableRows.NewWriteId();
        try
        {
            if (await ChangeTotalAsync(post, key, kind, delta, write, existing, cancellationToken) is { } counts)
            {
                return counts;
            }
        }
        catch (Exception e) when (e is RequestFailedException or OperationCanceledException or InvalidOperationException)
        {
            // The write may have landed before its answer was lost: then it stands, with its event.
            if (await WrittenAsync(key, write) is { } landed)
            {
                return landed;
            }

            await ForgetAsync(change);
            throw;
        }

        // The counts went below zero meanwhile: nothing changed, so the event goes.
        await ForgetAsync(change);
        return await GetAsync(post, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReactionEvent> ReadEventsAsync(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);

        // One query for the range rather than one a day: the partitions are the days and sort as
        // dates, and most days have none (F-0202).
        var first = VisitorKeys.Day(from);
        var last = VisitorKeys.Day(to);
        await foreach (var entity in _events.QueryAsync<TableEntity>(e => e.PartitionKey.CompareTo(first) >= 0 && e.PartitionKey.CompareTo(last) <= 0, cancellationToken: cancellationToken))
        {
            if (Reactions.Parse(entity.GetString("Kind")) is { } kind && entity.GetString("Path") is { } path)
            {
                yield return new ReactionEvent(entity.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue, path, kind, entity.GetInt32("Delta") ?? 0);
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanUpAsync(DateOnly from, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        return await TableRows.DeleteAllAsync(_events, TableClient.CreateQueryFilter($"PartitionKey lt {VisitorKeys.Day(from)}"), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        return await TableRows.DeleteAllAsync(_totals, null, cancellationToken) + await TableRows.DeleteAllAsync(_events, null, cancellationToken);
    }

    private static ReactionCounts Read(TableEntity entity) =>
        new(Count(entity, ReactionKind.Like), Count(entity, ReactionKind.Love), Count(entity, ReactionKind.Thanks), Count(entity, ReactionKind.Insightful), Count(entity, ReactionKind.Funny));

    private static int Count(TableEntity entity, ReactionKind kind) => Math.Max(0, entity.GetInt32(kind.ToString()) ?? 0);

    private static TableEntity Write(TableEntity entity, ReactionCounts counts)
    {
        foreach (var kind in Reactions.All)
        {
            entity[kind.ToString()] = counts.Of(kind);
        }

        return entity;
    }

    // Changes the total with the ETag, reading again when another replica changed it first.
    // Answers the new counts, or null when the change would now go below zero.
    private async Task<ReactionCounts?> ChangeTotalAsync(string post, string key, ReactionKind kind, int delta, string write, NullableResponse<TableEntity> existing, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0)
            {
                existing = await _totals.GetEntityIfExistsAsync<TableEntity>(TotalsPartition, key, cancellationToken: cancellationToken);
            }

            var row = existing.HasValue ? existing.Value : null;
            if (row is not null && TableRows.HasWrite(row, write))
            {
                // A write sent again after it had succeeded: it counted once.
                return Read(row);
            }

            var current = row is null ? ReactionCounts.None : Read(row);
            if (current.Of(kind) + delta < 0)
            {
                return null;
            }

            var next = current.Add(kind, delta);
            try
            {
                if (row is null)
                {
                    await _totals.AddEntityAsync(TableRows.MarkWrite(Write(new TableEntity(TotalsPartition, key) { ["Post"] = post }, next), write), cancellationToken);
                }
                else
                {
                    await _totals.UpdateEntityAsync(TableRows.MarkWrite(Write(row, next), write), row.ETag, TableUpdateMode.Replace, cancellationToken);
                }

                return next;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                // Another reaction to the post meanwhile, or this write sent again: read again, after
                // a short, random wait, so the writers that collided don't collide again at once.
                await Task.Delay(System.Security.Cryptography.RandomNumberGenerator.GetInt32(5, 20 * (attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException($"The reactions to {post} kept changing; the reaction wasn't counted.");
    }

    // The counts, if the write with this ID changed the total; null when it didn't or can't be read.
    private async Task<ReactionCounts?> WrittenAsync(string key, string write)
    {
        try
        {
            var row = await _totals.GetEntityIfExistsAsync<TableEntity>(TotalsPartition, key, cancellationToken: CancellationToken.None);
            return row.HasValue && row.Value is { } entity && TableRows.HasWrite(entity, write) ? Read(entity) : null;
        }
        catch (RequestFailedException)
        {
            return null;
        }
    }

    // Deletes an event whose total didn't change. Best effort: the failure being reported matters more.
    private async Task ForgetAsync(TableEntity change)
    {
        try
        {
            await _events.DeleteEntityAsync(change.PartitionKey, change.RowKey, ETag.All, CancellationToken.None);
        }
        catch (RequestFailedException)
        {
            // The analytics count this one once too often.
        }
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
