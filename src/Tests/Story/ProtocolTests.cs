using System;
using System.IO;
using System.Collections.Generic;
using OriCoop.Seams;
using OriCoop.Story;

static partial class StoryTests
{
    sealed class Clock : IClock
    {
        public float Time { get { return RealTime; } }
        public float RealTime { get; set; }
        public int Frame { get; set; }
    }
    sealed class Network
    {
        public readonly Dictionary<int, Bus> Seats = new Dictionary<int, Bus>();
        public Bus Add(int seat)
        {
            Bus bus = new Bus(this, seat); Seats.Add(seat, bus);
            foreach (Bus other in Seats.Values) if (other != bus) other.Join(seat);
            return bus;
        }
        public void Leave(int seat)
        {
            Seats.Remove(seat);
            foreach (Bus other in Seats.Values) other.Left(seat);
        }
        public void Deliver(int sender, int target, byte type, byte[] data)
        {
            Bus bus;
            if (Seats.TryGetValue(target, out bus) && sender != target) bus.Receive(sender, type, data);
        }
    }
    sealed class Bus : INetBus
    {
        readonly Network network;
        public readonly int Seat;
        readonly Action<BinaryReader>[] handlers = new Action<BinaryReader>[256];
        public int Sender { get; private set; }
        public int HostSeat { get { return m_hostSeat; } set { m_hostSeat = value; } } int m_hostSeat = 1;
        public bool Enabled { get { return true; } }
        public bool Connected { get { return true; } }
        public bool IsHost { get { return Seat == 1; } }
        public event Action SessionStarted { add { } remove { } }
        public event Action SessionEnded { add { } remove { } }
        public event Action<int> PlayerJoined;
        public event Action<int> PlayerLeft;
        public Bus(Network n, int seat) { network = n; Seat = seat; }
        public void Join(int seat) { if (PlayerJoined != null) PlayerJoined(seat); }
        public void Left(int seat) { if (PlayerLeft != null) PlayerLeft(seat); }
        public void On(byte type, Action<BinaryReader> handler) { handlers[type] = handler; }
        public void Forward(params byte[] types) { }
        static byte[] Encode(Action<BinaryWriter> body)
        { MemoryStream m = new MemoryStream(); BinaryWriter w = new BinaryWriter(m); body(w); w.Flush(); return m.ToArray(); }
        public void Send(byte type, bool reliable, Action<BinaryWriter> body)
        {
            byte[] data = Encode(body);
            if (!IsHost) network.Deliver(Seat, 1, type, data);
            else foreach (int seat in new List<int>(network.Seats.Keys)) network.Deliver(Seat, seat, type, data);
        }
        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        { network.Deliver(Seat, seat, type, Encode(body)); }
        public void Relay(int sender, byte type, byte[] body)
        { foreach (int seat in new List<int>(network.Seats.Keys)) if (seat != Seat) network.Deliver(sender, seat, type, body); }
        public void Receive(int sender, byte type, byte[] body)
        {
            Action<BinaryReader> handler = handlers[type];
            if (handler == null) return;
            int previous = Sender; Sender = sender;
            try { handler(new BinaryReader(new MemoryStream(body))); }
            finally { Sender = previous; }
        }
    }
    sealed class Run : StoryEntry { }
    sealed class StoryPeer
    {
        public readonly StoryLedger<Run> Ledger = new StoryLedger<Run>();
        public readonly Bus Bus;
        readonly Clock clock;
        public StoryPeer(Bus bus, Clock c)
        {
            Bus = bus; clock = c;
            bus.On(50, StartReceived); bus.On(51, EndReceived); bus.On(52, SkipReceived);
            bus.PlayerLeft += delegate(int seat) {
                foreach (ulong key in Ledger.RemoteSeatTokens(seat)) Ledger.Remote.Remove(key);
                foreach (ulong key in Ledger.AcceptedSeatTokens(seat)) Ledger.Accepted.Remove(key);
            };
        }
        static byte[] Bytes(BinaryReader r) { return ((MemoryStream)r.BaseStream).ToArray(); }
        void StartReceived(BinaryReader r)
        {
            byte[] bytes = Bytes(r); StoryStart data = StoryWire.ReadStart(r);
            Run run = new Run { Sender = Bus.Sender, Token = data.Token, Key = data.Path, Classified = data.Classified };
            if (!Ledger.CanReceive(run, Bus.IsHost, clock.RealTime)) return;
            if (!Bus.IsHost)
            {
                List<ulong> superseded = new List<ulong>();
                foreach (KeyValuePair<ulong, Run> pair in Ledger.Local) if (pair.Value.Key == run.Key) superseded.Add(pair.Key);
                foreach (ulong key in superseded) End(key);
            }
            Ledger.TrackRemote(run, Bus.IsHost, clock.RealTime);
            if (Bus.IsHost) Bus.Relay(run.Sender, 50, bytes);
        }
        void EndReceived(BinaryReader r)
        {
            byte[] bytes = Bytes(r); ulong key = StoryLedger<Run>.TokenKey(Bus.Sender, StoryWire.ReadToken(r));
            if (!(Bus.IsHost ? Ledger.Accepted : Ledger.Remote).ContainsKey(key)) return;
            if (Bus.IsHost) Bus.Relay(Bus.Sender, 51, bytes);
            Ledger.Accepted.Remove(key); Ledger.Remote.Remove(key);
        }
        void SkipReceived(BinaryReader r)
        {
            byte[] bytes = Bytes(r); ulong key = StoryLedger<Run>.TokenKey(Bus.Sender, StoryWire.ReadToken(r));
            if (!(Bus.IsHost ? Ledger.Accepted : Ledger.Remote).ContainsKey(key)) return;
            if (Bus.IsHost) Bus.Relay(Bus.Sender, 52, bytes);
            Run run; if (Ledger.Remote.TryGetValue(key, out run)) Ledger.Skip(run);
        }
        public ulong Start(string key)
        {
            Run run = new Run { Key = key, Classified = true };
            if (!Ledger.PrepareLocal(run, Bus.Seat, Bus.IsHost, clock.RealTime)) return 0;
            Ledger.TrackLocal(run, Bus.IsHost);
            StoryStart data = new StoryStart { Token = run.Token, Scene = Id(1), Id = Id(2), Path = key, State = "", Classified = true };
            Bus.Send(50, true, delegate(BinaryWriter w) { StoryWire.WriteStart(w, data); });
            return StoryLedger<Run>.TokenKey(run.Sender, run.Token);
        }
        public void End(ulong key)
        {
            if (Ledger.Local.ContainsKey(key)) Bus.Send(51, true, delegate(BinaryWriter w) { StoryWire.WriteToken(w, (uint)key); });
            Ledger.EndLocal(key);
        }
        public void Skip(ulong key) { Bus.Send(52, true, delegate(BinaryWriter w) { StoryWire.WriteToken(w, (uint)key); }); }
    }
    sealed class ProgressPeer
    {
        public readonly Bus Bus;
        public readonly ProgressInbox Inbox = new ProgressInbox();
        public readonly ProgressQueue<Guid> Queue = new ProgressQueue<Guid>();
        public bool SkillEnabled = true;
        public ProgressPeer(Bus bus, Clock clock)
        {
            Bus = bus;
            bus.On(20, delegate(BinaryReader r) { Inbox.Receive(bus.IsHost, ProgressSnapshot.Read(r), clock.RealTime); });
            bus.On(21, delegate(BinaryReader r) {
                ProgressAction a = ProgressAction.Read(r);
                if (bus.IsHost) Queue.Enqueue(bus.Sender, a);
            });
            bus.On(22, delegate(BinaryReader r) { SkillEnabled = ProgressWire.ApplySkillStatus(bus.IsHost, ProgressWire.ReadSkillStatus(r), SkillEnabled); });
        }
        public void Snapshot(ProgressSnapshot snapshot) { Bus.Send(20, true, snapshot.Write); }
        public void Action(ProgressAction action) { Bus.Send(21, true, action.Write); }
        public void Skill(bool enabled) { Bus.Send(22, true, delegate(BinaryWriter w) { ProgressWire.WriteSkillStatus(w, enabled); }); }
    }
    static byte[] Id(byte b) { byte[] bytes = new byte[16]; for (int i = 0; i < 16; i++) bytes[i] = (byte)(b + i); return bytes; }
    static byte[] Wire(Action<BinaryWriter> write)
    { MemoryStream m = new MemoryStream(); BinaryWriter w = new BinaryWriter(m); write(w); w.Flush(); return m.ToArray(); }
    static BinaryReader Reader(byte[] bytes) { return new BinaryReader(new MemoryStream(bytes)); }
    static void BytesEqual(byte[] a, byte[] b)
    { Equal(Convert.ToBase64String(a), Convert.ToBase64String(b)); }
    static StoryPeer Peer(Network n, Clock c, int seat) { return new StoryPeer(n.Add(seat), c); }

    static void StoryCases()
    {
        Check("host story mirrored guest", delegate {
            Network n = new Network(); Clock c = new Clock(); StoryPeer h = Peer(n, c, 1), g = Peer(n, c, 2);
            ulong key = h.Start("a"); Equal(true, g.Ledger.Remote.ContainsKey(key)); Equal(true, g.Ledger.Watching);
        });
        Check("story skip", delegate {
            Network n = new Network(); Clock c = new Clock(); StoryPeer h = Peer(n, c, 1), g = Peer(n, c, 2);
            ulong key = h.Start("a"); h.Skip(key); Equal(true, g.Ledger.Remote[key].Skipped);
        });
        Check("story end", delegate {
            Network n = new Network(); Clock c = new Clock(); StoryPeer h = Peer(n, c, 1), g = Peer(n, c, 2);
            ulong key = h.Start("a"); h.End(key); Equal(0, g.Ledger.Remote.Count); Equal(false, g.Ledger.Watching);
        });
        Check("late join does not replay prior start", delegate {
            Network n = new Network(); Clock c = new Clock(); StoryPeer h = Peer(n, c, 1); h.Start("a");
            StoryPeer g = Peer(n, c, 2); Equal(0, g.Ledger.Remote.Count);
        });
        Check("player leaves releases story", delegate {
            Network n = new Network(); Clock c = new Clock(); StoryPeer h = Peer(n, c, 1), g = Peer(n, c, 2);
            g.Start("a"); n.Leave(2); Equal(0, h.Ledger.Remote.Count); Equal(0, h.Ledger.Accepted.Count);
        });
        Check("leaving seat preserves other stories", delegate {
            StoryLedger<Run> l = new StoryLedger<Run>();
            Run a = new Run { Sender = 2, Token = 1, Key = "a" }, b = new Run { Sender = 3, Token = 1, Key = "b" };
            l.TrackRemote(a, true, 0f); l.TrackRemote(b, true, 0f);
            foreach (ulong key in l.RemoteSeatTokens(2)) l.Remote.Remove(key);
            foreach (ulong key in l.AcceptedSeatTokens(2)) l.Accepted.Remove(key);
            Equal(1, l.Remote.Count); Equal(3, l.Remote[StoryLedger<Run>.TokenKey(3, 1)].Sender); Equal(1, l.Accepted.Count);
        });
        Check("host wins concurrent matching start", delegate {
            StoryLedger<Run> host = new StoryLedger<Run>(); Run local = new Run { Key = "a" };
            host.PrepareLocal(local, 1, true, 0f); host.TrackLocal(local, true);
            Equal(false, host.CanReceive(new Run { Key = "a", Sender = 2, Token = 1 }, true, 0f));
            StoryLedger<Run> guest = new StoryLedger<Run>(); Run own = new Run { Key = "a" };
            guest.PrepareLocal(own, 2, false, 0f); guest.TrackLocal(own, false);
            Run incoming = new Run { Key = "a", Sender = 1, Token = 1 };
            Equal(true, guest.CanReceive(incoming, false, 0f)); guest.EndLocal(StoryLedger<Run>.TokenKey(2, own.Token));
            guest.TrackRemote(incoming, false, 0f); Equal(true, incoming.Resolved); Equal(false, guest.Watching);
        });
        Check("third and fourth seat relay preserves origin", delegate {
            Network n = new Network(); Clock c = new Clock(); StoryPeer h = Peer(n, c, 1), g = Peer(n, c, 2), p3 = Peer(n, c, 3), p4 = Peer(n, c, 4);
            ulong key = p3.Start("a"); Equal(true, h.Ledger.Remote.ContainsKey(key)); Equal(true, g.Ledger.Remote.ContainsKey(key));
            Equal(true, p4.Ledger.Remote.ContainsKey(key)); Equal(3, p4.Ledger.Remote[key].Sender);
            p3.Skip(key); Equal(true, p4.Ledger.Remote[key].Skipped); p3.End(key); Equal(0, p4.Ledger.Remote.Count);
        });
        // T-066: a story the other PC started that this PC cannot find is retried every 0.1 s for 2 s; each failed try
        // used to load the scenes around it again (20 loads). The scene load is the expensive part: few, spread out.
        Check("story unresolved retries load the scenes a few times with backoff", delegate {
            Run r = new Run { Key = "a", Began = 0f }; int loads = 0; float first = -1f, last = -1f;
            for (int f = 0; f <= 130; f++)
            {
                float t = f / 60f;
                if (StoryLedger<Run>.Resolution(r, t, false) != StoryResolution.Retry) break;
                if (!StoryLedger<Run>.ResolveDue(r, t, 0f)) continue;
                StoryLedger<Run>.ScheduleResolve(r, t);
                if (StoryLedger<Run>.SceneLoadDue(r, t)) { loads++; if (first < 0f) first = t; last = t; }
            }
            Equal(true, first >= 0f && first < 0.05f);   // the first failed try still loads at once
            Equal(true, loads >= 2 && loads <= 3);       // was 20
            Equal(true, last >= 1f);                     // spread over the 2 s window, not bunched up
        });
        Check("story duplicate key suppressed for three seconds", delegate {
            StoryLedger<Run> l = new StoryLedger<Run>(); Clock c = new Clock();
            Run first = new Run { Key = "a" }; Equal(true, l.PrepareLocal(first, 1, true, c.RealTime)); l.TrackLocal(first, true);
            c.RealTime = 2.99f; Equal(false, l.PrepareLocal(new Run { Key = "a" }, 1, true, c.RealTime));
            l.Accepted.Clear(); c.RealTime = 3f; Equal(true, l.PrepareLocal(new Run { Key = "a" }, 1, true, c.RealTime));
        });
        Check("story unresolved classification controls watching", delegate {
            StoryLedger<Run> l = new StoryLedger<Run>(); Run r = new Run { Key = "a", Sender = 2, Token = 1, Classified = false };
            l.TrackRemote(r, false, 0f); Equal(false, l.Watching); r.Classified = true; Equal(true, l.Watching);
            r.Resolved = true; Equal(false, l.Watching); r.Watch = true; Equal(true, l.Watching);
        });
        Check("story local timeout and lock rules", delegate {
            Run r = new Run { Began = 0f };
            Equal(false, StoryLedger<Run>.LocalEnded(r, false, true, 0.6f));
            Equal(true, StoryLedger<Run>.LocalEnded(r, false, false, 0.6f));
            Equal(false, StoryLedger<Run>.LocalEnded(r, true, false, 1f));
            r.SawLock = true; Equal(false, StoryLedger<Run>.LocalEnded(r, false, true, 0.5f));
            Equal(true, StoryLedger<Run>.LocalEnded(r, false, true, 0.51f));
            Equal(true, StoryLedger<Run>.LocalEnded(r, true, true, 60f));
        });
        Check("story deferred end timeout", delegate {
            Run r = new Run { Began = 1f, EndAt = 2f };
            Equal(false, StoryLedger<Run>.RemoteFinished(r, true, 11.99f));
            Equal(true, StoryLedger<Run>.RemoteFinished(r, true, 12f));
            Equal(true, StoryLedger<Run>.RemoteFinished(r, false, 3f));
        });
        Check("story delayed resolve retries on clock", delegate {
            Clock c = new Clock(); Run r = new Run { Began = 1f }; c.RealTime = 1.74f;
            Equal(false, StoryLedger<Run>.ResolveDue(r, c.RealTime, 0.75f));
            c.RealTime = 1.75f; Equal(true, StoryLedger<Run>.ResolveDue(r, c.RealTime, 0.75f));
            StoryLedger<Run>.ScheduleResolve(r, c.RealTime); c.RealTime = 1.8f;
            Equal(false, StoryLedger<Run>.ResolveDue(r, c.RealTime, 0.75f));
            c.RealTime = 1.85f; Equal(true, StoryLedger<Run>.ResolveDue(r, c.RealTime, 0.75f));
        });
        Check("story unclassified and classified resolve timeout", delegate {
            Run r = new Run { Began = 1f, Classified = false };
            Equal(StoryResolution.Retry, StoryLedger<Run>.Resolution(r, 2.99f, false));
            Equal(StoryResolution.Drop, StoryLedger<Run>.Resolution(r, 3f, false));
            Equal(StoryResolution.Watch, StoryLedger<Run>.Resolution(r, 3f, true));
            r.Classified = true; Equal(StoryResolution.Watch, StoryLedger<Run>.Resolution(r, 3f, false));
        });
        Check("story defer records only first end", delegate {
            Run r = new Run(); Equal(true, StoryLedger<Run>.DeferEnd(r, 2f)); Equal(2f, r.EndAt);
            Equal(false, StoryLedger<Run>.DeferEnd(r, 5f)); Equal(2f, r.EndAt);
        });
        Check("story skip only owned mirrors", delegate {
            Run r = new Run(); Equal(false, StoryLedger<Run>.CanSkip(r, false));
            Equal(true, StoryLedger<Run>.CanSkip(r, true)); r.SkipApplied = true;
            Equal(false, StoryLedger<Run>.CanSkip(r, true));
        });
        Check("story start wire and legacy classification", delegate {
            StoryStart s = new StoryStart { Token = 19, Kind = 1, Scene = Id(1), Id = Id(20), Path = "路径", State = "state", X = 1f, Y = -2f, Z = 3f, Classified = false };
            byte[] wire = Wire(delegate(BinaryWriter w) { StoryWire.WriteStart(w, s); });
            BytesEqual(Wire(delegate(BinaryWriter w) {
                w.Write(s.Token); w.Write(s.Kind); w.Write(s.Scene); w.Write(s.Id); w.Write(s.Path); w.Write(s.State);
                w.Write(s.X); w.Write(s.Y); w.Write(s.Z); w.Write(s.Classified);
            }), wire);
            StoryStart copy = StoryWire.ReadStart(Reader(wire));
            Equal(s.Token, copy.Token); Equal(s.Kind, copy.Kind); BytesEqual(s.Scene, copy.Scene); BytesEqual(s.Id, copy.Id);
            Equal(s.Path, copy.Path); Equal(s.State, copy.State); Equal(s.Y, copy.Y); Equal(false, copy.Classified);
            Array.Resize(ref wire, wire.Length - 1); Equal(true, StoryWire.ReadStart(Reader(wire)).Classified);
        });
    }

    static void ProgressCases()
    {
        Check("progress snapshots reach all guests on bus", delegate {
            Network n = new Network(); Clock c = new Clock(); ProgressPeer h = new ProgressPeer(n.Add(1), c), g = new ProgressPeer(n.Add(2), c);
            ProgressPeer p3 = new ProgressPeer(n.Add(3), c), p4 = new ProgressPeer(n.Add(4), c);
            c.RealTime = 4f; h.Snapshot(Snapshot()); Equal(Snapshot().Hash(), g.Inbox.Take().Hash());
            Equal(Snapshot().Hash(), p3.Inbox.Take().Hash()); Equal(Snapshot().Hash(), p4.Inbox.Take().Hash());
        });
        Check("progress latest snapshot and oldest receipt time", delegate {
            Network n = new Network(); Clock c = new Clock(); ProgressPeer h = new ProgressPeer(n.Add(1), c), g = new ProgressPeer(n.Add(2), c);
            c.RealTime = 4f; h.Snapshot(Snapshot()); c.RealTime = 5f; ProgressSnapshot next = Snapshot(); next.Points++;
            h.Snapshot(next); Equal(4f, g.Inbox.ReceivedAt); Equal(next.Points, g.Inbox.Take().Points); Equal(-1f, g.Inbox.ReceivedAt);
            g.Snapshot(next); Equal(null, h.Inbox.Snapshot);
        });
        Check("story end and skip token bytes", delegate {
            byte[] body = Wire(delegate(BinaryWriter w) { StoryWire.WriteToken(w, 0x12345678); });
            Equal("78563412", BitConverter.ToString(body).Replace("-", "")); Equal(0x12345678U, StoryWire.ReadToken(Reader(body)));
        });
        Check("progress actions across seats are queued and deduped", delegate {
            Network n = new Network(); Clock c = new Clock(); ProgressPeer h = new ProgressPeer(n.Add(1), c), a = new ProgressPeer(n.Add(2), c), b = new ProgressPeer(n.Add(3), c);
            ProgressAction first = new ProgressAction { Sequence = 1, Kind = ActionKind.Keystone, Guid = Id(1) };
            ProgressAction second = new ProgressAction { Sequence = 1, Kind = ActionKind.MapStone, Guid = Id(2) };
            a.Action(first); a.Action(first); b.Action(second); Equal(2, h.Queue.Count);
            Equal(ActionKind.Keystone, h.Queue.Dequeue().Kind); Equal(ActionKind.MapStone, h.Queue.Dequeue().Kind);
        });
        Check("skill status host broadcast", delegate {
            Network n = new Network(); Clock c = new Clock(); ProgressPeer h = new ProgressPeer(n.Add(1), c), a = new ProgressPeer(n.Add(2), c), b = new ProgressPeer(n.Add(4), c);
            h.Skill(false); Equal(false, a.SkillEnabled); Equal(false, b.SkillEnabled); a.Skill(false); Equal(true, h.SkillEnabled);
        });
        Check("progress snapshot wire round trip", delegate {
            ProgressSnapshot s = Snapshot(); byte[] data = Wire(s.Write); ProgressSnapshot t = ProgressSnapshot.Read(Reader(data));
            Equal(s.Abilities, t.Abilities); Equal(s.MaxHealth, t.MaxHealth); Equal(s.MaxEnergy, t.MaxEnergy);
            Equal(s.Keys, t.Keys); Equal(s.Maps, t.Maps); Equal(s.Collected, t.Collected);
            Equal(s.Experience, t.Experience); Equal(s.Level, t.Level); Equal(s.Points, t.Points); Equal(40, data.Length);
            Equal("F0DEBC9A785634120A000000000060400300000004000000050000002C0100000700000002000000", BitConverter.ToString(data).Replace("-", ""));
        });
        Check("progress hash stability", delegate {
            ProgressSnapshot s = Snapshot(); Equal(s.Hash(), ProgressSnapshot.Read(Reader(Wire(s.Write))).Hash());
            ulong before = s.Hash(); Equal(0x26c03cb01738c923UL, before); s.Points++; Equal(false, before == s.Hash());
        });
        Check("progress action wire round trip", delegate {
            ProgressAction a = new ProgressAction { Sequence = 42, Kind = ActionKind.MaxEnergy, Guid = Id(4), Amount = -8, Extra = 17 };
            byte[] bytes = Wire(a.Write);
            BytesEqual(Wire(delegate(BinaryWriter w) { w.Write(42U); w.Write((byte)ActionKind.MaxEnergy); w.Write(Id(4)); w.Write(-8); w.Write(17); }), bytes);
            ProgressAction b = ProgressAction.Read(Reader(bytes)); Equal(a.Sequence, b.Sequence); Equal(a.Kind, b.Kind);
            BytesEqual(a.Guid, b.Guid); Equal(a.Amount, b.Amount); Equal(a.Extra, b.Extra);
        });
        Check("progress action fifo", delegate {
            ProgressQueue<Guid> q = new ProgressQueue<Guid>(); ProgressAction a = new ProgressAction { Sequence = 2 }, b = new ProgressAction { Sequence = 1 };
            q.Enqueue(2, a); q.Enqueue(3, b); Equal(a, q.Peek()); Equal(a, q.Dequeue()); Equal(b, q.Dequeue());
        });
        Check("progress duplicate sequence per seat", delegate {
            ProgressQueue<Guid> q = new ProgressQueue<Guid>(); ProgressAction a = new ProgressAction { Sequence = 1 };
            Equal(true, q.Enqueue(2, a)); Equal(false, q.Enqueue(2, a)); Equal(true, q.Enqueue(3, a)); Equal(2, q.Count);
        });
        Check("progress pickup guid duplicate suppression", delegate {
            ProgressQueue<Guid> q = new ProgressQueue<Guid>(); Equal(true, q.RecordPickup(new Guid(Id(1)))); Equal(false, q.RecordPickup(new Guid(Id(1))));
            Equal(true, q.RecordPickup(new Guid(Id(2)))); q.ClearPickups(); Equal(true, q.RecordPickup(new Guid(Id(1))));
        });
        Check("skill status ack to guest only", delegate {
            Equal(false, ProgressWire.ReadSkillStatus(Reader(Wire(delegate(BinaryWriter w) { ProgressWire.WriteSkillStatus(w, false); }))));
            Equal(true, ProgressWire.ApplySkillStatus(true, false, true)); Equal(false, ProgressWire.ApplySkillStatus(false, false, true));
            Equal(true, ProgressWire.ApplySkillStatus(false, true, false));
        });
        Check("shared merge deltas", delegate {
            SharedResources r = new SharedResources(); r.Remember(3, 4, 2f);
            SharedValues s = r.Merge(5, 4, 2f, 5f, 2, 6, 3f); Equal(4, s.Keys); Equal(6, s.Maps); Equal(3f, s.Energy);
        });
        Check("shared merge clamps", delegate {
            SharedResources r = new SharedResources(); r.Remember(3, 4, 2f);
            SharedValues s = r.Merge(1, 0, 4f, 5f, 0, 0, 8f); Equal(0, s.Keys); Equal(0, s.Maps); Equal(5f, s.Energy);
            s = r.Merge(1, 1, 0f, 5f, 3, 4, 0f); Equal(0f, s.Energy);
        });
        Check("shared reset ignores prior deltas", delegate {
            SharedResources r = new SharedResources(); r.Remember(3, 4, 2f); r.Reset();
            SharedValues s = r.Merge(1, 2, 3f, 5f, 9, 9, 9f); Equal(1, s.Keys); Equal(2, s.Maps); Equal(3f, s.Energy);
        });
    }
    static ProgressSnapshot Snapshot()
    { return new ProgressSnapshot { Abilities = 0x123456789abcdef0UL, MaxHealth = 10, MaxEnergy = 3.5f, Keys = 3, Maps = 4, Collected = 5, Experience = 300, Level = 7, Points = 2 }; }
    static void ObjectCases()
    {
        Check("pedestal wire round trip", delegate {
            byte[] id = Id(2); byte[] bytes = Wire(delegate(BinaryWriter w) { ObjectWire.WritePedestal(w, id); });
            BytesEqual(id, bytes); BytesEqual(id, ObjectWire.ReadPedestal(Reader(bytes)));
        });
        Check("pedestal dedupe 30 second boundary", delegate {
            RecentObjects<string> seen = new RecentObjects<string>(); seen.Mark("a", 10f);
            Equal(true, seen.Recent("a", 39.99f, 30f)); Equal(false, seen.Recent("a", 40f, 30f)); Equal(false, seen.Recent("b", 11f, 30f));
        });
        Check("lever wire and direction dedupe", delegate {
            byte[] id = Id(6); byte[] bytes = Wire(delegate(BinaryWriter w) { ObjectWire.WriteLever(w, id, 2); }); byte direction;
            Equal(17, bytes.Length); BytesEqual(id, ObjectWire.ReadLever(Reader(bytes), out direction)); Equal((byte)2, direction);
            Equal((byte)2, bytes[16]);
            Equal(false, ObjectWire.LeverChanged(2, 2)); Equal(true, ObjectWire.LeverChanged(1, 2));
        });
    }
}
