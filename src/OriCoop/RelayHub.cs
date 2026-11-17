using System;
using System.Collections.Generic;
using System.Net;

namespace OriCoop.Net
{
    public sealed class RelayHub : IDisposable
    {
        readonly RelayTransport relay;
        readonly byte[] buffer = new byte[65536];
        readonly Dictionary<EndPoint, RoutedTransport> routes = new Dictionary<EndPoint, RoutedTransport>();
        readonly Dictionary<EndPoint, NetPeer> peers = new Dictionary<EndPoint, NetPeer>();

        public RelayHub(RelayTransport relay)
        {
            this.relay = relay;
            relay.PeerLeft += OnPeerLeft;
        }
        public int LocalPort { get { return relay.LocalPort; } }
        public event Action<NetPeer> PeerCreated, Connected;
        public event Action<NetPeer, string> Disconnected;

        public void Poll()
        {
            EndPoint from; int length;
            while ((length = relay.Receive(buffer, out from)) > 0)
            {
                RoutedTransport route;
                if (!routes.TryGetValue(from, out route))
                {
                    if (length != 13 || buffer[0] != 0x43 || buffer[1] != 0x4f || buffer[2] != 1) continue;
                    if (NetPeer.Trace != null) NetPeer.Trace("relay hub Request from " + from + ", peers=" + peers.Count);
                    EndPoint endpoint = from;
                    route = new RoutedTransport(relay);
                    NetPeer peer = new NetPeer(route);
                    routes.Add(endpoint, route); peers.Add(endpoint, peer);
                    peer.Connected += delegate
                    {
                        NetPeer current;
                        if (peers.TryGetValue(endpoint, out current) && current == peer && Connected != null) Connected(peer);
                    };
                    peer.Disconnected += delegate(string reason) { Remove(endpoint, peer, reason); };
                    if (PeerCreated != null) PeerCreated(peer);
                    peer.Listen();
                    route.Enqueue(buffer, length, endpoint);
                    peer.Poll();
                    if (peer.State != PeerState.Connected) Remove(endpoint, peer, "connection not accepted");
                    continue;
                }
                route.Enqueue(buffer, length, from);
            }
            foreach (NetPeer peer in new List<NetPeer>(peers.Values)) peer.Poll();
        }

        void OnPeerLeft(EndPoint endpoint)
        {
            NetPeer peer;
            if (peers.TryGetValue(endpoint, out peer)) Remove(endpoint, peer, "relay peer left");
        }
        void Remove(EndPoint endpoint, NetPeer peer, string reason)
        {
            NetPeer current;
            if (!peers.TryGetValue(endpoint, out current) || current != peer) return;
            routes[endpoint].Dispose(); routes.Remove(endpoint); peers.Remove(endpoint);
            if (Disconnected != null) Disconnected(peer, reason);
        }
        public void Flush() { foreach (NetPeer peer in new List<NetPeer>(peers.Values)) peer.Flush(); }
        public void Disconnect(string reason)
        {
            foreach (NetPeer peer in new List<NetPeer>(peers.Values)) { peer.Disconnect(reason); peer.Flush(); }
        }
        public void Dispose()
        {
            relay.PeerLeft -= OnPeerLeft;
            foreach (RoutedTransport route in routes.Values) route.Dispose();
            routes.Clear(); peers.Clear(); relay.Dispose();
        }
    }
}
