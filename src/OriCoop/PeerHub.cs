using System;
using System.Collections.Generic;
using System.Net;

namespace OriCoop.Net
{
    public sealed class PeerHub : IDisposable
    {
        readonly UdpTransport socket;
        readonly byte[] buffer = new byte[65536];
        readonly Dictionary<EndPoint, RoutedTransport> routes = new Dictionary<EndPoint, RoutedTransport>();
        readonly Dictionary<EndPoint, NetPeer> peers = new Dictionary<EndPoint, NetPeer>();

        public PeerHub(int port) { socket = new UdpTransport(port); }
        public int LocalPort { get { return socket.LocalPort; } }
        public event Action<NetPeer> PeerCreated, Connected;
        public event Action<NetPeer, string> Disconnected;

        public void Poll()
        {
            EndPoint from; int length;
            while ((length = socket.Receive(buffer, out from)) > 0)
            {
                RoutedTransport route;
                if (!routes.TryGetValue(from, out route))
                {
                    if (length != 13 || buffer[0] != 0x43 || buffer[1] != 0x4f || buffer[2] != 1) continue;
                    if (NetPeer.Trace != null) NetPeer.Trace("hub Request from " + from + ", peers=" + peers.Count);
                    if (peers.Count >= Players.MaxSeats - 1)
                    {
                        // Reuse NetPeer's existing rejection of an unrelated endpoint with "full".
                        foreach (KeyValuePair<EndPoint, NetPeer> pair in peers)
                            if (pair.Value.State == PeerState.Connected)
                            {
                                routes[pair.Key].Enqueue(buffer, length, from);
                                pair.Value.Poll();
                                break;
                            }
                        continue;
                    }
                    route = new RoutedTransport(socket);
                    NetPeer peer = new NetPeer(route);
                    EndPoint endpoint = from;
                    routes.Add(endpoint, route); peers.Add(endpoint, peer);
                    peer.Connected += delegate { if (Connected != null) Connected(peer); };
                    peer.Disconnected += delegate(string reason)
                    {
                        routes.Remove(endpoint); peers.Remove(endpoint); route.Dispose();
                        if (Disconnected != null) Disconnected(peer, reason);
                    };
                    if (PeerCreated != null) PeerCreated(peer);
                    peer.Listen();
                    route.Enqueue(buffer, length, from);
                    peer.Poll();
                    if (peer.State != PeerState.Connected)
                    {
                        routes.Remove(endpoint); peers.Remove(endpoint); route.Dispose();
                    }
                    continue;
                }
                route.Enqueue(buffer, length, from);
            }
            foreach (NetPeer peer in new List<NetPeer>(peers.Values)) peer.Poll();
        }

        public void Flush() { foreach (NetPeer peer in peers.Values) peer.Flush(); }
        public void Disconnect(string reason)
        {
            foreach (NetPeer peer in peers.Values) { peer.Disconnect(reason); peer.Flush(); }
        }
        public void Dispose()
        {
            foreach (RoutedTransport route in routes.Values) route.Dispose();
            routes.Clear(); peers.Clear(); socket.Dispose();
        }
    }

    sealed class RoutedTransport : IDatagramTransport
    {
        readonly IDatagramTransport socket;
        readonly Queue<Datagram> incoming = new Queue<Datagram>();
        sealed class Datagram { public byte[] Bytes; public EndPoint From; }

        public RoutedTransport(IDatagramTransport socket) { this.socket = socket; }
        public int LocalPort { get { return socket.LocalPort; } }
        public void SendTo(byte[] data, int offset, int length, EndPoint to) { socket.SendTo(data, offset, length, to); }
        internal void Enqueue(byte[] data, int length, EndPoint from)
        {
            byte[] bytes = new byte[length]; Buffer.BlockCopy(data, 0, bytes, 0, length);
            incoming.Enqueue(new Datagram { Bytes = bytes, From = from });
        }
        public int Receive(byte[] buffer, out EndPoint from)
        {
            if (incoming.Count == 0) { from = null; return 0; }
            Datagram item = incoming.Dequeue(); from = item.From;
            Buffer.BlockCopy(item.Bytes, 0, buffer, 0, item.Bytes.Length); return item.Bytes.Length;
        }
        public void Dispose() { incoming.Clear(); }
    }
}
