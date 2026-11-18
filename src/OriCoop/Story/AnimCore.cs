using System;
using System.IO;

namespace OriCoop.Story
{
    static class AnimWire
    {
        public static void Write(BinaryWriter w, byte seat, byte op, byte flags, string anim)
        {
            if (seat == 0 || op > 1 || string.IsNullOrEmpty(anim) || anim.Length > 64)
                throw new InvalidDataException("cutscene animation");
            w.Write(seat); w.Write(op); w.Write(flags); w.Write(anim);
        }
        public static bool TryRead(BinaryReader r, out byte seat, out byte op, out byte flags, out string anim)
        {
            seat = op = flags = 0; anim = null;
            try
            {
                seat = r.ReadByte(); op = r.ReadByte(); flags = r.ReadByte(); anim = r.ReadString();
                return seat != 0 && op <= 1 && !string.IsNullOrEmpty(anim) && anim.Length <= 64;
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
