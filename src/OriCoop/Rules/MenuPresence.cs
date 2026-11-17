using System.Collections.Generic;
using System.IO;

namespace OriCoop.Rules
{
    public sealed class MenuPresence
    {
        readonly HashSet<int> seats = new HashSet<int>();
        public void Set(int seat, bool paused) { if (paused) seats.Add(seat); else seats.Remove(seat); }
        public void Receive(int sender, BinaryReader r) { Set(sender, r.ReadBoolean()); }
        public void Clear() { seats.Clear(); }
        public bool InMenu(int seat) { return seats.Contains(seat); }
        public bool HasPartner(int localSeat)
        {
            foreach (int seat in seats) if (seat != localSeat) return true;
            return false;
        }
        public int FirstSeat
        {
            get { int first = 0; foreach (int seat in seats) if (first == 0 || seat < first) first = seat; return first; }
        }
        public static bool LocalPaused(bool online, bool paused) { return online && paused; }
        public static bool WantRemotePause(bool online, bool partner, bool ready, bool hasPlayer, bool respawning, bool gameplay)
        {
            return online && partner && ready && hasPlayer && !respawning && gameplay;
        }
    }
}
