namespace NetScope.Core.Services;

/// <summary>线程安全的定容环形缓冲，用于保存最近一段时间的采样与事件现场。</summary>
public sealed class MemoryRingBuffer<T>
{
    private readonly object _gate = new();
    private readonly T[] _items;
    private int _head;
    private int _count;

    public MemoryRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    /// <summary>按时间顺序返回当前内容（先入先出）。</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<T>(_count);
            var start = (_head - _count + _items.Length) % _items.Length;
            for (var i = 0; i < _count; i++)
                result.Add(_items[(start + i) % _items.Length]);
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
        }
    }
}
