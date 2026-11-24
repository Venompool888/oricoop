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
        public static bool ScreenRunsLive(bool onlineNoPause, bool menuOpen, bool popupOpen, bool coopPanelOpen)
        { return onlineNoPause && (menuOpen || popupOpen || coopPanelOpen); }
        public static bool ScreenLocksSeat(int seat, int primarySeat, bool nativeScreenOpen, int panelSeat)
        { return seat == panelSeat || (seat == primarySeat && nativeScreenOpen); }
        public static bool HoldOri(bool online, bool storyWatching, bool respawning)
        { return online && (storyWatching || respawning); }
        public static int RestoreScreenSuspends(ref int skipped, ref int vanilla)
        {
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

    public sealed class ScreenInputLocks
    {
        readonly HashSet<object> owners = new HashSet<object>();
        public void Set(object owner, bool locked) { if (locked) owners.Add(owner); else owners.Remove(owner); }
        public bool IsLocked(bool nativeLocked) { return nativeLocked || owners.Count != 0; }
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
}
