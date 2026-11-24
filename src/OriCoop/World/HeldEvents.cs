using System;
using System.Collections.Generic;

namespace OriCoop.World
{
    // Events held until their target can take them (T-074: WorldObjSync holds a partner's world-object event until the
    // object's scene has loaded its saved state). Ready events come out in ARRIVAL order, so two events for the same
    // object (damage then collapse, trigger then stomper) replay as the partner sent them.
    public sealed class HeldEvents<T>
    {
        readonly List<T> m_items = new List<T>();
        public int Count { get { return m_items.Count; } }
        public void Add(T item) { m_items.Add(item); }
        public bool Any(Predicate<T> match) { return m_items.Exists(match); }
        public void Clear() { m_items.Clear(); }

        // Removes and returns the ready items in arrival order; items that are gone are dropped (counted in `dropped`).
        public List<T> TakeReady(Func<T, bool> gone, Func<T, bool> ready, out int dropped)
        {
            List<T> taken = new List<T>(); dropped = 0;
            for (int i = 0; i < m_items.Count; )
            {
                T item = m_items[i];
                if (gone(item)) { m_items.RemoveAt(i); dropped++; continue; }
                if (!ready(item)) { i++; continue; }
                m_items.RemoveAt(i); taken.Add(item);
            }
            return taken;
        }
    }
}
