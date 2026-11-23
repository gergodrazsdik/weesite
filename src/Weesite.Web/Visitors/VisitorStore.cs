using System.Globalization;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Visitors;

/// <summary>How long a counter counts.</summary>
internal enum CountWindow
{
    /// <summary>The hour (UTC) of the moment counted.</summary>
    Hour,

    /// <summary>The day (UTC) of the moment counted.</summary>
    Day,
}

/// <summary>
/// The counters behind the limits on what visitors send, and the proof-of-work challenges already
/// used (ADR 0011). Kept in storage, so every replica counts the same. Rows hold address hashes,
/// never addresses, and are deleted after two days.
/// </summary>
internal interface IVisitorStore
{
    /// <summary>Counts one more, unless the count has reached the limit.</summary>
    /// <param name="name">What is counted, such as <c>contact</c>.</param>
    /// <param name="hash">Whose: an address hash.</param>
    /// <param name="at">When; with the window it says which counter.</param>
    /// <param name="window">How long the counter counts.</param>
    /// <param name="limit">The most it may count.</param>
    /// <param name="cancellationToken">Stops the count.</param>
    /// <returns>True when counted; false when the limit is reached.</returns>
    Task<bool> TryCountAsync(string name, string hash, DateTimeOffset at, CountWindow window, int limit, CancellationToken cancellationToken);

    /// <summary>Counts one less, if the counter has any.</summary>
    /// <param name="name">What is counted.</param>
    /// <param name="hash">Whose.</param>
    /// <param name="at">When.</param>
    /// <param name="window">How long the counter counts.</param>
    /// <param name="cancellationToken">Stops the count.</param>
    /// <returns>True when there was one to take back.</returns>
    Task<bool> TryUncountAsync(string name, string hash, DateTimeOffset at, CountWindow window, CancellationToken cancellationToken);

    /// <summary>Marks a challenge used, unless it already is.</summary>
    /// <param name="challenge">The challenge's hash.</param>
    /// <param name="expires">When it expires.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>True the first time; false when it was used before.</returns>
    Task<bool> TryUseChallengeAsync(string challenge, DateTimeOffset expires, CancellationToken cancellationToken);

    /// <summary>Deletes the counters and challenges of days before one.</summary>
    /// <param name="before">The first day kept.</param>
    /// <param name="cancellationToken">Stops the clean-up.</param>
    /// <returns>How many rows were deleted.</returns>
    Task<int> CleanUpAsync(DateOnly before, CancellationToken cancellationToken);
}

/// <summary>Counters in memory: content from files (tests).</summary>
internal sealed class MemoryVisitorStore : IVisitorStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Day, string Key), int> _rows = [];

    /// <summary>Gets every row's key, for tests that check what's stored.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_lock)
            {
                return [.. _rows.Keys.Select(k => $"{k.Day}/{k.Key}")];
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> TryCountAsync(string name, string hash, DateTimeOffset at, CountWindow window, int limit, CancellationToken cancellationToken)
    {
        var key = (VisitorKeys.Day(at), VisitorKeys.Counter(name, hash, at, window));
        lock (_lock)
        {
            var count = _rows.GetValueOrDefault(key);
            if (count >= limit)
            {
                return Task.FromResult(false);
            }

            _rows[key] = count + 1;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryUncountAsync(string name, string hash, DateTimeOffset at, CountWindow window, CancellationToken cancellationToken)
    {
        var key = (VisitorKeys.Day(at), VisitorKeys.Counter(name, hash, at, window));
        lock (_lock)
        {
            var count = _rows.GetValueOrDefault(key);
            if (count <= 0)
            {
                return Task.FromResult(false);
            }

            _rows[key] = count - 1;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryUseChallengeAsync(string challenge, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_rows.TryAdd((VisitorKeys.Day(expires), VisitorKeys.Challenge(challenge)), 1));
        }
    }

    /// <inheritdoc />
    public Task<int> CleanUpAsync(DateOnly before, CancellationToken cancellationToken)
    {
        var first = VisitorKeys.Day(before);
        lock (_lock)
        {
            var old = _rows.Keys.Where(k => string.CompareOrdinal(k.Day, first) < 0).ToList();
            foreach (var key in old)
            {
                _rows.Remove(key);
            }

            return Task.FromResult(old.Count);
        }
    }
}

/// <summary>
/// Counters in the <c>visitorlimits</c> table: one partition per day (<c>yyyy-MM-dd</c>, UTC), so
/// old days delete by partition, a row per counter (what, window, hash) with its count, changed
/// with the ETag so two replicas can't both take the last one. Used challenges are rows too, in
/// the partition of the day they expire. Each change of a count marks the row with its own ID
/// (<see cref="TableRows.MarkWrite"/>), so a change the SDK sent again after it had succeeded,
/// answered 409 or 412, is recognized rather than counted twice (F-0215). What's left: a change
/// whose every try timed out though one had landed throws, and the caller doesn't know it counted;
/// the counter is then one too high for the rest of its hour or day.
/// </summary>
/// <param name="tables">The table service.</param>
internal sealed class TableVisitorStore(TableServiceClient tables) : IVisitorStore
{
    private const int Attempts = 5;
    private readonly TableClient _table = tables.GetTableClient("visitorlimits");
    private readonly TableCreation _creation = new();

    /// <inheritdoc />
    public async Task<bool> TryCountAsync(string name, string hash, DateTimeOffset at, CountWindow window, int limit, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        var partition = VisitorKeys.Day(at);
        var row = VisitorKeys.Counter(name, hash, at, window);
        var write = TableRows.NewWriteId();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var existing = await _table.GetEntityIfExistsAsync<TableEntity>(partition, row, cancellationToken: cancellationToken);
            try
            {
                if (!existing.HasValue || existing.Value is null)
                {
                    if (limit <= 0)
                    {
                        return false;
                    }

                    await _table.AddEntityAsync(TableRows.MarkWrite(new TableEntity(partition, row) { ["Count"] = 1 }, write), cancellationToken);
                    return true;
                }

                var entity = existing.Value;
                if (TableRows.HasWrite(entity, write))
                {
                    // This count, sent again after it had succeeded.
                    return true;
                }

                var count = entity.GetInt32("Count") ?? 0;
                if (count >= limit)
                {
                    return false;
                }

                entity["Count"] = count + 1;
                await _table.UpdateEntityAsync(TableRows.MarkWrite(entity, write), entity.ETag, TableUpdateMode.Replace, cancellationToken);
                return true;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                // Another request counted meanwhile, or this one was sent again: read again.
            }
        }

        // Many at once from one hash: a flood, so it's refused.
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> TryUncountAsync(string name, string hash, DateTimeOffset at, CountWindow window, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        var partition = VisitorKeys.Day(at);
        var row = VisitorKeys.Counter(name, hash, at, window);
        var write = TableRows.NewWriteId();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var existing = await _table.GetEntityIfExistsAsync<TableEntity>(partition, row, cancellationToken: cancellationToken);
            if (existing.HasValue && existing.Value is { } sent && TableRows.HasWrite(sent, write))
            {
                // This take-back, sent again after it had succeeded.
                return true;
            }

            if (!existing.HasValue || existing.Value is not { } entity || (entity.GetInt32("Count") ?? 0) <= 0)
            {
                return false;
            }

            try
            {
                entity["Count"] = entity.GetInt32("Count")!.Value - 1;
                await _table.UpdateEntityAsync(TableRows.MarkWrite(entity, write), entity.ETag, TableUpdateMode.Replace, cancellationToken);
                return true;
            }
            catch (RequestFailedException e) when (e.Status == 412)
            {
                // Changed meanwhile, or this take-back was sent again: read again.
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> TryUseChallengeAsync(string challenge, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        var used = new TableEntity(VisitorKeys.Day(expires), VisitorKeys.Challenge(challenge));
        var write = TableRows.NewWriteId();
        try
        {
            await _table.AddEntityAsync(TableRows.MarkWrite(used, write), cancellationToken);
            return true;
        }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            // Used before, unless it was this add, sent again after it had succeeded.
            var row = await _table.GetEntityIfExistsAsync<TableEntity>(used.PartitionKey, used.RowKey, cancellationToken: cancellationToken);
            return row.HasValue && row.Value is { } entity && TableRows.HasWrite(entity, write);
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanUpAsync(DateOnly before, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);

        // Day partitions sort as dates; partitions with names (such as the digests' state) sort
        // after every date, so this filter skips them.
        return await TableRows.DeleteAllAsync(_table, TableClient.CreateQueryFilter($"PartitionKey lt {VisitorKeys.Day(before)}"), cancellationToken);
    }
}

/// <summary>The keys of the visitor counters, shared by both stores.</summary>
internal static class VisitorKeys
{
    /// <summary>The partition of a moment's day.</summary>
    /// <param name="at">The moment.</param>
    /// <returns><c>yyyy-MM-dd</c>, UTC.</returns>
    public static string Day(DateTimeOffset at) => Day(DateOnly.FromDateTime(at.UtcDateTime));

    /// <summary>The partition of a day.</summary>
    /// <param name="day">The day.</param>
    /// <returns><c>yyyy-MM-dd</c>.</returns>
    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A counter's row key: what, the window, whose.</summary>
    /// <param name="name">What is counted; may hold a post's path.</param>
    /// <param name="hash">The address hash.</param>
    /// <param name="at">The moment counted.</param>
    /// <param name="window">The window.</param>
    /// <returns>The key, with the characters row keys can't hold replaced.</returns>
    public static string Counter(string name, string hash, DateTimeOffset at, CountWindow window)
    {
        var slot = window == CountWindow.Hour ? at.UtcDateTime.ToString("HH", CultureInfo.InvariantCulture) : "day";
        return Safe($"{name}|{slot}|{hash}");
    }

    /// <summary>A used challenge's row key.</summary>
    /// <param name="challenge">The challenge's hash.</param>
    /// <returns>The key.</returns>
    public static string Challenge(string challenge) => Safe($"challenge|{challenge}");

    /// <summary>Replaces the characters Table Storage keys can't hold (<c>/ \ # ?</c> and control characters).</summary>
    /// <param name="key">The key.</param>
    /// <returns>The key, safe.</returns>
    public static string Safe(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Create(key.Length, key, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is '/' or '\\' or '#' or '?' || char.IsControl(c) ? '~' : c;
            }
        });
    }
}
