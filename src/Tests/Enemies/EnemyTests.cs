using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Enemies;
using OriCoop.Seams;

static class EnemyTests
{
    static int failures, count;
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static void Run(string name, Action test)
    {
        count++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    sealed class FakeClock : IClock
    {
        public float Time { get; set; }
        public float RealTime { get; set; }
        public int Frame { get; set; }
    }
    sealed class FakeBus : INetBus
    {
        public readonly int Seat;
        public readonly Dictionary<int, FakeBus> Peers;
        readonly Dictionary<byte, Action<BinaryReader>> handlers = new Dictionary<byte, Action<BinaryReader>>();
        public FakeBus(int seat, Dictionary<int, FakeBus> peers) { Seat = seat; Peers = peers; peers.Add(seat, this); }
        public bool Enabled { get { return true; } }
        public bool Connected { get { return true; } }
        public bool IsHost { get { return Seat == 1; } }
        public int Sender { get; private set; }
        public int HostSeat { get { return m_hostSeat; } set { m_hostSeat = value; } } int m_hostSeat = 1;
        public void On(byte type, Action<BinaryReader> handler) { handlers[type] = handler; }
        public void Send(byte type, bool reliable, Action<BinaryWriter> body)
        {
            foreach (KeyValuePair<int, FakeBus> peer in Peers)
                if (peer.Key != Seat && (IsHost || peer.Key == 1)) SendTo(peer.Key, type, reliable, body);
        }
        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            MemoryStream stream = new MemoryStream(); BinaryWriter writer = new BinaryWriter(stream); body(writer); writer.Flush();
            FakeBus peer = Peers[seat]; Action<BinaryReader> handler;
            if (peer.handlers.TryGetValue(type, out handler)) { peer.Sender = Seat; handler(new BinaryReader(new MemoryStream(stream.ToArray()))); }
        }
        public void Forward(params byte[] types) { }
        public event Action SessionStarted { add { } remove { } }
        public event Action SessionEnded { add { } remove { } }
        public event Action<int> PlayerJoined { add { } remove { } }
        public event Action<int> PlayerLeft { add { } remove { } }
    }
    static List<KeyValuePair<int, float>> Distances(params float[] values)
    {
        List<KeyValuePair<int, float>> result = new List<KeyValuePair<int, float>>();
        for (int i = 0; i < values.Length; i += 2) result.Add(new KeyValuePair<int, float>((int)values[i], values[i + 1]));
        return result;
    }
    static int Desired(EnemyAuthority a, int remotes, float now, float current, float local, params float[] distances)
    {
        return EnemyAuthority.Policy.DesiredOwner("scene:actor", delegate {
            return EnemyOwnership.DesiredOwner(remotes, a.Loaded, a.LoadedSeats, a.OwnerSeat, 1, delegate { return now; }, a.OwnerChangedAt,
                delegate(int seat) { return current; }, delegate { return local; }, Distances(distances));
        });
    }
    static void AuthorityTests()
    {
        Run("presence join and leave", delegate { EnemyAuthority a = new EnemyAuthority(); a.Presence(2, true); a.Presence(3, true); a.Presence(2, false); Check(!a.LoadedSeats.Contains(2) && a.LoadedSeats.Contains(3), "presence"); a.SeatLeft(3, false, delegate { return 0; }); Check(a.LoadedSeats.Count == 0, "leave"); });
        // T-147 (replaces "presence refresh clock", which expected unloaded enemies to be re-announced every 2 s):
        // a PC reports an enemy when it loads or unloads it, and refreshes only enemies it has loaded. Enemies it merely
        // heard of (every enemy anyone ever saw) cost nothing, however long the session runs.
        Run("presence refresh only while loaded", delegate {
            FakeClock clock = new FakeClock(); EnemyAuthority a = new EnemyAuthority(); Func<float> now = delegate { return clock.RealTime; };
            Check(!a.PresenceDue(now), "never loaded: nothing to report");
            clock.RealTime = 50; Check(!a.PresenceDue(now), "never loaded: no refresh either");
            a.Loaded = true; Check(a.PresenceDue(now), "load reported"); a.PresenceSent(now);
            clock.RealTime = 51.99f; Check(!a.PresenceDue(now), "early refresh");
            clock.RealTime = 52; Check(a.PresenceDue(now), "loaded enemy refreshed"); a.PresenceSent(now);
            a.Loaded = false; Check(a.PresenceDue(now), "unload reported"); a.PresenceSent(now);
            clock.RealTime = 500; Check(!a.PresenceDue(now), "unloaded enemy not refreshed");
        });
        // T-147: the host used to answer every presence report (one per known enemy every 2 s from each guest) with a
        // full Scan and a full-state grant to every guest, even when nothing changed.
        Run("presence with an unchanged owner sends nothing or one direct grant", delegate {
            EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = 1;
            Func<int> host = delegate { return 1; };
            Check(a.OnPresence(2, true, host) == 1, "guest just loaded it: owner and state to that guest only");
            Check(a.OnPresence(2, true, host) == 0, "refresh with the same owner: nothing");
            Check(a.OnPresence(3, false, host) == 0, "a guest without it loaded: nothing");
            Check(a.OnPresence(2, false, host) == 0, "unload with the same owner: nothing");
            Check(a.LoadedSeats.Count == 0, "loaded seats still tracked");
        });
        Run("presence that moves the owner runs the decision", delegate {
            EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = 1;
            Check(a.OnPresence(2, true, delegate { return 2; }) == 2, "owner should change");
            EnemyAuthority unknown = new EnemyAuthority();
            Check(unknown.OnPresence(2, true, delegate { return 1; }) == 2, "no owner yet");
            EnemyAuthority pending = new EnemyAuthority(); pending.Known = true; pending.PendingTakeover = true;
            Check(pending.OnPresence(2, true, delegate { return 1; }) == 2, "handover in progress: decision path (it waits)");
        });
        Run("two seat host loaded", delegate { EnemyAuthority a = new EnemyAuthority(); a.Loaded = true; a.Presence(2, true); Check(Desired(a, 1, 9, 20, 20, 2, 1) == 1, "host loaded"); });
        Run("two seat guest loaded", delegate { EnemyAuthority a = new EnemyAuthority(); a.Presence(2, true); Check(Desired(a, 1, 9, 20, 20, 2, 1) == 2, "guest loaded"); });
        Run("two seat none loaded", delegate { Check(Desired(new EnemyAuthority(), 1, 9, 20, 20) == 1, "fallback"); });
        Run("four seats nearest loaded", delegate { EnemyAuthority a = new EnemyAuthority(); a.Loaded = true; a.Presence(2, true); a.Presence(3, true); Check(Desired(a, 3, 10, 20, 20, 2, 8, 3, 3, 4, 1) == 3, "unloaded seat chosen"); });
        Run("owner retained during cooldown", delegate { EnemyAuthority a = new EnemyAuthority(); a.Loaded = true; a.Presence(2, true); a.OwnerChangedAt = 9; Check(Desired(a, 2, 10, 20, 20, 2, 1) == 1, "cooldown"); });
        Run("owner retained within margin", delegate { EnemyAuthority a = new EnemyAuthority(); a.Loaded = true; a.Presence(2, true); Check(Desired(a, 2, 10, 10, 10, 2, 9) == 1, "margin"); });
        Run("unavailable owner bypasses cooldown", delegate { EnemyAuthority a = new EnemyAuthority(); a.Presence(3, true); a.OwnerChangedAt = 10; Check(Desired(a, 3, 10, float.PositiveInfinity, float.PositiveInfinity, 3, 8) == 3, "unavailable owner"); });
        Run("no available owner falls back", delegate { EnemyAuthority a = new EnemyAuthority(); Check(Desired(a, 3, 10, float.PositiveInfinity, float.PositiveInfinity, 2, 1) == 1, "fallback"); });
        Run("local transfer increments epoch", delegate { EnemyAuthority a = new EnemyAuthority(); Check(a.BeginDecision(2, 1) == 2, "local decision"); a.CompleteLocalTransfer(delegate { return 4; }); Check(a.Frozen && a.OwnerSeat == 2 && a.Epoch == 2 && a.ExpectFirstSnapshot && a.OwnerChangedAt == 4, "local transfer"); });
        Run("remote transfer waits frozen", delegate { EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = 2; Check(a.BeginDecision(3, 1) == 1 && a.PendingTakeover && a.Frozen && a.PendingEpoch == 2 && a.PendingOwner == 3, "pending handoff"); });
        Run("stale freeze rejected", delegate { EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = 2; a.Epoch = 5; Check(!a.CanFreeze(1, 2, 5) && !a.CanFreeze(3, 2, 6) && a.CanFreeze(1, 2, 6), "freeze epoch or sender"); });
        Run("stale authority rejected", delegate { EnemyAuthority a = new EnemyAuthority(); a.Epoch = 5; Check(!a.CanAcceptGrant(false, 1, 4) && !a.CanAcceptGrant(false, 2, 6) && !a.CanAcceptGrant(true, 1, 6) && a.CanAcceptGrant(false, 1, 5), "grant epoch or sender"); });
        Run("handoff requires expected epoch and sender", delegate { EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = 2; a.BeginDecision(3, 1); Check(!a.CanAcceptHandoff(true, 2, 1, 3) && !a.CanAcceptHandoff(true, 3, 2, 3) && !a.CanAcceptHandoff(true, 2, 2, 4) && a.CanAcceptHandoff(true, 2, 2, 3), "handoff validation"); });
        Run("owner leave advances epoch", delegate { EnemyAuthority a = new EnemyAuthority(); a.OwnerSeat = 2; a.PendingTakeover = true; a.Presence(2, true); Check(a.SeatLeft(2, true, delegate { return 7; }) == 1 && a.OwnerSeat == 1 && a.Epoch == 2 && a.Frozen && !a.PendingTakeover && a.LoadedSeats.Count == 0, "owner leave"); });
        Run("pending target leave cancels transfer", delegate { EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = 2; a.BeginDecision(3, 1); Check(a.SeatLeft(3, true, delegate { return 7; }) == 2 && a.OwnerSeat == 2 && a.Epoch == 2 && !a.Frozen && !a.PendingTakeover, "target leave"); });
        Run("snapshot epoch and sender", delegate { EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = 2; a.Epoch = 5; Check(a.AcceptOwnerMessage(1, 5, 2, true) && !a.AcceptOwnerMessage(1, 4, 2, true) && !a.AcceptOwnerMessage(1, 5, 3, true), "snapshot validation"); a.Frozen = true; Check(!a.AcceptOwnerMessage(1, 5, 2, true), "frozen snapshot"); });
        Run("tick wrap and generations", delegate { Check(EnemyOwnership.AcceptState(1, uint.MaxValue - 1, false, 1, 1, false, false), "tick wrap"); Check(!EnemyOwnership.AcceptState(1, 10, true, 1, 11, false, false), "resurrection"); Check(EnemyOwnership.AcceptState(1, 10, true, 2, 1, false, false), "new generation"); Check(!EnemyOwnership.AcceptState(2, 10, false, 1, 11, false, true), "old generation"); });
        Run("hit from the previous epoch reaching the new owner is queued, not dropped (T-151)", delegate {
            Check(EnemyOwnership.HitDecision(3, 3, false, false, true, 4, 5, true) == 1, "host that just took over dropped the hit");
            Check(EnemyOwnership.HitDecision(3, 3, false, false, true, 4, 5, false) == 1, "guest that just took over dropped the hit");
            Check(EnemyOwnership.HitDecision(3, 3, true, false, true, 4, 5, true) == 0, "already applied hit queued again");
            Check(EnemyOwnership.HitDecision(3, 3, false, false, true, 6, 5, true) == 0, "future epoch accepted");
        });
        Run("hit routing across epochs", delegate { Check(EnemyOwnership.HitDecision(2, 2, false, false, true, 4, 4, true) == 2, "owner apply"); Check(EnemyOwnership.HitDecision(2, 2, false, true, false, 4, 5, true) == 1, "frozen queue"); Check(EnemyOwnership.HitDecision(2, 2, false, false, false, 4, 5, true) == 1, "stale queue"); Check(EnemyOwnership.HitDecision(2, 2, false, false, false, 5, 5, true) == 3, "host forward"); Check(EnemyOwnership.HitDecision(2, 2, false, false, false, 6, 5, true) == 0, "future drop"); Check(EnemyOwnership.HitDecision(2, 1, false, false, true, 5, 5, true) == 0 && EnemyOwnership.HitDecision(2, 2, true, false, true, 5, 5, true) == 0, "generation or duplicate"); });
        Run("life state ordering", delegate { Check(EnemyOwnership.AcceptLife(2, 10, false, false, 2, 10, false), "equal tick allowed"); Check(!EnemyOwnership.AcceptLife(2, 10, false, false, 2, 9, false), "reordered"); Check(EnemyOwnership.AcceptLife(2, 10, false, true, 2, 9, false), "new owner ticks"); Check(!EnemyOwnership.AcceptLife(2, 10, true, true, 2, 11, false), "resurrection"); Check(EnemyOwnership.AcceptLife(2, 10, true, false, 3, 1, false), "respawn"); });
        Run("fake bus remote handoff", delegate {
            Dictionary<int, FakeBus> peers = new Dictionary<int, FakeBus>(); FakeBus host = new FakeBus(1, peers), guest = new FakeBus(2, peers);
            EnemyAuthority h = new EnemyAuthority(), g = new EnemyAuthority(); FakeClock clock = new FakeClock(); clock.RealTime = 42;
            h.Known = g.Known = true; h.OwnerSeat = g.OwnerSeat = 2; byte[] id = new byte[32]; id[0] = 1; id[31] = 2;
            EnemyState source = Sample(); List<string> order = new List<string>();
            host.On(32, delegate(BinaryReader r) {
                order.Add("handoff"); Check(Equal(r.ReadBytes(32), id), "handoff identity"); EnemyTransfer t = EnemyWire.ReadHandoff(r);
                EnemyState received = EnemyWire.ReadState(r, Origin, true, Symbol); Check(received.Health == source.Health, "handoff state");
                if (!h.CanAcceptHandoff(host.IsHost, host.Sender, t.Epoch, t.Owner)) return;
                h.CompleteHandoff(t.Owner, t.Epoch, delegate { return clock.RealTime; }); h.Frozen = false;
                host.Send(31, true, delegate(BinaryWriter w) { w.Write(id); EnemyWire.WriteAuthority(w, 1, h.Epoch, h.OwnerSeat); EnemyWire.WriteVector(w, Origin); EnemyWire.WriteState(w, Origin, received, true, Symbol); });
            });
            guest.On(31, delegate(BinaryReader r) {
                Check(Equal(r.ReadBytes(32), id), "authority identity"); EnemyTransfer t = EnemyWire.ReadAuthority(r);
                if (t.Operation == 0) { order.Add("freeze"); if (!g.CanFreeze(guest.Sender, 2, t.Epoch)) return; g.Frozen = true;
                    guest.Send(32, true, delegate(BinaryWriter w) { w.Write(id); EnemyWire.WriteHandoff(w, t.Epoch, t.Owner); EnemyWire.WriteState(w, Origin, source, true, Symbol); }); }
                else { order.Add("grant"); EnemyVector origin = EnemyWire.ReadVector(r); EnemyState state = EnemyWire.ReadState(r, origin, true, Symbol); Check(state.Applied[1] == 9, "grant state"); if (g.CanAcceptGrant(false, guest.Sender, t.Epoch)) g.ApplyGrant(t.Owner, t.Epoch); }
            });
            Check(h.BeginDecision(1, 1) == 1, "decision"); host.SendTo(2, 31, true, delegate(BinaryWriter w) { w.Write(id); EnemyWire.WriteAuthority(w, 0, h.PendingEpoch, h.PendingOwner); });
            Check(h.OwnerSeat == 1 && h.Epoch == 2 && !h.PendingTakeover && h.OwnerChangedAt == 42 && g.OwnerSeat == 1 && g.Epoch == 2 && !g.Frozen, "handoff");
            Check(order.Count == 3 && order[0] == "freeze" && order[1] == "handoff" && order[2] == "grant", "packet order");
        });
        Run("fake bus four seats directed", delegate { Dictionary<int, FakeBus> peers = new Dictionary<int, FakeBus>(); FakeBus h = new FakeBus(1, peers), p2 = new FakeBus(2, peers), p3 = new FakeBus(3, peers), p4 = new FakeBus(4, peers); int got = 0; p2.On(30, delegate(BinaryReader r) { got += 1; }); p3.On(30, delegate(BinaryReader r) { got += r.ReadInt32(); Check(p3.Sender == 1, "sender"); }); p4.On(30, delegate(BinaryReader r) { got += 10; }); h.SendTo(3, 30, true, delegate(BinaryWriter w) { w.Write(7); }); Check(got == 7, "directed delivery"); });
        Run("four seat grant leave rejoin", delegate {
            Dictionary<int, FakeBus> peers = new Dictionary<int, FakeBus>(); FakeBus host = new FakeBus(1, peers); FakeBus[] guests = { new FakeBus(2, peers), new FakeBus(3, peers), new FakeBus(4, peers) };
            EnemyAuthority h = new EnemyAuthority(); h.Known = true; EnemyAuthority[] states = { new EnemyAuthority(), new EnemyAuthority(), new EnemyAuthority() };
            for (int i = 0; i < guests.Length; i++) { int k = i; states[k].Known = true; guests[k].On(31, delegate(BinaryReader r) { uint epoch = r.ReadUInt32(); int owner = r.ReadByte(); if (states[k].CanAcceptGrant(false, guests[k].Sender, epoch)) states[k].ApplyGrant(owner, epoch); }); }
            h.BeginDecision(3, 1); h.CompleteLocalTransfer(delegate { return 10; });
            host.Send(31, true, delegate(BinaryWriter w) { w.Write(h.Epoch); w.Write((byte)h.OwnerSeat); }); h.Frozen = false;
            foreach (EnemyAuthority state in states) Check(state.OwnerSeat == 3 && state.Epoch == 2, "grant to four seats");
            peers.Remove(3); Check(h.SeatLeft(3, true, delegate { return 12; }) == 1, "leave recovery"); h.Frozen = false;
            host.Send(31, true, delegate(BinaryWriter w) { w.Write(h.Epoch); w.Write((byte)h.OwnerSeat); });
            host.SendTo(2, 31, true, delegate(BinaryWriter w) { w.Write((uint)2); w.Write((byte)3); });
            Check(states[0].OwnerSeat == 1 && states[0].Epoch == 3 && states[2].OwnerSeat == 1, "leave and stale grant");
            peers.Add(3, guests[1]); host.SendTo(3, 31, true, delegate(BinaryWriter w) { w.Write(h.Epoch); w.Write((byte)h.OwnerSeat); });
            Check(states[1].OwnerSeat == 1 && states[1].Epoch == 3, "rejoin refresh");
        });
    }
    static int Main() { VictimHitsTests.Run(Run); AuthorityTests(); EnemyOwnerPolicyTests.Run(Run); WireTests(); Console.WriteLine("Enemies: " + count + " cases, " + failures + " failed"); return failures == 0 ? 0 : 1; }
    static byte[] Bytes(Action<BinaryWriter> write)
    { MemoryStream s = new MemoryStream(); BinaryWriter w = new BinaryWriter(s); write(w); w.Flush(); return s.ToArray(); }
    static BinaryReader Reader(byte[] bytes) { return new BinaryReader(new MemoryStream(bytes)); }
    static bool Equal(byte[] a, byte[] b)
    { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static EnemyState Sample()
    {
        EnemyState s = new EnemyState(); s.Tick = 123; s.Generation = 7; s.Position = new EnemyVector(-497.12f, -18.45f, 0);
        s.VelocityX = -12.34f; s.VelocityY = 9.87f; s.Health = 17.5f; s.Alive = s.Left = s.DealerActive = true;
        s.StateName = "jump"; s.Animation = "frame"; s.AnimationTime = 1.25f; s.Respawn = 2.5f; s.StateTime = 3.5f;
        s.Controller = new byte[] { 1, 2, 3 }; s.TraceMovement = new byte[] { 4, 5 }; s.Applied = new ulong[] { 8, 9 };
        s.ShouldStomp = true; s.HasVisualAngle = true; s.VisualAngle = 237.5f; return s;
    }
    static readonly EnemyVector Origin = new EnemyVector(-500, -20, 0);
    static ushort Symbol(string s) { return s == "jump" ? (ushort)1 : (ushort)2; }
    static string Symbol(ushort s) { return s == 1 ? "jump" : "frame"; }
    static void WireTests()
    {
        Run("full state round trip", delegate { EnemyState s = Sample(); byte[] b = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, Origin, s, true, Symbol); }); BinaryReader r = Reader(b); EnemyState v = EnemyWire.ReadState(r, Origin, true, Symbol); Check(v.Tick == s.Tick && v.Generation == s.Generation && Math.Abs(v.Position.X - s.Position.X) < 0.006f && v.Health == s.Health && v.ShouldStomp && v.HasVisualAngle && v.VisualAngle == s.VisualAngle && v.Controller.Length == 3 && v.TraceMovement.Length == 2 && v.Applied[1] == 9 && r.BaseStream.Position == b.Length, "full state"); });
        Run("snapshot round trip", delegate { EnemyState s = Sample(); byte[] b = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, Origin, s, false, Symbol); }); EnemyState v = EnemyWire.ReadState(Reader(b), Origin, false, Symbol); Check(v.StateName == "jump" && v.Animation == "frame" && v.AnimationTime == 1.25f && v.Alive && v.Left && v.DealerActive && !v.Killed, "snapshot"); });
        Run("wide state round trip", delegate { EnemyState s = Sample(); s.Position = new EnemyVector(900, -20, 3); s.VelocityX = 400; byte[] b = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, Origin, s, false, Symbol); }); EnemyState v = EnemyWire.ReadState(Reader(b), Origin, false, Symbol); Check(b[8] == 1 && v.Position.X == 900 && v.Position.Z == 3 && v.VelocityX == 400, "wide encoding"); });
        Run("snapshot bytes unchanged", delegate { EnemyState s = new EnemyState(); s.Tick = 1; s.Generation = 2; s.Position = new EnemyVector(1, -2, 3); s.VelocityX = 4; s.VelocityY = -5; s.Health = 6; s.Alive = true; s.StateName = "jump"; s.Animation = "frame"; s.AnimationTime = 1.25f; byte[] actual = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, new EnemyVector(), s, false, Symbol); }); byte[] expected = Bytes(delegate(BinaryWriter w) { w.Write((uint)1); w.Write((uint)2); w.Write(false); w.Write((short)100); w.Write((short)-200); w.Write((short)300); w.Write((short)400); w.Write((short)-500); w.Write(6f); w.Write((byte)1); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)1250); w.Write((byte)0); }); Check(Equal(actual, expected), "snapshot layout"); });
        Run("full state bytes unchanged", delegate { EnemyState s = new EnemyState(); s.Tick = 1; s.Generation = 2; s.Position = new EnemyVector(1, -2, 3); s.VelocityX = 4; s.VelocityY = -5; s.Health = 6; s.Killed = true; s.Respawn = 7; s.StateName = "jump"; s.Animation = "frame"; s.StateTime = 8; s.AnimationTime = 9; s.Controller = new byte[] { 10 }; s.TraceMovement = new byte[] { 11, 12 }; s.Applied = new ulong[] { 13 }; s.ShouldStomp = true; s.HasVisualAngle = true; s.VisualAngle = 14; byte[] actual = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, new EnemyVector(), s, true, Symbol); }); byte[] expected = Bytes(delegate(BinaryWriter w) { w.Write((uint)1); w.Write((uint)2); w.Write(false); w.Write((short)100); w.Write((short)-200); w.Write((short)300); w.Write((short)400); w.Write((short)-500); w.Write(6f); w.Write((byte)2); w.Write(7f); w.Write("jump"); w.Write("frame"); w.Write(8f); w.Write(9f); w.Write((ushort)1); w.Write((byte)10); w.Write((ushort)2); w.Write((byte)11); w.Write((byte)12); w.Write(1); w.Write((ulong)13); w.Write((byte)1); w.Write((byte)1); w.Write(14f); }); Check(Equal(actual, expected), "full state layout"); });
        Run("wide state bytes unchanged", delegate { EnemyState s = new EnemyState(); s.Tick = 1; s.Generation = 2; s.Position = new EnemyVector(400, -2, 3); s.VelocityX = 4; s.VelocityY = -5; s.Health = 6; s.Alive = true; s.StateName = "jump"; s.Animation = "frame"; byte[] actual = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, new EnemyVector(), s, false, Symbol); }); byte[] expected = Bytes(delegate(BinaryWriter w) { w.Write((uint)1); w.Write((uint)2); w.Write(true); w.Write(400f); w.Write(-2f); w.Write(3f); w.Write(4f); w.Write(-5f); w.Write(6f); w.Write((byte)1); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)0); w.Write((byte)0); }); Check(Equal(actual, expected), "wide layout"); });
        Run("snapshot quantization rounding", delegate { EnemyState s = new EnemyState(); s.Position = new EnemyVector(0.125f, 0.375f, -0.125f); byte[] b = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, new EnemyVector(), s, false, Symbol); }); BinaryReader r = Reader(b); r.ReadUInt32(); r.ReadUInt32(); Check(!r.ReadBoolean() && r.ReadInt16() == 12 && r.ReadInt16() == 38 && r.ReadInt16() == -12, "bankers rounding"); });
        Run("snapshot symbol write order", delegate { EnemyState s = Sample(); MemoryStream stream = new MemoryStream(); BinaryWriter w = new BinaryWriter(stream); List<string> calls = new List<string>(); EnemyWire.WriteState(w, Origin, s, false, delegate(string value) { calls.Add(value + ":" + stream.Position); return Symbol(value); }); Check(calls.Count == 2 && calls[0] == "jump:24" && calls[1] == "frame:26", "symbol order"); });
        Run("truncated full state rejected", delegate { EnemyState s = Sample(); byte[] b = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteState(w, Origin, s, true, Symbol); }); Array.Resize(ref b, b.Length - 1); bool rejected = false; try { EnemyWire.ReadState(Reader(b), Origin, true, Symbol); } catch (EndOfStreamException) { rejected = true; } Check(rejected, "truncated state"); });
        Run("hit bytes and round trip", delegate { EnemyHit h = new EnemyHit(); h.Epoch = 8; h.Generation = 7; h.Id = 0x200000003UL; h.Damage = new EnemyDamage(); h.Damage.Amount = 3; h.Damage.Type = 5; h.Damage.ForceX = 1; h.Damage.ForceY = 2; h.Damage.Position = new EnemyVector(-4, -5, 6); byte[] actual = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteHit(w, h); }); byte[] expected = Bytes(delegate(BinaryWriter w) { w.Write((uint)8); w.Write((uint)7); w.Write(0x200000003UL); w.Write(3f); w.Write(5); w.Write(1f); w.Write(2f); w.Write(-4f); w.Write(-5f); w.Write(6f); }); EnemyHit v = EnemyWire.ReadHit(Reader(actual)); Check(Equal(actual, expected) && v.Id == h.Id && v.Damage.Position.Z == 6 && v.Damage.Type == 5, "hit layout"); });
        Run("transfer header bytes unchanged", delegate { byte[] authority = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteAuthority(w, 1, 0x12345678, 3); }); byte[] handoff = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteHandoff(w, 0x12345678, 3); }); Check(Equal(authority, new byte[] { 1, 0x78, 0x56, 0x34, 0x12, 3 }) && Equal(handoff, new byte[] { 0x78, 0x56, 0x34, 0x12, 3 }), "transfer layout"); });
        Run("shot bytes and round trip", delegate { EnemyShot s = new EnemyShot(); s.Epoch = 2; s.Id = 42; s.Prefab = "shot"; s.Position = new EnemyVector(1, 2, 3); s.Velocity = new EnemyVector(4, 5, 6); s.Gravity = 7; s.Lifetime = 8; s.Damage = 9; s.Type = 10; byte[] actual = Bytes(delegate(BinaryWriter w) { EnemyWire.WriteShot(w, s); }); byte[] expected = Bytes(delegate(BinaryWriter w) { w.Write((uint)2); w.Write((ulong)42); w.Write("shot"); for (int i = 1; i <= 9; i++) w.Write((float)i); w.Write(10); }); EnemyShot v = EnemyWire.ReadShot(Reader(actual)); Check(Equal(actual, expected) && v.Prefab == "shot" && v.Velocity.Y == 5 && v.Type == 10, "shot layout"); });
        Run("negative damage rejected", delegate { byte[] b = Bytes(delegate(BinaryWriter w) { w.Write(-1f); w.Write(0); for (int i = 0; i < 5; i++) w.Write(0f); }); bool rejected = false; try { EnemyWire.ReadDamage(Reader(b)); } catch (InvalidDataException) { rejected = true; } Check(rejected, "negative damage"); });
        // T-023: a player who starts pushing a stone another player only holds keeps simulating it until the arbiter's
        // grant comes back (request + grant = one RTT plus server and send delays), then owns it without a gap.
        Run("stone takeover survives the grant round trip", delegate {
            OriCoop.Authority.StoneLeaseSimulation sim = new OriCoop.Authority.StoneLeaseSimulation();
            const float rtt = 150f, grantAt = 0.25f;
            Check(!sim.Allows(1, 1, 2, -0.1f, rtt, false), "not pushing: the holder's stone is followed");
            for (int f = 0; f <= 30; f++)
            {
                float t = f / 60f; bool granted = t >= grantAt;
                Check(sim.Allows(granted ? 2 : 1, granted ? 2u : 1u, 2, t, rtt, true), "pushing stone simulated without a gap at t=" + t.ToString("F3"));
            }
        });
        Run("stone takeover denied stops predicting", delegate {
            OriCoop.Authority.StoneLeaseSimulation sim = new OriCoop.Authority.StoneLeaseSimulation();
            bool late = false;
            for (int f = 0; f <= 90; f++) { float t = f / 60f; bool allowed = sim.Allows(1, 1, 2, t, 150f, true); if (t >= 1f) late |= allowed; }
            Check(!late, "a never granted takeover is dropped within 1 s");
        });
        // T-049: two players pushing the same stone the same way move it faster (x1.5); anything else keeps normal speed.
        Run("co-push same direction speeds up", delegate {
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, 1, 0.05f) == 1.5f, "both right");
            Check(OriCoop.Authority.CoPushRule.Multiplier(-1, -1, 0.2f) == 1.5f, "both left");
        });
        Run("co-push otherwise normal speed", delegate {
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, -1, 0.05f) == 1f, "opposite directions");
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, 0, 0.05f) == 1f, "partner only holds");
            Check(OriCoop.Authority.CoPushRule.Multiplier(0, 1, 0.05f) == 1f, "only the partner pushes");
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, 1, 0.31f) == 1f, "partner's report is stale");
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, 1, -1f) == 1f, "no report yet");
        });
        Run("co-push with several remote pushers (T-115)", delegate {
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, new[] { 1, -1 }, new[] { 0.05f, 0.05f }) == 1.5f, "a third pushing the other way keeps the boost");
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, new[] { -1, 1 }, new[] { 0.05f, 0.05f }) == 1.5f, "order of reports does not matter");
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, new[] { -1, 0 }, new[] { 0.05f, 0.05f }) == 1f, "nobody pushes our way");
            Check(OriCoop.Authority.CoPushRule.Multiplier(1, new[] { 1 }, new[] { 0.5f }) == 1f, "the only same-way report is stale");
            Check(OriCoop.Authority.CoPushRule.Multiplier(0, new[] { 1, 1 }, new[] { 0.05f, 0.05f }) == 1f, "we do not push");
            Check(OriCoop.Authority.CoPushRule.Multiplier(-1, new int[0], new float[0]) == 1f, "no remote reports");
        });
        Run("first snapshot from new owner", delegate { Check(!EnemyOwnership.AcceptState(1, 1000, false, 1, 100, false, false) && EnemyOwnership.AcceptState(1, 1000, false, 1, 100, false, true), "peer tick clocks"); });
    }
}
