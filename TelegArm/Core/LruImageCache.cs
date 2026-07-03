using System.Collections.Generic;
using System.Drawing;

namespace TelegArm.Core
{
    /// <summary>
    /// Bounded LRU cache of small bitmaps keyed by id (avatars). Caps how many are held in memory so the
    /// per-session set can't grow without bound on ARM32. When over capacity the least-recently-used entry
    /// is EVICTED (removed) but deliberately NOT disposed — a visible control (a chat-list row, a bubble's
    /// sender avatar) may still reference it; the GC frees it once unreferenced. Entries are disk-backed
    /// (thumbs/avatar_*.jpg), so an evicted avatar reloads instantly without a re-download. Drop-in for the
    /// Dictionary API the call sites use: TryGetValue / indexer-set / Values / Clear. Values + Clear at
    /// shutdown dispose whatever is still cached.
    /// </summary>
    public sealed class LruImageCache
    {
        private readonly int _capacity;
        private readonly Dictionary<long, LinkedListNode<KeyValuePair<long, Image>>> _map;
        private readonly LinkedList<KeyValuePair<long, Image>> _order;   // first = most recently used

        public LruImageCache(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _map = new Dictionary<long, LinkedListNode<KeyValuePair<long, Image>>>();
            _order = new LinkedList<KeyValuePair<long, Image>>();
        }

        /// <summary>Number of entries currently held (for [PERF] growth diagnostics).</summary>
        public int Count { get { return _map.Count; } }

        public bool TryGetValue(long key, out Image image)
        {
            LinkedListNode<KeyValuePair<long, Image>> node;
            if (_map.TryGetValue(key, out node))
            {
                _order.Remove(node);
                _order.AddFirst(node);          // touch → most recently used
                image = node.Value.Value;
                return true;
            }
            image = null;
            return false;
        }

        public Image this[long key]
        {
            set
            {
                LinkedListNode<KeyValuePair<long, Image>> node;
                if (_map.TryGetValue(key, out node))
                {
                    _order.Remove(node);
                    node.Value = new KeyValuePair<long, Image>(key, value);
                    _order.AddFirst(node);
                }
                else
                {
                    _map[key] = _order.AddFirst(new KeyValuePair<long, Image>(key, value));
                    while (_map.Count > _capacity)   // evict LRU (remove only — a live control may hold it; GC frees it)
                    {
                        var tail = _order.Last;
                        _order.RemoveLast();
                        _map.Remove(tail.Value.Key);
                    }
                }
            }
        }

        /// <summary>Snapshot of the currently-cached images (for shutdown disposal).</summary>
        public IEnumerable<Image> Values
        {
            get
            {
                var list = new List<Image>(_map.Count);
                foreach (var kv in _order) list.Add(kv.Value);
                return list;
            }
        }

        public void Clear() { _map.Clear(); _order.Clear(); }
    }
}
