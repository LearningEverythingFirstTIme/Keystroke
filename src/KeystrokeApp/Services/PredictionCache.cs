namespace KeystrokeApp.Services;

/// <summary>
/// Simple LRU cache for prediction results.
/// Keyed on typed text prefix — avoids redundant API calls
/// for backspace/retype patterns and repeated phrases.
/// Thread-safe via locking.
/// </summary>
public class PredictionCache
{
    private readonly int _maxSize;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _lru;
    private readonly object _lock = new();

    private class CacheEntry
    {
        public required string Key { get; init; }
        public required string? Value { get; init; }
    }

    public PredictionCache(int maxSize = 50)
    {
        _maxSize = maxSize;
        _map = new(maxSize);
        _lru = new();
    }

    /// <summary>
    /// Try to get a cached prediction for the given prefix.
    /// Returns true if found (value may be null for cached "no result").
    /// </summary>
    public bool TryGet(string prefix, out string? completion)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(prefix, out var node))
            {
                // Move to front (most recently used)
                _lru.Remove(node);
                _lru.AddFirst(node);
                completion = node.Value.Value;
                return true;
            }
        }

        completion = null;
        return false;
    }

    /// <summary>
    /// Store a prediction result in the cache.
    /// </summary>
    public void Put(string prefix, string? completion)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(prefix, out var existing))
            {
                // Update existing entry and move to front
                _lru.Remove(existing);
                var updated = new LinkedListNode<CacheEntry>(
                    new CacheEntry { Key = prefix, Value = completion });
                _lru.AddFirst(updated);
                _map[prefix] = updated;
                return;
            }

            // Evict oldest if at capacity
            if (_map.Count >= _maxSize)
            {
                var oldest = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }

            var node = new LinkedListNode<CacheEntry>(
                new CacheEntry { Key = prefix, Value = completion });
            _lru.AddFirst(node);
            _map[prefix] = node;
        }
    }

    /// <summary>
    /// Clear all cached entries (e.g. when switching contexts).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }
}
