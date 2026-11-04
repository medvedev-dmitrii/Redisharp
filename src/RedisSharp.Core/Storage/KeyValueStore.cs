using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace RedisSharp.Core.Storage;

/// <summary>
/// Thread-safe in-memory key/value store with optional per-key expiry.
/// Expired keys are removed lazily on access and actively by <see cref="EvictExpired"/>,
/// mirroring Redis' two-pronged expiry strategy.
/// </summary>
public sealed class KeyValueStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IClock _clock;

    public KeyValueStore(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    /// <summary>Number of live (non-expired) keys. O(n) – intended for INFO/DBSIZE, not hot paths.</summary>
    public int Count => _entries.Count(kv => !IsExpired(kv.Value));

    public bool TryGet(string key, [NotNullWhen(true)] out string? value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (!IsExpired(entry))
            {
                value = entry.Value;
                return true;
            }
            _entries.TryRemove(KeyValuePair.Create(key, entry));
        }

        value = null;
        return false;
    }

    public void Set(string key, string value, TimeSpan? ttl = null)
    {
        var expiresAt = ttl.HasValue ? _clock.UtcNow + ttl.Value : (DateTimeOffset?)null;
        _entries[key] = new Entry(value, expiresAt);
    }

    /// <summary>Sets only if the key does not already exist (SET NX / SETNX). Returns true on success.</summary>
    public bool SetIfAbsent(string key, string value, TimeSpan? ttl = null)
    {
        var expiresAt = ttl.HasValue ? _clock.UtcNow + ttl.Value : (DateTimeOffset?)null;
        var fresh = new Entry(value, expiresAt);

        while (true)
        {
            if (_entries.TryAdd(key, fresh))
                return true;

            if (!_entries.TryGetValue(key, out var existing))
                continue; // raced with a delete; retry add

            if (!IsExpired(existing))
                return false;

            if (_entries.TryUpdate(key, fresh, existing))
                return true;
        }
    }

    /// <summary>Sets only if the key already exists (SET XX). Returns true on success.</summary>
    public bool SetIfPresent(string key, string value, TimeSpan? ttl = null)
    {
        var expiresAt = ttl.HasValue ? _clock.UtcNow + ttl.Value : (DateTimeOffset?)null;
        var fresh = new Entry(value, expiresAt);

        while (true)
        {
            if (!_entries.TryGetValue(key, out var existing) || IsExpired(existing))
                return false;

            if (_entries.TryUpdate(key, fresh, existing))
                return true;
        }
    }

    public bool Delete(string key) =>
        _entries.TryRemove(key, out var entry) && !IsExpired(entry);

    public bool Exists(string key) => TryGet(key, out _);

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to the integer stored at <paramref name="key"/>,
    /// treating a missing key as 0. Preserves any existing TTL.
    /// </summary>
    /// <exception cref="InvalidOperationException">Value is not an integer or would overflow.</exception>
    public long Increment(string key, long delta)
    {
        while (true)
        {
            if (!_entries.TryGetValue(key, out var existing) || IsExpired(existing))
            {
                if (_entries.TryAdd(key, new Entry(delta.ToString(), null)))
                    return delta;
                if (existing is not null && _entries.TryUpdate(key, new Entry(delta.ToString(), null), existing))
                    return delta;
                continue;
            }

            if (!long.TryParse(existing.Value, out var current))
                throw new InvalidOperationException("value is not an integer or out of range");

            long next;
            try { next = checked(current + delta); }
            catch (OverflowException) { throw new InvalidOperationException("increment or decrement would overflow"); }

            if (_entries.TryUpdate(key, existing with { Value = next.ToString() }, existing))
                return next;
        }
    }

    /// <summary>Remaining TTL, <c>null</c> if the key has no expiry, or <c>TimeSpan.MinValue</c> if the key is missing.</summary>
    public TimeSpan? GetTtl(string key)
    {
        if (!_entries.TryGetValue(key, out var entry) || IsExpired(entry))
            return TimeSpan.MinValue;

        return entry.ExpiresAt.HasValue ? entry.ExpiresAt.Value - _clock.UtcNow : null;
    }

    /// <summary>Sets a TTL on an existing key. Returns false if the key does not exist.</summary>
    public bool Expire(string key, TimeSpan ttl)
    {
        while (true)
        {
            if (!_entries.TryGetValue(key, out var existing) || IsExpired(existing))
                return false;

            if (_entries.TryUpdate(key, existing with { ExpiresAt = _clock.UtcNow + ttl }, existing))
                return true;
        }
    }

    /// <summary>Removes a TTL. Returns false if the key is missing or already persistent.</summary>
    public bool Persist(string key)
    {
        while (true)
        {
            if (!_entries.TryGetValue(key, out var existing) || IsExpired(existing) || existing.ExpiresAt is null)
                return false;

            if (_entries.TryUpdate(key, existing with { ExpiresAt = null }, existing))
                return true;
        }
    }

    public IEnumerable<string> Keys(string pattern = "*")
    {
        var matcher = GlobMatcher.Compile(pattern);
        foreach (var (key, entry) in _entries)
        {
            if (!IsExpired(entry) && matcher(key))
                yield return key;
        }
    }

    public void Clear() => _entries.Clear();

    /// <summary>Active expiry sweep. Returns the number of keys removed.</summary>
    public int EvictExpired()
    {
        var removed = 0;
        foreach (var (key, entry) in _entries)
        {
            if (IsExpired(entry) && _entries.TryRemove(KeyValuePair.Create(key, entry)))
                removed++;
        }
        return removed;
    }

    private bool IsExpired(Entry entry) =>
        entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= _clock.UtcNow;

    private sealed record Entry(string Value, DateTimeOffset? ExpiresAt);
}
