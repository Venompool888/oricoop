using System;
using System.IO;
using OriCoop.Seams;

// Smoke test for the seams + a reusable in-memory bus pattern for module tests.
static class CoreTests
{
    sealed class FakeBus : INetBus
    {
        public bool Enabled { get { return true; } }
        public bool Connected { get { return true; } }
        public bool IsHost { get; set; }
        public int Sender { get; set; }
        readonly Action<BinaryReader>[] m_handlers = new Action<BinaryReader>[256];
        public FakeBus Peer;
        public void On(byte type, Action<BinaryReader> handler) { m_handlers[type] = handler; }
        public void Send(byte type, bool reliable, Action<BinaryWriter> body) { SendTo(1, type, reliable, body); }
        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            MemoryStream ms = new MemoryStream();
            body(new BinaryWriter(ms));
            Action<BinaryReader> h = Peer.m_handlers[type];
            if (h != null) { Peer.Sender = 0; h(new BinaryReader(new MemoryStream(ms.ToArray()))); }
        }
        public void Forward(params byte[] types) { }
        public event Action SessionStarted { add { } remove { } }
        public event Action SessionEnded { add { } remove { } }
        public event Action<int> PlayerJoined { add { } remove { } }
        public event Action<int> PlayerLeft { add { } remove { } }
    }

    static int Main()
    {
        FakeBus a = new FakeBus(), b = new FakeBus();
        a.Peer = b; b.Peer = a;
        int got = -1;
        b.On(7, r => got = r.ReadInt32());
        Services.Net = a;
        Services.Net.Send(7, true, w => w.Write(42));
        if (got != 42) { Console.WriteLine("FAIL bus round trip: " + got); return 1; }
        Console.WriteLine("ok bus round trip");
        return 0;
    }
}
