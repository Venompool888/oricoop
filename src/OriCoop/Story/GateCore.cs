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
        public bool AnyInside(string key, IEnumerable<int> seats, float now)
        { foreach (int seat in seats) if (IsInside(key, seat, now)) return true; return false; }
        public bool IsInside(string key, int seat, float now)
        {
            Dictionary<int, float> seats; float refreshed;
            return gates.TryGetValue(key, out seats) && seats.TryGetValue(seat, out refreshed) && now - refreshed <= Timeout;
        }
        public void MarkFired(string key, float now) { fired[key] = now; gates.Remove(key); }
        public bool RecentlyFired(string key, float now) { float at; return fired.TryGetValue(key, out at) && now - at < 15f; }
        // T-196: a fire that no story followed did nothing (the native trigger refused it). Its 15 s of "recently
        // fired" would swallow every further attempt without a word (playtest 4, Sein's pickup: dead for 15 s).
        // lastStoryAt: when a story last began or ended on this PC (same clock as now; negative = never).
        public const float FizzleSeconds = 3f;
        public bool Fizzled(string key, float now, float lastStoryAt)
        {
            float at;
            return !queued.Contains(key) && fired.TryGetValue(key, out at) && now - at >= FizzleSeconds && now - at < 15f && lastStoryAt < at;
        }
        // T-207 review (1): a fire the host still has in its queue (it has not found the trigger yet: the host's players
        // were moved to the forcing player's area and the area is loading, up to 10 s). Such a fire has not come to
        // nothing: the 3 s guess must leave its mark alone, or the forcing player's next request queues the same area a
        // second time and the first request's give-up then wipes the second one's mark.
        readonly HashSet<string> queued = new HashSet<string>();
        public void SetQueued(string key, bool on) { if (on) queued.Add(key); else queued.Remove(key); }
        public bool Queued(string key) { return queued.Contains(key); }
        public List<string> FizzledKeys(float now, float lastStoryAt)
        {
            List<string> keys = new List<string>();
            foreach (string key in fired.Keys) if (Fizzled(key, now, lastStoryAt)) keys.Add(key);
            return keys;
        }
        public void Release(string key) { fired.Remove(key); }
        // T-212 (6): a checkpoint restore puts the game's own triggers back (a TriggerOnce is armed again); a mark from
        // before it would make the area ignore every player for the rest of its 15 s.
        public void ClearFired() { fired.Clear(); queued.Clear(); }
        public IEnumerable<string> ActiveKeys(float now)
        { foreach (string key in gates.Keys) if (Count(key, now) > 0 && !RecentlyFired(key, now)) yield return key; }
    }
    // T-110: the gate waits for every player in the session (each seat, including the extra local players a PC
    // added), and every PC reports presence for each of its own seats. A PC may only report seats it owns.
    static class GateSeats
    {
        public const int MaxSeat = 4;
        public static int Total(int localSeats, int readyRemoteSeats) { return Math.Max(1, localSeats + readyRemoteSeats); }
        public static bool MayReport(int sender, int seat, int seatDevice) { return seat >= 1 && seat <= MaxSeat && seatDevice == sender; }
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
    // T-212 (2): a forced fire whose trigger the host has not loaded. Before: the host waited 3 s, dropped the request
    // without a word and was never pulled over; the forcing player's ring stayed full and could not ask again.
    static class GateForce
    {
        public const float NotFound = 3f;      // no position to go to (older build): give up as before
        public const float AfterMove = 10f;    // the host's players were moved to the area: wait for it to load
        public const float RetryAfter = 4f;    // nothing fired this long after a force: the ring may be held again

        // sinceStart: seconds since the request (not moved) or since the host's players were moved there.
        public static bool GiveUp(float sinceStart, bool moved) { return sinceStart >= (moved ? AfterMove : NotFound); }
        public static bool Rearm(float sinceForce, bool fired) { return !fired && sinceForce >= RetryAfter; }
        // T-207 review (2): the position in a force request comes from the forcing PC. The host only goes there when it
        // is where that player stands (the host knows the player's position; a gate area is a few units wide).
        // dx, dy: from the forcing player's Ori to the position in the request.
        public const float MaxHintDistance = 40f;
        public static bool HintNear(float dx, float dy) { return dx * dx + dy * dy <= MaxHintDistance * MaxHintDistance; }
    }
    // T-207 (4): finding a gate's trigger walks every Trigger and CutsceneTrigger in memory. For an area this PC has
    // not loaded (a partner waits there) that walk ran twice every frame (the presence check and the ward drawing).
    // An unresolved gate is looked for again at most every Retry seconds.
    static class GateResolve
    {
        public const float Retry = 0.5f;
        // lastTry: when it was last looked for (negative: never).
        public static bool Due(float now, float lastTry) { return lastTry < 0f || now - lastTry >= Retry; }
    }
    static class GateWire
    {
        static string Key(BinaryReader r)
        { string key = r.ReadString(); if (key.Length > 1024) throw new InvalidDataException("gate key"); return key; }
        static float Position(BinaryReader r)
        { float v = r.ReadSingle(); if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException("gate position"); return v; }
        public static void WriteState(BinaryWriter w, string key, int seat, bool inside) { w.Write(key); w.Write((byte)seat); w.Write(inside); }
        public static string ReadState(BinaryReader r, out int seat, out bool inside)
        {
            string key = Key(r); seat = r.ReadByte(); inside = r.ReadBoolean();
            if (seat < 1 || seat > GateSeats.MaxSeat) throw new InvalidDataException("gate seat"); return key;
        }
        public static void WriteCount(BinaryWriter w, string key, byte n, byte total) { w.Write(key); w.Write(n); w.Write(total); }
        public static string ReadCount(BinaryReader r, out byte n, out byte total)
        {
            string key = Key(r); n = r.ReadByte(); total = r.ReadByte();
            if (n > 8 || total < 1 || total > 8) throw new InvalidDataException("gate count"); return key;
        }
        public static void WriteForce(BinaryWriter w, string key) { w.Write(key); }
        public static string ReadForce(BinaryReader r) { return Key(r); }
        // T-212 (2): the forcing PC adds where the trigger is, so a host that has not loaded that area can go there.
        // The position is optional on the wire: a body that ends after the key (older build) reads as "no position".
        public static void WriteForce(BinaryWriter w, string key, float x, float y, float z) { w.Write(key); w.Write(x); w.Write(y); w.Write(z); }
        public static string ReadForce(BinaryReader r, out bool hasPosition, out float x, out float y, out float z)
        {
            string key = Key(r); x = y = z = 0f;
            hasPosition = r.BaseStream.CanSeek && r.BaseStream.Length - r.BaseStream.Position >= 12;
            if (hasPosition) { x = Position(r); y = Position(r); z = Position(r); }
            return key;
        }
        // T-207 (3): host -> all, "the fire of this area came to nothing" (the game's trigger refused it, or the host
        // never found it). Every PC forgets the fired mark at once. Before, a PC guessed from "no story began within
        // 3 s" (GateTable.Fizzled, kept as a fallback), and any unrelated story in those 3 s made it guess wrong: the
        // area then ignored the players for the full 15 s.
        public static void WriteRelease(BinaryWriter w, string key) { w.Write(key); }
        public static string ReadRelease(BinaryReader r) { return Key(r); }
        public static void WriteFire(BinaryWriter w, string key, float x, float y, float z) { w.Write(key); w.Write(x); w.Write(y); w.Write(z); }
        public static string ReadFire(BinaryReader r, out float x, out float y, out float z)
        { string key = Key(r); x = Position(r); y = Position(r); z = Position(r); return key; }
    }
}
