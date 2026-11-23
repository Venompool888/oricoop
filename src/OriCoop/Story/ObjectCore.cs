using System.Collections.Generic;
using System.IO;

namespace OriCoop.Story
{
    internal static class ObjectWire
    {
        public static void WritePedestal(BinaryWriter w, byte[] id) { w.Write(id); }
        public static byte[] ReadPedestal(BinaryReader r) { return StoryWire.ReadId(r); }
        public static void WriteLever(BinaryWriter w, byte[] id, byte direction) { w.Write(id); w.Write(direction); }
        public static byte[] ReadLever(BinaryReader r, out byte direction)
        { byte[] id = StoryWire.ReadId(r); direction = r.ReadByte(); return id; }
        public static bool LeverChanged(int current, int received) { return current != received; }
    }
    // T-194: what this PC's own Ori is doing with a lever ("grabbed" / "in range", Lever.IsGrabbed / InRange) across a
    // read of the lever's saved state. The game's Lever.Serialize clears both on every read; that is right for a load
    // (the player's SeinLever is reset by the same load) and wrong for a partner's world blob applied to the live
    // lever: SeinLever still holds it, can then neither release nor leave it and pulls Ori back to it every frame.
    internal static class LeverHold
    {
        public const int None = -1;   // not a live world apply (the game's own load, or a write): keep nothing
        public static int Capture(bool liveWorldApply, bool reading, bool grabbed, bool inRange)
        { return liveWorldApply && reading ? (grabbed ? 1 : 0) | (inRange ? 2 : 0) : None; }
        public static bool Kept(int state) { return state > 0; }   // something to put back after the read
        public static bool Grabbed(int state) { return state > 0 && (state & 1) != 0; }
        public static bool InRange(int state) { return state > 0 && (state & 2) != 0; }
    }
    // T-204: a map stone saves one int: Normal / Highlighted / Activated (MapStone.State). "Highlighted" belongs to the
    // PC whose own Ori stands at the stone (spirit flame locked, the orb parked in the stone); only "Activated" is
    // shared. MapStone.Serialize just overwrites the state, so a partner's blob read into the live stone could leave
    // this PC's highlight on with nothing left to take it off (only Highlighted -> Normal and the activation story do).
    internal static class MapStoneApply
    {
        public const int Normal = 0, Highlighted = 1, Activated = 2;
        // The state the live stone ends up in when a partner's saved state is read into it.
        public static int Resolve(int local, int incoming)
        {
            if (incoming == Activated) return Activated;
            if (local == Activated) return Normal;   // un-activated again (the host rolled back): usable from scratch
            return local;                            // the partner's Normal / Highlighted says nothing about my Ori
        }
        // This PC's highlight has to be taken off now: the stone leaves Highlighted without doing it itself.
        public static bool ReleaseHighlight(int local, int incoming)
        { return local == Highlighted && Resolve(local, incoming) != Highlighted; }
    }
    internal sealed class RecentObjects<T>
    {
        readonly Dictionary<T, float> played = new Dictionary<T, float>();
        public void Mark(T id, float now) { played[id] = now; }
        public bool Recent(T id, float now, float duration)
        { float last; return played.TryGetValue(id, out last) && now - last < duration; }
    }
}
