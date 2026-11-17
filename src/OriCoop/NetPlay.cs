using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using BepInEx.Configuration;
using Game;
using HarmonyLib;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop
{
    // Online co-op, phase 1: two PCs, each runs its own game. On each PC the local player is P1 (the normal
    // Ori) and the other player's Ori is P2, a "puppet" driven by that player's input sent over the network.
    // So the same code runs on host and guest. Each Ori is owned by its own player: the owner sends, every
    // fixed step, its input plus its position; the receiver replays the input (so the puppet animates and
    // uses abilities exactly like the real one) and pulls the puppet toward the owner's position (correction).
    //
    // Wire messages (first bytes = type, sender seat):
    //   Hello   (reliable)   mod version
    //   Status  (reliable)   in gameplay?, scene name
    //   Frames  (unreliable) the last few fixed steps (redundant copies: one lost packet loses nothing)
    public static class NetPlay
    {
        public const string ModVersion = "0.2.0-net2";
        public static int Sender { get; private set; }
        public static bool HostOverride;
        public static Func<int, byte, bool, Action<BinaryWriter>, bool> SendToOverride;
        // C-server hook: (transport, isHost, target from Begin) -> peer, or null when the transport is not handled.
        public static Func<string, bool, string, NetPeer> TransportFactory;
        // Set by a factory transport (e.g. the C-server code after its welcome) so the lobby shows that room code.
        public static string RoomCodeOverride;
        static string s_factoryTarget = "";
        const byte MsgHello = 1, MsgStatus = 2, MsgFrames = 3, MsgSeat = 4, MsgLeft = 5, MsgTest = 99;
        // ---- message registry for the sync subsystems (each in its own file). Type ranges:
        //   1-9 NetPlay core, 10-19 save/world (WorldSync), 20-29 progress, 30-49 enemies, 50-59 story, 99 test.
        static readonly Dictionary<byte, Action<BinaryReader>> s_handlers = new Dictionary<byte, Action<BinaryReader>>();
        static readonly HashSet<byte> s_forward = new HashSet<byte>();
        static NetPlay()
        {
            Forward(75, 70, 71, 66, 67, Revive.MsgDowned, Revive.MsgRevive, Revive.MsgRevived,
                Revive.MsgBothDown, Revive.MsgProgress, 53);
        }
        static readonly bool s_noForward = TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_NET_NO_FORWARD") == "1";
        public static void Forward(params byte[] types)
        {
            foreach (byte type in types) s_forward.Add(type);
        }
        public static void On(byte type, Action<BinaryReader> handler)
        {
            s_handlers[type] = handler;
            // Deliver what arrived before this subsystem registered (the partner's game may be seconds ahead).
            Queue<byte[]> q;
            if (s_early.TryGetValue(type, out q))
            {
                s_early.Remove(type);
                while (q.Count > 0)
                {
                    byte[] m = q.Dequeue();
                    int previous = Sender;
                    try { BinaryReader r = new BinaryReader(new MemoryStream(m)); r.ReadByte(); Sender = r.ReadByte(); handler(r); }
                    catch (Exception e) { Plugin.Log.LogWarning("net: early message " + type + ": " + e.Message); }
                    finally { Sender = previous; }
                }
            }
        }
        // Messages of a registrable type (10+) with no handler yet, kept in arrival order (max 64 per type).
        static readonly Dictionary<byte, Queue<byte[]>> s_early = new Dictionary<byte, Queue<byte[]>>();
        static void KeepEarly(byte type, byte[] m)
        {
            Queue<byte[]> q;
            if (!s_early.TryGetValue(type, out q)) { q = new Queue<byte[]>(); s_early[type] = q; }
            if (q.Count >= 64) q.Dequeue();
            q.Enqueue(m);
        }
        public static bool Connected { get { return Peer != null && Peer.State == PeerState.Connected; } }
        public static void Send(byte type, bool reliable, Action<BinaryWriter> body)
        {
            if (!Connected || WaitingForSeat) return;
            byte[] message = Message(type, body);
            Broadcast(message, message.Length, reliable);
        }
        public static void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            if (SendToOverride != null && SendToOverride(seat, type, reliable, body)) return;
            RemoteState remote;
            if (!IsHost || !s_remotes.TryGetValue(seat, out remote) || remote.Peer == null) return;
            byte[] message = Message(type, body);
            SendBytes(remote.Peer, message, message.Length, reliable);
        }
        static byte[] Message(byte type, Action<BinaryWriter> body)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                w.Write(type); w.Write((byte)Players.LocalSeat); body(w); w.Flush();
                return ms.ToArray();
            }
        }
        static void SendBytes(NetPeer peer, byte[] message, int length, bool reliable)
        {
            if (reliable) peer.SendReliable(message, 0, length);
            else peer.SendUnreliable(message, 0, length);
        }
        static void Broadcast(byte[] message, int length, bool reliable)
        {
            if (!HasHub) { if (s_peer != null) SendBytes(s_peer, message, length, reliable); return; }
            foreach (RemoteState remote in s_remotes.Values) if (remote.Peer != null) SendBytes(remote.Peer, message, length, reliable);
        }
        internal static void Rebroadcast(BinaryReader reader)
        {
            if (!IsHost || s_factorySession) return;
            byte[] message = ((MemoryStream)reader.BaseStream).ToArray();
            Broadcast(message, message.Length, true);
        }
        // Main-thread events; the session spans the first Hello through the last departure.
        public static event Action SessionStarted, SessionEnded;
        public static event Action<int> PlayerJoined, PlayerLeft;
        public static string LastTestSignal = "";
        internal static event Action<int, string> TestSignalReceived;
        internal static float TestAvgError(int seat)
        { RemoteState remote; return s_remotes.TryGetValue(seat, out remote) ? remote.AvgError : float.PositiveInfinity; }
        internal static long TestFramesPlayed(int seat)
        { RemoteState remote; return s_remotes.TryGetValue(seat, out remote) ? remote.FramesPlayed : 0; }
        public static void SendTestSignal(string s) { SendReliable(MsgTest, w => w.Write(s)); }

        // Say goodbye when the game closes, so the partner sees it at once instead of after the timeout.
        public static void Shutdown()
        {
            if (s_hub != null) { try { s_hub.Disconnect("partner quit the game"); } catch { } return; }
            if (s_relayHub != null) { try { s_relayHub.Disconnect("partner quit the game"); } catch { } return; }
            if (Peer != null && Peer.State == PeerState.Connected) { try { Peer.Disconnect("partner quit the game"); Peer.Flush(); } catch { } }
        }
        const int Redundancy = 4;                 // frames per packet (current + 3 previous)

        // XInput button bits (same as P2Input).
        const int A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000, BACK = 0x20, LB = 0x100, RB = 0x200, LS = 0x40, RS = 0x80,
            DUP = 1, DDOWN = 2, DLEFT = 4, DRIGHT = 8;
        const int FlagLT = 1, FlagRT = 2, FlagNeutral = 4;

        public struct Frame
        {
            public uint Tick;
            public ushort Buttons;
            public sbyte LX, LY, RX, RY;
            public byte Flags;
            public Vector3 Pos;
            public Vector2 Vel;
        }

        // ---- config ----
        static ConfigEntry<string> s_mode, s_address;
        static ConfigEntry<int> s_port;
        static ConfigEntry<KeyCode> s_statsKey;
        static ConfigEntry<float> s_snapDist, s_blend;
        static string s_modeNow = "off";
        public static bool Enabled { get { return s_modeNow == "host" || s_modeNow == "join"; } }
        public static bool IsHost { get { return s_modeNow == "host" || HostOverride; } }

        // ---- session ----
        sealed class RemoteState
        {
            public int Seat;
            public NetPeer Peer;
            public bool Hello, InGameplay;
            public string Version = "", Scene = "";
            public byte[] LastHello, LastStatus;
            public readonly SortedDictionary<uint, Frame> Buffer = new SortedDictionary<uint, Frame>();
            public uint PlayTick, NewestTick;
            public bool HavePlay;
            public Frame Last;
            public int MissingRun;
            public readonly float[] Late = new float[120], SortTmp = new float[120];
            public int LateN, LateI;
            public float DelayChangedAt, LatenessSpreadMs, OneWayMs;
            public int TargetDelay = 3;
            public long FramesPlayed, FramesMissing, FramesLate, Snaps, SkipAheads;
            public float LastError, AvgError, MaxErrorRecent;
            public Vector3 LatestRemotePos;
            public bool HaveRemotePos;
            public double LastHeardMs;     // QpcMs of the last message from this seat (server mode: no own peer)
            public Puppet Puppet;
            public float SpawnTryAt;
        }
        static readonly Dictionary<int, RemoteState> s_remotes = new Dictionary<int, RemoteState>();
        static RemoteState Primary
        {
            get
            {
                RemoteState result = null;
                if (!IsHost) { s_remotes.TryGetValue(1, out result); return result; }
                foreach (RemoteState remote in s_remotes.Values)
                    if (result == null || remote.Seat < result.Seat) result = remote;
                return result;
            }
        }
        internal static int[] RemoteSeats
        {
            get { int[] seats = new int[s_remotes.Count]; s_remotes.Keys.CopyTo(seats, 0); Array.Sort(seats); return seats; }
        }
        internal static int RemoteCount
        {
            get { int count = 0; foreach (RemoteState remote in s_remotes.Values) if (remote.Hello) count++; return count; }
        }
        internal static bool AnyRemoteInGameplay
        {
            get { foreach (RemoteState remote in s_remotes.Values) if (remote.InGameplay) return true; return false; }
        }
        static NetPeer s_peer;
        static PeerHub s_hub;
        static RelayHub s_relayHub;
        static bool s_factorySession;
        static bool HasHub { get { return s_hub != null || s_relayHub != null; } }
        static bool s_sessionStarted;
        static bool WaitingForSeat { get { return (s_factorySession || !IsHost && !UsesSteam) && Players.AssignedSeat == 0; } }
        public static NetPeer Peer { get { return !HasHub ? s_peer : Primary == null ? null : Primary.Peer; } }
        static IDatagramTransport s_transport;
        public static bool RemoteHello { get { return Primary != null && Primary.Hello; } }
        public static bool RemoteInGameplay { get { return Primary != null && Primary.InGameplay; } }
        public static string RemoteVersion { get { return Primary == null ? "" : Primary.Version; } }
        public static string RemoteScene { get { return Primary == null ? "" : Primary.Scene; } }
        static bool s_sentInGameplay; static string s_sentScene; static bool s_statusDirty = true;
        static float s_retryAt;

        // ---- send side ----
        static uint s_localTick;
        public static uint LocalTick { get { return s_localTick; } }
        public static uint LastPlayedTick { get { return Primary == null ? 0 : Primary.Last.Tick; } }
        public static uint NewestRemoteTick { get { return Primary == null ? 0 : Primary.NewestTick; } }
        static readonly Frame[] s_sent = new Frame[Redundancy];
        static int s_sentCount;
        static readonly byte[] s_buf = new byte[1200];

        // ---- stats (shown on the F6 overlay, read by the test) ----
        public static int TargetDelay { get { return Primary == null ? 3 : Primary.TargetDelay; } }
        public static long FramesPlayed { get { return Primary == null ? 0 : Primary.FramesPlayed; } }
        public static long FramesMissing { get { return Primary == null ? 0 : Primary.FramesMissing; } }
        public static long FramesLate { get { return Primary == null ? 0 : Primary.FramesLate; } }
        public static long Snaps { get { return Primary == null ? 0 : Primary.Snaps; } }
        public static long SkipAheads { get { return Primary == null ? 0 : Primary.SkipAheads; } }
        public static float LastError { get { return Primary == null ? 0 : Primary.LastError; } }
        public static float AvgError { get { return Primary == null ? 0 : Primary.AvgError; } }
        public static float MaxErrorRecent { get { return Primary == null ? 0 : Primary.MaxErrorRecent; } }
        public static Vector3 LatestRemotePos { get { return Primary == null ? Vector3.zero : Primary.LatestRemotePos; } }
        public static IEnumerable<KeyValuePair<int, Vector3>> RemotePositions
        {
            get
            {
                foreach (RemoteState remote in s_remotes.Values)
                    if (remote.HaveRemotePos) yield return new KeyValuePair<int, Vector3>(remote.Seat, remote.LatestRemotePos);
            }
        }
        public static bool RemotePos(int seat, out Vector3 position)
        {
            RemoteState remote;
            bool have = s_remotes.TryGetValue(seat, out remote) && remote.HaveRemotePos;
            position = have ? remote.LatestRemotePos : Vector3.zero;
            return have;
        }
        internal static bool NewestRemoteFrame(int seat, uint tick)
        {
            RemoteState remote;
            return s_remotes.TryGetValue(seat, out remote) && remote.HaveRemotePos && remote.NewestTick == tick;
        }
        public static bool HaveRemotePos { get { return Primary != null && Primary.HaveRemotePos; } }
        public static float LatenessSpreadMs { get { return Primary == null ? 0 : Primary.LatenessSpreadMs; } }
        public static float OneWayMs { get { return Primary == null ? 0 : Primary.OneWayMs; } }
        static bool s_showStats = true;
        static GUIStyle s_style;

        public static void Init(ConfigFile cfg)
        {
            s_mode = cfg.Bind("Net", "Mode", "off", "Online co-op: off, host (wait for a friend) or join (connect to Address).");
            s_address = cfg.Bind("Net", "Address", "127.0.0.1", "Host's IP address (for join).");
            s_port = cfg.Bind("Net", "Port", 47011, "UDP port. The host must let it through its firewall/router (or use Tailscale/ZeroTier).");
            s_statsKey = cfg.Bind("Net", "StatsKey", KeyCode.F6, "Shows/hides the network status line.");
            s_snapDist = cfg.Bind("Net", "SnapDistance", 4f, "Puppet farther than this from the owner's position jumps there at once; closer, it is pulled smoothly.");
            s_blend = cfg.Bind("Net", "CorrectionBlend", 0.2f, "Share of the position error removed per fixed step (0..1).");
            s_transportKind = cfg.Bind("Net", "Transport", "direct", "direct = IP address and port (LAN, Tailscale, port forwarding). relay = through a relay server with a room code (no ports to open). steam = Steam friends: the host invites from the Steam overlay, both must own Ori and run Steam.");
            s_inviteKey = cfg.Bind("Net", "InviteKey", KeyCode.F5, "Transport = steam, host: opens the Steam overlay to invite a friend.");
            s_relayServer = cfg.Bind("Net", "RelayServer", "", "Relay server as host:port (Transport = relay). Run src\\RelayServer\\bin\\RelayServer.exe on any PC or VPS with a public UDP port.");
            s_roomCode = cfg.Bind("Net", "RoomCode", "", "Room code the host's screen shows (Transport = relay, Mode = join).");
            s_menuAddress = cfg.Bind("OnlineMenu", "LastAddress", "", "Last address typed in the in-game online menu (prefill only).");
            s_menuRoom = cfg.Bind("OnlineMenu", "LastRoomCode", "", "Last room code typed in the in-game online menu (prefill only).");
            SteamTransport.Trace += m => Plugin.Log.LogInfo("net: Steam: " + m);
            NetPeer.Trace = m => Plugin.Log.LogInfo("net: peer: " + m);
            // The relay server list (BepInEx\config\oricoop-relays.txt, edited live by OriCoop-Relays.html). While a
            // relay session is live, changes to the file wait until it ends.
            RelayList.InUse = () => Enabled && UsesRelay && s_relay != null;
            try { RelayList.Init(s_relayServer.Value); } catch (Exception e) { Plugin.Log.LogWarning("relay list: " + e.Message); }
            string env = Environment.GetEnvironmentVariable("ORICOOP_NET");
            s_modeNow = (string.IsNullOrEmpty(env) ? s_mode.Value : env).Trim().ToLowerInvariant();
            if (!Enabled) return;
            long connectAt;
            if (TestHarness.Enabled && !IsHost && long.TryParse(Environment.GetEnvironmentVariable("ORICOOP_NET_CONNECT_AT"), out connectAt) && connectAt > 0)
            {
                DateTime at = new DateTime(connectAt, DateTimeKind.Utc);
                s_retryAt = Time.realtimeSinceStartup + (float)Math.Max(0, (at - DateTime.UtcNow).TotalSeconds);
                Plugin.Log.LogInfo("net: test connect held until " + at.ToString("o") + " (" + (s_retryAt - Time.realtimeSinceStartup).ToString("F1") + " s)");
            }
            Plugin.Log.LogInfo("online co-op: mode " + s_modeNow);
        }

        // ---------------------------------------------------------------- runtime start (in-game online menu)
        // The menu starts and stops online play while the game runs. Its choices live in runtime fields (s_rt*)
        // that win over env/config only for that session, so a menu session never changes how a config- or
        // env-driven launch behaves (tests, the [Net] file). The last address / room code are remembered in
        // [OnlineMenu] for prefilling; the relay server the player types is a real setting ([Net] RelayServer).
        static string s_rtTransport, s_rtAddress, s_rtRoom;
        static int s_rtPort;
        static ConfigEntry<string> s_menuAddress, s_menuRoom;
        static string TransportRaw { get { return s_rtTransport ?? Env("ORICOOP_NET_TRANSPORT", s_transportKind.Value); } }
        public static string TransportName { get { return TransportRaw.Trim().ToLowerInvariant(); } }
        public static string ConfigAddress { get { return s_menuAddress.Value.Length > 0 ? s_menuAddress.Value : s_address.Value; } }
        public static string ConfigRoomCode { get { return s_menuRoom.Value; } }
        // The relay in use: the selected entry of the relay list, else the old [Net] RelayServer setting.
        public static string ConfigRelayServer { get { string c = RelayList.Current; return !string.IsNullOrEmpty(c) ? c : s_relayServer.Value; } }
        public static int ConfigPort { get { return s_port.Value; } }
        // A relay typed or pasted in the game (host:port or an oricoop:// link): added to / selected in the list.
        public static void SetRelayServer(string hostPort)
        {
            string text = (hostPort ?? "").Trim(), address, room, name;
            if (text.Length == 0) return;
            if (!RelayList.ParseLink(text, out address, out room, out name)) { Plugin.Log.LogWarning("relay list: not a relay address"); return; }
            s_relayServer.Value = address;
            RelayList.AddOrSelect(address, name);
        }
        // Last problem worth showing to the player (room not found, cannot resolve, Steam error...). "" when fine.
        public static string Problem = "";
        public static float StartedAt;
        // True while End() reports the session end (the player chose to leave: no "connection lost" dialog).
        public static bool EndingByChoice;

        // host: true = create a room / listen. transport: direct | relay | steam.
        // target: direct join = "ip[:port]"; relay join = room code; relay host/join may pass "server|code" to
        // also set the relay server; ignored otherwise.
        public static void Begin(bool host, string transport, string target)
        {
            transport = (transport ?? "direct").Trim().ToLowerInvariant();
            // Steam guest: a join started by an accepted invite (SteamInviteHook) may already be running when the
            // player opens Online -> Steam -> Join. Restarting it would leave the lobby and lose the invite.
            if (!host && transport == "steam" && SteamJoinHealthy)
            {
                Plugin.Log.LogInfo("online co-op: Steam join already in progress (lobby " + s_steamLobby + "); keeping it");
                return;
            }
            // Retry on the Steam join page: the invite was consumed by the first attempt; rejoin the same lobby.
            ulong rejoin = !host && transport == "steam" && Enabled && !IsHost && UsesSteam ? s_steamLobby : 0;
            End("restarting online play");
            s_steamLobby = rejoin;
            if (rejoin != 0) Plugin.Log.LogInfo("online co-op: retrying the Steam lobby " + rejoin);
            s_rtTransport = transport;
            s_factorySession = TransportFactory != null && transport != "direct" && transport != "relay" && transport != "steam";
            target = (target ?? "").Trim();
            s_factoryTarget = target;
            if (transport == "relay")
            {
                int bar = target.IndexOf('|');
                if (bar >= 0) { SetRelayServer(target.Substring(0, bar)); target = target.Substring(bar + 1).Trim(); }
                if (!host) { s_rtRoom = target.ToUpperInvariant(); s_menuRoom.Value = s_rtRoom; }
            }
            else if (transport == "direct" && !host && target.Length > 0)
            {
                s_menuAddress.Value = target;
                int colon = target.LastIndexOf(':'); int p;
                if (colon > 0 && int.TryParse(target.Substring(colon + 1), out p)) { s_rtPort = p; target = target.Substring(0, colon); }
                s_rtAddress = target;
            }
            if (P2Context.P2 != null) Plugin.DespawnNow("online play starting");
            Problem = ""; StartedAt = Time.realtimeSinceStartup; s_retryAt = 0f; s_showStats = false;
            s_modeNow = host ? "host" : "join";
            Plugin.Log.LogInfo("online co-op: started from the menu: " + s_modeNow + " via " + transport);
        }

        // Back to offline. Tells the partner, ends the session for every sync system, frees the socket.
        public static void End(string why)
        {
            s_rtTransport = s_rtAddress = s_rtRoom = null; s_rtPort = 0; RoomCodeOverride = null;
            s_steamLobby = 0; s_steamWaitLogged = false; s_steamReconnects = 0;
            if (!Enabled) return;
            bool wasConnected = Connected || s_sessionStarted || s_remotes.Count > 0;
            Shutdown();
            if (wasConnected)
            {
                RemoveRemote(why);
                EndingByChoice = true;
                try
                {
                    foreach (RemoteState remote in new List<RemoteState>(s_remotes.Values)) Leave(remote, why);
                    EndSession();
                }
                finally { EndingByChoice = false; }
            }
            Close();
            s_reListen = false; s_modeNow = "off"; Problem = "";
            Plugin.Log.LogInfo("online co-op: stopped (" + why + ")");
        }

        public enum Phase { Off, Starting, Waiting, Connecting, Connected, Retrying }
        // What the UI shows: Waiting = host ready for a partner (RoomCode set for relay); Retrying = Problem set.
        public static Phase CurrentPhase
        {
            get
            {
                if (!Enabled) return Phase.Off;
                // C-server: seated in the room with nobody else there yet = waiting for a partner, not connecting.
                if (Peer != null && Peer.State == PeerState.Connected && s_factorySession && !RemoteHello && Players.AssignedSeat != 0) return Phase.Waiting;
                if (Peer != null && Peer.State == PeerState.Connected) return RemoteHello ? Phase.Connected : Phase.Connecting;
                if (Problem.Length > 0) return Phase.Retrying;
                if (IsHost)
                {
                    if (s_relay != null) return s_relay.Ready ? Phase.Waiting : Phase.Starting;
                    if (UsesSteam) return s_steam != null && s_steam.Ready ? Phase.Waiting : Phase.Starting;
                    return HasHub || Peer != null ? Phase.Waiting : Phase.Starting;
                }
                return Peer != null ? Phase.Connecting : Phase.Starting;
            }
        }
        public static bool PartnerHere { get { return RemoteCount > 0; } }
        public static float RttMs { get { return Peer != null && Peer.State == PeerState.Connected ? Peer.Stats.RttMs : 0f; } }
        public static float LossPercent { get { return Peer != null && Peer.State == PeerState.Connected ? Peer.Stats.LossPercent : 0f; } }
        // Longest time since any connected remote last sent a packet (ms); 0 when not connected. Drives NetHud's
        // "signal lost" hint, because RttMs keeps its last value while the link is silent (lag test ②).
        public static float SilenceMs
        {
            get
            {
                double worst = 0;
                foreach (RemoteState remote in s_remotes.Values)
                {
                    if (remote.Peer != null && remote.Peer.State == PeerState.Connected)
                        worst = Math.Max(worst, remote.Peer.Stats.LastReceiveAgeMs);
                    // C-server: the server link stays alive while a partner goes silent; measure the partner itself.
                    // Only partners in gameplay stream frames; one on the title or in a menu is quiet, not lost.
                    else if (remote.Peer == null && s_factorySession && remote.InGameplay && remote.LastHeardMs > 0 && Connected)
                        worst = Math.Max(worst, QpcMs() - remote.LastHeardMs);
                }
                if (worst == 0 && Peer != null && Peer.State == PeerState.Connected) worst = Peer.Stats.LastReceiveAgeMs;
                return (float)worst;
            }
        }
        public const float SilenceHintMs = 1000f;
        public static bool SignalLost { get { return Connected && SilenceMs >= SilenceHintMs; } }
        public static bool RelayDirect { get { return s_relay != null && s_relay.Direct; } }
        public static string SteamPartnerName { get { return s_steam != null ? s_steam.RemoteName : ""; } }
        public static bool SteamReady { get { return s_steam != null && s_steam.Ready; } }
        public static void OpenSteamInvite() { if (s_steam != null && s_steam.Ready) s_steam.OpenInviteOverlay(); }

        // This PC's LAN IPv4 addresses, for the direct-host page ("tell your friend this address").
        public static string[] LocalAddresses()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.")) list.Add(a.ToString());   // 169.254 = no DHCP, unreachable
            }
            catch { }
            return list.ToArray();
        }

        static string Address { get { string e = Environment.GetEnvironmentVariable("ORICOOP_NET_ADDR"); return !string.IsNullOrEmpty(e) ? e : s_rtAddress ?? s_address.Value; } }
        // ---- Steam: lobby + Valve-relayed P2P under Ori's own Steam app. Host invites from the overlay (InviteKey);
        // the friend accepts the invite or uses "Join Game" in the friends list.
        static SteamTransport s_steam;
        static ConfigEntry<KeyCode> s_inviteKey;
        public static bool UsesSteam { get { return TransportName == "steam"; } }

        static void OpenSteam()
        {
            if (IsHost) { s_steam = SteamTransport.Host(); s_transport = s_steam; NewPeer(); Peer.Listen(); Plugin.Log.LogInfo("net: Steam: creating a friends-only lobby"); }
            else
            {
                // A new invite wins; otherwise rejoin the lobby of this session (after a dropped connection or
                // Retry). The invite is consumed here, so it cannot restart a join the player cancelled.
                bool fromInvite = SteamTransport.PendingInviteLobby != 0;
                ulong lobby = fromInvite ? SteamTransport.TakePendingInvite() : s_steamLobby;
                if (lobby == 0) return;          // wait for an invite (PumpSteam checks again every frame)
                s_steamWaitLogged = false;
                if (fromInvite) { Problem = ""; StartedAt = Time.realtimeSinceStartup; }   // the join page's 15 s counts from here, not from Begin
                s_steamLobby = lobby;
                s_steam = SteamTransport.Join(lobby); s_transport = s_steam; NewPeer(); s_pendingConnect = true;
                Plugin.Log.LogInfo("net: Steam: joining lobby " + lobby + (fromInvite ? " (invite)" : " (rejoin)"));
            }
        }

        // The Steam lobby a guest joined in this session (0 = none / host).
        static ulong s_steamLobby;
        static bool s_steamWaitLogged;
        static int s_steamReconnects;        // in-lobby reconnects since the last good connection (Open caps at 2)
        public static ulong SteamLobby { get { return s_steamLobby; } }
        // A Steam guest session that is connected, or still within its first 15 s without an error.
        public static bool SteamJoinHealthy
        {
            get
            {
                if (!Enabled || IsHost || !UsesSteam || s_steam == null || !string.IsNullOrEmpty(s_steam.Error) || Problem.Length > 0) return false;
                if (Peer != null && Peer.State == PeerState.Connected) return true;
                return Time.realtimeSinceStartup - StartedAt < 15f;
            }
        }

        // false while the lobby is not ready (nothing to poll yet).
        static bool s_steamReadyLogged;
        static bool PumpSteam()
        {
            if (s_steam == null)
            {
                bool haveTarget = IsHost || s_steamLobby != 0 || SteamTransport.PendingInviteLobby != 0;
                if (haveTarget && Time.realtimeSinceStartup >= s_retryAt) OpenSteam();
                else if (!haveTarget && !s_steamWaitLogged)
                {
                    s_steamWaitLogged = true;
                    Plugin.Log.LogInfo("net: Steam: waiting for an invite (accept it in Steam, or Join Game from the friends list)");
                }
                if (s_steam == null) return false;
            }
            s_steam.Update();
            if (!string.IsNullOrEmpty(s_steam.Error))
            {
                Plugin.Log.LogWarning("net: Steam: " + s_steam.Error); Problem = "steam: " + s_steam.Error;
                Close(); s_retryAt = Time.realtimeSinceStartup + 5f;
                return false;
            }
            if (IsHost && s_steam.Ready && Input.GetKeyDown(s_inviteKey.Value)) s_steam.OpenInviteOverlay();
            if (!s_steam.Ready) return false;
            if (s_steamReconnects > 0 && Peer != null && Peer.State == PeerState.Connected) s_steamReconnects = 0;
            if (IsHost && !s_steamReadyLogged) { s_steamReadyLogged = true; Plugin.Log.LogInfo("net: Steam: lobby ready " + s_steam.LobbyId); }
            if (s_pendingConnect && s_steam.RemoteEndPoint != null) { s_pendingConnect = false; Peer.Connect(s_steam.RemoteEndPoint); Plugin.Log.LogInfo("net: Steam: in lobby; connecting to " + s_steam.RemoteName); }
            return true;
        }

        static ConfigEntry<string> s_transportKind, s_relayServer, s_roomCode;
        static RelayTransport s_relay;
        static bool s_pendingConnect;
        static string Env(string name, string fallback) { string e = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(e) ? fallback : e; }
        public static bool UsesRelay { get { return TransportName == "relay"; } }
        public static string RoomCode { get { return !string.IsNullOrEmpty(RoomCodeOverride) ? RoomCodeOverride : s_relay != null ? (s_relay.RoomCode ?? "") : ""; } }

        static IPEndPoint Resolve(string hostPort, int defaultPort)
        {
            string h = hostPort.Trim(); int port = defaultPort;
            int colon = h.LastIndexOf(':');
            if (colon > 0) { int p; if (int.TryParse(h.Substring(colon + 1), out p)) port = p; h = h.Substring(0, colon); }
            IPAddress ip;
            if (!IPAddress.TryParse(h, out ip))
                ip = Array.Find(Dns.GetHostAddresses(h), a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip == null) throw new Exception("cannot resolve " + hostPort);
            return new IPEndPoint(ip, port);
        }

        // Relay: the room is created/joined first; NetPeer runs on top once the relay reports Ready.
        static void OpenRelay()
        {
            IPEndPoint server = Resolve(Env("ORICOOP_NET_RELAY", ConfigRelayServer), 47020);
            string code = Env("ORICOOP_NET_ROOM", s_rtRoom ?? s_roomCode.Value).Trim().ToUpperInvariant();
            // Test hook: the room code can arrive later in a file (the test starts both games before the host has a room).
            string codeFile = Environment.GetEnvironmentVariable("ORICOOP_NET_ROOM_FILE");
            if (!IsHost && code.Length == 0 && !string.IsNullOrEmpty(codeFile))
            {
                try { if (File.Exists(codeFile)) code = File.ReadAllText(codeFile).Trim().ToUpperInvariant(); } catch { }
                if (code.Length == 0) { s_retryAt = Time.realtimeSinceStartup + 0.5f; return; }
            }
            if (!IsHost && code.Length == 0) { Plugin.Log.LogWarning("net: relay join needs [Net] RoomCode"); Problem = "no room code"; s_retryAt = Time.realtimeSinceStartup + 5f; return; }
            if (IsHost) s_relay = RelayTransport.Host(server);
            else s_relay = RelayTransport.Join(server, code);
            s_relay.Log += delegate(string message) { Plugin.Log.LogInfo("net: relay " + message); };
            s_transport = s_relay;
            if (IsHost)
            {
                s_relayHub = new RelayHub(s_relay);
                s_relayHub.PeerCreated += p => ApplySimFromEnv(p.Sim);
                s_relayHub.Connected += OnHubConnected;
                s_relayHub.Disconnected += OnHubDisconnected;
            }
            else NewPeer();
            s_pendingConnect = !IsHost;
            Plugin.Log.LogInfo("net: relay " + server + (IsHost ? " (creating a room)" : " (joining room " + code + ")"));
        }

        static void NewPeer()
        {
            s_peer = new NetPeer(s_transport);
            ApplySimFromEnv(Peer.Sim);
            Peer.Connected += OnConnected;
            Peer.Disconnected += OnDisconnected;
        }

        // Called every frame before the peer is polled. Returns false while the relay room is not ready.
        static bool PumpRelay()
        {
            if (s_relay == null) return true;
            s_relay.Update();
            if (!string.IsNullOrEmpty(s_relay.Error))
            {
                Plugin.Log.LogWarning("net: relay: " + s_relay.Error); Problem = "relay: " + s_relay.Error;
                Close(); s_retryAt = Time.realtimeSinceStartup + 5f;
                return false;
            }
            if (!s_relay.Ready) return false;
            if (s_pendingConnect) { s_pendingConnect = false; Peer.Connect(s_relay.RemoteEndPoint); Plugin.Log.LogInfo("net: relay room joined; connecting"); }
            if (IsHost && s_lastRoom != s_relay.RoomCode) { s_lastRoom = s_relay.RoomCode; Plugin.Log.LogInfo("net: relay room code " + s_lastRoom + " (give it to your friend)"); }
            return true;
        }
        static string s_lastRoom = "";

        static int Port { get { int p; return int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_NET_PORT"), out p) ? p : s_rtPort > 0 ? s_rtPort : s_port.Value; } }

        static void Open()
        {
            if (!IsHost && !UsesRelay && !UsesSteam && s_transport is UdpTransport && s_peer != null && s_peer.State == PeerState.Disconnected)
            {
                EndPoint host = s_peer.Remote;
                ResetSession(); NewPeer(); Peer.Connect(host);
                Plugin.Log.LogInfo("net: reconnecting to " + host + " on UDP " + s_transport.LocalPort);
                return;
            }
            // Steam guest whose connection dropped while still in the lobby: reconnect on the same transport
            // instead of leaving and rejoining the lobby.
            // At most 2 tries: a host that crashed can leave a lobby that still looks Ready; then leave and rejoin.
            if (UsesSteam && !IsHost && s_steam != null && s_steam.Ready && string.IsNullOrEmpty(s_steam.Error) && s_steamReconnects < 2)
            {
                s_steamReconnects++;
                ResetSession();                  // no stale hello/version/frames from the old session
                NewPeer(); s_pendingConnect = true;
                Plugin.Log.LogInfo("net: Steam: reconnecting to the host in lobby " + s_steam.LobbyId + " (try " + s_steamReconnects + " of 2)");
                return;
            }
            if (UsesSteam && !IsHost && s_steamReconnects >= 2) Plugin.Log.LogInfo("net: Steam: reconnects failed; leaving and rejoining lobby " + s_steamLobby);
            s_steamReconnects = 0;
            Close();
            try
            {
                if (UsesRelay) { OpenRelay(); return; }
                if (UsesSteam) { OpenSteam(); return; }
                if (TransportFactory != null && TransportName != "direct")
                {
                    s_factorySession = true;
                    s_peer = TransportFactory(TransportName, s_modeNow == "host", s_factoryTarget);
                    if (s_peer == null) throw new InvalidOperationException("transport factory did not create " + TransportName);
                    ApplySimFromEnv(s_peer.Sim);
                    s_peer.Connected += OnConnected;
                    s_peer.Disconnected += OnDisconnected;
                    if (s_peer.State == PeerState.Connected) OnConnected();
                    return;
                }
                if (IsHost)
                {
                    s_hub = new PeerHub(Port);
                    s_hub.PeerCreated += p => ApplySimFromEnv(p.Sim);
                    s_hub.Connected += OnHubConnected;
                    s_hub.Disconnected += OnHubDisconnected;
                    Plugin.Log.LogInfo("net: hosting on UDP " + s_hub.LocalPort);
                }
                else
                {
                    s_transport = new UdpTransport(0);
                    NewPeer();
                    IPAddress ip;
                    if (!IPAddress.TryParse(Address, out ip))
                    {
                        IPAddress[] all = Dns.GetHostAddresses(Address);
                        ip = Array.Find(all, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    }
                    Peer.Connect(new IPEndPoint(ip, Port));
                    Plugin.Log.LogInfo("net: connecting to " + ip + ":" + Port);
                }
            }
            catch (Exception e) { Plugin.Log.LogError("net: could not start: " + e.Message); Problem = e.Message; Close(); s_retryAt = Time.realtimeSinceStartup + 5f; }
        }

        static void Close()
        {
            if (s_hub != null) { s_hub.Dispose(); s_hub = null; }
            if (s_relayHub != null) { s_relayHub.Dispose(); s_relayHub = null; }
            if (s_transport != null) { try { s_transport.Dispose(); } catch { } }
            s_transport = null; s_peer = null; s_relay = null; s_steam = null; s_pendingConnect = false; s_steamReadyLogged = false; s_lastRoom = "";
            s_factorySession = false; s_reListen = false;
            ResetSession();
        }

        static void ResetSession()
        {
            s_remotes.Clear(); Players.AssignedSeat = 0; s_sessionStarted = false;
            s_statusDirty = true; s_sentCount = 0;
            s_early.Clear();
        }

        // Test hook: ORICOOP_NET_SIM = "lagMs,jitterMs,lossPercent,dupPercent" on outgoing packets.
        static void ApplySimFromEnv(NetSim sim)
        {
            string s = Environment.GetEnvironmentVariable("ORICOOP_NET_SIM");
            if (string.IsNullOrEmpty(s)) return;
            string[] p = s.Split(',');
            int i; float f;
            if (p.Length > 0 && int.TryParse(p[0], out i)) sim.LagMs = i;
            if (p.Length > 1 && int.TryParse(p[1], out i)) sim.JitterMs = i;
            if (p.Length > 2 && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) sim.LossPercent = f;
            if (p.Length > 3 && float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) sim.DupPercent = f;
            Plugin.Log.LogInfo("net: simulating lag " + sim.LagMs + " ms, jitter " + sim.JitterMs + " ms, loss " + sim.LossPercent + "%, dup " + sim.DupPercent + "%");
        }

        public static void SetSim(int lag, int jitter, float loss, float dup)
        {
            if (HasHub)
            {
                foreach (RemoteState remote in s_remotes.Values)
                {
                    remote.Peer.Sim.LagMs = lag; remote.Peer.Sim.JitterMs = jitter;
                    remote.Peer.Sim.LossPercent = loss; remote.Peer.Sim.DupPercent = dup;
                }
                return;
            }
            if (Peer == null) return;
            Peer.Sim.LagMs = lag; Peer.Sim.JitterMs = jitter; Peer.Sim.LossPercent = loss; Peer.Sim.DupPercent = dup;
        }

        static void OnConnected()
        {
            Plugin.Log.LogInfo("net: connected to " + Peer.Remote); Problem = "";
            ResetSession();
            if (s_factorySession) return;
            int seat = IsHost ? 2 : 1;
            s_remotes.Add(seat, new RemoteState { Seat = seat, Peer = s_peer });
            if (!WaitingForSeat) SendReliable(MsgHello, w => w.Write(ModVersion));
        }

        static void OnHubConnected(NetPeer peer)
        {
            int seat = 2;
            while (s_remotes.ContainsKey(seat)) seat++;
            s_remotes.Add(seat, new RemoteState { Seat = seat, Peer = peer });
            Problem = "";
            SendTo(seat, MsgSeat, true, w => w.Write((byte)seat));
            SendTo(seat, MsgHello, true, w => w.Write(ModVersion));
            bool inGame = LocalInGameplay();
            string scene = LocalScene(inGame);
            SendTo(seat, MsgStatus, true, w => { w.Write(inGame); w.Write(scene); });
            foreach (RemoteState remote in s_remotes.Values)
            {
                if (remote.Seat == seat) continue;
                if (remote.LastHello != null) SendBytes(peer, remote.LastHello, remote.LastHello.Length, true);
                if (remote.LastStatus != null) SendBytes(peer, remote.LastStatus, remote.LastStatus.Length, true);
            }
            Plugin.Log.LogInfo("net: connected P" + seat + " at " + peer.Remote);
        }

        static void OnHubDisconnected(NetPeer peer, string reason)
        {
            RemoteState leaving = null;
            foreach (RemoteState remote in s_remotes.Values) if (remote.Peer == peer) { leaving = remote; break; }
            if (leaving == null) return;
            int seat = leaving.Seat;
            Plugin.Log.LogInfo("net: P" + seat + " disconnected from " + peer.Remote + ": " + reason);
            Leave(leaving, reason);
            SendReliable(MsgLeft, w => w.Write((byte)seat));
        }

        static void Leave(RemoteState remote, string why)
        {
            if (remote.Puppet != null) { Plugin.DespawnPuppet(remote.Puppet); remote.Puppet = null; }
            s_remotes.Remove(remote.Seat);
            foreach (Queue<byte[]> queue in s_early.Values)
            {
                int count = queue.Count;
                while (count-- > 0) { byte[] m = queue.Dequeue(); if (m[1] != remote.Seat) queue.Enqueue(m); }
            }
            if (PlayerLeft != null) PlayerLeft(remote.Seat);
            if (s_remotes.Count == 0) EndSession();
        }

        public static void AssignSeat(int seat)
        {
            Players.AssignedSeat = seat;
            Plugin.Log.LogInfo("net: assigned P" + seat);
            SendReliable(MsgHello, w => w.Write(ModVersion));
        }

        // Factory (C-server) sessions: nobody relays earlier Hello/Status to a seat that joins later (the hub's
        // OnHubConnected does that for direct/relay), so each client introduces itself to every newly online seat.
        public static void IntroduceTo(int seat)
        {
            SendTo(seat, MsgHello, true, w => w.Write(ModVersion));
            bool inGame = LocalInGameplay();
            string scene = LocalScene(inGame);
            SendTo(seat, MsgStatus, true, w => { w.Write(inGame); w.Write(scene); });
        }

        public static void RemoteLeft(int seat)
        {
            RemoteState remote;
            if (s_remotes.TryGetValue(seat, out remote)) Leave(remote, "partner left");
        }

        static void EndSession()
        {
            if (!s_sessionStarted) return;
            s_sessionStarted = false;
            if (SessionEnded != null) { try { SessionEnded(); } catch (Exception e) { Plugin.Log.LogError(e); } }
        }

        static void OnDisconnected(string reason)
        {
            Plugin.Log.LogWarning("net: disconnected: " + reason); if (!IsHost) Problem = reason;
            RemoveRemote("connection lost (" + reason + ")");
            foreach (RemoteState remote in new List<RemoteState>(s_remotes.Values)) Leave(remote, reason);
            EndSession();
            ResetSession();
            // Host keeps listening for a new guest; a guest retries after a moment.
            s_retryAt = Time.realtimeSinceStartup + (IsHost ? 0.5f : 3f);
            // A relay host keeps its room (same code for the friend): new peer on the same relay transport.
            if (IsHost && ((s_relay != null && s_relay.Ready) || (s_steam != null && s_steam.Ready))) { s_reListen = true; }
        }
        static bool s_reListen;
        static void ReListenIfNeeded()
        {
            if (!s_reListen) return;
            s_reListen = false;
            NewPeer(); Peer.Listen();
            Plugin.Log.LogInfo("net: relay room " + RoomCode + " waiting for a new guest");
        }

        static void RemoveRemote(string why)
        {
            foreach (RemoteState remote in s_remotes.Values)
            {
                if (remote.Puppet != null) Plugin.DespawnPuppet(remote.Puppet);
                remote.Puppet = null;
            }
        }

        static void SendReliable(byte type, Action<BinaryWriter> body)
        {
            Send(type, true, body);
        }

        static readonly Action s_inviteTick = SteamInviteHook.Tick, s_relayListTick = RelayList.Tick;
        static readonly Action s_inviteDraw = SteamInviteHook.DrawGUI;
        static readonly Action s_sendLocal = SendLocal, s_playRemote = PlayRemote;

        // ---------------------------------------------------------------- per frame (Runner.Update)
        public static void Update()
        {
            // Before the Enabled check: Steam invites must be caught (and acted on) while online play is off.
            SubsystemGuard.Run("SteamInviteHook.Tick", s_inviteTick);
            SubsystemGuard.Run("RelayList.Tick", s_relayListTick);
            if (!Enabled) return;
            if (Input.GetKeyDown(s_statsKey.Value)) s_showStats = !s_showStats;
            ReListenIfNeeded();
            if (s_relay != null && !PumpRelay()) return;
            if (UsesSteam && !PumpSteam()) return;
            if (!HasHub && (Peer == null || Peer.State == PeerState.Disconnected || (Peer.State == PeerState.Idle && !s_pendingConnect)))
            {
                if (Time.realtimeSinceStartup >= s_retryAt) Open();
                if (!HasHub && Peer == null) return;
            }
            if (HasHub)
            {
                if (s_hub != null) s_hub.Poll(); else s_relayHub.Poll();
                foreach (RemoteState remote in new List<RemoteState>(s_remotes.Values)) ReadPeer(remote.Peer, remote);
                if (s_hub != null) s_hub.Flush(); else s_relayHub.Flush();
            }
            else { Peer.Poll(); if (Peer != null) ReadPeer(Peer, IsHost && !s_factorySession ? Primary : null); }
            if (!Connected || WaitingForSeat) return;

            bool inGame = LocalInGameplay();
            string scene = LocalScene(inGame);
            if (inGame != s_sentInGameplay || scene != s_sentScene || s_statusDirty)
            {
                s_sentInGameplay = inGame; s_sentScene = scene; s_statusDirty = false;
                SendReliable(MsgStatus, w => { w.Write(inGame); w.Write(scene ?? ""); });
            }

            foreach (RemoteState remote in s_remotes.Values)
            {
                if (remote.Puppet != null && remote.Puppet.Sein == null) remote.Puppet = null;
                if (remote.Puppet == null && remote.Hello && remote.InGameplay && inGame && remote.HaveRemotePos && Characters.Sein != null
                    && Time.realtimeSinceStartup >= remote.SpawnTryAt && SaveSync.ReadyForSeat(remote.Seat))
                {
                    remote.SpawnTryAt = Time.realtimeSinceStartup + 1f;
                    remote.Puppet = Plugin.Instance.SpawnPuppet(remote.Seat);
                    if (remote.Puppet != null)
                    {
                        Plugin.Teleport(remote.Puppet.Sein, remote.LatestRemotePos);
                        Plugin.Log.LogInfo("net: P" + remote.Seat + " appears at " + remote.LatestRemotePos);
                    }
                }
                else if (remote.Puppet != null && (!remote.InGameplay || !inGame))
                { Plugin.DespawnPuppet(remote.Puppet); remote.Puppet = null; }
            }
        }

        static string LocalScene(bool inGame)
        {
            return inGame && Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null ? Core.Scenes.Manager.CurrentScene.Scene : "";
        }

        static void ReadPeer(NetPeer peer, RemoteState source)
        {
            byte[] msg; bool reliable;
            while (peer.TryReceive(out msg, out reliable))
            {
                try { Handle(msg, reliable, source); }
                catch (Exception e) { Plugin.Log.LogWarning("net: bad message: " + e.Message); }
            }
        }

        public static bool LocalInGameplay()
        {
            SeinCharacter s = Characters.Sein;
            if (s == null || !s.gameObject.activeInHierarchy || (P2Context.P2 != null && s == P2Context.P2)) return false;
            GameController gc = GameController.Instance;
            if (gc == null || gc.IsLoadingGame) return false;
            if (GameStateMachine.Instance != null && GameStateMachine.Instance.IsInExtendedTitleScreen()) return false;
            return true;
        }

        static void Handle(byte[] m, bool reliable, RemoteState source)
        {
            if (m.Length < 2) return;
            BinaryReader r = new BinaryReader(new MemoryStream(m));
            byte type = r.ReadByte();
            int previous = Sender;
            Sender = r.ReadByte();
            try
            {
                if (s_factorySession && type >= 176 && type <= 186)
                {
                    Action<BinaryReader> handler;
                    if (s_handlers.TryGetValue(type, out handler)) handler(r); else KeepEarly(type, m);
                    return;
                }
                if (type == MsgSeat)
                {
                    if (IsHost || UsesSteam || Sender != 1) return;
                    AssignSeat(r.ReadByte());
                    return;
                }
                if (WaitingForSeat) return;
                if (!s_factorySession && source != null && Sender != source.Seat) return;
                if (type == MsgLeft)
                {
                    if (IsHost && !s_factorySession || UsesSteam || Sender != 1) return;
                    RemoteLeft(r.ReadByte());
                    return;
                }
                if (Sender < 1 || Sender > Players.MaxSeats || Sender == Players.LocalSeat) return;
                RemoteState remote;
                if (!s_remotes.TryGetValue(Sender, out remote))
                {
                    if (IsHost && !s_factorySession) return;
                    remote = new RemoteState { Seat = Sender, Peer = !s_factorySession && Sender == 1 ? s_peer : null };
                    s_remotes.Add(Sender, remote);
                }
                remote.LastHeardMs = QpcMs();
                // ORICOOP_NET_NO_FORWARD=1 (tests only): mutation check that the 4P checks fail without host forwarding.
                if (IsHost && s_forward.Contains(type) && !s_noForward)
                    foreach (RemoteState other in s_remotes.Values)
                        if (other != remote && other.Peer != null) SendBytes(other.Peer, m, m.Length, reliable);
                if (type == MsgHello)
                {
                    remote.Version = r.ReadString(); remote.LastHello = m;
                    bool joined = !remote.Hello;
                    remote.Hello = true;
                    Plugin.Log.LogInfo("net: P" + Sender + " runs " + remote.Version + (remote.Version == ModVersion ? "" : " (different from ours: " + ModVersion + ")"));
                    if (!s_sessionStarted)
                    {
                        s_sessionStarted = true;
                        if (SessionStarted != null) SessionStarted();
                    }
                    if (joined && PlayerJoined != null) PlayerJoined(remote.Seat);
                }
                else if (type == MsgStatus)
                {
                    remote.InGameplay = r.ReadBoolean(); remote.Scene = r.ReadString(); remote.LastStatus = m;
                    Plugin.Log.LogInfo("net: P" + Sender + " " + (remote.InGameplay ? "in gameplay (" + remote.Scene + ")" : "not in gameplay"));
                    if (!remote.InGameplay) { remote.Buffer.Clear(); remote.HavePlay = false; remote.HaveRemotePos = false; }
                }
                else if (s_handlers.ContainsKey(type)) s_handlers[type](r);
                else if (type >= 10 && type != MsgTest) KeepEarly(type, m);
                else if (type == MsgTest)
                {
                    LastTestSignal = r.ReadString(); Plugin.Log.LogInfo("net: test signal " + LastTestSignal);
                    if (TestSignalReceived != null) TestSignalReceived(Sender, LastTestSignal);
                }
                else if (type == MsgFrames)
                {
                    NoteLateness(remote, QpcMs() - r.ReadDouble());
                    int n = r.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        Frame f = new Frame();
                        f.Tick = r.ReadUInt32(); f.Buttons = r.ReadUInt16();
                        f.LX = r.ReadSByte(); f.LY = r.ReadSByte(); f.RX = r.ReadSByte(); f.RY = r.ReadSByte();
                        f.Flags = r.ReadByte();
                        f.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        f.Vel = new Vector2(r.ReadSingle(), r.ReadSingle());
                        Receive(remote, f);
                    }
                }
                if (IsHost && (type == MsgHello || type == MsgStatus || type == MsgFrames))
                    foreach (RemoteState other in s_remotes.Values)
                        if (other != remote && other.Peer != null) SendBytes(other.Peer, m, m.Length, reliable);
            }
            finally { Sender = previous; }
        }

        static void Receive(RemoteState remote, Frame f)
        {
            if (!remote.InGameplay) return;
            if (remote.HavePlay && f.Tick < remote.PlayTick) { return; }     // already played (redundant copy) or too late
            if (remote.Buffer.ContainsKey(f.Tick)) return;
            if (!remote.HaveRemotePos || f.Tick > remote.NewestTick) { remote.NewestTick = f.Tick; remote.LatestRemotePos = f.Pos; remote.HaveRemotePos = true; }
            remote.Buffer[f.Tick] = f;
            if (!remote.HavePlay) { remote.HavePlay = true; remote.PlayTick = f.Tick; }
        }

        // ---------------------------------------------------------------- per fixed step (Runner.FixedUpdate)
        public static void FixedTick()
        {
            if (!Enabled) return;
            SubsystemGuard.Run("NetPlay.SendLocal", s_sendLocal);
            SubsystemGuard.Run("NetPlay.PlayRemote", s_playRemote);
        }

        static sbyte Q(float v) { return (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * 127f), -127, 127); }
        static float DQ(sbyte v) { return v / 127f; }

        static void SendLocal()
        {
            if (!Connected || WaitingForSeat || !LocalInGameplay()) { s_sentCount = 0; return; }
            SeinCharacter p1 = Characters.Sein;
            GameController gc = GameController.Instance;
            bool neutral = gc == null || gc.GameplaySuspended || gc.GameplaySuspendedForUI || gc.InputLocked
                || p1.Controller == null || !p1.Controller.CanMove || p1.Controller.LockMovementInput || p1.Controller.IgnoreControllerInput;

            Frame f = new Frame();
            f.Tick = ++s_localTick;
            if (!neutral)
            {
                // Core.Input holds P1's input (keyboard or pad) for this step; encode it pad-style.
                int b = 0;
                if (Core.Input.Jump.IsPressed) b |= A;
                if (Core.Input.SpiritFlame.IsPressed) b |= X;
                if (Core.Input.SoulFlame.IsPressed) b |= B;
                if (Core.Input.Bash.IsPressed) b |= Y;
                if (Core.Input.LeftShoulder.IsPressed) b |= LB;
                if (Core.Input.RightShoulder.IsPressed) b |= RB;
                if (Core.Input.LeftStick.IsPressed) b |= LS;
                if (Core.Input.RightStick.IsPressed) b |= RS;
                if (Core.Input.HorizontalDigiPad > 0) b |= DRIGHT; else if (Core.Input.HorizontalDigiPad < 0) b |= DLEFT;
                if (Core.Input.VerticalDigiPad > 0) b |= DUP; else if (Core.Input.VerticalDigiPad < 0) b |= DDOWN;
                f.Buttons = (ushort)b;
                f.LX = Q(Core.Input.HorizontalAnalogLeft); f.LY = Q(Core.Input.VerticalAnalogLeft);
                f.RX = Q(Core.Input.HorizontalAnalogRight); f.RY = Q(Core.Input.VerticalAnalogRight);
                if (Core.Input.ChargeJump.IsPressed) f.Flags |= FlagLT;
                if (Core.Input.Glide.IsPressed || Core.Input.Grab.IsPressed) f.Flags |= FlagRT;
            }
            else f.Flags |= FlagNeutral;
            f.Pos = p1.Position;
            f.Vel = new Vector2(p1.Speed.x, p1.Speed.y);

            // Keep the last few frames and send them all: any single lost packet is covered by the next one.
            for (int i = Redundancy - 1; i > 0; i--) s_sent[i] = s_sent[i - 1];
            s_sent[0] = f;
            if (s_sentCount < Redundancy) s_sentCount++;

            MemoryStream ms = new MemoryStream(s_buf);
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(MsgFrames); w.Write((byte)Players.LocalSeat); w.Write(QpcMs()); w.Write((byte)s_sentCount);
            for (int i = s_sentCount - 1; i >= 0; i--)
            {
                Frame s = s_sent[i];
                w.Write(s.Tick); w.Write(s.Buttons); w.Write(s.LX); w.Write(s.LY); w.Write(s.RX); w.Write(s.RY); w.Write(s.Flags);
                w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z); w.Write(s.Vel.x); w.Write(s.Vel.y);
            }
            w.Flush();
            Broadcast(s_buf, (int)ms.Position, false);
            if (s_hub != null) s_hub.Flush(); else if (s_relayHub != null) s_relayHub.Flush(); else Peer.Flush();
        }

        static void PlayRemote()
        {
            foreach (RemoteState remote in s_remotes.Values) PlayRemote(remote);
        }

        static void PlayRemote(RemoteState remote)
        {
            if (remote.Puppet == null || remote.Puppet.Sein == null) return;
            SeinCharacter p2 = remote.Puppet.Sein;
            if (!remote.HavePlay) { HoldNeutral(remote); return; }

            // Jitter buffer depth follows how late packets really arrive (see NoteLateness): deeper on a bumpy
            // link, shallow on a clean one. It grows at once but shrinks only one step per second (no pumping).
            float stepMs = Time.fixedDeltaTime * 1000f;
            int want = Mathf.Clamp(Mathf.CeilToInt(remote.LatenessSpreadMs / stepMs) + 1, 2, 12);
            if (want > remote.TargetDelay) remote.TargetDelay = want;
            else if (want < remote.TargetDelay && Time.realtimeSinceStartup - remote.DelayChangedAt > 1f) { remote.TargetDelay--; remote.DelayChangedAt = Time.realtimeSinceStartup; }

            // Too far behind (our game hitched, or it was paused while the partner kept playing): catch up.
            if (remote.NewestTick >= remote.PlayTick && remote.NewestTick - remote.PlayTick > (uint)(remote.TargetDelay + 6))
            {
                uint to = remote.NewestTick - (uint)remote.TargetDelay;
                if (to - remote.PlayTick > 1024)   // a huge gap (a remote tick near uint.MaxValue): jump, do not loop billions of times
                {
                    List<uint> stale = new List<uint>();
                    foreach (uint k in remote.Buffer.Keys) { if (k < to - 1024) stale.Add(k); else break; }
                    foreach (uint k in stale) remote.Buffer.Remove(k);
                    remote.PlayTick = to - 1024;
                }
                while (remote.PlayTick < to) { Frame skipped; if (remote.Buffer.TryGetValue(remote.PlayTick, out skipped)) remote.Last = skipped; remote.Buffer.Remove(remote.PlayTick); remote.PlayTick++; }
                remote.SkipAheads++;
            }
            // Start of a stream: wait until the buffer has remote.TargetDelay frames.
            if (remote.FramesPlayed == 0 && remote.NewestTick - remote.PlayTick < (uint)remote.TargetDelay) { HoldNeutral(remote); return; }

            Frame f;
            bool have = remote.Buffer.TryGetValue(remote.PlayTick, out f);
            if (have)
            {
                remote.Buffer.Remove(remote.PlayTick);
                remote.MissingRun = 0;
                remote.Last = f;
                remote.PlayTick++;
                remote.FramesPlayed++;
                Correct(remote, p2, f);
            }
            else if (remote.NewestTick > remote.PlayTick)
            {
                // This step is lost (all redundant copies missing): repeat the last input, move on.
                remote.FramesMissing++;
                remote.MissingRun++;
                f = remote.Last; remote.PlayTick++;
            }
            else
            {
                // Starved: nothing newer yet. Repeat the last input briefly, then let go of the buttons.
                remote.FramesLate++;
                remote.MissingRun++;
                f = remote.Last;
                if (remote.MissingRun > 6) { HoldNeutral(remote); return; }
            }
            if ((f.Flags & FlagNeutral) != 0) { HoldNeutral(remote); return; }
            P2Input.Fill(remote.Puppet.Input, f.Buttons, DQ(f.LX), DQ(f.LY), DQ(f.RX), DQ(f.RY), (f.Flags & FlagLT) != 0, (f.Flags & FlagRT) != 0, false);
        }

        // ---- lateness: each Frames packet carries the sender's clock. (receive time - send time) = one-way delay
        // + an unknown but constant clock offset, so its spread is the jitter. Spread = 90th percentile minus the
        // fastest packet over the last ~2 s. On one PC both games share the QPC clock, so remote.OneWayMs is the real delay.
        static double QpcMs() { return System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        static void NoteLateness(RemoteState remote, double ms)
        {
            remote.Late[remote.LateI] = (float)ms; remote.LateI = (remote.LateI + 1) % remote.Late.Length; if (remote.LateN < remote.Late.Length) remote.LateN++;
            Array.Copy(remote.Late, remote.SortTmp, remote.LateN);
            Array.Sort(remote.SortTmp, 0, remote.LateN);
            float min = remote.SortTmp[0], p90 = remote.SortTmp[Mathf.Min(remote.LateN - 1, (int)(remote.LateN * 0.9f))];
            remote.LatenessSpreadMs = p90 - min;
            remote.OneWayMs = remote.SortTmp[remote.LateN / 2];
        }

        static void HoldNeutral(RemoteState remote) { P2Input.Fill(remote.Puppet.Input, 0, 0, 0, 0, 0, false, false, false); }

        // "Write-back": the owner's position is the truth. Small errors are pulled in gradually (invisible),
        // big ones (door, respawn, teleport, long loss) jump.
        static void Correct(RemoteState remote, SeinCharacter p2, Frame f)
        {
            Vector3 err = f.Pos - p2.Position;
            float e = err.magnitude;
            remote.LastError = e;
            remote.AvgError = remote.AvgError * 0.98f + e * 0.02f;
            remote.MaxErrorRecent = Mathf.Max(remote.MaxErrorRecent * 0.995f, e);
            if (e > s_snapDist.Value)
            {
                Plugin.Teleport(p2, f.Pos - new Vector3(0f, 0.5f, 0f));
                p2.Speed = new Vector3(f.Vel.x, f.Vel.y, 0f);
                remote.Snaps++;
                return;
            }
            if (e > 0.01f) p2.Position = p2.Position + err * Mathf.Clamp01(s_blend.Value);
            Vector3 v = p2.Speed;
            p2.Speed = new Vector3(Mathf.Lerp(v.x, f.Vel.x, 0.3f), Mathf.Lerp(v.y, f.Vel.y, 0.3f), v.z);
        }

        // ---------------------------------------------------------------- overlay
        public static string StatusLine()
        {
            if (!Enabled) return "";
            string st = Peer == null ? (HasHub ? "listening" : "starting") : Peer.State.ToString().ToLowerInvariant();
            string s = "NET " + (IsHost ? "host P1" : "guest P" + Players.LocalSeat) + " " + st;
            if (s_relay != null)
                s += !s_relay.Ready ? " (relay: " + (IsHost ? "creating room" : "joining room") + ")"
                    : "  ROOM " + s_relay.RoomCode + (s_relay.Direct ? " direct" : " via relay");
            if (UsesSteam)
                s += s_steam == null ? (IsHost ? " (Steam: starting)" : " (Steam: waiting for an invite: accept it in Steam, or Join Game from your friends list)")
                    : !s_steam.Ready ? " (Steam: lobby starting)"
                    : (IsHost && (Peer == null || Peer.State != PeerState.Connected) ? "  STEAM lobby ready: press " + s_inviteKey.Value + " to invite a friend" : "  STEAM " + s_steam.RemoteName);
            if (Peer != null && Peer.State == PeerState.Connected)
            {
                NetStats n = Peer.Stats;
                s += string.Format("  RTT {0:F0} ms  jitter {1:F0}  spread {11:F0}  loss {2:F1}%  {3:F1}/{4:F1} KB/s  buf {5}/{6}  err {7:F2} (avg {8:F2})  snaps {9}  lost {10}",
                    n.RttMs, n.JitterMs, n.LossPercent, n.SendKBps, n.RecvKBps, (Primary == null ? 0 : Primary.Buffer.Count), TargetDelay, LastError, AvgError, Snaps, FramesMissing, LatenessSpreadMs);
                if (!RemoteInGameplay) s += "  (partner not in gameplay)";
                if (RemoteHello && RemoteVersion != ModVersion) s += "  VERSION MISMATCH " + RemoteVersion;
            }
            else if (Peer != null && Peer.State == PeerState.Disconnected) s += " (" + Peer.DisconnectReason + ")";
            return s;
        }

        public static void DrawGUI()
        {
            SubsystemGuard.Run("SteamInviteHook.DrawGUI", s_inviteDraw);
            if (!Enabled || !s_showStats) return;
            if (s_style == null)
            {
                s_style = new GUIStyle(GUI.skin.label);
                s_style.fontSize = Mathf.Max(11, Screen.height / 60);
            }
            bool ok = Peer != null && Peer.State == PeerState.Connected;
            // Like the game's HUD: hidden in menus/cutscenes, except that connection problems always show.
            float a = ok ? P2Hud.GameHudOpacity() : 1f;
            if (a <= 0.01f) return;
            s_style.normal.textColor = ok ? new Color(0.6f, 1f, 0.7f, a) : new Color(1f, 0.6f, 0.4f, a);
            GUI.Label(new Rect(10f, 6f, Screen.width - 20f, 30f), StatusLine(), s_style);
        }

        // The partner's Ori is simulated here only to look right; its owner decides what happens to it.
        // So it takes no damage here (otherwise enemies that differ between the two games would kill it).
        [HarmonyPatch(typeof(SeinDamageReciever), "OnRecieveDamage")]
        static class PuppetNoDamage
        {
            static bool Prefix(SeinDamageReciever __instance)
            {
                return !(Enabled && Players.OfInstance(__instance.GetInstanceID()) != null);
            }
        }
    }
}
