using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Story
{
    internal struct StoryPoint
    {
        public float X, Y, Z;
        public StoryPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    // Pure boundary shared by IStoryHost and the unit-test fake.
    internal interface IIntroHost
    {
        bool IntroAuthority { get; }
        bool IntroPlaying { get; }
        bool IntroP1Free { get; }
        bool IntroAtTitle { get; }
        StoryPoint IntroP1Position { get; }
        int[] IntroSeats { get; }
        // Seats taken in the online session's seat table right now, every device's extra local players included; null when
        // unknown (offline, no table yet, C-server). Unlike IntroSeats it does not wait for an Ori to exist. (T-139)
        int[] IntroSessionSeats { get; }
        bool CanPlaceIntroSeat(int seat);
        IntroState RemoteIntro { get; }
        // A guest has a session with the host right now (not disconnected / waiting to reconnect). (T-106)
        bool IntroHostSession { get; }
        bool TryIntroGround(StoryPoint candidate, out StoryPoint spot);
        // Held means neutral input, frozen position, and hidden Ori/companion (not just a movement lock).
        void SetIntroHeld(int seat, bool held);
        void PlaceIntroSeat(int seat, StoryPoint position);
        void PublishIntro(IntroState state);
    }

    internal sealed class IntroState
    {
        public uint Generation;
        public bool Active, Woke;
        public StoryPoint[] Spots;
        // T-105: seats (bit seat-1) that joined or left after the wake. They were not part of it: never held, never placed.
        public byte Late;
    }

    internal static class IntroWire
    {
        public static void Write(BinaryWriter w, IntroState state)
        {
            w.Write(state.Generation); w.Write(state.Active); w.Write(state.Woke);
            if (state.Woke) { foreach (StoryPoint p in state.Spots) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); } w.Write(state.Late); }
        }
        public static IntroState Read(BinaryReader r)
        {
            IntroState state = new IntroState { Generation = r.ReadUInt32(), Active = r.ReadBoolean(), Woke = r.ReadBoolean() };
            if (state.Active && state.Woke) throw new InvalidDataException("invalid intro phase");
            if (state.Woke)
            {
                state.Spots = new StoryPoint[4];
                for (int i = 0; i < 4; i++)
                {
                    StoryPoint p = new StoryPoint(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    if (float.IsNaN(p.X) || float.IsInfinity(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.Y)
                        || float.IsNaN(p.Z) || float.IsInfinity(p.Z)) throw new InvalidDataException("invalid intro spot");
                    state.Spots[i] = p;
                }
                state.Late = r.ReadByte();
            }
            return state;
        }
    }

    internal sealed class IntroRule
    {
        // Fixed identities, independent of player count or which seats already have an Ori.
        // 3.5-unit slots leave tolerance for ground settling while guaranteeing a 3-unit gap.
        public const float MinimumSpacing = 3f;
        static readonly float[] Offsets = { 0f, 3.5f, -3.5f, 7f };
        IntroState state = new IntroState();
        readonly HashSet<int> placed = new HashSet<int>();
        bool playing;
        // T-139: seats this PC itself saw join or leave the seat table after the wake (kept for this wake only). A guest
        // sees its own new extra player in the table before the host's snapshot with the late bit arrives; this bridges
        // that gap and survives an older snapshot.
        byte seenLate;
        readonly bool[] inSession = new bool[5];
        bool sessionKnown;
        uint lateGeneration;
        public bool Active { get { return state.Active; } }
        public bool HoldsSeat(int seat)
        { return seat >= 2 && seat <= 4 && (state.Active || state.Woke && !placed.Contains(seat) && !IsLate(seat)); }
        static byte Bit(int seat) { return (byte)(1 << (seat - 1)); }
        bool IsLate(int seat) { return ((state.Late | seenLate) & Bit(seat)) != 0; }
        // T-139: PlayerJoined / PlayerLeft fire only for a device's primary seat. An extra local player added (F8) or
        // removed after the wake changes only the seat table, so compare it with the last tick's. The first look at a
        // table (session start) marks nothing; seats that appear during the intro (Active) are part of it.
        void WatchSessionSeats(IIntroHost host)
        {
            if (state.Generation != lateGeneration || !state.Woke || state.Active) { seenLate = 0; lateGeneration = state.Generation; }
            int[] seats = host.IntroSessionSeats;
            if (seats == null) { sessionKnown = false; return; }
            bool[] now = new bool[5];
            foreach (int seat in seats) if (seat >= 1 && seat <= 4) now[seat] = true;
            for (int seat = 2; seat <= 4; seat++)
            {
                if (sessionKnown && now[seat] != inSession[seat] && state.Woke && !state.Active)
                {
                    if (host.IntroAuthority) MarkLate(seat);   // first: MarkLate skips seats that are already late
                    seenLate |= Bit(seat);
                }
                inSession[seat] = now[seat];
            }
            sessionKnown = true;
        }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        // Host: a seat connected (or reconnected) or left after the wake. (T-105) The host stays "woke" with the spots
        // until it returns to the title, so without this a player joining an hour later was held, hidden and moved to
        // the wake spot. A seat that leaves after the wake gives up its placement too, so a rejoin is a late join.
        // Seats that join during the intro (Active) are part of it and keep waiting for the wake.
        public void SeatJoined(int seat) { MarkLate(seat); }
        public void SeatLeft(int seat) { MarkLate(seat); }
        void MarkLate(int seat)
        {
            if (seat < 2 || seat > 4 || !state.Woke || state.Active || IsLate(seat)) return;
            // A new snapshot object, so PublishIntro sends it right away.
            state = new IntroState { Generation = state.Generation, Woke = true, Spots = state.Spots, Late = (byte)(state.Late | Bit(seat)) };
        }
        public void Reset(IIntroHost host)
        {
            for (int seat = 1; seat <= 4; seat++) host.SetIntroHeld(seat, false);
            state = new IntroState(); placed.Clear(); playing = false;
            seenLate = 0; sessionKnown = false;
        }
        public void Tick(IIntroHost host)
        {
            if (host.IntroAuthority)
            {
                if (host.IntroPlaying && !playing)
                {
                    state = new IntroState { Active = true, Generation = state.Generation + 1 };
                    placed.Clear();
                }
                playing = host.IntroPlaying;
                // Logos end at the title, not at a new-game wake. A later save load is not an intro.
                if (!playing && host.IntroAtTitle && (state.Active || state.Woke))
                {
                    state = new IntroState { Generation = state.Generation + 1 }; placed.Clear();
                }
                // SetToGame/SkipPrologue may precede Ori's creation, scene load and wake animation.
                if (state.Active && !playing && host.IntroP1Free)
                {
                    StoryPoint anchor = host.IntroP1Position;
                    StoryPoint[] spots = new StoryPoint[4]; spots[0] = anchor;
                    for (int i = 1; i < 4; i++)
                    {
                        StoryPoint candidate = new StoryPoint(anchor.X + Offsets[i], anchor.Y, anchor.Z), ground;
                        spots[i] = host.TryIntroGround(candidate, out ground) && Finite(ground.X) && Finite(ground.Y) && Finite(ground.Z)
                            && Math.Abs(ground.X - candidate.X) < 0.1f && Math.Abs(ground.Y - anchor.Y) <= 4f ? ground : candidate;
                    }
                    state = new IntroState { Generation = state.Generation, Woke = true, Spots = spots };
                }
                host.PublishIntro(state);
            }
            else
            {
                // Guests skip their own new-game movie; only a host snapshot can release them. That wait needs a host:
                // without a session (host gone, reconnecting) the guest is not held (T-106; its seat even falls back to 2).
                IntroState remote = host.RemoteIntro ?? (host.IntroHostSession ? new IntroState { Active = true } : new IntroState());
                // Reliable snapshots may arrive late: an old hold must not rewind an observed wake.
                if (remote.Generation > state.Generation || remote.Generation == state.Generation && !(state.Woke && !remote.Woke))
                {
                    if (state.Generation != remote.Generation) placed.Clear();
                    state = remote;
                }
            }
            WatchSessionSeats(host);
            if (state.Woke && !state.Active)
                foreach (int seat in host.IntroSeats)
                    if (seat >= 2 && seat <= 4 && !placed.Contains(seat) && !IsLate(seat) && host.CanPlaceIntroSeat(seat))
                    {
                        // A guest may still be restoring the host save when wake arrives. Do not reveal it
                        // or consume its one placement until that restore can no longer overwrite the spot.
                        host.SetIntroHeld(seat, false);
                        host.PlaceIntroSeat(seat, state.Spots[seat - 1]); placed.Add(seat);
                    }
            for (int seat = 1; seat <= 4; seat++) host.SetIntroHeld(seat, HoldsSeat(seat));
        }
    }

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
        public float Began, NextResolve, EndAt = -1f, NextSceneLoad;
        public int SceneLoads;
        public bool SawLock, Resolved, Watch, Skipped, SkipApplied, Classified = true;
        public bool TripWait;   // T-185: its mirror start waits for this PC's own scene trip (SceneTripRule)
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
        {
            // T-185: a start that only waits for this PC's own scene trip is not "watching" (own Ori stays as the
            // trip leaves it; natively nothing hides it there).
            get { foreach (T item in Remote.Values) if ((!item.Resolved && item.Classified && !item.TripWait) || item.Watch) return true; return false; }
        }
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
        // A resolve attempt failed: may it load the scenes around the story? (T-066) The 0.1 s resolve retry is cheap, but
        // loading the scenes each time made 20 loads in the 2 s window. Now: at once, then after 0.5 s and 1 s more; that is all.
        public const int MaxSceneLoads = 3;
        public static bool SceneLoadDue(T item, float now)
        {
            if (item.SceneLoads >= MaxSceneLoads || now < item.NextSceneLoad) return false;
            item.SceneLoads++; item.NextSceneLoad = now + 0.5f * item.SceneLoads;
            return true;
        }
        public static StoryResolution Resolution(T item, float now, bool off)
        {
            if (now - item.Began >= 2f) return !item.Classified && !off ? StoryResolution.Drop : StoryResolution.Watch;
            return StoryResolution.Retry;
        }
        public static bool LocalEnded(T item, bool locked, bool running, float now)
        { return now - item.Began >= 60f || (!locked && now - item.Began > 0.5f && (item.SawLock || !running)); }
        // T-196: a story sequence that still has a takeover step ahead (a lock, the letterbox, a story text) is not
        // over because no lock is on right now. Sein's pickup: "08 Sein Run To Target" releases its lock, "11 Lock
        // player input" comes one physics frame later; the story was declared over 0.9 s into a 22 s script.
        public static bool LocalEnded(T item, bool locked, bool running, bool takeoverAhead, float now)
        { return now - item.Began >= 60f || !(running && takeoverAhead) && LocalEnded(item, locked, running, now); }
        // index: the last action the sequence has run (ActionSequence.Index); lastTakeover: the index of its last
        // takeover action (-1: none).
        public static bool TakeoverAhead(bool running, int index, int lastTakeover) { return running && index < lastTakeover; }
        public static bool RemoteFinished(T item, bool running, float now)
        { return now - item.Began >= 60f || item.EndAt >= 0 && (!running || now - item.EndAt >= 10f); }
    }
}
