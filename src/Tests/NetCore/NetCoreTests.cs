using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Authority;
using OriCoop.Net;

static partial class NetCoreTests
{
    sealed class Packet
    {
        public byte[] Bytes;
        public bool Reliable;
    }

    sealed class FakePeer : IMessagePeer
    {
        public bool IsConnected = true;
        public double Age;
        public readonly List<Packet> Sent = new List<Packet>();
        public bool Connected { get { return IsConnected; } }
        public double LastReceiveAgeMs { get { return Age; } }
        public void Send(byte[] message, int length, bool reliable)
        {
            byte[] copy = new byte[length];
            Buffer.BlockCopy(message, 0, copy, 0, length);
            Sent.Add(new Packet { Bytes = copy, Reliable = reliable });
        }
    }

    sealed class Rig : IMessageContext
    {
        public bool EnabledValue = true, Host, Factory, Steam, Hub, SteamReadyValue, NoForward;
        public string ProblemValue = "";
        public int Seat, Max = 4;
        public double Now = 100;
        public FakePeer Single = new FakePeer();
        public readonly SessionMachine Session;
        public readonly MessageBus Bus;
        public readonly List<string> Events = new List<string>();
        public Func<int, byte, bool, Action<BinaryWriter>, bool> Override;
        public int OverrideCalls, CoreFrames, TestSignals;
        public string Version;

        public Rig(bool host, bool factory, bool hub)
        {
            Host = host; Factory = factory; Hub = hub;
            Session = new SessionMachine(this, delegate { return Now; }, delegate(int seat) { Seat = seat; Events.Add("seat:" + seat); },
                delegate(SessionMachine.Remote remote) { Events.Add("cleanup:" + remote.Seat); },
                delegate(int seat) { Bus.ForgetSeat(seat); },
                delegate(Exception e) { Events.Add("end-error:" + e.Message); });
            Bus = new MessageBus(this, Session, delegate(byte type, Exception e) { Events.Add("early-error:" + type + ":" + e.Message); });
            Session.SessionStarted += delegate { Events.Add("start"); };
            Session.SessionEnded += delegate { Events.Add("end"); };
            Session.PlayerJoined += delegate(int seat) { Events.Add("join:" + seat); };
            Session.PlayerLeft += delegate(int seat) { Events.Add("left:" + seat); };
        }

        SessionMachine.Remote Primary
        {
            get
            {
                SessionMachine.Remote found = null;
                if (!Host) { Session.Remotes.TryGetValue(1, out found); return found; }
                foreach (SessionMachine.Remote remote in Session.Remotes.Values)
                    if (found == null || remote.Seat < found.Seat) found = remote;
                return found;
            }
        }
        IMessagePeer Peer { get { return Hub ? Primary == null ? null : Primary.Peer : Single; } }
        public bool Enabled { get { return EnabledValue; } }
        public bool IsHost { get { return Host; } }
        public bool FactorySession { get { return Factory; } }
        public bool UsesSteam { get { return Steam; } }
        public bool HasHub { get { return Hub; } }
        public bool HasPeer { get { return Peer != null; } }
        public bool Connected { get { return Peer != null && Peer.Connected; } }
        public bool PrimaryHello { get { return Primary != null && Primary.Hello; } }
        public bool SteamReady { get { return SteamReadyValue; } }
        public string Problem { get { return ProblemValue; } }
        public int AssignedSeat { get { return Seat; } }
        public double PeerLastReceiveAgeMs { get { return Peer.LastReceiveAgeMs; } }
        public int LocalSeat { get { return Seat != 0 ? Seat : Enabled && !Host ? 2 : 1; } }
        public int MaxSeats { get { return Max; } }
        public bool DisableForward { get { return NoForward; } }
        public IMessagePeer SinglePeer { get { return Single; } }
        public bool SendToOverride(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            if (Override == null) return false;
            OverrideCalls++;
            return Override(seat, type, reliable, body);
        }
        public void AssignSeat(int seat) { Session.AssignSeat(seat); Events.Add("hello-send"); }
        public SessionMachine.Remote CreateRemote(int seat)
        {
            return new SessionMachine.Remote { Seat = seat, Peer = !Factory && seat == 1 ? Single : null };
        }
        public void HandleCore(byte type, BinaryReader reader, byte[] message, SessionMachine.Remote remote)
        {
            if (type == 1)
            {
                Version = reader.ReadString();
                bool joined = Session.MarkHello(remote);
                Events.Add("hello:" + remote.Seat);
                Session.AnnounceHello(remote, joined);
            }
            else if (type == 2) { remote.InGameplay = reader.ReadBoolean(); reader.ReadString(); }
            else if (type == 3) CoreFrames++;
            else if (type == 99) { reader.ReadString(); TestSignals++; }
        }
        public SessionMachine.Remote Add(int seat, FakePeer peer)
        {
            SessionMachine.Remote remote = new SessionMachine.Remote { Seat = seat, Peer = peer };
            Session.Remotes.Add(seat, remote);
            return remote;
        }
        public void Receive(byte type, byte sender, bool reliable, int source, Action<BinaryWriter> body)
        {
            Bus.Dispatch(Frame(type, sender, body), reliable, source);
        }
    }

    // T-251: level geometry for the stand-in's wall test: axis-aligned boxes; a segment is blocked when it crosses one.
    sealed class FakeWalls : StandInCorrection.IWallProbe
    {
        public readonly List<float[]> Boxes = new List<float[]>();   // xMin, yMin, xMax, yMax
        public int Calls;
        public FakeWalls Box(float xMin, float yMin, float xMax, float yMax) { Boxes.Add(new float[] { xMin, yMin, xMax, yMax }); return this; }
        public bool Blocked(float ax, float ay, float bx, float by) { Calls++; return Crosses(ax, ay, bx, by); }
        public bool Crosses(float ax, float ay, float bx, float by)
        {
            foreach (float[] b in Boxes)
            {
                // Liang-Barsky clip of the segment against the box.
                float t0 = 0f, t1 = 1f, dx = bx - ax, dy = by - ay;
                if (Clip(-dx, ax - b[0], ref t0, ref t1) && Clip(dx, b[2] - ax, ref t0, ref t1)
                    && Clip(-dy, ay - b[1], ref t0, ref t1) && Clip(dy, b[3] - ay, ref t0, ref t1)) return true;
            }
            return false;
        }
        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (p == 0f) return q >= 0f;
            float r = q / p;
            if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
    }

    // NetPlay.Correct with the default config (SnapDistance 4, CorrectionBlend 0.2): the stand-in at (x, y) follows its
    // owner at (ox, oy) for some fixed steps; the stand-in's own replay is stuck (it does not move by itself), as when
    // it stopped against a wall its owner got past. Returns the snaps; fails when a pull moves it across a wall.
    static int Follow(FakeWalls walls, ref float x, ref float y, float ox, float oy, int steps)
    {
        int snaps = 0;
        for (int i = 0; i < steps; i++)
        {
            float ex = ox - x, ey = oy - y, e = (float)Math.Sqrt(ex * ex + ey * ey);
            StandInCorrection.Fix fix = StandInCorrection.Decide(e, 4f, x, y, ox, oy, walls);
            if (fix == StandInCorrection.Fix.Snap) { x = ox; y = oy; snaps++; continue; }
            if (fix != StandInCorrection.Fix.Blend) continue;
            float nx = x + ex * 0.2f, ny = y + ey * 0.2f;
            Check(!walls.Crosses(x, y, nx, ny), "step " + i + ": the stand-in slid through a wall from (" + x + "," + y + ") to (" + nx + "," + ny + ")");
            x = nx; y = ny;
        }
        return snaps;
    }

    static int cases, failures;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual)
    {
        if (!Object.Equals(expected, actual)) throw new Exception("expected " + expected + ", got " + actual);
    }
    static void Bytes(byte[] expected, byte[] actual)
    {
        Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Equal(expected[i], actual[i]);
    }
    static void Run(string name, Action test)
    {
        cases++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static byte[] Frame(byte type, byte sender, Action<BinaryWriter> body)
    {
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream);
        writer.Write(type); writer.Write(sender);
        if (body != null) body(writer);
        return stream.ToArray();
    }
    static Action<BinaryWriter> Hello = delegate(BinaryWriter w) { w.Write("v1"); };
    static Action<BinaryWriter> Gameplay = delegate(BinaryWriter w) { w.Write(true); w.Write("scene"); };
    static Action<BinaryWriter> Empty = delegate(BinaryWriter w) { };
    static string Events(Rig r) { return string.Join(",", r.Events.ToArray()); }

    static int Main()
    {
        MovementHandover();
        Run("owner notice: a token rejoin's first online roster is silent even after waiting with our seat offline", delegate {
            RoomOwnerNotice notice = new RoomOwnerNotice();
            Equal(0, notice.TakePreviousOwner(2, false, 0));
            Equal(0, notice.TakePreviousOwner(2, false, 1000));
            Equal(0, notice.TakePreviousOwner(1, true, 1200));
            Equal(0, notice.TakePreviousOwner(1, true, 1800));
        });
        Run("owner notice: a remote owner's brief offline/online roster pair does not announce either flip", delegate {
            RoomOwnerNotice notice = new RoomOwnerNotice();
            Equal(0, notice.TakePreviousOwner(1, true, 0));
            Equal(0, notice.TakePreviousOwner(1, true, 600));
            Equal(0, notice.TakePreviousOwner(2, true, 700));
            Equal(0, notice.TakePreviousOwner(1, true, 900));
            Equal(0, notice.TakePreviousOwner(1, true, 1500));
        });
        Run("owner notice: one settled handover is announced, including when the new owner is local", delegate {
            RoomOwnerNotice notice = new RoomOwnerNotice();
            Equal(0, notice.TakePreviousOwner(1, true, 0));
            Equal(0, notice.TakePreviousOwner(1, true, 600));
            Equal(0, notice.TakePreviousOwner(2, true, 700));
            Equal(1, notice.TakePreviousOwner(2, true, 1300));
            Equal(0, notice.TakePreviousOwner(2, true, 1900));
        });
        Run("menu Steam and LAN transports still override the C-server default in session-end callbacks", delegate {
            foreach (string transport in new[] { "steam", "direct" })
            {
                Rig r = new Rig(true, false, false);
                Func<string> configured = delegate { return "cserver"; };
                r.Session.ActiveTransport = transport;
                SessionMachine.Remote remote = r.Add(2, r.Single);
                r.Session.AnnounceHello(remote, r.Session.MarkHello(remote));
                string seen = null;
                r.Session.SessionEnded += delegate { seen = r.Session.TransportName(configured); };
                r.Session.Leave(remote);
                Equal(transport, seen);
                r.Session.Reset();
                Equal(transport, r.Session.TransportName(configured));
                r.Session.ActiveTransport = null;
                Equal("cserver", r.Session.TransportName(configured));
            }
        });
        Run("handler receives body and sender", delegate {
            Rig r = new Rig(false, false, false); int got = 0, sender = 0;
            r.Bus.On(70, delegate(BinaryReader b) { got = b.ReadInt32(); sender = r.Bus.Sender; });
            r.Seat = 2; r.Receive(70, 1, true, 0, delegate(BinaryWriter b) { b.Write(42); });
            Equal(42, got); Equal(1, sender); Equal(0, r.Bus.Sender);
        });
        Run("handler registration replaces previous handler", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; int got = 0;
            r.Bus.On(70, delegate(BinaryReader b) { got = 1; }); r.Bus.On(70, delegate(BinaryReader b) { got = 2; });
            r.Receive(70, 1, true, 0, Empty); Equal(2, got);
        });
        Run("early messages drain in order with original senders", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; List<int> got = new List<int>();
            r.Receive(70, 1, true, 0, delegate(BinaryWriter b) { b.Write((byte)8); });
            r.Receive(70, 3, true, 0, delegate(BinaryWriter b) { b.Write((byte)9); });
            r.Bus.On(70, delegate(BinaryReader b) { got.Add(r.Bus.Sender * 10 + b.ReadByte()); });
            Equal("18,39", string.Join(",", got.ConvertAll<string>(delegate(int i) { return i.ToString(); }).ToArray()));
            Equal(0, r.Bus.Sender);
        });
        Run("early queue retains newest 64", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; List<int> got = new List<int>();
            for (int i = 0; i < 66; i++) { int n = i; r.Receive(70, 1, true, 0, delegate(BinaryWriter b) { b.Write(n); }); }
            r.Bus.On(70, delegate(BinaryReader b) { got.Add(b.ReadInt32()); });
            Equal(64, got.Count); Equal(2, got[0]); Equal(65, got[63]);
        });
        Run("early handler exception is logged and draining continues", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; int n = 0;
            r.Receive(70, 1, true, 0, Empty); r.Receive(70, 1, true, 0, Empty);
            r.Bus.On(70, delegate(BinaryReader b) { if (++n == 1) throw new Exception("bad"); });
            Equal(2, n); Equal("early-error:70:bad", Events(r)); Equal(0, r.Bus.Sender);
        });
        Run("sender is restored after nested dispatch and exception", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; int after = 0;
            r.Bus.On(71, delegate(BinaryReader b) { Equal(3, r.Bus.Sender); throw new Exception("nested"); });
            r.Bus.On(70, delegate(BinaryReader b) { try { r.Receive(71, 3, true, 0, Empty); } catch (Exception) { } after = r.Bus.Sender; });
            r.Receive(70, 1, true, 0, Empty); Equal(1, after); Equal(0, r.Bus.Sender);
        });
        Run("seat validator rejects before handler and host forwarding", delegate {
            Rig r = new Rig(true, false, true); FakePeer source = new FakePeer(), other = new FakePeer();
            r.Add(2, source); r.Add(4, other); r.Bus.Forward(9); int handled = 0;
            r.Bus.On(9, delegate(BinaryReader b) { handled++; });
            r.Bus.Validate = delegate(byte[] bytes, int connection) { return connection == 2 && bytes[1] == 2 && bytes[2] == 3; };
            r.Receive(9, 2, false, 2, delegate(BinaryWriter w) { w.Write((byte)4); });
            Equal(0, handled); Equal(0, other.Sent.Count);
            r.Receive(9, 2, false, 2, delegate(BinaryWriter w) { w.Write((byte)3); });
            Equal(1, handled); Equal(1, other.Sent.Count); Equal(0, source.Sent.Count);
        });
        Run("send serializes type seat and body once to hub", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b); int calls = 0;
            r.Bus.Send(70, true, delegate(BinaryWriter w) { calls++; w.Write((byte)42); });
            Equal(1, calls); Bytes(new byte[] { 70, 1, 42 }, a.Sent[0].Bytes); Bytes(a.Sent[0].Bytes, b.Sent[0].Bytes); Check(b.Sent[0].Reliable, "reliability");
        });
        Run("guest send uses single peer and reliability", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 3;
            r.Bus.Send(70, false, delegate(BinaryWriter w) { w.Write((byte)7); });
            Bytes(new byte[] { 70, 3, 7 }, r.Single.Sent[0].Bytes); Check(!r.Single.Sent[0].Reliable, "unreliable");
        });
        Run("send is gated while disconnected or waiting for seat", delegate {
            Rig r = new Rig(false, false, false); int calls = 0;
            r.Bus.Send(70, true, delegate(BinaryWriter w) { calls++; }); r.Seat = 2; r.Single.IsConnected = false;
            r.Bus.Send(70, true, delegate(BinaryWriter w) { calls++; }); Equal(0, calls); Equal(0, r.Single.Sent.Count);
        });
        Run("sendto targets only specified guest", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b);
            r.Bus.SendTo(3, 70, false, delegate(BinaryWriter w) { w.Write((byte)9); });
            Equal(0, a.Sent.Count); Bytes(new byte[] { 70, 1, 9 }, b.Sent[0].Bytes); Check(!b.Sent[0].Reliable, "unreliable");
        });
        Run("sendto override precedes host and seat checks", delegate {
            Rig r = new Rig(false, true, false); int called = 0;
            r.Override = delegate(int seat, byte type, bool reliable, Action<BinaryWriter> body) { called = seat; return true; };
            r.Bus.SendTo(4, 70, true, Empty); Equal(4, called); Equal(1, r.OverrideCalls); Equal(0, r.Single.Sent.Count);
        });
        Run("sendto fallback requires host and peer", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(); r.Add(2, a); int calls = 0;
            r.Override = delegate(int seat, byte type, bool reliable, Action<BinaryWriter> body) { return false; };
            r.Bus.SendTo(2, 70, true, delegate(BinaryWriter w) { calls++; }); r.Bus.SendTo(3, 70, true, delegate(BinaryWriter w) { calls++; });
            r.Host = false; r.Bus.SendTo(2, 70, true, delegate(BinaryWriter w) { calls++; }); Equal(1, calls); Equal(1, a.Sent.Count);
        });
        Run("forward relays exact bytes to other guests", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(), c = new FakePeer();
            r.Add(2, a); r.Add(3, b); r.Add(4, c); r.Bus.Forward(70, 71); r.Bus.On(70, delegate(BinaryReader x) { });
            r.Receive(70, 2, false, 2, delegate(BinaryWriter w) { w.Write((byte)5); });
            Equal(0, a.Sent.Count); Bytes(new byte[] { 70, 2, 5 }, b.Sent[0].Bytes); Bytes(b.Sent[0].Bytes, c.Sent[0].Bytes); Check(!b.Sent[0].Reliable, "forward reliability");
        });
        Run("forward disabled only disables registered forwarding", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b);
            r.NoForward = true; r.Bus.Forward(70); r.Receive(70, 2, true, 2, Empty); Equal(0, b.Sent.Count);
            r.Receive(1, 2, true, 2, Hello); Equal(1, b.Sent.Count);
        });
        Run("hello status and frames automatically forward after handling", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b);
            r.Receive(1, 2, true, 2, Hello); r.Receive(2, 2, true, 2, Gameplay); r.Receive(3, 2, false, 2, Empty);
            Equal(0, a.Sent.Count); Equal(3, b.Sent.Count); Check(!b.Sent[2].Reliable, "frame reliability"); Equal(1, r.CoreFrames);
        });
        Run("rebroadcast uses full original frame reliably", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b);
            byte[] m = Frame(53, 2, delegate(BinaryWriter w) { w.Write((byte)8); });
            BinaryReader reader = new BinaryReader(new MemoryStream(m)); reader.ReadByte(); reader.ReadByte(); reader.ReadByte();
            r.Bus.Rebroadcast(reader); Bytes(m, a.Sent[0].Bytes); Bytes(m, b.Sent[0].Bytes); Check(a.Sent[0].Reliable, "rebroadcast reliability");
        });
        Run("rebroadcast skips guests and factory sessions", delegate {
            Rig r = new Rig(false, false, false); BinaryReader reader = new BinaryReader(new MemoryStream(new byte[] { 53, 1 }));
            r.Bus.Rebroadcast(reader); r.Host = true; r.Factory = true; r.Bus.Rebroadcast(reader); Equal(0, r.Single.Sent.Count);
        });
        Run("assign seat writes before hello send", delegate {
            Rig r = new Rig(false, false, false); r.Receive(4, 1, true, 0, delegate(BinaryWriter w) { w.Write((byte)3); });
            Equal(3, r.Seat); Equal("seat:3,hello-send", Events(r)); Check(!r.Session.WaitingForSeat, "still waiting");
        });
        Run("only the host assigns a seat including on Steam", delegate {
            Rig r = new Rig(false, false, false); Action<BinaryWriter> seat = delegate(BinaryWriter w) { w.Write((byte)3); };
            r.Steam = true; r.Receive(4, 2, true, 0, seat); r.Host = true; r.Receive(4, 1, true, 0, seat);
            Equal(0, r.Seat); Equal("", Events(r));
            r.Host = false; r.Receive(4, 1, true, 0, seat);
            Equal(3, r.Seat); Equal("seat:3,hello-send", Events(r));
        });
        Run("a two-player Steam host can bootstrap a waiting guest with its targeted table", delegate {
            Rig r = new Rig(false, false, false); r.Steam = true;
            int tables = 0; r.Bus.On(8, delegate(BinaryReader reader) { tables++; });
            r.Receive(8, 3, true, 0, Empty); r.Receive(8, 1, true, 0, Empty);
            Equal(1, tables);
        });
        Run("waiting guest drops ordinary traffic", delegate {
            Rig r = new Rig(false, false, false); int received = 0; r.Bus.On(70, delegate(BinaryReader b) { received++; });
            r.Receive(70, 1, true, 0, Empty); r.Receive(1, 1, true, 0, Hello); Equal(0, received); Equal(0, r.Session.Remotes.Count);
        });
        Run("factory messages b0 through ba dispatch before seat", delegate {
            Rig r = new Rig(false, true, false); int count = 0;
            for (int i = 176; i <= 186; i++) { byte t = (byte)i; r.Bus.On(t, delegate(BinaryReader b) { Equal(0, r.Bus.Sender); count++; }); r.Receive(t, 0, true, 0, Empty); }
            Equal(11, count); Check(r.Session.WaitingForSeat, "seat assigned unexpectedly");
        });
        Run("factory welcome assigns seat and sends hello during dispatch", delegate {
            Rig r = new Rig(false, true, false); int sender = -1;
            r.Bus.On(176, delegate(BinaryReader reader) {
                sender = r.Bus.Sender; r.Session.AssignSeat(reader.ReadByte()); r.Bus.Send(1, true, delegate(BinaryWriter w) { w.Write((byte)42); });
            });
            r.Receive(176, 0, true, 0, delegate(BinaryWriter w) { w.Write((byte)3); });
            Equal(0, sender); Equal(3, r.Seat); Bytes(new byte[] { 1, 3, 42 }, r.Single.Sent[0].Bytes); Equal(0, r.Bus.Sender);
        });
        Run("factory messages outside range wait for seat", delegate {
            Rig r = new Rig(false, true, false); int count = 0; r.Bus.On(175, delegate(BinaryReader b) { count++; }); r.Bus.On(187, delegate(BinaryReader b) { count++; });
            r.Receive(175, 0, true, 0, Empty); r.Receive(187, 0, true, 0, Empty); Equal(0, count);
        });
        Run("factory early message drains after registration while waiting", delegate {
            Rig r = new Rig(false, true, false); int count = 0;
            r.Receive(176, 0, true, 0, Empty); r.Bus.On(176, delegate(BinaryReader b) { Equal(0, r.Bus.Sender); count++; }); Equal(1, count);
        });
        Run("sender and source validation reject spoofed and invalid frames", delegate {
            Rig r = new Rig(true, false, true); r.Add(2, new FakePeer()); int count = 0; r.Bus.On(70, delegate(BinaryReader b) { count++; });
            r.Receive(70, 3, true, 2, Empty); r.Receive(70, 0, true, 2, Empty); r.Receive(70, 1, true, 0, Empty); r.Receive(70, 5, true, 0, Empty); r.Receive(70, 3, true, 0, Empty);
            Equal(0, count); Equal(1, r.Session.Remotes.Count);
        });
        Run("short incoming frame is ignored", delegate {
            Rig r = new Rig(false, false, false); r.Bus.Dispatch(new byte[] { 70 }, true, 0); Equal(0, r.Bus.Sender);
        });
        Run("join starts session before player joined", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello);
            Equal("hello:1,start,join:1", Events(r)); Equal("v1", r.Version); Check(r.Session.Started, "session not started");
        });
        Run("duplicate hello does not duplicate start or join", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello); r.Receive(1, 1, true, 0, Hello);
            Equal("hello:1,start,join:1,hello:1", Events(r));
        });
        Run("second join does not restart session", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello); r.Receive(1, 3, true, 0, Hello);
            Equal("hello:1,start,join:1,hello:3,join:3", Events(r));
        });
        Run("last leave cleans up then reports left and end", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello); r.Events.Clear(); r.Session.RemoteLeft(1);
            Equal("cleanup:1,left:1,end", Events(r)); Check(!r.Session.Started, "session still started"); Equal(0, r.Session.Remotes.Count);
        });
        Run("nonlast leave keeps session running", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello); r.Receive(1, 3, true, 0, Hello); r.Events.Clear();
            r.Session.RemoteLeft(1); Equal("cleanup:1,left:1", Events(r)); Check(r.Session.Started, "session ended early"); r.Session.RemoteLeft(3); Equal("cleanup:1,left:1,cleanup:3,left:3,end", Events(r));
        });
        Run("remote leave removes queued messages from that seat", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; int count = 0;
            r.Receive(70, 1, true, 0, Empty); r.Receive(70, 3, true, 0, Empty); r.Session.RemoteLeft(1);
            r.Bus.On(70, delegate(BinaryReader b) { Equal(3, r.Bus.Sender); count++; }); Equal(1, count);
        });
        Run("leave without hello still fires player left but no end", delegate {
            Rig r = new Rig(true, false, true); r.Add(2, new FakePeer()); r.Session.RemoteLeft(2); Equal("cleanup:2,left:2", Events(r));
        });
        Run("a remote in the session still counts while loading", delegate {
            Rig r = new Rig(true, true, false); r.Seat = 1;
            r.Add(2, null);
            Equal(false, r.Session.RemoteInSession(2));
            r.Receive(1, 2, true, 0, Hello); r.Receive(2, 2, true, 0, Gameplay);
            Equal(true, r.Session.RemoteInSession(2));
            r.Receive(2, 2, true, 0, delegate(BinaryWriter w) { w.Write(false); w.Write(""); });
            Equal(false, r.Session.Remotes[2].InGameplay);
            Equal(true, r.Session.RemoteInSession(2));
            r.Session.RemoteLeft(2);
            Equal(false, r.Session.RemoteInSession(2));
        });
        Run("unknown leave does nothing", delegate { Rig r = new Rig(false, true, false); r.Session.RemoteLeft(3); Equal("", Events(r)); });
        Run("left frame is accepted from host only", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; r.Add(3, null); Action<BinaryWriter> body = delegate(BinaryWriter w) { w.Write((byte)3); };
            r.Receive(5, 3, true, 0, body); Equal(1, r.Session.Remotes.Count); r.Receive(5, 1, true, 0, body); Equal(0, r.Session.Remotes.Count); Equal("cleanup:3,left:3", Events(r));
        });
        // T-213 (5): the "seat left" notice is reliable and can overtake that seat's last unreliable frames packet.
        Run("a late unreliable packet from a seat that left creates no remote, and the session still ends", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2;
            r.Receive(1, 1, true, 0, Hello); r.Receive(1, 3, true, 0, Hello); r.Receive(2, 3, true, 0, Gameplay);
            r.Session.RemoteLeft(3); r.Events.Clear();
            r.Receive(3, 3, false, 0, Empty);
            Equal(1, r.Session.Remotes.Count); Equal(0, r.CoreFrames);
            r.Session.RemoteLeft(1);
            Equal("cleanup:1,left:1,end", Events(r)); Check(!r.Session.Started, "session still started");
        });
        Run("an unreliable message never introduces a seat, a reliable one still does", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; int got = 0; r.Bus.On(70, delegate(BinaryReader b) { got++; });
            r.Receive(70, 3, false, 0, Empty); Equal(0, got); Equal(0, r.Session.Remotes.Count);
            r.Receive(70, 3, true, 0, Empty); Equal(1, got); Equal(1, r.Session.Remotes.Count);
            r.Receive(70, 3, false, 0, Empty); Equal(2, got);
        });
        Run("end session catches subscriber exception and resets state", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello); r.Events.Clear();
            r.Session.SessionEnded += delegate { throw new Exception("oops"); }; r.Session.EndSession(); r.Session.EndSession();
            Equal("end,end-error:oops", Events(r)); Check(!r.Session.Started, "session still started");
        });
        Run("reset clears remotes seat and started without events", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello); r.Events.Clear(); r.Session.Reset();
            Equal("seat:0", Events(r)); Equal(0, r.Seat); Equal(0, r.Session.Remotes.Count); Check(!r.Session.Started, "session still started");
        });
        Run("factory seated alone is waiting", delegate {
            Rig r = new Rig(true, true, false); r.Seat = 1; Equal(SessionMachine.Phase.Waiting, r.Session.CurrentPhase);
            r.Receive(1, 2, true, 0, Hello); Equal(SessionMachine.Phase.Connected, r.Session.CurrentPhase);
        });
        Run("phase distinguishes off starting waiting connecting retrying", delegate {
            Rig r = new Rig(true, false, true); r.EnabledValue = false; Equal(SessionMachine.Phase.Off, r.Session.CurrentPhase);
            r.EnabledValue = true; r.Hub = false; r.Single = null; Equal(SessionMachine.Phase.Starting, r.Session.CurrentPhase);
            r.Hub = true; Equal(SessionMachine.Phase.Waiting, r.Session.CurrentPhase); r.ProblemValue = "bad"; Equal(SessionMachine.Phase.Retrying, r.Session.CurrentPhase);
            r.ProblemValue = ""; r.Host = false; r.Hub = false; r.Single = new FakePeer(); Equal(SessionMachine.Phase.Connecting, r.Session.CurrentPhase);
        });
        Run("steam readiness sets host phase", delegate {
            Rig r = new Rig(true, false, false); r.Single = null; r.Steam = true;
            Equal(SessionMachine.Phase.Starting, r.Session.CurrentPhase); r.SteamReadyValue = true; Equal(SessionMachine.Phase.Waiting, r.Session.CurrentPhase);
        });
        Run("connected peer silence reaches signal lost threshold", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; r.Single.Age = 999; Check(!r.Session.SignalLost, "early hint");
            r.Single.Age = 1000; Equal(1000f, r.Session.SilenceMs); Check(r.Session.SignalLost, "no hint"); r.Single.IsConnected = false; Check(!r.Session.SignalLost, "hint while disconnected");
        });
        Run("factory gameplay silence measures last partner frame", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Receive(2, 1, true, 0, Gameplay); r.Now = 1100;
            Equal(1000f, r.Session.SilenceMs); Check(r.Session.SignalLost, "no hint"); r.Receive(70, 1, true, 0, Empty); Equal(0f, r.Session.SilenceMs); Check(!r.Session.SignalLost, "hint not reset");
        });
        Run("factory non gameplay silence falls back to link age", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Single.Age = 50; r.Receive(1, 1, true, 0, Hello); r.Now = 3000;
            Equal(50f, r.Session.SilenceMs); Check(!r.Session.SignalLost, "quiet menu marked lost");
        });
        Run("silence uses worst connected remote", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); a.Age = 800; b.Age = 1500; r.Add(2, a); r.Add(3, b);
            Equal(1500f, r.Session.SilenceMs); Check(r.Session.SignalLost, "worst ignored");
        });
        Run("disconnected remote does not use factory partner clock", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; FakePeer a = new FakePeer(); a.IsConnected = false; SessionMachine.Remote remote = r.Add(1, a);
            remote.InGameplay = true; remote.LastHeardMs = 1; r.Now = 5000; r.Single.Age = 30; Equal(30f, r.Session.SilenceMs);
        });
        Run("guest host guest forwarding preserves sender and payload", delegate {
            Rig host = new Rig(true, false, true), guest2 = new Rig(false, false, false), guest3 = new Rig(false, false, false);
            FakePeer to2 = new FakePeer(), to3 = new FakePeer(); host.Add(2, to2); host.Add(3, to3); guest2.Seat = 2; guest3.Seat = 3;
            host.Bus.Forward(70); int hostSender = 0, guestSender = 0, value = 0;
            host.Bus.On(70, delegate(BinaryReader reader) { hostSender = host.Bus.Sender; Equal(1234, reader.ReadInt32()); });
            guest3.Bus.On(70, delegate(BinaryReader reader) { guestSender = guest3.Bus.Sender; value = reader.ReadInt32(); });
            guest3.Receive(1, 2, true, 0, Hello);   // T-213: seat 2 is introduced by its (reliable) hello; an unreliable message never introduces a seat
            guest2.Bus.Send(70, false, delegate(BinaryWriter writer) { writer.Write(1234); });
            Packet incoming = guest2.Single.Sent[0]; host.Bus.Dispatch(incoming.Bytes, incoming.Reliable, 2);
            Equal(0, to2.Sent.Count); Equal(1, to3.Sent.Count);
            Packet forwarded = to3.Sent[0]; guest3.Bus.Dispatch(forwarded.Bytes, forwarded.Reliable, 0);
            Equal(2, hostSender); Equal(2, guestSender); Equal(1234, value); Check(!forwarded.Reliable, "reliability changed");
        });
        Run("host seat frame and guest hello round trip", delegate {
            Rig host = new Rig(true, false, true), guest = new Rig(false, false, false); FakePeer toGuest = new FakePeer(); host.Add(2, toGuest);
            host.Bus.SendTo(2, 4, true, delegate(BinaryWriter writer) { writer.Write((byte)2); });
            Packet seat = toGuest.Sent[0]; guest.Bus.Dispatch(seat.Bytes, seat.Reliable, 0); Equal(2, guest.Seat);
            guest.Bus.Send(1, true, Hello); Packet hello = guest.Single.Sent[0]; host.Bus.Dispatch(hello.Bytes, hello.Reliable, 2);
            Equal("hello:2,start,join:2", Events(host)); Check(host.Session.Started, "host not started");
        });
        Run("factory control gate belongs to session state", delegate {
            Rig r = new Rig(false, true, false);
            Check(r.Session.IsFactoryMessage(176) && r.Session.IsFactoryMessage(186), "factory endpoints");
            Check(!r.Session.IsFactoryMessage(175) && !r.Session.IsFactoryMessage(187), "factory range");
            r.Factory = false; Check(!r.Session.IsFactoryMessage(176), "ordinary session");
        });
        Run("host and steam waiting-for-seat rules", delegate {
            Rig r = new Rig(true, false, false); Check(!r.Session.WaitingForSeat, "direct host waiting");
            r.Factory = true; Check(r.Session.WaitingForSeat, "factory host not waiting");
            r.Factory = false; r.Host = false; Check(r.Session.WaitingForSeat, "direct guest not waiting");
            r.Steam = true; Check(r.Session.WaitingForSeat, "Steam guest sent before its host assigned a seat");
        });
        Run("session events see sender and state at each transition", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; List<string> seen = new List<string>();
            r.Session.SessionStarted += delegate { seen.Add("start:" + r.Bus.Sender + ":" + r.Session.Started); };
            r.Session.PlayerJoined += delegate(int seat) { seen.Add("join:" + r.Bus.Sender + ":" + r.Session.Remotes[seat].Hello); };
            r.Session.PlayerLeft += delegate(int seat) { seen.Add("left:" + r.Bus.Sender + ":" + r.Session.Remotes.ContainsKey(seat)); };
            r.Session.SessionEnded += delegate { seen.Add("end:" + r.Bus.Sender + ":" + r.Session.Started); };
            r.Receive(1, 1, true, 0, Hello); r.Receive(5, 1, true, 0, delegate(BinaryWriter w) { w.Write((byte)1); });
            Equal("start:1:True,join:1:True,left:1:False,end:1:False", string.Join(",", seen.ToArray())); Equal(0, r.Bus.Sender);
        });
        Run("removed session subscriber is not called", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; int calls = 0;
            Action<int> joined = delegate(int seat) { calls++; }; r.Session.PlayerJoined += joined; r.Session.PlayerJoined -= joined;
            r.Receive(1, 1, true, 0, Hello); Equal(0, calls);
        });
        Run("session start exception stops join and forwarding", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b);
            r.Session.SessionStarted += delegate { throw new Exception("start"); };
            try { r.Receive(1, 2, true, 2, Hello); throw new Exception("missing error"); } catch (Exception e) { Equal("start", e.Message); }
            Equal("hello:2,start", Events(r)); Check(r.Session.Started && r.Session.Remotes[2].Hello, "state changed");
            Equal(0, b.Sent.Count); Equal(0, r.Bus.Sender);
        });
        Run("registered frame handler overrides core but still forwards", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b); int calls = 0;
            r.Bus.On(3, delegate(BinaryReader reader) { calls++; }); r.Receive(3, 2, false, 2, Empty);
            Equal(1, calls); Equal(0, r.CoreFrames); Equal(1, b.Sent.Count); Check(!b.Sent[0].Reliable, "frame reliability");
        });
        Run("hello handler cannot override core session events", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; int calls = 0; r.Bus.On(1, delegate(BinaryReader reader) { calls++; });
            r.Receive(1, 1, true, 0, Hello); Equal(0, calls); Check(r.Session.Started, "session not started");
        });
        Run("registered hello forwarding retains original duplicate", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), b = new FakePeer(); r.Add(2, a); r.Add(3, b);
            r.Bus.Forward(1); r.Receive(1, 2, true, 2, Hello); Equal(2, b.Sent.Count); Bytes(b.Sent[0].Bytes, b.Sent[1].Bytes);
        });
        Run("leave subscriber exception prevents session end", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; r.Receive(1, 1, true, 0, Hello); r.Events.Clear();
            r.Session.PlayerLeft += delegate(int seat) { throw new Exception("left"); };
            try { r.Session.RemoteLeft(1); throw new Exception("missing error"); } catch (Exception e) { Equal("left", e.Message); }
            Equal("cleanup:1,left:1", Events(r)); Check(r.Session.Started, "session ended despite exception"); Equal(0, r.Session.Remotes.Count);
        });
        Run("clearing early queue retains handlers", delegate {
            Rig r = new Rig(false, true, false); r.Seat = 2; int calls = 0; r.Receive(70, 1, true, 0, Empty); r.Bus.ClearEarly();
            r.Bus.On(70, delegate(BinaryReader reader) { calls++; }); Equal(0, calls); r.Bus.ClearEarly(); r.Receive(70, 1, true, 0, Empty); Equal(1, calls);
        });
        // T-149: host 1, A was seat 3, B (seat 2) left, C is seat 4. A adds a local player, the host renumbers A to 2
        // (its extra player gets 3) and moves A's connection to seat 2. A stamps 3 until our seat table arrives.
        Run("renumbered device keeps its old sender until it uses the new seat", delegate {
            Rig r = new Rig(true, false, true); FakePeer a = new FakePeer(), c = new FakePeer();
            r.Add(2, a); r.Add(3, null); r.Add(4, c); r.Bus.Forward(70);
            r.Bus.Validate = delegate(byte[] bytes, int connection) { return bytes[1] == connection; };   // like ValidateSeatPacket
            List<int> senders = new List<int>(); r.Bus.On(70, delegate(BinaryReader b) { senders.Add(r.Bus.Sender); b.ReadByte(); });
            r.Bus.SeatRenumbered(3, 2);

            r.Receive(70, 3, true, 2, delegate(BinaryWriter w) { w.Write((byte)7); });     // A, old stamp, reliable one-shot event
            Equal(1, senders.Count); Equal(2, senders[0]);
            Equal(1, c.Sent.Count); Bytes(new byte[] { 70, 2, 7 }, c.Sent[0].Bytes); Equal(0, a.Sent.Count);
            r.Receive(70, 3, true, 4, delegate(BinaryWriter w) { w.Write((byte)8); });     // C claiming A's old seat: dropped
            Equal(1, senders.Count); Equal(1, c.Sent.Count); Equal(0, a.Sent.Count);
            r.Receive(70, 2, false, 2, delegate(BinaryWriter w) { w.Write((byte)9); });    // unreliable new stamp: window stays
            r.Receive(70, 3, false, 2, delegate(BinaryWriter w) { w.Write((byte)10); });
            Equal(3, senders.Count); Equal(2, senders[2]); Bytes(new byte[] { 70, 2, 10 }, c.Sent[2].Bytes);
            r.Receive(70, 2, true, 2, delegate(BinaryWriter w) { w.Write((byte)11); });    // reliable new stamp ends the window
            r.Receive(70, 3, true, 2, delegate(BinaryWriter w) { w.Write((byte)12); });
            Equal(4, senders.Count); Equal(4, c.Sent.Count);
        });
        Run("renumber alias ends when the device leaves or the session resets", delegate {
            Rig r = new Rig(true, false, true); r.Add(2, new FakePeer()); r.Add(4, new FakePeer());
            int count = 0; r.Bus.On(70, delegate(BinaryReader b) { count++; });
            r.Bus.SeatRenumbered(3, 2); r.Bus.ForgetSeat(2); r.Receive(70, 3, true, 2, Empty); Equal(0, count);
            r.Bus.SeatRenumbered(3, 2); r.Bus.ClearRenumbered(); r.Receive(70, 3, true, 2, Empty); Equal(0, count);
            r.Bus.SeatRenumbered(3, 2); r.Receive(70, 3, true, 2, Empty); Equal(1, count);
        });
        // T-150: frames that arrive while no puppet can play them stay bounded; the newest are kept for the spawn.
        Run("idle frame buffer stays bounded and keeps the newest frames", delegate {
            SortedDictionary<uint, int> buffer = new SortedDictionary<uint, int>(); uint play = 1;
            for (uint tick = 1; tick <= 3600; tick++)   // one minute at 60 frames/s with this PC on the title screen
            {
                buffer[tick] = (int)tick;
                play = FrameBufferTrim.TrimIdle(buffer, tick, play, FrameBufferTrim.IdleKeep);
                Check(buffer.Count <= FrameBufferTrim.IdleKeep, "buffer grew to " + buffer.Count + " at tick " + tick);
            }
            Equal(FrameBufferTrim.IdleKeep, buffer.Count);
            Check(buffer.ContainsKey(3600) && buffer.ContainsKey(3600 - 12), "newest frames (deepest jitter delay) kept");
            Check(!buffer.ContainsKey(3600 - (uint)FrameBufferTrim.IdleKeep), "older frames dropped");
            Equal(3600u - (uint)FrameBufferTrim.IdleKeep + 1, play);
            Check(3600u - play <= 1024, "spawn catch-up is short");
        });
        Run("idle frame trim follows a tick wraparound", delegate {
            SortedDictionary<uint, int> buffer = new SortedDictionary<uint, int>(); uint play = uint.MaxValue - 100, tick = play;
            for (int i = 0; i < 300; i++, tick++) { buffer[tick] = i; play = FrameBufferTrim.TrimIdle(buffer, tick, play, FrameBufferTrim.IdleKeep); }
            uint newest = tick - 1;
            Equal(FrameBufferTrim.IdleKeep, buffer.Count); Check(buffer.ContainsKey(newest), "newest kept"); Equal(newest - 31u, play);
        });
        Run("idle frame trim never moves an already-played stream backwards", delegate {
            SortedDictionary<uint, int> buffer = new SortedDictionary<uint, int>();
            Equal(501u, FrameBufferTrim.TrimIdle(buffer, 500, 501, FrameBufferTrim.IdleKeep));
            buffer[510] = 1; Equal(501u, FrameBufferTrim.TrimIdle(buffer, 510, 501, FrameBufferTrim.IdleKeep)); Equal(1, buffer.Count);
        });
        // T-201: after a local leave this side's log said "partner quit the game" (the text sent to the partner).
        Run("a connection this side closed reads 'left: <why>' here, any other reason is kept", delegate {
            string goodbye = SessionMachine.PartnerGoodbye;
            Check(SessionMachine.OwnDisconnectReason(goodbye, true, "returned to the title screen") == "left: returned to the title screen", "local leave");
            Check(SessionMachine.OwnDisconnectReason(goodbye, false, "returned to the title screen") == goodbye, "the partner really quit");
            Check(SessionMachine.OwnDisconnectReason("timed out", true, "returned to the title screen") == "timed out", "another reason on a closed connection");
            Check(SessionMachine.OwnDisconnectReason(null, false, null) == "", "no reason");
            Check(SessionMachine.OwnDisconnectReason(goodbye, true, null) == "left: ", "no why");
        });
        // T-213 (4): a straggler of a seat's previous player (a much higher tick) had set the play position: the new
        // player's frames, far behind it, were all dropped as "late copies" until their ticks caught up.
        if (Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_STREAM_RESTART") == "1") FrameBufferTrim.MutateNoRestart = true;
        Run("frame admission: late copies are dropped, a frame far behind the play position restarts the stream", delegate {
            Equal(FrameBufferTrim.Admit.Take, FrameBufferTrim.Judge(false, 0, 5));
            Equal(FrameBufferTrim.Admit.Take, FrameBufferTrim.Judge(true, 100, 100));
            Equal(FrameBufferTrim.Admit.Take, FrameBufferTrim.Judge(true, 100, 100100));
            Equal(FrameBufferTrim.Admit.Drop, FrameBufferTrim.Judge(true, 100, 99));
            Equal(FrameBufferTrim.Admit.Drop, FrameBufferTrim.Judge(true, 5000, 5000 - (uint)FrameBufferTrim.RestartGap));
            Equal(FrameBufferTrim.Admit.Restart, FrameBufferTrim.Judge(true, 5000, 4999 - (uint)FrameBufferTrim.RestartGap));
            Equal(FrameBufferTrim.Admit.Restart, FrameBufferTrim.Judge(true, 100000, 7));
        });
        // T-213 review (5): the previous player of the seat had sent only a few hundred frames, so its straggler put the
        // play position a few hundred ticks ahead of the new player: those frames were dropped one by one for up to
        // 17 s. A real late copy is at most about 36 ticks back (32 packets, 4 frames each packet).
        Run("frame admission: a frame a few hundred ticks behind the play position restarts the stream too", delegate {
            Equal(FrameBufferTrim.Admit.Drop, FrameBufferTrim.Judge(true, 700, 700 - 36));
            Equal(FrameBufferTrim.Admit.Restart, FrameBufferTrim.Judge(true, 700, 100));
            Equal(FrameBufferTrim.Admit.Restart, FrameBufferTrim.Judge(true, 400, 1));
        });
        Run("frame admission follows a tick wraparound", delegate {
            Equal(FrameBufferTrim.Admit.Take, FrameBufferTrim.Judge(true, uint.MaxValue - 1, 2));
            Equal(FrameBufferTrim.Admit.Drop, FrameBufferTrim.Judge(true, 2, uint.MaxValue - 1));
        });
        // The stream as NetPlay.Receive keeps it: a play position, set by the first frame and moved on by playing.
        Run("a new player's low ticks play again after a straggler of the previous player", delegate {
            bool havePlay = false; uint playTick = 0; int taken = 0;
            Action<uint> receive = delegate(uint tick) {
                FrameBufferTrim.Admit admit = FrameBufferTrim.Judge(havePlay, playTick, tick);
                if (admit == FrameBufferTrim.Admit.Drop) return;
                if (admit == FrameBufferTrim.Admit.Restart) havePlay = false;
                taken++;
                if (!havePlay) { havePlay = true; playTick = tick; }
            };
            receive(1); receive(2); playTick = 3;          // the new player's first frames, played
            receive(90000); playTick = 90001;              // the previous player's straggler, played (the catch-up jumps to it)
            taken = 0; for (uint tick = 3; tick < 10; tick++) receive(tick);
            Equal(7, taken); Equal(3u, playTick);
        });
        // T-251 (playtest 6, 34% loss): "it slides around, I saw you go through the wall". The stand-in's position error
        // was pulled in 20% a step by writing the position, with no collision: across a wall it slid through it.
        if (Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STANDIN_SLIDES") == "1") StandInCorrection.MutateNoWallSnap = true;
        Run("stand-in: a pull whose path crosses a wall jumps to the owner instead of sliding through (T-251)", delegate {
            FakeWalls walls = new FakeWalls().Box(0f, -5f, 0.3f, 5f);   // a wall 0.3 thick
            float x = -0.5f, y = 0f;                                   // stopped against it; the owner went on to x = 1.2
            int snaps = Follow(walls, ref x, ref y, 1.2f, 0f, 30);
            Equal(1, snaps); Check(x == 1.2f && y == 0f, "at the owner (" + x + "," + y + ")");
        });
        Run("stand-in: a floor or ceiling between it and its owner is a wall too (T-251)", delegate {
            FakeWalls walls = new FakeWalls().Box(-10f, 1f, 10f, 1.4f);   // a ledge: the owner jumped onto it
            float x = 0.5f, y = 0.5f;
            Equal(1, Follow(walls, ref x, ref y, 0.2f, 2.0f, 20));
            Check(y == 2.0f, "on the ledge (" + y + ")");
        });
        Run("stand-in: with a free path the error is pulled in smoothly, no jump (T-251)", delegate {
            FakeWalls walls = new FakeWalls().Box(5f, -5f, 5.3f, 5f);   // a wall, but not between them
            float x = 0f, y = 0f, last = 3.5f;
            for (int i = 0; i < 25; i++)
            {
                Equal(0, Follow(walls, ref x, ref y, 3.5f, 0f, 1));
                float e = 3.5f - x;
                Check(e < last && e > last * 0.75f, "step " + i + ": error " + e + " after " + last + " (20% a step)");
                last = e;
            }
            Check(last < 0.02f, "converged (" + last + ")");
            Check(walls.Calls > 0, "the path was checked");
        });
        Run("stand-in: far errors jump and tiny ones cast no line, as before (T-251)", delegate {
            FakeWalls walls = new FakeWalls().Box(0f, -5f, 0.3f, 5f);
            Equal(StandInCorrection.Fix.Snap, StandInCorrection.Decide(4.5f, 4f, -1f, 0f, 3.5f, 0f, walls));
            Equal(StandInCorrection.Fix.Snap, StandInCorrection.Decide(9f, 4f, -1f, 0f, 8f, 0f, null));
            Equal(StandInCorrection.Fix.None, StandInCorrection.Decide(0.01f, 4f, 0f, 0f, 0.01f, 0f, walls));
            Equal(StandInCorrection.Fix.Blend, StandInCorrection.Decide(0.05f, 4f, -0.02f, 0f, 0.03f, 0f, walls));
            Equal(0, walls.Calls);
            Equal(StandInCorrection.Fix.Blend, StandInCorrection.Decide(2f, 4f, -1f, 0f, 1f, 0f, null));   // no probe: the old pull
        });
        // T-251: during a stall the stand-in repeats its owner's last input (extrapolation) only briefly. The starved path
        // stopped after 6 steps, but a burst whose first ticks were lost replayed the stale input again, uncapped.
        Run("stand-in: a stall followed by a burst with lost ticks repeats the stale input at most 6 steps (T-251)", delegate {
            int missing = 0, repeated = 0, worst = 0, holds = 0;
            Action<bool, bool> step = delegate(bool have, bool newer) {
                StandInCorrection.Step s = StandInCorrection.NextStep(have, newer, ref missing);
                if (s == StandInCorrection.Step.Play) { repeated = 0; return; }
                if (StandInCorrection.Holds(s)) { holds++; return; }
                repeated++; if (repeated > worst) worst = repeated;
            };
            for (int i = 0; i < 10; i++) step(true, true);     // frames play
            for (int i = 0; i < 20; i++) step(false, false);   // the stream stalls (starved)
            for (int i = 0; i < 12; i++) step(false, true);    // a burst arrives: its first 12 ticks were lost
            for (int i = 0; i < 5; i++) step(true, true);
            Check(worst <= StandInCorrection.RepeatSteps, "the last input was repeated " + worst + " steps in a row");
            Equal(20 + 12 - StandInCorrection.RepeatSteps, holds);
            Equal(0, missing);
        });
        Run("stand-in: a long run of lost ticks alone is capped too, and a lost tick still moves the stream on (T-251)", delegate {
            int missing = 0;
            for (int i = 1; i <= StandInCorrection.RepeatSteps; i++) Equal(StandInCorrection.Step.RepeatLost, StandInCorrection.NextStep(false, true, ref missing));
            Equal(StandInCorrection.Step.HoldLost, StandInCorrection.NextStep(false, true, ref missing));
            Check(StandInCorrection.Lost(StandInCorrection.Step.HoldLost) && StandInCorrection.Holds(StandInCorrection.Step.HoldLost), "held and moved on");
            Equal(StandInCorrection.Step.HoldLate, StandInCorrection.NextStep(false, false, ref missing));
            Check(!StandInCorrection.Lost(StandInCorrection.Step.HoldLate), "a starved hold waits");
            Equal(StandInCorrection.Step.Play, StandInCorrection.NextStep(true, false, ref missing)); Equal(0, missing);
        });
        Run("stand-in: under scattered loss (runs of up to 6 lost ticks) it never lets go of the buttons (T-251)", delegate {
            int missing = 0, holds = 0; uint seed = 251;
            for (int i = 0; i < 3600; i++)
            {
                seed = seed * 1664525U + 1013904223U;
                bool lose = (seed >> 24) < 87 && missing < StandInCorrection.RepeatSteps;   // ~34% of ticks lost, runs capped at 6
                if (StandInCorrection.Holds(StandInCorrection.NextStep(!lose, true, ref missing))) holds++;
            }
            Equal(0, holds);
        });
        Console.WriteLine("NetCore: " + cases + " cases, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
