using System.Collections.Generic;

namespace OriCoop.World
{
    // Diagnostic history only. World objects may come and go as the two players stream different scenes.
    internal sealed class TriggerTrails
    {
        internal const int Capacity = 256;
        readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        readonly Queue<int> order = new Queue<int>();

        internal sealed class Entry
        {
            readonly Queue<string> steps = new Queue<string>();
            public float SentAt = -1f, StartedAt = -1f;

            public void Add(string step)
            {
                if (steps.Count == 8) steps.Dequeue();
                steps.Enqueue(step);
            }

            public string Describe() { return string.Join(" | ", steps.ToArray()); }
        }

        public Entry Find(int id)
        {
            Entry entry;
            return entries.TryGetValue(id, out entry) ? entry : null;
        }

        public Entry Record(int id)
        {
            Entry entry = Find(id);
            if (entry != null) return entry;
            if (entries.Count == Capacity) entries.Remove(order.Dequeue());
            entry = new Entry();
            entries.Add(id, entry);
            order.Enqueue(id);
            return entry;
        }

        public void Clear() { entries.Clear(); order.Clear(); }
    }
}
