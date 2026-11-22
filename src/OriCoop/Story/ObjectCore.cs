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
    internal sealed class RecentObjects<T>
    {
        readonly Dictionary<T, float> played = new Dictionary<T, float>();
        public void Mark(T id, float now) { played[id] = now; }
        public bool Recent(T id, float now, float duration)
        { float last; return played.TryGetValue(id, out last) && now - last < duration; }
    }
}
