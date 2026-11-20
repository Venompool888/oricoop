using System.Collections.Generic;

namespace OriCoop.World
{
    // Duplicate filter for WorldObjSync message 75 (sent twice: unreliable + reliable; forwarded copies keep the original
    // sender). Key = (sender seat << 32) | seq, the last `capacity` keys kept (FIFO).
    // T-145: a new process that takes a freed seat starts its seq at 1 again, so the seat's old keys must go when it
    // joins (its first Hello: NetPlay.PlayerJoined), or its first events are dropped as duplicates everywhere. Not when
    // the seat leaves: a copy of the old process's last event still in flight (the unreliable one, or a host-forwarded
    // one) would then be applied a second time. A new process sends world events only after its save join, long after
    // its Hello, so no old copy is still in flight by then.
    public sealed class SeenEvents
    {
        readonly int m_capacity;
        readonly HashSet<long> m_seen = new HashSet<long>();
        readonly Queue<long> m_order = new Queue<long>();
        public SeenEvents(int capacity) { m_capacity = capacity; }
        public int Count { get { return m_seen.Count; } }

        public static long Key(int sender, uint seq) { return ((long)sender << 32) | seq; }

        // True the first time this (sender, seq) is seen: apply it. False: a duplicate copy.
        public bool FirstTime(int sender, uint seq)
        {
            long key = Key(sender, seq);
            if (!m_seen.Add(key)) return false;
            m_order.Enqueue(key);
            while (m_order.Count > m_capacity) m_seen.Remove(m_order.Dequeue());
            return true;
        }

        // A (possibly new) player now holds this seat: its numbering starts over.
        public void SeatJoined(int seat)
        {
            int count = m_order.Count;
            while (count-- > 0)
            {
                long key = m_order.Dequeue();
                if ((int)(key >> 32) == seat) m_seen.Remove(key); else m_order.Enqueue(key);
            }
        }

        public void Clear() { m_seen.Clear(); m_order.Clear(); }
    }
}
