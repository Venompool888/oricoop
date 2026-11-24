using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using OriCoop.Net;

// NetPeer.Poll/Flush run every frame for every connection. A frame's packets must not each allocate an MTU-sized
// buffer, a fresh writer, several lists and a closure; and reusing those buffers must never leak one packet's bytes
// into another (a delayed packet keeps its own copy).
static class PeerAllocTests
{
    static int failures;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    // GC.GetAllocatedBytesForCurrentThread exists on Mono and .NET Core, not on .NET Framework 3.5/4.x.
    static readonly MethodInfo AllocatedBytes = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
    static long Allocated() { return (long)AllocatedBytes.Invoke(null, null); }

    // Two endpoints joined by in-memory queues; delivery happens on the receiver's next Receive.
    sealed class Wire : IDatagramTransport
    {
        public readonly IPEndPoint Endpoint;
        public Wire Other;
        readonly Queue<byte[]> inbox = new Queue<byte[]>();
        public Wire(int port) { Endpoint = new IPEndPoint(IPAddress.Loopback, port); }
        public int LocalPort { get { return Endpoint.Port; } }
        public void SendTo(byte[] data, int offset, int length, EndPoint to)
        {
            byte[] copy = new byte[length]; Buffer.BlockCopy(data, offset, copy, 0, length); Other.inbox.Enqueue(copy);
        }
        public int Receive(byte[] buffer, out EndPoint from)
        {
            if (inbox.Count == 0) { from = null; return 0; }
            byte[] bytes = inbox.Dequeue(); Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length); from = Other.Endpoint; return bytes.Length;
        }
        public void Dispose() { }
    }

    sealed class Rig
    {
        public double Now;
        public readonly Wire HostWire = new Wire(1), GuestWire = new Wire(2);
        public readonly NetPeer Host, Guest;
        public Rig()
        {
            HostWire.Other = GuestWire; GuestWire.Other = HostWire;
            Host = new NetPeer(HostWire, delegate { return Now; });
            Guest = new NetPeer(GuestWire, delegate { return Now; });
            Host.Listen(); Guest.Connect(HostWire.Endpoint);
            for (int i = 0; i < 20; i++) { Host.Poll(); Guest.Poll(); Now += 16; }
            Check(Host.State == PeerState.Connected && Guest.State == PeerState.Connected, "no connection");
        }
    }

    static void Drain(NetPeer peer) { byte[] m; bool r; while (peer.TryReceive(out m, out r)) { } }

    static void SteadyFrameAllocations()
    {
        if (AllocatedBytes == null) { Console.WriteLine("  (no allocation counter on this runtime: skipped)"); return; }
        Rig rig = new Rig();
        byte[] frame = new byte[100], state = new byte[40];
        long polled = 0; int frames = 600;
        for (int i = 0; i < frames + 200; i++)
        {
            rig.Guest.SendUnreliable(frame, 0, frame.Length); rig.Host.SendUnreliable(frame, 0, frame.Length);
            if (i % 10 == 0) rig.Guest.SendReliable(state, 0, state.Length);
            long before = Allocated();
            rig.Host.Poll();
            if (i >= 200) polled += Allocated() - before;   // first 200 frames warm up queues and lists
            rig.Guest.Poll(); Drain(rig.Host); Drain(rig.Guest); rig.Now += 16;
        }
        long perFrame = polled / frames;
        // What stays: the received payload copies, the sent packet's exact-size array, its history entry and the
        // receive parse, about 1 KB here. Before: about 3.2 KB, mostly a fresh 1200-byte MemoryStream buffer per packet sent.
        Check(perFrame < 1400, "host Poll allocated " + perFrame + " bytes per frame");
    }

    static void DelayedPacketsKeepTheirBytes()
    {
        Rig rig = new Rig();
        rig.Guest.Sim.LagMs = 50;
        for (int i = 0; i < 20; i++)
        {
            byte[] m = new byte[900]; for (int j = 0; j < m.Length; j++) m[j] = (byte)(i * 7 + j);
            rig.Guest.SendUnreliable(m, 0, m.Length);
            byte[] r = new byte[1500 + i * 100]; for (int j = 0; j < r.Length; j++) r[j] = (byte)(i + j * 3);
            rig.Guest.SendReliable(r, 0, r.Length);
        }
        rig.Guest.Flush();   // many packets in one flush, all held back by the simulated lag
        int unreliable = 0, reliable = 0;
        for (int step = 0; step < 100; step++)
        {
            rig.Host.Poll(); rig.Guest.Poll(); rig.Now += 16;
            byte[] m; bool isReliable;
            while (rig.Host.TryReceive(out m, out isReliable))
            {
                if (isReliable)
                {
                    int i = reliable++;
                    Check(m.Length == 1500 + i * 100, "reliable " + i + " has length " + m.Length);
                    for (int j = 0; j < m.Length; j++) Check(m[j] == (byte)(i + j * 3), "reliable " + i + " corrupted at " + j);
                }
                else
                {
                    int i = m[0] / 7;
                    Check(m.Length == 900, "unreliable length " + m.Length);
                    for (int j = 0; j < m.Length; j++) Check(m[j] == (byte)(m[0] + j), "unreliable corrupted at " + j);
                    unreliable++;
                }
            }
        }
        Check(reliable == 20, "reliable messages delivered: " + reliable);
        Check(unreliable == 20, "unreliable messages delivered: " + unreliable);
    }

    static int Main()
    {
        Run("a steady frame does not allocate a buffer per packet", SteadyFrameAllocations);
        Run("packets held back by lag keep their own bytes", DelayedPacketsKeepTheirBytes);
        return failures == 0 ? 0 : 1;
    }
}
