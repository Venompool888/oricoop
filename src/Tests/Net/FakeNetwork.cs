using System;
using System.Collections.Generic;
using System.Net;
using OriCoop.Net;

sealed class FakeClock
{
    public double Milliseconds;
    public double Seconds { get { return Milliseconds / 1000.0; } }
    public void Advance(double milliseconds) { Milliseconds += milliseconds; }
}

sealed class FakeNetwork
{
    sealed class Datagram
    {
        public byte[] Bytes;
        public EndPoint From, To;
        public double Due;
        public long Order;
    }

    readonly FakeClock clock;
    readonly Random random = new Random(12345);
    readonly List<Datagram> pending = new List<Datagram>();
    readonly Dictionary<int, long> lastDelivered = new Dictionary<int, long>();
    long order;

    public int LossPercent, DuplicatePercent, DelayMs, JitterMs;
    public int Sent, Dropped, Duplicated, Reordered;
    public Func<byte[], EndPoint, EndPoint, bool> Drop;

    public FakeNetwork(FakeClock clock) { this.clock = clock; }
    public FakeTransport Open(int port) { return new FakeTransport(this, port); }

    internal void Send(byte[] data, int offset, int length, EndPoint from, EndPoint to)
    {
        byte[] copy = new byte[length];
        Buffer.BlockCopy(data, offset, copy, 0, length);
        Sent++;
        if ((Drop != null && Drop(copy, from, to)) || random.Next(100) < LossPercent)
        {
            Dropped++;
            return;
        }
        Schedule(copy, from, to);
        if (random.Next(100) < DuplicatePercent) { Duplicated++; Schedule(copy, from, to); }
    }

    void Schedule(byte[] bytes, EndPoint from, EndPoint to)
    {
        double jitter = JitterMs == 0 ? 0 : random.Next(JitterMs + 1);
        pending.Add(new Datagram { Bytes = bytes, From = from, To = to,
            Due = clock.Milliseconds + DelayMs + jitter, Order = ++order });
    }

    internal int Receive(int port, byte[] buffer, out EndPoint from)
    {
        int selected = -1;
        for (int i = 0; i < pending.Count; i++)
        {
            Datagram item = pending[i];
            if (((IPEndPoint)item.To).Port != port || item.Due > clock.Milliseconds) continue;
            if (selected < 0 || item.Due < pending[selected].Due ||
                (item.Due == pending[selected].Due && item.Order < pending[selected].Order)) selected = i;
        }
        if (selected < 0) { from = null; return 0; }
        Datagram packet = pending[selected];
        pending.RemoveAt(selected);
        long last;
        if (lastDelivered.TryGetValue(port, out last) && packet.Order < last) Reordered++;
        lastDelivered[port] = Math.Max(last, packet.Order);
        from = packet.From;
        int length = Math.Min(buffer.Length, packet.Bytes.Length);
        Buffer.BlockCopy(packet.Bytes, 0, buffer, 0, length);
        return length;
    }
}

sealed class FakeTransport : IDatagramTransport
{
    readonly FakeNetwork network;
    readonly IPEndPoint endpoint;
    public FakeTransport(FakeNetwork network, int port)
    {
        this.network = network;
        endpoint = new IPEndPoint(IPAddress.Loopback, port);
    }
    public EndPoint Endpoint { get { return endpoint; } }
    public int LocalPort { get { return endpoint.Port; } }
    public void SendTo(byte[] data, int offset, int length, EndPoint to)
    {
        network.Send(data, offset, length, endpoint, to);
    }
    public int Receive(byte[] buffer, out EndPoint from) { return network.Receive(LocalPort, buffer, out from); }
    public void Dispose() { }
}
