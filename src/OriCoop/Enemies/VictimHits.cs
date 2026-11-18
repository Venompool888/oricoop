using System.Collections.Generic;

namespace OriCoop.Enemies
{
    // One enemy projectile may legitimately damage several local Oris, once per victim.
    public sealed class VictimHits
    {
        readonly Dictionary<int, HashSet<ulong>> hits = new Dictionary<int, HashSet<ulong>>();
        public bool Add(int seat, ulong id)
        {
            if (seat < 1 || seat > 4) return false;
            HashSet<ulong> victim;
            if (!hits.TryGetValue(seat, out victim)) { victim = new HashSet<ulong>(); hits.Add(seat, victim); }
            return victim.Add(id);
        }
        public void Clear() { hits.Clear(); }
    }
}
