using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Weesite.Content.Markdown;

/// <summary>
/// Results kept by key up to a size in all (T-0177), in two generations: when the newer one is
/// half full it becomes the older one and the oldest results go. A result found in the older
/// generation moves to the newer, so what's used often stays. Thread-safe.
/// </summary>
/// <typeparam name="TKey">The key.</typeparam>
/// <typeparam name="TValue">The result.</typeparam>
/// <param name="maxSize">The most kept in all, in the unit <paramref name="size"/> counts.</param>
/// <param name="size">A result's size, such as its characters; a result over half the limit isn't kept.</param>
internal sealed class BoundedCache<TKey, TValue>(long maxSize, Func<TValue, long> size)
    where TKey : notnull
{
    private readonly Lock _lock = new();
    private Dictionary<TKey, (TValue Value, long Size)> _newer = [];
    private Dictionary<TKey, (TValue Value, long Size)> _older = [];
    private long _newerSize;

    /// <summary>Finds a result.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="value">The result, when found.</param>
    /// <returns>Whether it was found.</returns>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_lock)
        {
            if (_newer.TryGetValue(key, out var kept))
            {
                value = kept.Value;
                return true;
            }

            if (_older.Remove(key, out kept))
            {
                AddNow(key, kept);
                value = kept.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Keeps a result.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="value">The result.</param>
    public void Add(TKey key, TValue value)
    {
        var bytes = size(value);
        if (bytes > maxSize / 2)
        {
            return;
        }

        lock (_lock)
        {
            AddNow(key, (value, bytes));
        }
    }

    /// <summary>Removes a result, such as one that no longer holds.</summary>
    /// <param name="key">Its key.</param>
    public void Remove(TKey key)
    {
        lock (_lock)
        {
            if (_newer.Remove(key, out var kept))
            {
                _newerSize -= kept.Size;
            }

            _older.Remove(key);
        }
    }

    private void AddNow(TKey key, (TValue Value, long Size) kept)
    {
        if (_newer.TryGetValue(key, out var replaced))
        {
            _newerSize -= replaced.Size;
        }

        _newer[key] = kept;
        _newerSize += kept.Size;
        if (_newerSize > maxSize / 2)
        {
            _older = _newer;
            _newer = [];
            _newerSize = 0;
        }
    }
}

/// <summary>A text's SHA-256, as the key of what was worked out from it.</summary>
internal static class TextHash
{
    /// <summary>Hashes a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The hash, in hex.</returns>
    public static string Of(string text) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())));
}
