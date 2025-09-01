// Assets/Scripts/Pathfinding/MinHeap.cs
using System.Collections.Generic;

namespace EnemyAI
{
    /// <summary>
    /// Binary min-heap that stores (item, key) pairs.
    /// Supports O(log n) insert/decrease-key and pop-min.
    /// </summary>
    public class MinHeap<T>
    {
        private List<T> _heap;
        private List<float> _keys;
        private Dictionary<T, int> _index;

        public int Count => _heap.Count;

        public MinHeap(int capacity = 128, IEqualityComparer<T> comparer = null)
        {
            _heap = new List<T>(capacity);
            _keys = new List<float>(capacity);
            _index = new Dictionary<T, int>(comparer ?? EqualityComparer<T>.Default);
        }

        public bool Contains(T item) => _index.ContainsKey(item);

        public void Clear()
        {
            _heap.Clear();
            _keys.Clear();
            _index.Clear();
        }

        /// <summary>
        /// Insert a new (item,key). If the item already exists and the new key
        /// is smaller, decrease-key and bubble up.
        /// </summary>
        public void InsertOrDecrease(T item, float key)
        {
            if (_index.TryGetValue(item, out int i))
            {
                if (key < _keys[i])
                {
                    _keys[i] = key;
                    BubbleUp(i);
                }
                return;
            }

            int ni = _heap.Count;
            _heap.Add(item);
            _keys.Add(key);
            _index[item] = ni;
            BubbleUp(ni);
        }

        /// <summary>Remove and return the item with the smallest key.</summary>
        public T PopMin()
        {
            if (_heap.Count == 0) return default;

            T min = _heap[0];

            int last = _heap.Count - 1;
            Swap(0, last);

            _heap.RemoveAt(last);
            _keys.RemoveAt(last);
            _index.Remove(min);

            if (_heap.Count > 0)
                SiftDown(0);

            return min;
        }

        // -------- internals --------

        private void BubbleUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_keys[i] < _keys[p])
                {
                    Swap(i, p);
                    i = p;
                }
                else break;
            }
        }

        // Name kept for profiler readability
        private void SiftDown(int i)
        {
            int n = _heap.Count;
            while (true)
            {
                int l = (i << 1) + 1;
                if (l >= n) break;
                int r = l + 1;
                int s = (r < n && _keys[r] < _keys[l]) ? r : l;

                if (_keys[s] < _keys[i])
                {
                    Swap(i, s);
                    i = s;
                }
                else break;
            }
        }

        // Name kept for profiler readability
        private void Swap(int a, int b)
        {
            if (a == b) return;

            T ta = _heap[a];
            T tb = _heap[b];
            _heap[a] = tb;
            _heap[b] = ta;

            float ka = _keys[a];
            float kb = _keys[b];
            _keys[a] = kb;
            _keys[b] = ka;

            _index[tb] = a;
            _index[ta] = b;
        }
    }

    /// <summary>
    /// Non-generic alias specialized for Node so you can write `new MinHeap()`.
    /// Matches your Pathfinding usage: InsertOrDecrease(Node, float), PopMin(), etc.
    /// </summary>
    public class MinHeap : MinHeap<Node>
    {
        public MinHeap() : base(128) { }
        public MinHeap(int capacity) : base(capacity) { }
    }
}
