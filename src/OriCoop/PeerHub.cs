using System;
using System.Collections.Generic;
using System.Net;

namespace OriCoop.Net
{
    public sealed class PeerHub : IDisposable
    {
        readonly IDatagramTransport socket;
        readonly byte[] buffer = new byte[65536];
        readonly Dictionary<EndPoint, RoutedTransport> routes = new Dictionary<EndPoint, RoutedTransport>();
        readonly Dictionary<EndPoint, NetPeer> peers = new Dictionary<EndPoint, NetPeer>();

        public PeerHub(int port) : this(new UdpTransport(port)) { }
        public PeerHub(IDatagramTransport transport) { socket = transport; }
        public int LocalPort { get { return socket.LocalPort; } }
        public event Action<NetPeer> PeerCreated, Connected;
        public event Action<NetPeer, string> Disconnected;

        public void Poll() { Poll(true); }

        // flush=false: as NetPeer.Poll(false); the caller must Flush() afterwards.
        public void Poll(bool flush)
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
            foreach (NetPeer peer in new List<NetPeer>(peers.Values)) peer.Poll(flush);
        }

        public void Flush() { foreach (NetPeer peer in peers.Values) peer.Flush(); }
        // A lobby departure or a P2P failure ends only this route. Ordinary UDP routes time out in Poll.
        public void Drop(EndPoint endpoint, string reason)
        {
            NetPeer peer;
            if (peers.TryGetValue(endpoint, out peer)) peer.Disconnect(reason);
        }
        public void Disconnect(string reason, Action<NetPeer> beforeDisconnect = null)
        {
            foreach (NetPeer peer in peers.Values)
            {
                if (peer.State != PeerState.Connected && peer.State != PeerState.Connecting) continue;
                if (beforeDisconnect != null) beforeDisconnect(peer);
                peer.Disconnect(reason); peer.Flush();
            }
        }
        public void Dispose()
        {
            foreach (RoutedTransport route in routes.Values) route.Dispose();
            routes.Clear(); peers.Clear(); socket.Dispose();
        }
    }

}
