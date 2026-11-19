using System;
using System.IO;

namespace OriCoop.Story
{
    // T-121: the key that names an animation on the wire. name|MoonGuid "A B C D"|frames was up to ~80 chars and
    // AnimWire.Write threw above 64 (inside the game's PlayCharacterAnimation). Now: the name cut to 40 chars, an
    // 8-hex FNV-1a hash of the first frame's guid, and the frame count; at most 61 chars, the same on every PC.
    static class AnimKey
    {
        public const int MaxName = 40;
        public static string Make(string name, string firstFrame, int frames)
        {
            if (name == null) name = "";
            if (name.Length > MaxName) name = name.Substring(0, MaxName);
            if (firstFrame == null) return name;
            uint hash = 2166136261;
            foreach (char c in firstFrame) { hash ^= c; hash *= 16777619; }
            return name + "|" + hash.ToString("x8") + "|" + Math.Max(0, Math.Min(frames, 99999999));
        }
    }
    static class AnimWire
    {
        public const int MaxAnim = 64;
        public static void Write(BinaryWriter w, byte seat, byte op, byte flags, string anim)
        {
            if (seat == 0 || op > 1 || string.IsNullOrEmpty(anim) || anim.Length > MaxAnim)
                throw new InvalidDataException("cutscene animation");
            w.Write(seat); w.Write(op); w.Write(flags); w.Write(anim);
        }
        public static bool TryRead(BinaryReader r, out byte seat, out byte op, out byte flags, out string anim)
        {
            seat = op = flags = 0; anim = null;
            try
            {
                seat = r.ReadByte(); op = r.ReadByte(); flags = r.ReadByte(); anim = r.ReadString();
                return seat != 0 && op <= 1 && !string.IsNullOrEmpty(anim) && anim.Length <= MaxAnim;
            }
            catch (IOException) { return false; }
            catch (FormatException) { return false; }
        }
    }
    sealed class AnimFollow
    {
        public const float Timeout = 8f;
        public bool Active { get; private set; }
        public string Anim { get; private set; }
        public int Seat { get; private set; }
        float started;
        public void Start(int seat, string anim, float now)
        { Seat = seat; Anim = anim; started = now; Active = true; }
        public bool Stop(int seat)
        {
            if (!Active || Seat != seat) return false;
            Active = false; Seat = 0; Anim = null; return true;
        }
        public bool Expired(float now)
        { return Active && now - started > Timeout && Stop(Seat); }
    }
}
