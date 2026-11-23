using System.Diagnostics.CodeAnalysis;

namespace Weesite.Web.Analytics;

/// <summary>
/// Finished days' data kept in memory on this replica for a few minutes (F-0202), so the
/// analytics' refresh every 30 s reads only the days still changing. Bounded by an estimate of
/// the bytes it holds (F-0222): past the budget, the entries closest to expiring go first, and a
/// day bigger than the whole budget isn't kept. Expiry follows the given clock, so tests can move
/// it. <see cref="Clear"/> also turns away a read that began before it, so a reset can't be
/// undone by a read still on its way.
/// </summary>
/// <typeparam name="T">A day's data.</typeparam>
/// <param name="time">The clock.</param>
/// <param name="budget">The most bytes kept, as estimated by the callers.</param>
/// <param name="keptFor">How long a day is kept after it was read.</param>
internal sealed class DayCache<T>(TimeProvider time, long budget, TimeSpan keptFor)
    where T : class
{
    private readonly Lock _lock = new();
    private readonly Dictionary<DateOnly, Entry> _days = [];
    private long _size;
    private long _generation;

    /// <summary>Gets the bytes held, as estimated.</summary>
    public long Size
    {
        get
        {
            lock (_lock)
            {
                return _size;
            }
        }
    }

    /// <summary>Gets the days held, expired or not.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _days.Count;
            }
        }
    }

    /// <summary>Gets a number that changes with each <see cref="Clear"/>: take it before reading a day, and give it to <see cref="Set"/>.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Gets a day's data if it's held and hasn't expired.</summary>
    /// <param name="day">The day.</param>
    /// <param name="value">The data.</param>
    /// <returns>True when it was held.</returns>
    public bool TryGet(DateOnly day, [NotNullWhen(true)] out T? value)
    {
        lock (_lock)
        {
            if (_days.TryGetValue(day, out var entry))
            {
                if (entry.Until > time.GetUtcNow())
                {
                    value = entry.Value;
                    return true;
                }

                Remove(day);
            }
        }

        value = null;
        return false;
    }

    /// <summary>Keeps a day's data, unless the cache was cleared since it was read or it's bigger than the budget.</summary>
    /// <param name="day">The day.</param>
    /// <param name="value">The data.</param>
    /// <param name="size">Its size in bytes, estimated.</param>
    /// <param name="generation">The <see cref="Generation"/> taken before it was read.</param>
    public void Set(DateOnly day, T value, long size, long generation)
    {
        if (size > budget)
        {
            return;
        }

        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }

            var now = time.GetUtcNow();
            Remove(day);
            foreach (var expired in _days.Where(d => d.Value.Until <= now).Select(d => d.Key).ToList())
            {
                Remove(expired);
            }

            while (_size + size > budget && _days.Count > 0)
            {
                Remove(_days.MinBy(d => d.Value.Until).Key);
            }

            _days[day] = new Entry(value, size, now + keptFor);
            _size += size;
        }
    }

    /// <summary>Forgets every day, and turns away the reads that began before.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _days.Clear();
            _size = 0;
            Interlocked.Increment(ref _generation);
        }
    }

    // Called with the lock held.
    private void Remove(DateOnly day)
    {
        if (_days.Remove(day, out var entry))
        {
            _size -= entry.Size;
        }
    }

    private sealed record Entry(T Value, long Size, DateTimeOffset Until);
}

/// <summary>Estimates of what the cached analytics take in memory (64-bit), for <see cref="DayCache{T}"/>'s budget (F-0222).</summary>
internal static class MemorySize
{
    /// <summary>A string: its object header, length and characters, rounded up to 8 bytes.</summary>
    /// <param name="value">The string, or null.</param>
    /// <returns>The bytes; none for null.</returns>
    public static long Of(string? value) => value is null ? 0 : (22 + (2L * value.Length) + 7) / 8 * 8;

    /// <summary>
    /// Keeps one instance of each string, so a day's thousands of views share their paths,
    /// visitors, clients and countries; adds each new one's size to a total.
    /// </summary>
    internal sealed class Strings
    {
        private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);

        /// <summary>Gets the bytes of the distinct strings seen.</summary>
        public long Bytes { get; private set; }

        /// <summary>Gets the one instance of a string.</summary>
        /// <param name="value">The string, or null.</param>
        /// <returns>The instance kept, or null.</returns>
        [return: NotNullIfNotNull(nameof(value))]
        public string? Share(string? value)
        {
            if (value is null)
            {
                return null;
            }

            if (_seen.TryGetValue(value, out var kept))
            {
                return kept;
            }

            _seen[value] = value;
            Bytes += Of(value);
            return value;
        }
    }
}
