namespace OnePdf.Internal;

/// <summary>
/// Byte-budgeted least-recently-used cache with single-flight loading: concurrent requests for one key run the loader
/// once and share its result. A load that is already running on the calling thread (a reference cycle) is reported
/// instead of deadlocking.
/// </summary>
internal sealed class ObjectCache
{
    private sealed class Node
    {
        public long Key;
        public object? Value;
        public long Size;
        public bool Done;
        public bool Linked;
        public int Owner;
        public Node? Prev;
        public Node? Next;
    }

    private readonly Dictionary<long, Node> _map = [];
    private readonly object _gate = new();
    private readonly long _budget;
    private Node? _head;
    private Node? _tail;
    private long _bytes;

    public ObjectCache(long budgetBytes) => _budget = budgetBytes;

    public object GetOrLoad<TState>(long key, TState state, Func<TState, (object Value, long Size)> load)
    {
        Node node;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out Node? existing))
            {
                node = existing;
                if (node.Done)
                {
                    MoveToFront(node);
                    return node.Value!;
                }
            }
            else
            {
                node = new Node { Key = key };
                _map[key] = node;
            }
        }

        lock (node)
        {
            if (node.Done)
            {
                return node.Value!;
            }

            int me = Environment.CurrentManagedThreadId;
            if (node.Owner == me)
            {
                throw new PdfFormatException("Objects refer to each other in a cycle that cannot be resolved.");
            }

            node.Owner = me;
            bool ok = false;
            try
            {
                (object value, long size) = load(state);
                lock (_gate)
                {
                    node.Value = value;
                    node.Size = size;
                    node.Done = true;
                    if (!_map.TryGetValue(key, out Node? cur))
                    {
                        _map[key] = node;
                    }
                    else if (!ReferenceEquals(cur, node))
                    {
                        return value; // another node took over the key; leave it be
                    }

                    PushFront(node);
                    _bytes += size;
                    Evict();
                }

                ok = true;
                return value;
            }
            finally
            {
                node.Owner = 0;
                if (!ok)
                {
                    lock (_gate)
                    {
                        if (_map.TryGetValue(key, out Node? cur) && ReferenceEquals(cur, node))
                        {
                            _map.Remove(key);
                        }
                    }
                }
            }
        }
    }

    private void PushFront(Node n)
    {
        n.Prev = null;
        n.Next = _head;
        if (_head is not null)
        {
            _head.Prev = n;
        }

        _head = n;
        _tail ??= n;
        n.Linked = true;
    }

    private void Unlink(Node n)
    {
        if (n.Prev is not null)
        {
            n.Prev.Next = n.Next;
        }
        else
        {
            _head = n.Next;
        }

        if (n.Next is not null)
        {
            n.Next.Prev = n.Prev;
        }
        else
        {
            _tail = n.Prev;
        }

        n.Prev = n.Next = null;
        n.Linked = false;
    }

    private void MoveToFront(Node n)
    {
        if (ReferenceEquals(_head, n) || !n.Linked)
        {
            return;
        }

        Unlink(n);
        PushFront(n);
    }

    private void Evict()
    {
        // The newest entry always stays, even when it alone exceeds the budget; it is dropped by the next load.
        while (_bytes > _budget && _tail is not null && !ReferenceEquals(_tail, _head))
        {
            Node victim = _tail;
            Unlink(victim);
            _map.Remove(victim.Key);
            _bytes -= victim.Size;
        }
    }
}
