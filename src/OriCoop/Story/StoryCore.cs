using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Story
{
    internal sealed class StoryStart
    {
        public uint Token;
        public byte Kind;
        public byte[] Scene, Id;
        public string Path = "", State = "";
        public float X, Y, Z;
        public bool Classified = true;
    }

    internal static class StoryWire
    {
        public static byte[] ReadId(BinaryReader r)
        {
            byte[] id = new byte[16];
            for (int i = 0; i < id.Length; i++) id[i] = r.ReadByte();
            return id;
        }
        public static void WriteStart(BinaryWriter w, StoryStart s)
        {
            w.Write(s.Token); w.Write(s.Kind); w.Write(s.Scene); w.Write(s.Id);
            w.Write(s.Path); w.Write(s.State); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); w.Write(s.Classified);
        }
        public static StoryStart ReadStart(BinaryReader r)
        {
            StoryStart s = new StoryStart();
            s.Token = r.ReadUInt32(); s.Kind = r.ReadByte(); s.Scene = ReadId(r); s.Id = ReadId(r);
            s.Path = r.ReadString(); s.State = r.ReadString();
            s.X = r.ReadSingle(); s.Y = r.ReadSingle(); s.Z = r.ReadSingle();
            s.Classified = r.BaseStream.Position >= r.BaseStream.Length || r.ReadBoolean();
            if (s.Kind > 1 || s.Path.Length > 4096 || s.State.Length > 4096
                || float.IsNaN(s.X) || float.IsInfinity(s.X) || float.IsNaN(s.Y) || float.IsInfinity(s.Y)
                || float.IsNaN(s.Z) || float.IsInfinity(s.Z)) throw new InvalidDataException("invalid StoryStart");
            return s;
        }
        public static void WriteToken(BinaryWriter w, uint token) { w.Write(token); }
        public static uint ReadToken(BinaryReader r) { return r.ReadUInt32(); }
    }

    internal class StoryEntry
    {
        public string Key;
        public uint Token;
        public int Sender;
        public float Began, NextResolve, EndAt = -1f;
        public bool SawLock, Resolved, Watch, Skipped, SkipApplied, Classified = true;
    }

    internal enum StoryResolution { Retry, Drop, Watch }

    internal sealed class StoryLedger<T> where T : StoryEntry
    {
        public readonly Dictionary<string, float> Seen = new Dictionary<string, float>();
        public readonly Dictionary<ulong, T> Local = new Dictionary<ulong, T>();
        public readonly Dictionary<ulong, T> Remote = new Dictionary<ulong, T>();
        public readonly Dictionary<ulong, T> Accepted = new Dictionary<ulong, T>();
        uint token;
        public static ulong TokenKey(int sender, uint value) { return ((ulong)sender << 32) | value; }
        public bool RecentlySeen(string key, float now)
        { float last; return Seen.TryGetValue(key, out last) && now - last < 3f; }
        public bool HasAccepted(string key)
        { foreach (T item in Accepted.Values) if (item.Key == key) return true; return false; }
        public bool PrepareLocal(T item, int seat, bool host, float now)
        {
            if (RecentlySeen(item.Key, now) || host && HasAccepted(item.Key)) return false;
            Seen[item.Key] = now; item.Sender = seat; item.Token = ++token; item.Began = now;
            return true;
        }
        public void TrackLocal(T item, bool host)
        {
            ulong key = TokenKey(item.Sender, item.Token); Local[key] = item;
            if (host) Accepted[key] = item;
        }
        public bool CanReceive(T item, bool host, float now)
        {
            if (Remote.ContainsKey(TokenKey(item.Sender, item.Token))) return false;
            return !host || !HasAccepted(item.Key) && !RecentlySeen(item.Key, now);
        }
        public void TrackRemote(T item, bool host, float now)
        {
            if (RecentlySeen(item.Key, now)) item.Resolved = true;
            Seen[item.Key] = now; item.Began = now;
            ulong key = TokenKey(item.Sender, item.Token); Remote[key] = item;
            if (host) Accepted[key] = item;
        }
        public void EndLocal(ulong key) { Local.Remove(key); Accepted.Remove(key); }
        public void Skip(T item) { item.Skipped = true; }
        public int LiveRemotes
        { get { int count = 0; foreach (T item in Remote.Values) if (item.EndAt < 0) count++; return count; } }
        public bool Watching
        { get { foreach (T item in Remote.Values) if ((!item.Resolved && item.Classified) || item.Watch) return true; return false; } }
        static List<ulong> SeatTokens(Dictionary<ulong, T> items, int seat)
        {
            List<ulong> result = new List<ulong>();
            foreach (KeyValuePair<ulong, T> p in items) if (p.Value.Sender == seat) result.Add(p.Key);
            return result;
        }
        public List<ulong> RemoteSeatTokens(int seat) { return SeatTokens(Remote, seat); }
        public List<ulong> AcceptedSeatTokens(int seat) { return SeatTokens(Accepted, seat); }
        public static bool DeferEnd(T item, float now)
        {
            if (item.EndAt < 0) { item.EndAt = now; return true; }
            return false;
        }
        public static bool CanSkip(T item, bool hasMirror) { return !item.SkipApplied && hasMirror; }
        public static bool ResolveDue(T item, float now, float delay)
        { return now >= item.NextResolve && now - item.Began >= delay; }
        public static void ScheduleResolve(T item, float now) { item.NextResolve = now + 0.1f; }
        public static StoryResolution Resolution(T item, float now, bool off)
        {
            if (now - item.Began >= 2f) return !item.Classified && !off ? StoryResolution.Drop : StoryResolution.Watch;
            return StoryResolution.Retry;
        }
        public static bool LocalEnded(T item, bool locked, bool running, float now)
        { return now - item.Began >= 60f || (!locked && now - item.Began > 0.5f && (item.SawLock || !running)); }
        public static bool RemoteFinished(T item, bool running, float now)
        { return now - item.Began >= 60f || item.EndAt >= 0 && (!running || now - item.EndAt >= 10f); }
    }
}
