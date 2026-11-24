using System;
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
        public static bool MapRunsLive(bool onlineNoPause, bool mapOpen, bool teleporterPicker)
        { return onlineNoPause && mapOpen && !teleporterPicker; }
        public static int RestoreMapSuspends(bool liveMap, ref int skipped, ref int vanilla)
        {
            if (!liveMap) return 0;
            int restored = skipped;
            vanilla += restored; skipped = 0;
            return restored;
        }
        public static bool ConsumeMenuResume(ref int skipped, ref int vanilla)
        {
            if (skipped > 0) { skipped--; return false; }
            if (vanilla == 0) return false;
            vanilla--; return true;
        }
        public static bool WantRemotePause(bool online, bool partner, bool ready, bool hasPlayer, bool respawning, bool gameplay)
        {
            return online && partner && ready && hasPlayer && !respawning && gameplay;
        }
    }

    // What this PC last told the others about its own pause (msg 53) in the current session.
    // T-104: nothing is sent before this PC's session has started. Connected comes first; a pause sent then is wrong
    // twice over: the receiver clears what it heard at its own session start (which may already be past), and this PC's
    // offline pause stops pausing at its session start, after which "not paused" equals the reset state and was never
    // sent, so the guest stayed frozen for good.
    public sealed class MenuReport
    {
        bool reported, started;
        public void SessionStarted() { started = true; reported = false; }
        public void SessionEnded() { started = false; reported = false; }
        // true = send `paused` now (and it is remembered as told).
        public bool ShouldSend(bool online, bool paused)
        {
            if (!online || !started || paused == reported) return false;
            reported = paused; return true;
        }
    }

    // T-044: online nothing pauses. Native screens that suspend the game (help popups, page popups, skill-tree item
    // help) call Suspend/Resume in pairs, sometimes Resume without Suspend. Online the suspend is skipped and remembered
    // per owner, so its matching resume is skipped too and no other suspension's reference count moves. Offline, and a
    // popup opened before going online, stay vanilla.
    public sealed class PopupLedger<T> where T : class
    {
        readonly Dictionary<T, int> skipped = new Dictionary<T, int>();
        // true = do the native suspend (offline)
        public bool Open(T owner, bool online)
        {
            if (!online) return true;
            int n; skipped.TryGetValue(owner, out n); skipped[owner] = n + 1; return false;
        }
        // true = do the native resume (it was not skipped)
        public bool Close(T owner)
        {
            int n;
            if (!skipped.TryGetValue(owner, out n)) return true;
            if (n <= 1) skipped.Remove(owner); else skipped[owner] = n - 1;
            return false;
        }
        public int OpenCount { get { int total = 0; foreach (int n in skipped.Values) total += n; return total; } }
        public bool AnyOpen { get { return skipped.Count > 0; } }
        // Owners that are gone (destroyed screen) without closing: forget them so nothing stays locked.
        public int Prune(Predicate<T> gone)
        {
            List<T> dead = new List<T>();
            foreach (T owner in skipped.Keys) if (gone(owner)) dead.Add(owner);
            foreach (T owner in dead) skipped.Remove(owner);
            return dead.Count;
        }
        public void Clear() { skipped.Clear(); }
    }
}
