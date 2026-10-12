namespace BetterNotepad.Pdf;

/// <summary>Thread-safe LRU cache bounded by an approximate byte budget.</summary>
internal sealed class ByteLru<TKey, TValue> where TKey : notnull
{
    private readonly record struct Entry(TKey Key, TValue Value, long Size);

    private readonly object _lock = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _list = new();
    private long _bytes;
    private long _budget;

    public ByteLru(long budgetBytes) => _budget = budgetBytes;

    public long Bytes { get { lock (_lock) return _bytes; } }
    public int Count { get { lock (_lock) return _map.Count; } }

    public long Budget
    {
        get { lock (_lock) return _budget; }
        set { lock (_lock) { _budget = Math.Max(0, value); Evict(_budget); } }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value, long size)
    {
        lock (_lock)
        {
            if (_map.Remove(key, out var old)) { _list.Remove(old); _bytes -= old.Value.Size; }
            if (size > _budget) return;      // never cache something bigger than the whole budget
            _map[key] = _list.AddFirst(new Entry(key, value, size));
            _bytes += size;
            Evict(_budget);
        }
    }

    public void Trim(long targetBytes = 0)
    {
        lock (_lock) Evict(Math.Max(0, targetBytes));
    }

    private void Evict(long target)
    {
        while (_bytes > target && _list.Last is { } last)
        {
            _list.RemoveLast();
            _map.Remove(last.Value.Key);
            _bytes -= last.Value.Size;
        }
    }
}
