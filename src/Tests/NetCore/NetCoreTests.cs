using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Net;

static class NetCoreTests
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
        public bool EnabledValue = true, Host, Factory, Steam, Hub, Relay, RelayReadyValue, SteamReadyValue, NoForward;
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
        public bool RelayActive { get { return Relay; } }
        public bool RelayReady { get { return RelayReadyValue; } }
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
        Run("seat message validates host steam and sender", delegate {
            Rig r = new Rig(false, false, false); Action<BinaryWriter> seat = delegate(BinaryWriter w) { w.Write((byte)3); };
            r.Receive(4, 2, true, 0, seat); r.Steam = true; r.Receive(4, 1, true, 0, seat); r.Steam = false; r.Host = true; r.Receive(4, 1, true, 0, seat);
            Equal(0, r.Seat); Equal("", Events(r));
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
        Run("unknown leave does nothing", delegate { Rig r = new Rig(false, true, false); r.Session.RemoteLeft(3); Equal("", Events(r)); });
        Run("left frame is accepted from host only", delegate {
            Rig r = new Rig(false, false, false); r.Seat = 2; r.Add(3, null); Action<BinaryWriter> body = delegate(BinaryWriter w) { w.Write((byte)3); };
            r.Receive(5, 3, true, 0, body); Equal(1, r.Session.Remotes.Count); r.Receive(5, 1, true, 0, body); Equal(0, r.Session.Remotes.Count); Equal("cleanup:3,left:3", Events(r));
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
        Run("relay and steam readiness set host phase", delegate {
            Rig r = new Rig(true, false, false); r.Single = null; r.Relay = true; Equal(SessionMachine.Phase.Starting, r.Session.CurrentPhase);
            r.RelayReadyValue = true; Equal(SessionMachine.Phase.Waiting, r.Session.CurrentPhase); r.Relay = false; r.Steam = true;
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
            r.Steam = true; Check(!r.Session.WaitingForSeat, "steam guest waiting");
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
        Console.WriteLine("NetCore: " + cases + " cases, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
