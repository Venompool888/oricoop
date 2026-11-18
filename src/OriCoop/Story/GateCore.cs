using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Story
{
    sealed class GateTable
    {
        public const float Timeout = 1.5f;
        readonly Dictionary<string, Dictionary<int, float>> gates = new Dictionary<string, Dictionary<int, float>>();
        readonly Dictionary<string, float> fired = new Dictionary<string, float>();
        public void SetInside(string key, int seat, bool inside, float now)
        {
            if (RecentlyFired(key, now)) return;
            Dictionary<int, float> seats;
            if (!gates.TryGetValue(key, out seats))
            {
                if (!inside) return;
                seats = new Dictionary<int, float>(); gates[key] = seats;
            }
            if (inside) seats[seat] = now; else seats.Remove(seat);
        }
        public int Count(string key, float now)
        {
            Dictionary<int, float> seats; int n = 0;
            if (gates.TryGetValue(key, out seats)) foreach (float refreshed in seats.Values) if (now - refreshed <= Timeout) n++;
            return n;
        }
        public bool AllHere(string key, int players, float now) { return players >= 1 && Count(key, now) >= players; }
        public void Forget(int seat) { foreach (Dictionary<int, float> seats in gates.Values) seats.Remove(seat); }
        public bool IsInside(string key, int seat, float now)
        {
            Dictionary<int, float> seats; float refreshed;
            return gates.TryGetValue(key, out seats) && seats.TryGetValue(seat, out refreshed) && now - refreshed <= Timeout;
        }
        public void MarkFired(string key, float now) { fired[key] = now; gates.Remove(key); }
        public bool RecentlyFired(string key, float now) { float at; return fired.TryGetValue(key, out at) && now - at < 15f; }
        public IEnumerable<string> ActiveKeys(float now)
        { foreach (string key in gates.Keys) if (Count(key, now) > 0 && !RecentlyFired(key, now)) yield return key; }
    }
    sealed class HoldMeter
    {
        public const float FillSeconds = 1.5f;
        float elapsed;
        public float Progress { get { return Math.Min(1f, elapsed / FillSeconds); } }
        public bool Update(bool held, float dt)
        {
            if (Progress >= 1f) return false;
            if (!held) { elapsed = 0f; return false; }
            elapsed += dt;
            return Progress >= 1f;
        }
        public void Reset() { elapsed = 0f; }
    }
    static class GateWire
    {
        static string Key(BinaryReader r)
        { string key = r.ReadString(); if (key.Length > 1024) throw new InvalidDataException("gate key"); return key; }
        static float Position(BinaryReader r)
        { float v = r.ReadSingle(); if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException("gate position"); return v; }
        public static void WriteState(BinaryWriter w, string key, bool inside) { w.Write(key); w.Write(inside); }
        public static string ReadState(BinaryReader r, out bool inside) { string key = Key(r); inside = r.ReadBoolean(); return key; }
        public static void WriteCount(BinaryWriter w, string key, byte n, byte total) { w.Write(key); w.Write(n); w.Write(total); }
        public static string ReadCount(BinaryReader r, out byte n, out byte total)
        {
            string key = Key(r); n = r.ReadByte(); total = r.ReadByte();
            if (n > 8 || total < 1 || total > 8) throw new InvalidDataException("gate count"); return key;
        }
        public static void WriteForce(BinaryWriter w, string key) { w.Write(key); }
        public static string ReadForce(BinaryReader r) { return Key(r); }
        public static void WriteFire(BinaryWriter w, string key, float x, float y, float z) { w.Write(key); w.Write(x); w.Write(y); w.Write(z); }
        public static string ReadFire(BinaryReader r, out float x, out float y, out float z)
        { string key = Key(r); x = Position(r); y = Position(r); z = Position(r); return key; }
    }
}
