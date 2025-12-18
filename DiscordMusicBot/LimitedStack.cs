namespace DiscordMusicBot;

public sealed class LimitedStack<T>
{
    private readonly int _capacity;
    private readonly LinkedList<T> _items = new();
    private readonly object _gate = new();

    public LimitedStack(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    public void Push(T item)
    {
        lock (_gate)
        {
            _items.AddFirst(item);
            while (_items.Count > _capacity)
            {
                _items.RemoveLast();
            }
        }
    }

    public bool TryPop(out T item)
    {
        lock (_gate)
        {
            var first = _items.First;
            if (first == null)
            {
                item = default!;
                return false;
            }

            item = first.Value;
            _items.RemoveFirst();
            return true;
        }
    }

    public IReadOnlyList<T> GetSnapshot()
    {
        lock (_gate)
        {
            return _items.ToList();
        }
    }
}
