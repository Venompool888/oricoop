using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OriCoop.Net
{
    // A datagram transport that meets the peer through a small rendezvous server.
    // It relays until a local hole-punch succeeds, then sends marked raw datagrams.
    // All work happens in SendTo/Receive/Update on the caller's thread.
    public sealed class RelayTransport : IDatagramTransport
    {
        private const ushort Magic = 0x534F;
        private const byte OpHostRoom = 1;
        private const byte OpJoinRoom = 2;
        private const byte OpRoomCreated = 3;
        private const byte OpJoined = 4;
        private const byte OpPeerJoined = 5;
        private const byte OpJoinFailed = 6;
        private const byte OpRelay = 7;
        private const byte OpRelayed = 8;
        private const byte OpKeepAlive = 9;
        private const byte OpPeerLeft = 10;
        private const byte OpKeepAliveAck = 13;
        private const byte OpLeaveRoom = 14;
        private const byte MarkerData = 1;
        private const byte MarkerPunch = 2;
        private const byte MarkerPunchAck = 3;
        private const int MaxRelayPayload = 1300;
        private const double RetrySeconds = 0.5;
        private const double KeepAliveSeconds = 5.0;
        private const double PunchIntervalSeconds = 0.1;
        private const double PunchSeconds = 2.0;
        private const double DirectTimeoutSeconds = 3.0;
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private static readonly IPAddress VirtualAddress = IPAddress.Parse("10.255.255.1");
        private static readonly EndPoint VirtualRemote = new IPEndPoint(VirtualAddress, 1);
        private static readonly Random NonceSource = new Random();

        // Optional identity appended to HostRoom/JoinRoom for the relay operator's join log (SteamID64 and a
        // display name). The mod sets these (RelayList.Init); null/0/"" sends "Steam not running". Read once
        // per transport on the creating (main) thread. Older relays ignore the trailing bytes.
        public static Func<ulong> LocalSteamIdSource;
        public static Func<string> LocalNameSource;
        private const byte IdentityTag = 0x49;
        private const byte IdentityVersion = 1;
        private const int MaxIdentityNameBytes = 32;
        private readonly byte[] identity;

        private readonly IDatagramTransport udp;
        private readonly EndPoint server;
        private readonly bool hosting;
        private readonly uint nonce;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<double> now;
        private readonly byte[] receiveBuffer = new byte[65536];
        private sealed class Path
        {
            public int Seat;
            public EndPoint Address, Remote;
            public bool Direct, Blocked, ReceivedPayload;
            public double PunchStart = -1000000, LastPunch = -1000000, LastDirectReceive = -1000000;
        }
        private sealed class Datagram { public Path Path; public byte[] Bytes; }
        private readonly Queue<Datagram> incoming = new Queue<Datagram>();
        private readonly Dictionary<int, Path> paths = new Dictionary<int, Path>();
        private string roomCode;
        private string error;
        private bool ready;
        private bool forceRelay;
        private bool updating;
        private bool disposed;
        private double lastHostRoom;
        private double lastJoinRoom;
        private double lastKeepAlive;
        private double lastServerReceive;

        private RelayTransport(EndPoint server, bool hosting, string code)
            : this(server, hosting, code, null, null)
        {
        }

        internal RelayTransport(EndPoint server, bool hosting, string code, IDatagramTransport udp, Func<double> now)
        {
            if (server == null) throw new ArgumentNullException("server");
            this.server = server;
            this.hosting = hosting;
            this.roomCode = code;
            Seat = hosting ? 1 : 0;
            this.nonce = NewNonce();
            this.identity = BuildIdentity();
            this.udp = udp ?? new UdpTransport(0);
            this.now = now;
            this.lastHostRoom = -1000000;
            this.lastJoinRoom = -1000000;
            this.lastKeepAlive = -1000000;
            this.lastServerReceive = -1000000;
            Update();
        }

        public static RelayTransport Host(EndPoint server)
        {
            return new RelayTransport(server, true, null);
        }

        public static RelayTransport Join(EndPoint server, string code)
        {
            if (code == null) throw new ArgumentNullException("code");
            code = code.Trim().ToUpperInvariant();
            if (code.Length != 6) throw new ArgumentException("room code must be 6 characters", "code");
            return new RelayTransport(server, false, code);
        }

        public string RoomCode { get { return roomCode; } }
        public bool Ready { get { return ready; } }
        public bool Direct
        {
            get { foreach (Path peer in paths.Values) if (!peer.Direct) return false; return paths.Count > 0; }
        }
        public int Seat { get; private set; }
        public string Error { get { return error; } }
        public EndPoint RemoteEndPoint { get { return hosting ? new IPEndPoint(VirtualAddress, 2) : VirtualRemote; } }
        public int LocalPort { get { return udp.LocalPort; } }
        public event Action<string> Log;
        internal event Action<EndPoint> PeerLeft;

        private void LogState(string message)
        {
            Action<string> log = Log;
            if (log == null) return;
            try { log("local=" + LocalPort + " " + message); }
            catch (Exception) { } // Diagnostics must not interrupt the transport.
        }

        private void LogFirstPayload(Path peer, string path, int length)
        {
            if (peer.ReceivedPayload) return;
            peer.ReceivedPayload = true;
            LogState("first payload via " + path + " seat=" + peer.Seat + " peer=" + peer.Address + " bytes=" + length);
        }

        private void SetDirect(Path peer, bool value, string reason)
        {
            if (peer.Direct == value) return;
            peer.Direct = value;
            LogState("direct " + (value ? "on" : "off") + " seat=" + peer.Seat + " peer=" + peer.Address + " (" + reason + ")");
        }

        // Test-only: never hole punch, always use the relay path.
        public bool ForceRelay
        {
            get { return forceRelay; }
            set
            {
                forceRelay = value;
                foreach (Path peer in paths.Values)
                {
                    if (value) SetDirect(peer, false, "forced relay");
                    else if (ready && peer.Address != null) BeginPunch(peer);
                }
            }
        }

        private static uint NewNonce()
        {
            lock (NonceSource) { return (uint)NonceSource.Next(1, Int32.MaxValue); }
        }

        private double Now { get { return now == null ? clock.Elapsed.TotalSeconds : now(); } }

        public void Update()
        {
            if (disposed || updating) return;
            updating = true;
            try
            {
                Drain();
                double now = Now;
                if (hosting)
                {
                    if (!ready)
                    {
                        if (now - lastHostRoom >= RetrySeconds) { SendHostRoom(); lastHostRoom = now; }
                    }
                    else if (now - lastKeepAlive >= KeepAliveSeconds) { SendKeepAlive(); lastKeepAlive = now; }
                }
                else
                {
                    if (!ready)
                    {
                        if (now - lastJoinRoom >= RetrySeconds) { SendJoinRoom(); lastJoinRoom = now; }
                    }
                    else if (now - lastKeepAlive >= KeepAliveSeconds) { SendKeepAlive(); lastKeepAlive = now; }
                }
                foreach (Path peer in paths.Values) { TryPunch(peer, now); CheckDirectTimeout(peer, now); }
            }
            finally
            {
                updating = false;
            }
        }

        public void SendTo(byte[] data, int offset, int length, EndPoint to)
        {
            if (disposed) return;
            if (data == null) throw new ArgumentNullException("data");
            if (offset < 0 || length < 0 || offset > data.Length - length) throw new ArgumentOutOfRangeException("length");
            Update();
            if (!ready || roomCode == null) return;
            int seat = hosting ? ((IPEndPoint)to).Port : 1;
            if (hosting && (seat < 2 || seat > 4)) return;
            Path peer;
            paths.TryGetValue(seat, out peer);
            if (peer != null && peer.Direct && !forceRelay && peer.Address != null)
            {
                byte[] raw = new byte[1 + length];
                raw[0] = MarkerData;
                Buffer.BlockCopy(data, offset, raw, 1, length);
                SendRaw(raw, peer.Address);
                return;
            }
            if (length > MaxRelayPayload) return;
            byte[] packet = new byte[12 + length];
            WriteMagicOp(packet, OpRelay);
            WriteCode(packet, 3, roomCode);
            packet[9] = (byte)seat;
            WriteU16(packet, 10, (ushort)length);
            Buffer.BlockCopy(data, offset, packet, 12, length);
            SendRaw(packet, server);
        }

        public int Receive(byte[] buffer, out EndPoint from)
        {
            from = VirtualRemote;
            if (disposed) return 0;
            if (buffer == null) throw new ArgumentNullException("buffer");
            Update();
            if (incoming.Count == 0) return 0;
            Datagram datagram = incoming.Dequeue();
            byte[] payload = datagram.Bytes; from = datagram.Path.Remote;
            int length = Math.Min(buffer.Length, payload.Length);
            Buffer.BlockCopy(payload, 0, buffer, 0, length);
            return length;
        }

        public void Dispose()
        {
            if (disposed) return;
            if (!hosting && ready && roomCode != null)
            {
                byte[] packet = new byte[9];
                WriteMagicOp(packet, OpLeaveRoom);
                WriteCode(packet, 3, roomCode);
                // Best effort over UDP; duplicates are harmless at the server.
                for (int i = 0; i < 3; i++) SendRaw(packet, server);
            }
            disposed = true;
            udp.Dispose();
        }

        private void Drain()
        {
            while (true)
            {
                EndPoint from;
                int length;
                try { length = udp.Receive(receiveBuffer, out from); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                if (length <= 0) break;
                Handle(receiveBuffer, length, from);
            }
        }

        private void Handle(byte[] data, int length, EndPoint from)
        {
            if (length >= 2 && ReadU16(data, 0) == Magic)
            {
                // Control and relayed packets are only trusted from the relay server itself: anyone else who
                // learns this UDP port could otherwise forge OpJoined / OpRelayed and inject or break the session.
                if (SameEndpoint(from, server)) HandleServer(data, length);
                return;
            }
            if (!forceRelay) HandleDirect(data, length, from);
        }

        private static bool SameEndpoint(EndPoint a, EndPoint b)
        {
            IPEndPoint x = a as IPEndPoint, y = b as IPEndPoint;
            if (x == null || y == null) return Equals(a, b);
            return x.Port == y.Port && V4(x.Address).Equals(V4(y.Address));
        }

        // ::ffff:a.b.c.d (dual-stack socket) compares equal to a.b.c.d.
        private static IPAddress V4(IPAddress a)
        {
            if (a.AddressFamily != AddressFamily.InterNetworkV6) return a;
            byte[] b = a.GetAddressBytes();
            for (int i = 0; i < 10; i++) if (b[i] != 0) return a;
            if (b[10] != 0xFF || b[11] != 0xFF) return a;
            return new IPAddress(new byte[] { b[12], b[13], b[14], b[15] });
        }

        private void HandleServer(byte[] data, int length)
        {
            if (length < 3) return;
            switch (data[2])
            {
                case OpRoomCreated:
                    if (!hosting || length < 13) return;
                    if (ReadU32(data, 3) != nonce) return;
                    string created = ReadCode(data, 7);
                    if (!IsValidCode(created)) return;
                    roomCode = created;
                    if (!ready) { ready = true; error = null; }
                    lastServerReceive = Now;
                    return;
                case OpJoined:
                    if (hosting || length < 16 || data[9] < 2 || data[9] > 4) return;
                    if (!CodeEquals(ReadCode(data, 3))) return;
                    EndPoint hostEndpoint = ReadEndpoint(data, 10);
                    if (hostEndpoint == null) return;
                    lastServerReceive = Now;
                    Seat = data[9];
                    if (!ready) { ready = true; error = null; }
                    SetPeer(1, hostEndpoint);
                    return;
                case OpPeerJoined:
                    if (!hosting || length < 16 || data[9] < 2 || data[9] > 4) return;
                    if (!CodeEquals(ReadCode(data, 3))) return;
                    EndPoint guestEndpoint = ReadEndpoint(data, 10);
                    if (guestEndpoint == null) return;
                    lastServerReceive = Now;
                    SetPeer(data[9], guestEndpoint);
                    return;
                case OpJoinFailed:
                    if (hosting || length < 4 || ready) return;
                    int count = data[3];
                    if (count > length - 4) return;
                    error = Encoding.UTF8.GetString(data, 4, count);
                    lastServerReceive = Now;
                    return;
                case OpRelayed:
                    if (length < 6) return;
                    int source = data[3];
                    if (hosting ? source < 2 || source > 4 : source != 1) return;
                    int size = ReadU16(data, 4);
                    if (size > length - 6) return;
                    byte[] payload = new byte[size];
                    Buffer.BlockCopy(data, 6, payload, 0, size);
                    Path relayed = PathOf(source);
                    incoming.Enqueue(new Datagram { Path = relayed, Bytes = payload });
                    LogFirstPayload(relayed, "relay", size);
                    lastServerReceive = Now;
                    return;
                case OpKeepAliveAck:
                    lastServerReceive = Now;
                    return;
                case OpPeerLeft:
                    if (!hosting || length < 10 || !CodeEquals(ReadCode(data, 3))) return;
                    Path departed;
                    if (paths.TryGetValue(data[9], out departed))
                    {
                        LogState("PeerLeft seat=" + departed.Seat + " guest=" + departed.Address);
                        SetDirect(departed, false, "peer left");
                        paths.Remove(departed.Seat);
                        int queued = incoming.Count;
                        while (queued-- > 0) { Datagram item = incoming.Dequeue(); if (item.Path != departed) incoming.Enqueue(item); }
                        if (PeerLeft != null) PeerLeft(departed.Remote);
                    }
                    lastServerReceive = Now;
                    return;
            }
        }

        private void HandleDirect(byte[] data, int length, EndPoint from)
        {
            Path peer = null;
            foreach (Path candidate in paths.Values) if (candidate.Address != null && candidate.Address.Equals(from)) { peer = candidate; break; }
            if (length < 1 || peer == null || peer.Blocked) return;
            byte marker = data[0];
            if (marker == MarkerData)
            {
                if (length <= 1) return;
                peer.LastDirectReceive = Now;
                SetDirect(peer, true, "data");
                byte[] payload = new byte[length - 1];
                Buffer.BlockCopy(data, 1, payload, 0, length - 1);
                incoming.Enqueue(new Datagram { Path = peer, Bytes = payload });
                LogFirstPayload(peer, "direct", length - 1);
                return;
            }
            if (marker != MarkerPunch && marker != MarkerPunchAck) return;
            if (length < 11) return;
            if (!CodeEquals(ReadCode(data, 1))) return;
            peer.LastDirectReceive = Now;
            SetDirect(peer, true, "punch");
            if (marker == MarkerPunch) SendPunch(peer, MarkerPunchAck);
        }

        private Path PathOf(int seat)
        {
            Path peer;
            if (!paths.TryGetValue(seat, out peer))
            {
                peer = new Path { Seat = seat, Remote = new IPEndPoint(VirtualAddress, seat) };
                paths.Add(seat, peer);
            }
            return peer;
        }

        private void SetPeer(int seat, EndPoint address)
        {
            Path peer = PathOf(seat);
            if (Object.Equals(peer.Address, address)) return;
            peer.Address = address; peer.ReceivedPayload = false;
            LogState("PeerJoined seat=" + seat + " peer=" + address);
            BeginPunch(peer);
        }

        private void BeginPunch(Path peer)
        {
            if (forceRelay) return;
            SetDirect(peer, false, "punch start");
            if (peer.Blocked) LogState("blocked off seat=" + peer.Seat + " peer=" + peer.Address);
            peer.Blocked = false;
            LogState("punch start seat=" + peer.Seat + " peer=" + peer.Address);
            peer.PunchStart = Now;
            peer.LastPunch = -1000000;
            peer.LastDirectReceive = Now;
        }

        private void TryPunch(Path peer, double now)
        {
            if (forceRelay || peer.Blocked || peer.Direct || !ready || peer.Address == null) return;
            if (peer.PunchStart < 0 || now - peer.PunchStart > PunchSeconds) return;
            if (now - peer.LastPunch < PunchIntervalSeconds) return;
            peer.LastPunch = now;
            SendPunch(peer, MarkerPunch);
        }

        private void CheckDirectTimeout(Path peer, double now)
        {
            if (!peer.Direct || forceRelay) return;
            if (now - peer.LastDirectReceive < DirectTimeoutSeconds) return;
            // Only abandon the direct path when the relay path is demonstrably alive.
            if (lastServerReceive < 0 || now - lastServerReceive > 7.0) return;
            SetDirect(peer, false, "direct timeout");
            peer.Blocked = true;
            LogState("blocked on seat=" + peer.Seat + " peer=" + peer.Address);
        }

        private void SendHostRoom()
        {
            byte[] packet = new byte[7 + identity.Length];
            WriteMagicOp(packet, OpHostRoom);
            WriteU32(packet, 3, nonce);
            Buffer.BlockCopy(identity, 0, packet, 7, identity.Length);
            SendRaw(packet, server);
        }

        private void SendJoinRoom()
        {
            if (roomCode == null) return;
            byte[] packet = new byte[9 + identity.Length];
            WriteMagicOp(packet, OpJoinRoom);
            WriteCode(packet, 3, roomCode);
            Buffer.BlockCopy(identity, 0, packet, 9, identity.Length);
            SendRaw(packet, server);
        }

        // 'I', version, u64 SteamID64 (LE), u8 name length, UTF-8 name (cut on a character boundary).
        internal static byte[] BuildIdentity()
        {
            ulong steamId = 0;
            string name = null;
            try { if (LocalSteamIdSource != null) steamId = LocalSteamIdSource(); }
            catch (Exception) { steamId = 0; }
            try { if (LocalNameSource != null) name = LocalNameSource(); }
            catch (Exception) { name = null; }
            byte[] nameBytes = new byte[0];
            if (!String.IsNullOrEmpty(name))
            {
                int chars = Math.Min(name.Length, MaxIdentityNameBytes);
                while (chars > 0 && Encoding.UTF8.GetByteCount(name.Substring(0, chars)) > MaxIdentityNameBytes) chars--;
                if (chars > 0 && Char.IsHighSurrogate(name[chars - 1])) chars--;
                nameBytes = Encoding.UTF8.GetBytes(name.Substring(0, chars));
            }
            byte[] block = new byte[11 + nameBytes.Length];
            block[0] = IdentityTag;
            block[1] = IdentityVersion;
            for (int i = 0; i < 8; i++) block[2 + i] = (byte)(steamId >> (8 * i));
            block[10] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, block, 11, nameBytes.Length);
            return block;
        }

        private void SendKeepAlive()
        {
            if (roomCode == null) return;
            byte[] packet = new byte[9];
            WriteMagicOp(packet, OpKeepAlive);
            WriteCode(packet, 3, roomCode);
            SendRaw(packet, server);
        }

        private void SendPunch(Path peer, byte marker)
        {
            if (peer.Address == null || roomCode == null) return;
            byte[] packet = new byte[11];
            packet[0] = marker;
            WriteCode(packet, 1, roomCode);
            WriteU32(packet, 7, nonce);
            SendRaw(packet, peer.Address);
        }

        private void SendRaw(byte[] data, EndPoint to)
        {
            try { udp.SendTo(data, 0, data.Length, to); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private bool CodeEquals(string code)
        {
            return roomCode != null && String.Equals(code, roomCode, StringComparison.Ordinal);
        }

        private static bool IsValidCode(string code)
        {
            if (code == null || code.Length != 6) return false;
            for (int i = 0; i < code.Length; i++)
                if (CodeAlphabet.IndexOf(code[i]) < 0) return false;
            return true;
        }

        private static ushort ReadU16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static void WriteU16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteMagicOp(byte[] data, byte op)
        {
            data[0] = (byte)(Magic & 0xFF);
            data[1] = (byte)(Magic >> 8);
            data[2] = op;
        }

        private static void WriteCode(byte[] data, int offset, string code)
        {
            Encoding.ASCII.GetBytes(code, 0, 6, data, offset);
        }

        private static string ReadCode(byte[] data, int offset)
        {
            return Encoding.ASCII.GetString(data, offset, 6);
        }

        private static EndPoint ReadEndpoint(byte[] data, int offset)
        {
            int port = ReadU16(data, offset + 4);
            if (port == 0) return null;
            byte[] address = new byte[] { data[offset], data[offset + 1], data[offset + 2], data[offset + 3] };
            return new IPEndPoint(new IPAddress(address), port);
        }
    }
}
