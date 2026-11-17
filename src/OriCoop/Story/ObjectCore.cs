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
    internal sealed class RecentObjects<T>
    {
        readonly Dictionary<T, float> played = new Dictionary<T, float>();
        public void Mark(T id, float now) { played[id] = now; }
        public bool Recent(T id, float now, float duration)
        { float last; return played.TryGetValue(id, out last) && now - last < duration; }
    }
}
