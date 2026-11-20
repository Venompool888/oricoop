using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Saves;
using OriCoop.Seams;

sealed class FakeClock : IClock
{
    public float Now;
    public float Time { get { return Now; } }
    public float RealTime { get { return Now; } }
    public int Frame { get { return 0; } }
}

sealed class FakeNetwork
{
    public sealed class Packet
    {
        public int From, To;
        public byte Type;
        public byte[] Body;
    }
    readonly Dictionary<int, FakeBus> m_buses = new Dictionary<int, FakeBus>();
    readonly Queue<Packet> m_pending = new Queue<Packet>();
    public readonly List<Packet> Sent = new List<Packet>();
    public int Host = 1;   // seat that is the host now (T-126: the server can hand the role to a guest)

    public FakeBus Add(int seat)
    {
        FakeBus bus = new FakeBus(this, seat);
        m_buses.Add(seat, bus);
        if (seat != 1 && m_buses.ContainsKey(1)) m_buses[1].Join(seat);
        return bus;
    }

    public void Remove(int seat)
    {
        m_buses.Remove(seat);
        Queue<Packet> remaining = new Queue<Packet>();
        while (m_pending.Count > 0)
        {
            Packet p = m_pending.Dequeue();
            if (p.From != seat && p.To != seat) remaining.Enqueue(p);
        }
        while (remaining.Count > 0) m_pending.Enqueue(remaining.Dequeue());
        if (m_buses.ContainsKey(1)) m_buses[1].Leave(seat);
    }

    public void Send(int from, int to, byte type, byte[] body)
    {
        Packet p = new Packet(); p.From = from; p.To = to; p.Type = type; p.Body = body;
        Sent.Add(p); m_pending.Enqueue(p);
    }

    public void Broadcast(int from, byte type, byte[] body)
    {
        List<int> seats = new List<int>(m_buses.Keys); seats.Sort();
        foreach (int seat in seats)
            if (seat != from && (from == Host || seat == Host)) Send(from, seat, type, body);
    }

    public bool PumpOne()
    {
        if (m_pending.Count == 0) return false;
        Packet packet = m_pending.Dequeue();
        FakeBus bus;
        if (m_buses.TryGetValue(packet.To, out bus)) bus.Deliver(packet);
        return true;
    }
    public void Pump() { while (PumpOne()) { } }
}

sealed class FakeBus : INetBus
{
    readonly FakeNetwork m_network;
    readonly int m_seat;
    readonly Dictionary<byte, Action<BinaryReader>> m_handlers = new Dictionary<byte, Action<BinaryReader>>();
    public FakeBus(FakeNetwork network, int seat) { m_network = network; m_seat = seat; }
    public bool Enabled { get { return true; } }
    public bool Connected { get { return true; } }
    public bool IsHost { get { return m_seat == m_network.Host; } }
    public int Sender { get; private set; }
    public int HostSeat { get { return m_hostSeat; } set { m_hostSeat = value; } } int m_hostSeat = 1;
    public void On(byte type, Action<BinaryReader> handler) { m_handlers[type] = handler; }
    byte[] Body(bool reliable, Action<BinaryWriter> body)
    {
        if (!reliable) throw new Exception("save protocol was sent unreliably");
        MemoryStream stream = new MemoryStream(); body(new BinaryWriter(stream)); return stream.ToArray();
    }
    public void Send(byte type, bool reliable, Action<BinaryWriter> body) { m_network.Broadcast(m_seat, type, Body(reliable, body)); }
    public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body) { m_network.Send(m_seat, seat, type, Body(reliable, body)); }
    public void Forward(params byte[] types) { }
    public event Action SessionStarted;
    public event Action SessionEnded;
    public event Action<int> PlayerJoined;
    public event Action<int> PlayerLeft;
    public void Start() { if (SessionStarted != null) SessionStarted(); }
    public void End() { if (SessionEnded != null) SessionEnded(); }
    public void Join(int seat) { if (PlayerJoined != null) PlayerJoined(seat); }
    public void Leave(int seat) { if (PlayerLeft != null) PlayerLeft(seat); }
    public void Deliver(FakeNetwork.Packet packet)
    {
        Action<BinaryReader> handler;
        if (!m_handlers.TryGetValue(packet.Type, out handler)) return;
        int previous = Sender; Sender = packet.From;
        try { handler(new BinaryReader(new MemoryStream(packet.Body))); }
        finally { Sender = previous; }
    }
}

sealed class FakeLineage : ILineageActions
{
    readonly INetBus m_bus;
    public string Hash = "host", Outcome = "";
    public LineageReply Reply = new LineageReply();
    public LineageCommit ReceivedCommit;
    public byte[] StoredBytes = new byte[] { 7, 8 }, UploadBytes;
    public int CommitsTo, Commits, Choosing, Cancellations, InvalidUploads, Adoptions, ReplyTimeouts, UploadTimeouts, Ends, Resets, OfferFailures;
    public bool DivergedHost, NoFreeSlot, FailOffer;
    public FakeLineage(INetBus bus) { m_bus = bus; }
    public string PlayerId { get { return m_bus.IsHost ? "host-id" : "guest-id"; } }
    public string LocalName { get { return m_bus.IsHost ? "host-name" : "guest-name"; } }
    public string HostHash { get { return Hash; } }
    public LineageOffer PrepareOffer() { if (FailOffer) throw new IOException("offer failed"); return new LineageOffer { Coop = "lineage", Hash = Hash, Summary = "host summary" }; }
    public void OfferSent(LineageOffer offer) { }
    public void OfferFailed(Exception error) { OfferFailures++; }
    public LineageReply PrepareReply(LineageOffer offer) { return Reply; }
    public void ReplySent(LineageOffer offer, LineageReply reply) { }
    public void ReplyReceived(LineageReply reply) { }
    public void NewReply(LineageReply reply) { Outcome = "new"; NoFreeSlot = !reply.FreeSlot; }
    public void SameReply() { Outcome = "same"; }
    public void BeginChoosing(LineageReply reply) { Choosing++; }
    public void ChoosingSent() { }
    public void HostChosen(bool diverged) { if (diverged) { DivergedHost = true; Outcome = "diverged-host"; } }
    public void Commit()
    {
        Commits++;
        m_bus.Send(LineageWire.Commit, true, delegate(BinaryWriter w)
        {
            LineageCommit commit = new LineageCommit();
            commit.Coop = "lineage"; commit.PlayerId = PlayerId; commit.Name = LocalName; commit.SaveId = "save"; commit.Bytes = StoredBytes;
            LineageWire.WriteCommit(w, commit);
        });
    }
    public void CommitTo(int seat)
    {
        CommitsTo++;
        m_bus.SendTo(seat, LineageWire.Commit, true, delegate(BinaryWriter w)
        {
            LineageCommit commit = new LineageCommit();
            commit.Coop = "lineage"; commit.PlayerId = PlayerId; commit.Name = LocalName; commit.SaveId = "save"; commit.Bytes = StoredBytes;
            LineageWire.WriteCommit(w, commit);
        });
    }
    public void SetCancelOutcome() { Outcome = "cancel"; }
    public void Cancelled() { }
    public bool ValidUpload(int length, byte[] bytes)
    {
        if (length > 0 && bytes.Length == length) return true;
        InvalidUploads++; return false;
    }
    public void PrepareAdoption(byte[] bytes) { StoredBytes = bytes; Hash = "adopted"; Outcome = "diverged-partner"; }
    public void Adopt(byte[] bytes) { Adoptions++; }
    public void AdoptionFailed(Exception error) { throw error; }
    public void AdoptionFinished() { }
    public void CommitReceived(LineageCommit commit) { ReceivedCommit = commit; }
    public byte[] ReadUpload() { return UploadBytes; }
    public void UploadSent(byte[] bytes) { }
    public void RequestChoosing() { Choosing++; }
    public void RequestCancel() { Cancellations++; }
    public void ReplyTimeout() { ReplyTimeouts++; Outcome = "legacy"; }
    public void UploadTimeout() { UploadTimeouts++; }
    public void End() { Ends++; }
    public void Reset() { Resets++; }
}

sealed class Scenario
{
    public readonly FakeNetwork Network = new FakeNetwork();
    public readonly FakeClock Clock = new FakeClock();
    public readonly FakeBus HostBus, GuestBus;
    public readonly FakeLineage Host, Guest;
    public readonly LineageMachine HostMachine, GuestMachine;
    public Scenario()
    {
        // The host's machine runs before any guest joins (as in the game, where it is created at start-up).
        HostBus = Network.Add(1); Host = new FakeLineage(HostBus);
        HostMachine = new LineageMachine(HostBus, Clock, Host); HostMachine.Init();
        GuestBus = Network.Add(2); Guest = new FakeLineage(GuestBus);
        GuestMachine = new LineageMachine(GuestBus, Clock, Guest); GuestMachine.Init();
    }
    public void Offer() { HostMachine.HostGate(); Network.Pump(); }
    public void Diverge(string hash)
    {
        Guest.Reply.Found = true; Guest.Reply.Slot = 2; Guest.Reply.Hash = hash; Guest.Reply.LastSync = "old";
        Offer();
    }
}
