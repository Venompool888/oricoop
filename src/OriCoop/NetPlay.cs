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
    public static partial class NetPlay
    {
        public const string ModVersion = OriCoop.Net.ProtocolVersion.Mod;   // T-140: bump it there (pinned by the unit tests)
        public static int Sender { get { return s_bus.Sender; } private set { s_bus.Sender = value; } }
        public static bool HostOverride;
        public static Func<int, byte, bool, Action<BinaryWriter>, bool> SendToOverride;
        // C-server hook: (transport, isHost, target from Begin) -> peer, or null when the transport is not handled.
        public static Func<string, bool, string, NetPeer> TransportFactory;
        // Called by End() once the session is closed: a factory transport frees its own state (T-031: the C-server link
        // kept its socket, routing and arbiter role, because its peer's queued Disconnected event is never polled after End).
        public static Action Stopped;
        public static Action Leaving;   // T-113: NetPlay.End is about to leave on purpose (still connected)
        // Set by a factory transport (e.g. the C-server code after its welcome) so the lobby shows that room code.
        public static string RoomCodeOverride;
        static string s_factoryTarget = "";
        const byte MsgHello = 1, MsgStatus = 2, MsgFrames = 3, MsgSeat = 4, MsgLeft = 5, MsgTest = 99;
        // ---- message registry for the sync subsystems (each in its own file). Type ranges:
        //   1-9 NetPlay core, 10-19 save/world (WorldSync), 20-29 progress, 30-49 enemies, 50-59 story, 99 test.
        static readonly NetAdapter s_net = new NetAdapter();
        static readonly SessionMachine s_session = new SessionMachine(s_net, QpcMs, delegate(int seat) { Players.AssignedSeat = seat; },
            OnRemoteLeaving, delegate(int seat) { s_bus.ForgetSeat(seat); }, delegate(Exception e) { Plugin.Log.LogError(e); });
        static readonly MessageBus s_bus = new MessageBus(s_net, s_session,
            delegate(byte type, Exception e) { Plugin.Log.LogWarning("net: early message " + type + ": " + e.Message); });
        static NetPlay()
        {
            InitLocalSeats();
            Forward(9, 75, 70, 71, 66, 67, Revive.MsgDowned, Revive.MsgRevive, Revive.MsgRevived,
                Revive.MsgBothDown, Revive.MsgProgress, Revive.MsgDownedState, 53);
        }
        static readonly bool s_noForward = TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_NET_NO_FORWARD") == "1";
        public static void Forward(params byte[] types) { s_bus.Forward(types); }
        public static void On(byte type, Action<BinaryReader> handler) { s_bus.On(type, handler); }
        public static bool Connected { get { return Peer != null && Peer.State == PeerState.Connected; } }
        public static void Send(byte type, bool reliable, Action<BinaryWriter> body) { s_bus.Send(type, reliable, body); }
        public static void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body) { s_bus.SendTo(DeviceSeat(seat), type, reliable, body); }
        static void SendBytes(NetPeer peer, byte[] message, int length, bool reliable)
        {
            if (reliable) peer.SendReliable(message, 0, length);
            else peer.SendUnreliable(message, 0, length);
        }
        static void Broadcast(byte[] message, int length, bool reliable) { s_bus.Broadcast(message, length, reliable); }
        internal static void Rebroadcast(BinaryReader reader) { s_bus.Rebroadcast(reader); }
        // Main-thread events; the session spans the first Hello through the last departure.
        public static event Action SessionStarted { add { s_session.SessionStarted += value; } remove { s_session.SessionStarted -= value; } }
        public static event Action SessionEnded { add { s_session.SessionEnded += value; } remove { s_session.SessionEnded -= value; } }
        public static event Action<int> PlayerJoined { add { s_session.PlayerJoined += value; } remove { s_session.PlayerJoined -= value; } }
        public static event Action<int> PlayerLeft { add { s_session.PlayerLeft += value; } remove { s_session.PlayerLeft -= value; } }
        public static string LastTestSignal = "";
        internal static event Action<int, string> TestSignalReceived;
        internal static float TestAvgError(int seat)
        { RemoteState remote; return TryGetRemote(seat, out remote) ? remote.AvgError : float.PositiveInfinity; }
        internal static long TestFramesPlayed(int seat)
        { RemoteState remote; return TryGetRemote(seat, out remote) ? remote.FramesPlayed : 0; }
        public static void SendTestSignal(string s) { SendReliable(MsgTest, w => w.Write(s)); }

        // Say goodbye when the game closes, so the partner sees it at once instead of after the timeout.
        public static void Shutdown() { Shutdown("closed the game"); }

        // T-201: the partner is told "partner quit the game"; this side's own trace and status say that the local player
        // left and why (OwnDisconnectReason), instead of repeating the text meant for the partner. The connection code
        // (NetPeer, shared with the server) is not involved: NetPlay remembers which connection it closed itself.
        static NetPeer s_leftPeer;
        static string s_leftWhy = "";
        public static string OwnDisconnectReason(NetPeer peer, string reason)
        {
            return SessionMachine.OwnDisconnectReason(reason, peer != null && peer == s_leftPeer, s_leftWhy);
        }
        static void Shutdown(string why)
        {
            if (s_hub != null) { try { s_hub.Disconnect(SessionMachine.PartnerGoodbye); } catch { } return; }
            if (Peer != null && Peer.State == PeerState.Connected)
            {
                s_leftPeer = Peer; s_leftWhy = why;
                try { Peer.Disconnect(SessionMachine.PartnerGoodbye); Peer.Flush(); } catch { }
            }
        }
        const int Redundancy = 4;                 // frames per packet (current + 3 previous)

        // XInput button bits (same as P2Input).
        const int A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000, BACK = 0x20, LB = 0x100, RB = 0x200, LS = 0x40, RS = 0x80,
            DUP = 1, DDOWN = 2, DLEFT = 4, DRIGHT = 8;
        // T-199: FlagFaceKnown says FlagFaceLeft is meaningful (a frame from an older build has neither: facing is then
        // left to the puppet's own replay, as before). Two spare bits of the same byte: the frame's size is unchanged.
        const int FlagLT = 1, FlagRT = 2, FlagNeutral = 4, FlagFaceKnown = 8, FlagFaceLeft = 16, FlagMap = 32;

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
        // T-158: in a server-mode (transport factory) session the host role is the arbiter only (HostOverride, set by the
        // C-server link from the room roster). The room creator loses it while another seat arbitrates: after its seat
        // expired and it came back on another seat (T-113 fallback), or while it has no seat. P2P / relay / Steam unchanged.
        static readonly bool s_mutateCreatorHost = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_CREATOR_HOST") == "1";
        public static bool IsHost { get { return s_factorySession && !s_mutateCreatorHost ? HostOverride : (s_modeNow == "host" || HostOverride); } }

        sealed class PeerAdapter : IMessagePeer
        {
            public readonly NetPeer Peer;
            public PeerAdapter(NetPeer peer) { Peer = peer; }
            public bool Connected { get { return Peer.State == PeerState.Connected; } }
            public double LastReceiveAgeMs { get { return Peer.Stats.LastReceiveAgeMs; } }
            public void Send(byte[] message, int length, bool reliable) { SendBytes(Peer, message, length, reliable); }
        }

        sealed class NetAdapter : IMessageContext, IMessagePeer
        {
            public bool Enabled { get { return NetPlay.Enabled; } }
            public bool IsHost { get { return NetPlay.IsHost; } }
            public bool FactorySession { get { return s_factorySession; } }
            public bool UsesSteam { get { return NetPlay.UsesSteam; } }
            public bool HasHub { get { return NetPlay.HasHub; } }
            public bool HasPeer { get { return NetPlay.Peer != null; } }
            public bool Connected { get { return NetPlay.Connected; } }
            public bool PrimaryHello { get { return RemoteHello; } }
            public bool SteamReady { get { return NetPlay.SteamReady; } }
            public string Problem { get { return NetPlay.Problem; } }
            public int AssignedSeat { get { return Players.AssignedSeat; } }
            public double PeerLastReceiveAgeMs { get { return NetPlay.Peer.Stats.LastReceiveAgeMs; } }
            public int LocalSeat { get { return Players.LocalSeat; } }
            public int MaxSeats { get { return Players.MaxSeats; } }
            public bool DisableForward { get { return s_noForward; } }
            public IMessagePeer SinglePeer { get { return s_peer == null ? null : this; } }
            bool IMessagePeer.Connected { get { return s_peer.State == PeerState.Connected; } }
            double IMessagePeer.LastReceiveAgeMs { get { return s_peer.Stats.LastReceiveAgeMs; } }
            void IMessagePeer.Send(byte[] message, int length, bool reliable) { SendBytes(s_peer, message, length, reliable); }
            public bool SendToOverride(int seat, byte type, bool reliable, Action<BinaryWriter> body)
            { return NetPlay.SendToOverride != null && NetPlay.SendToOverride(seat, type, reliable, body); }
            public void AssignSeat(int seat) { NetPlay.AssignSeat(seat); }
            public SessionMachine.Remote CreateRemote(int seat)
            { return new RemoteState { Seat = seat, Peer = !s_factorySession && seat == 1 ? s_peer : null }; }
            public void HandleCore(byte type, BinaryReader reader, byte[] message, SessionMachine.Remote remote)
            { NetPlay.HandleCore(type, reader, message, (RemoteState)remote); }
        }

        // ---- session ----
        sealed class RemoteState : SessionMachine.Remote
        {
            public new NetPeer Peer
            {
                get { return base.Peer == null ? null : ((PeerAdapter)base.Peer).Peer; }
                set { base.Peer = value == null ? null : new PeerAdapter(value); }
            }
            public string Version = "", Scene = "";
            public byte[] LastHello, LastStatus;
            public readonly SortedDictionary<uint, Frame> Buffer = new SortedDictionary<uint, Frame>();
            public uint PlayTick, NewestTick;
            public bool HavePlay;
            public Frame Last;
            public bool HaveFace, FaceLeft;   // T-199: the owner's facing in the frame played last
            public int MissingRun;
            public readonly float[] Late = new float[120], SortTmp = new float[120];
            public int LateN, LateI;
            public float DelayChangedAt, LatenessSpreadMs, OneWayMs;
            public int TargetDelay = 3;
            public long FramesPlayed, FramesMissing, FramesLate, Snaps, SkipAheads;
            public float LastError, AvgError, MaxErrorRecent;
            public Vector3 LatestRemotePos;
            public bool HaveRemotePos;
            public Puppet Puppet;
            public float SpawnTryAt;
        }
        static readonly Dictionary<int, SessionMachine.Remote> s_remotes = s_session.Remotes;
        static bool TryGetRemote(int seat, out RemoteState remote)
        {
            SessionMachine.Remote value;
            bool found = s_remotes.TryGetValue(seat, out value);
            remote = (RemoteState)value;
            return found;
        }
        // T-213 (2, 9): a guest's primary remote is the current host: seat 1 in P2P, the arbiter's seat on a server (it
        // moves to the lowest online seat when the first host leaves). Looking only at seat 1, a guest that outlived the
        // first host saw "the host has not said hello": it stopped syncing enemies, and a guest that joined later was not
        // placed beside the new host (no host position).
        static readonly bool s_mutatePrimarySeatOne = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PRIMARY_SEAT_ONE") == "1";
        static RemoteState Primary
        {
            get
            {
                RemoteState result = null;
                if (!IsHost) { TryGetRemote(s_mutatePrimarySeatOne ? 1 : Authority.NetRoute.HostSeat, out result); return result; }
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
        // T-168: "P<seat> <version>" of a remote whose Hello names another protocol version, null if none. World sync
        // refuses to run with it (mixed item / whole-blob world sync would silently lose changes).
        internal static string MismatchedVersion
        {
            get
            {
                foreach (RemoteState remote in s_remotes.Values)
                    if (remote.Hello && !string.IsNullOrEmpty(remote.Version) && remote.Version != ModVersion) return "P" + remote.Seat + " " + remote.Version;
                return null;
            }
        }
        static NetPeer s_peer;
        static PeerHub s_hub;
        static bool s_factorySession;
        // C-server session: the server hands every seat's message to every other seat (no host relaying needed).
        internal static bool ServerForwardsAll { get { return s_factorySession; } }
        static bool HasHub { get { return s_hub != null; } }
        static bool WaitingForSeat { get { return s_session.WaitingForSeat; } }
        public static NetPeer Peer { get { return !HasHub ? s_peer : Primary == null ? null : Primary.Peer; } }
        static IDatagramTransport s_transport;
        public static bool RemoteHello { get { return Primary != null && Primary.Hello; } }
        public static bool RemoteInGameplay { get { return Primary != null && Primary.InGameplay; } }
        public static string RemoteVersion { get { return Primary == null ? "" : Primary.Version; } }
        public static string RemoteScene { get { return Primary == null ? "" : Primary.Scene; } }
        static bool s_sentInGameplay; static string s_sentScene; static bool s_statusDirty = true;
        static float s_retryAt;
        // T-148: the transport knows that trying again cannot work (the C-server refused the join). The session stays as it
        // is, without a connection: Problem holds the reason (Phase.Retrying, so the join / host page shows it with its
        // retry and back rows) and nothing reconnects until Begin (retry) or End (back).
        static bool s_holdRetry;
        public static bool RetryHeld { get { return s_holdRetry; } }
        public static void HoldRetry(string reason)
        {
            if (!Enabled) return;
            s_holdRetry = true;
            Problem = reason ?? "";
        }

        // ---- send side ----
        static uint s_localTick;
        public static uint LocalTick { get { return s_localTick; } }
        public static uint LastPlayedTick { get { return Primary == null ? 0 : Primary.Last.Tick; } }
        public static uint NewestRemoteTick { get { return Primary == null ? 0 : Primary.NewestTick; } }
        static readonly Frame[] s_sent = new Frame[Redundancy];
        static readonly Dictionary<int, Frame[]> s_extraSent = new Dictionary<int, Frame[]>();
        static readonly int[] s_extraCount = new int[5];
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
            bool have = TryGetRemote(seat, out remote) && remote.HaveRemotePos;
            position = have ? remote.LatestRemotePos : Vector3.zero;
            return have;
        }
        internal static bool NewestRemoteFrame(int seat, uint tick)
        {
            RemoteState remote;
            return TryGetRemote(seat, out remote) && remote.HaveRemotePos && remote.NewestTick == tick;
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
            s_transportKind = cfg.Bind("Net", "Transport", "direct", "direct = host mode on the local network: one player hosts, the others join its IP address and port (players on different networks can set up their own tunnel or VPN). steam = Steam friends: the host invites from the Steam overlay, both must own Ori and run Steam. An old 'relay' value falls back to direct.");
            s_inviteKey = cfg.Bind("Net", "InviteKey", KeyCode.F5, "Transport = steam, host: opens the Steam overlay to invite a friend.");
            s_menuAddress = cfg.Bind("OnlineMenu", "LastAddress", "", "Last address typed in the in-game online menu (prefill only).");
            s_menuRoom = cfg.Bind("OnlineMenu", "LastRoomCode", "", "Last room code typed in the in-game online menu (prefill only).");
            FrameBufferTrim.MutateNoRestart = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_STREAM_RESTART") == "1";
            SteamTransport.Trace += m => Plugin.Log.LogInfo("net: Steam: " + m);
            NetPeer.Trace = m => Plugin.Log.LogInfo("net: peer: " + m);
            // T-159: the relay is gone. An old [Net] Transport = relay (its RelayServer / RoomCode keys stay in the file
            // as orphans, BepInEx ignores them) falls back to direct (host mode on the local network).
            if (s_transportKind.Value.Trim().ToLowerInvariant() == "relay")
            {
                Plugin.Log.LogWarning("online co-op: the relay transport was removed; [Net] Transport = relay falls back to direct (LAN host mode)");
                s_transportKind.Value = "direct";
            }
            string env = Environment.GetEnvironmentVariable("ORICOOP_NET");
            s_modeNow = (string.IsNullOrEmpty(env) ? s_mode.Value : env).Trim().ToLowerInvariant();
            if (!Enabled) return;
            s_session.ActiveTransport = SessionMachine.NormalizeTransport(s_configuredTransport());
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
        // The selected transport and the menu's address/port override env/config for this session only.
        // The last address / room code are remembered in [OnlineMenu] for prefilling.
        static string s_rtAddress;
        static int s_rtPort;
        static ConfigEntry<string> s_menuAddress, s_menuRoom;
        static readonly Func<string> s_configuredTransport = delegate { return Env("ORICOOP_NET_TRANSPORT", s_transportKind.Value); };
        public static string TransportName { get { return s_session.TransportName(s_configuredTransport); } }
        public static string ConfigAddress { get { return s_menuAddress.Value.Length > 0 ? s_menuAddress.Value : s_address.Value; } }
        public static string ConfigRoomCode { get { return s_menuRoom.Value; } }
        public static int ConfigPort { get { return s_port.Value; } }
        // Last problem worth showing to the player (room not found, cannot resolve, Steam error...). "" when fine.
        public static string Problem = "";
        public static float StartedAt;
        // True while End() reports the session end (the player chose to leave: no "connection lost" dialog).
        public static bool EndingByChoice;

        // host: true = create a room / listen. transport: direct | steam | a TransportFactory name (cserver).
        // target: direct join = "ip[:port]"; factory transports get it as is; ignored otherwise.
        public static void Begin(bool host, string transport, string target)
        {
            transport = SessionMachine.NormalizeTransport(transport);
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
            s_session.ActiveTransport = transport;
            s_factorySession = TransportFactory != null && transport != "direct" && transport != "steam";
            target = (target ?? "").Trim();
            s_factoryTarget = target;
            if (transport == "direct" && !host && target.Length > 0)
            {
                s_menuAddress.Value = target;
                int colon = target.LastIndexOf(':'); int p;
                if (colon > 0 && int.TryParse(target.Substring(colon + 1), out p)) { s_rtPort = p; target = target.Substring(0, colon); }
                s_rtAddress = target;
            }
            if (P2Context.P2 != null) Plugin.DespawnNow("online play starting");
            OnlineRoomScreen.Reset();
            Problem = ""; StartedAt = Time.realtimeSinceStartup; s_retryAt = 0f; s_holdRetry = false; s_showStats = false;
            s_modeNow = host ? "host" : "join";
            Plugin.Log.LogInfo("online co-op: started from the menu: " + s_modeNow + " via " + transport);
        }

        // Back to offline. Tells the partner, ends the session for every sync system, frees the socket.
        public static void End(string why)
        {
            // T-113: a deliberate end, before the goodbye below: transports that hold seats (C-server) release them.
            // T-213: the hook may poll the connection for a moment (the C-server release waits for its acknowledgement). A
            // connection that ends during that wait ends by this player's choice too: no "connection lost" dialog.
            if (Enabled && Leaving != null)
            {
                bool byChoice = EndingByChoice; EndingByChoice = true;
                try { Leaving(); } catch (Exception e) { Plugin.Log.LogWarning("online co-op: leaving hook: " + e.Message); }
                finally { EndingByChoice = byChoice; }
            }
            OnlineRoomScreen.Reset();
            s_rtAddress = null; s_rtPort = 0; RoomCodeOverride = null;
            s_steamLobby = 0; s_steamWaitLogged = false; s_steamReconnects = 0;
            s_holdRetry = false;
            if (!Enabled) return;
            bool wasConnected = Connected || s_session.Started || s_remotes.Count > 0;
            Shutdown(why);
            if (wasConnected)
            {
                RemoveRemote(why);
                EndingByChoice = true;
                try
                {
                    foreach (RemoteState remote in new List<SessionMachine.Remote>(s_remotes.Values)) Leave(remote, why);
                    EndSession();
                }
                finally { EndingByChoice = false; }
            }
            Close();
            s_reListen = false; s_modeNow = "off"; Problem = "";
            s_session.ActiveTransport = null;
            if (Stopped != null) { try { Stopped(); } catch (Exception e) { Plugin.Log.LogWarning("online co-op: stop hook: " + e.Message); } }
            HostOverride = false;      // a role from the ended session (server-mode arbiter) never carries into the next one
            Plugin.Log.LogInfo("online co-op: stopped (" + why + ")");
        }

        public enum Phase { Off, Starting, Waiting, Connecting, Connected, Retrying }
        // What the UI shows: Waiting = host ready for a partner (RoomCode set for C-server); Retrying = Problem set.
        public static Phase CurrentPhase { get { return (Phase)s_session.CurrentPhase; } }
        public static bool PartnerHere { get { return RemoteCount > 0; } }
        public static float RttMs { get { return Peer != null && Peer.State == PeerState.Connected ? Peer.Stats.RttMs : 0f; } }
        public static float LossPercent { get { return Peer != null && Peer.State == PeerState.Connected ? Peer.Stats.LossPercent : 0f; } }
        // Longest time since any connected remote last sent a packet (ms); 0 when not connected. Drives NetHud's
        // "signal lost" hint, because RttMs keeps its last value while the link is silent (lag test ②).
        public static float SilenceMs { get { return s_session.SilenceMs; } }
        public const float SilenceHintMs = 1000f;
        public static bool SignalLost { get { return s_session.SignalLost; } }
        public static string SteamPartnerName { get { return s_steam != null ? s_steam.RemoteName : ""; } }
        public static bool SteamReady { get { return s_steam != null && s_steam.Ready; } }
        static float s_overlayToastAt;
        public static void OpenSteamInvite()
        {
            if (s_steam != null && s_steam.Ready && !s_steam.OpenInviteOverlay() && Time.realtimeSinceStartup >= s_overlayToastAt)
            {
                s_overlayToastAt = Time.realtimeSinceStartup + 5f;
                NetHud.Toast(L10n.T("steam.overlay.off", true), L10n.T("steam.overlay.off", false));
            }
        }

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
        internal static bool UsesCServer { get { return Enabled && TransportName == "cserver"; } }

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
        static readonly bool s_mutateKeepSteamProblem = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STEAM_KEEP_PROBLEM") == "1";
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
                Plugin.Log.LogWarning("net: Steam: " + s_steam.Error);
                TransportFailed("steam: " + s_steam.Error, 5f);
                return false;
            }
            if (IsHost && s_steam.Ready && Input.GetKeyDown(s_inviteKey.Value)) OpenSteamInvite();
            if (!s_steam.Ready) return false;
            // T-216: the host's lobby is up again after a Steam error, so the failure is over. Problem stayed set
            // until a guest connected: the phase stayed Retrying, the host page showed "failed" and Start did nothing.
            if (IsHost && Problem.Length > 0 && !s_mutateKeepSteamProblem) Problem = "";
            if (s_steamReconnects > 0 && Peer != null && Peer.State == PeerState.Connected) s_steamReconnects = 0;
            if (IsHost && !s_steamReadyLogged) { s_steamReadyLogged = true; Plugin.Log.LogInfo("net: Steam: lobby ready " + s_steam.LobbyId); }
            if (s_pendingConnect && s_steam.RemoteEndPoint != null) { s_pendingConnect = false; Peer.Connect(s_steam.RemoteEndPoint); Plugin.Log.LogInfo("net: Steam: in lobby; connecting to " + s_steam.RemoteName); }
            return true;
        }

        static ConfigEntry<string> s_transportKind;
        static bool s_pendingConnect;
        static string Env(string name, string fallback) { string e = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(e) ? fallback : e; }
        public static string RoomCode { get { return RoomCodeOverride ?? ""; } }

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

        static void NewPeer()
        {
            s_peer = new NetPeer(s_transport);
            ApplySimFromEnv(Peer.Sim);
            Peer.Connected += OnConnected;
            Peer.Disconnected += OnDisconnected;
        }

        static int Port { get { int p; return int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_NET_PORT"), out p) ? p : s_rtPort > 0 ? s_rtPort : s_port.Value; } }

        static void Open()
        {
            if (!IsHost && !UsesSteam && s_transport is UdpTransport && s_peer != null && s_peer.State == PeerState.Disconnected)
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

        // T-213: the transport itself failed (Steam: the host left the lobby, Steam went away) before a goodbye was read
        // from the connection, so OnDisconnected never runs for it. The session ends the way a goodbye ends it: the
        // partners' Oris go, every remote leaves, the session-ended handlers run. Only then is the transport closed
        // (Close alone just forgets the remotes: the partner's Ori stayed on screen and nothing was told the session ended).
        static readonly bool s_mutateFailKeepsRemotes = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_FAIL_KEEPS_REMOTES") == "1";
        static void TransportFailed(string problem, float retryIn)
        {
            Problem = problem;
            if (!s_mutateFailKeepsRemotes) EndLostSession(problem);
            Close(); s_retryAt = Time.realtimeSinceStartup + retryIn;
        }

        // The connection is gone without this side having chosen to leave: every remote leaves and the session ends.
        static void EndLostSession(string reason)
        {
            RemoveRemote("connection lost (" + reason + ")");
            foreach (RemoteState remote in new List<SessionMachine.Remote>(s_remotes.Values)) Leave(remote, reason);
            EndSession();
        }

        // Test only (T-213): this side says goodbye (so the partner sees it at once) and, before its own connection has
        // reported the end, the transport fails: the order a Steam guest sees when the host leaves the lobby. No retry.
        internal static void TestTransportFailure(string problem)
        {
            Shutdown("test: transport failure");
            TransportFailed(problem, 3600f);
        }

        static void Close()
        {
            if (s_hub != null) { s_hub.Dispose(); s_hub = null; }
            if (s_transport != null) { try { s_transport.Dispose(); } catch { } }
            s_transport = null; s_peer = null; s_steam = null; s_pendingConnect = false; s_steamReadyLogged = false;
            s_factorySession = false; s_reListen = false;
            ResetSession();
        }

        static void ResetSession(bool keepHostLocals = false)
        {
            s_session.Reset();
            if (keepHostLocals) RetainHostLocalSeats(); else ResetLocalSeats();
            s_statusDirty = true; s_sentCount = 0;
            s_bus.ClearEarly(); s_bus.ClearRenumbered();
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
                    if (remote.Peer == null) continue;
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
            ResetSession(IsHost && !s_factorySession);
            if (s_factorySession) return;
            int seat = 1;
            if (IsHost)
            {
                EnsureHostSeats(); seat = 2;
                while (SeatOwner(seat) != 0) seat++;
                if (seat > Players.MaxSeats) { s_peer.Disconnect("room full"); return; }
                s_seats.Add(seat);
            }
            s_remotes.Add(seat, new RemoteState { Seat = seat, Peer = s_peer });
            if (IsHost) PublishSeats();
            if (!WaitingForSeat) SendReliable(MsgHello, w => w.Write(ModVersion));
        }

        static void OnHubConnected(NetPeer peer)
        {
            int seat = 2;
            EnsureHostSeats();
            while (SeatOwner(seat) != 0) seat++;
            if (seat > Players.MaxSeats) { peer.Disconnect("room full"); return; }
            s_seats.Add(seat);
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
            PublishSeats();
            Plugin.Log.LogInfo("net: connected P" + seat + " at " + peer.Remote);
        }

        static void OnHubDisconnected(NetPeer peer, string reason)
        {
            RemoteState leaving = null;
            foreach (RemoteState remote in s_remotes.Values) if (remote.Peer == peer) { leaving = remote; break; }
            if (leaving == null) return;
            int seat = leaving.Seat;
            Plugin.Log.LogInfo("net: P" + seat + " disconnected from " + peer.Remote + ": " + reason);
            foreach (int owned in s_seats.SeatsOf(seat))
            {
                RemoteState entry;
                if (TryGetRemote(owned, out entry)) Leave(entry, reason);
                int gone = owned; SendReliable(MsgLeft, w => w.Write((byte)gone));
            }
            s_seats.RemoveDevice(seat);
            PublishSeats();
        }

        static void Leave(RemoteState remote, string why) { s_session.Leave(remote); }

        static void OnRemoteLeaving(SessionMachine.Remote entry)
        {
            RemoteState remote = (RemoteState)entry;
            if (remote.Puppet != null) { Plugin.DespawnPuppet(remote.Puppet); remote.Puppet = null; }
        }

        public static void AssignSeat(int seat)
        {
            s_session.AssignSeat(seat);
            Plugin.Log.LogInfo("net: assigned P" + seat);
            SendReliable(MsgHello, w => w.Write(ModVersion));
        }

        // Factory (C-server) sessions: nobody relays earlier Hello/Status to a seat that joins later (the hub's
        // OnHubConnected does that for direct), so each client introduces itself to every newly online seat.
        public static void IntroduceTo(int seat)
        {
            SendTo(seat, MsgHello, true, w => w.Write(ModVersion));
            bool inGame = LocalInGameplay();
            string scene = LocalScene(inGame);
            SendTo(seat, MsgStatus, true, w => { w.Write(inGame); w.Write(scene); });
        }

        public static void RemoteLeft(int seat) { s_session.RemoteLeft(seat); }

        static void EndSession() { s_session.EndSession(); }

        static void OnDisconnected(string reason)
        {
            // T-201: creating a C-server room is a client connection too: while it has no seat yet (still on the menu), a failed
            // connection shows its reason on the host page instead of "preparing" for ever. A held refusal keeps its reason.
            Plugin.Log.LogWarning("net: disconnected: " + reason);
            if (!s_holdRetry && (!IsHost || (s_factorySession && WaitingForSeat))) Problem = reason;
            EndLostSession(reason);
            ResetSession(IsHost && !s_factorySession);
            // Host keeps listening for a new guest; a guest retries after a moment.
            // T-030: a guest tries again after 1 s (was 3 s). Each connect attempt itself keeps asking for up to 10 s, so this
            // only shortens the gap after a timeout; the host listens again after 0.5 s.
            s_retryAt = Time.realtimeSinceStartup + (IsHost ? 0.5f : 1f);
            // A Steam host keeps its lobby (same lobby for the friend): new peer on the same Steam transport.
            if (IsHost && s_steam != null && s_steam.Ready) { s_reListen = true; }
        }
        static bool s_reListen;
        static void ReListenIfNeeded()
        {
            if (!s_reListen) return;
            s_reListen = false;
            NewPeer(); Peer.Listen();
            Plugin.Log.LogInfo("net: Steam lobby waiting for a new guest");
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

        static readonly Action s_inviteTick = SteamInviteHook.Tick;
        static readonly Action s_inviteDraw = SteamInviteHook.DrawGUI;
        static readonly Action s_sendLocal = SendLocal, s_playRemote = PlayRemote;
        static readonly Action s_extraTick = OnlineLocalPlayers.Tick, s_extraSample = OnlineLocalPlayers.SampleExtras;

        // ---------------------------------------------------------------- per frame (Runner.Update)
        public static void Update()
        {
            // Before the Enabled check: Steam invites must be caught (and acted on) while online play is off.
            SteamTransport.PumpFake();
            SubsystemGuard.Run("SteamInviteHook.Tick", s_inviteTick);
            if (!Enabled) return;
            if (Input.GetKeyDown(s_statsKey.Value)) s_showStats = !s_showStats;
            ReListenIfNeeded();
            if (UsesSteam && !PumpSteam()) return;
            if (!HasHub && (Peer == null || Peer.State == PeerState.Disconnected || (Peer.State == PeerState.Idle && !s_pendingConnect)))
            {
                if (!s_holdRetry && Time.realtimeSinceStartup >= s_retryAt) Open();
                if (!HasHub && Peer == null) return;
            }
            SubsystemGuard.Run("OnlineLocalPlayers.Tick", s_extraTick);
            if (HasHub)
            {
                s_hub.Poll();
                foreach (RemoteState remote in new List<SessionMachine.Remote>(s_remotes.Values))
                    if (remote.Peer != null) ReadPeer(remote.Peer, remote, true);
                s_hub.Flush();
            }
            else { Peer.Poll(); if (Peer != null) ReadPeer(Peer, IsHost && !s_factorySession ? Primary : null); }
            if (s_factorySession) Authority.CServerLink.TickOwnerNotice();
            if (!Connected || WaitingForSeat) return;

            bool inGame = LocalInGameplay() && !TestLoading;   // TestLoading: as while loading, also drop the puppets here
            bool shown = inGame && !TestReportLoading;   // what the others are told
            string scene = LocalScene(shown);
            if (shown != s_sentInGameplay || scene != s_sentScene || s_statusDirty)
            {
                s_sentInGameplay = shown; s_sentScene = scene; s_statusDirty = false;
                SendReliable(MsgStatus, w => { w.Write(shown); w.Write(scene ?? ""); });
            }

            foreach (RemoteState remote in s_remotes.Values)
            {
                if (remote.Puppet != null && remote.Puppet.Sein == null) remote.Puppet = null;
                if (remote.Puppet == null && remote.Hello && remote.InGameplay && inGame && remote.HaveRemotePos && Characters.Sein != null
                    && Time.realtimeSinceStartup >= remote.SpawnTryAt && SaveSync.ReadyForSeat(DeviceSeat(remote.Seat)))
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
            SubsystemGuard.Run("Revive.TickRoomFlame", s_roomFlameTick);
        }
        static readonly Action s_roomFlameTick = Revive.TickRoomFlame;

        static string LocalScene(bool inGame)
        {
            return inGame && Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null ? Core.Scenes.Manager.CurrentScene.Scene : "";
        }

        static void ReadPeer(NetPeer peer, RemoteState source, bool stopIfMoved = false)
        {
            byte[] msg; bool reliable;
            // T-149: a message (MsgLocalAdd) can renumber this device's primary and move its peer to another seat's
            // remote; the rest of its queue is read on the next Update under the new seat, not under the stale one.
            while ((!stopIfMoved || source.Peer == peer) && peer.TryReceive(out msg, out reliable))
            {
                try { Handle(msg, reliable, source); }
                catch (Exception e) { Plugin.Log.LogWarning("net: bad message: " + e.Message); }
            }
        }

        // Test only (T-131, the 4P wipe check): tell the others "not in gameplay", as while loading, without loading.
        // T-213: like a real load it also stops this PC's movement frames (SendLocal).
        internal static bool TestReportLoading;
        static readonly bool s_mutateExtraKeepsHistory = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_EXTRA_KEEPS_HISTORY") == "1";
        static readonly bool s_mutateExtraKeepsStream = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_EXTRA_KEEPS_STREAM") == "1";
        // Test only (T-166): behave as if loading here too (the puppets of the others go, so revive messages are missed).
        internal static bool TestLoading;

        // T-131: a teammate in this session (it said hello), also while its Ori is away (loading, rejoining).
        internal static bool RemoteInSession(int seat) { return s_session.RemoteInSession(seat); }
        internal static bool RemoteLoading(int seat)
        { RemoteState remote; return TryGetRemote(seat, out remote) && remote.Hello && !remote.InGameplay; }

        public static bool LocalInGameplay()
        {
            SeinCharacter s = Characters.Sein;
            if (s == null || !s.gameObject.activeInHierarchy || (P2Context.P2 != null && s == P2Context.P2)) return false;
            GameController gc = GameController.Instance;
            if (gc == null || gc.IsLoadingGame) return false;
            if (GameStateMachine.Instance != null && GameStateMachine.Instance.IsInExtendedTitleScreen()) return false;
            // T-203: the new-game prologue is not co-op gameplay: no checkpoint leaves a host in the middle of it.
            if (GameStateMachine.Instance != null && GameStateMachine.Instance.CurrentState == GameStateMachine.State.Prologue) return false;
            return true;
        }

        static void Handle(byte[] m, bool reliable, RemoteState source)
        {
            s_bus.Dispatch(m, reliable, source == null ? 0 : source.Seat);
        }

        static void HandleCore(byte type, BinaryReader r, byte[] m, RemoteState remote)
        {
            if (type == MsgHello)
            {
                remote.Version = r.ReadString(); remote.LastHello = m;
                bool joined = s_session.MarkHello(remote);
                Plugin.Log.LogInfo("net: P" + Sender + " runs " + remote.Version + (remote.Version == ModVersion ? "" : " (different from ours: " + ModVersion + ")"));
                s_session.AnnounceHello(remote, joined);
                CopyDeviceStatus(remote);
            }
            else if (type == MsgStatus)
            {
                remote.InGameplay = r.ReadBoolean(); remote.Scene = r.ReadString(); remote.LastStatus = m;
                Plugin.Log.LogInfo("net: P" + Sender + " " + (remote.InGameplay ? "in gameplay (" + remote.Scene + ")" : "not in gameplay"));
                if (!remote.InGameplay) ClearStream(remote);
                CopyDeviceStatus(remote);
            }
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
        }

        // The seat's movement stream starts over: nothing buffered, no play position, no known position or facing.
        static void ClearStream(RemoteState remote)
        {
            remote.Buffer.Clear(); remote.HavePlay = false; remote.HaveRemotePos = false; remote.HaveFace = false;
        }

        // Test only (T-213): a straggler of the seat's previous player: one frame far ahead of the stream, somewhere else.
        internal static void TestStaleFrame(int seat, uint ahead, Vector3 at)
        {
            RemoteState remote;
            if (!TryGetRemote(seat, out remote)) return;
            Frame f = new Frame(); f.Tick = remote.NewestTick + ahead; f.Flags = (byte)FlagNeutral; f.Pos = at;
            Receive(remote, f);
        }
        internal static long TestSnaps(int seat) { RemoteState remote; return TryGetRemote(seat, out remote) ? remote.Snaps : -1; }

        static void Receive(RemoteState remote, Frame f)
        {
            if (!remote.InGameplay) return;
            FrameBufferTrim.Admit admit = FrameBufferTrim.Judge(remote.HavePlay, remote.PlayTick, f.Tick);
            if (admit == FrameBufferTrim.Admit.Drop) return;                 // already played (redundant copy) or too late
            if (admit == FrameBufferTrim.Admit.Restart) ClearStream(remote);  // T-213: the seat's stream started over
            if (remote.Buffer.ContainsKey(f.Tick)) return;
            if (!remote.HaveRemotePos || f.Tick > remote.NewestTick) { remote.NewestTick = f.Tick; remote.LatestRemotePos = f.Pos; remote.HaveRemotePos = true; }
            remote.Buffer[f.Tick] = f;
            if (!remote.HavePlay) { remote.HavePlay = true; remote.PlayTick = f.Tick; }
        }

        // ---------------------------------------------------------------- per fixed step (Runner.FixedUpdate)
        public static void FixedTick()
        {
            if (!Enabled) return;
            SubsystemGuard.Run("OnlineLocalPlayers.SampleExtras", s_extraSample);
            SubsystemGuard.Run("NetPlay.SendLocal", s_sendLocal);
            SubsystemGuard.Run("NetPlay.PlayRemote", s_playRemote);
        }

        static sbyte Q(float v) { return (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * 127f), -127, 127); }
        static float DQ(sbyte v) { return v / 127f; }

        static void SendLocal()
        {
            // T-213 (6): while this PC is not in gameplay (a load) nothing is sent, and afterwards no frame from before
            // goes out again: for every local seat, not only the first. The extra local players' histories were kept, so
            // their first packet after a load repeated their last three frames at the old place.
            if (!Connected || WaitingForSeat || !LocalInGameplay() || TestReportLoading)
            {
                s_sentCount = 0;
                if (!s_mutateExtraKeepsHistory) Array.Clear(s_extraCount, 0, s_extraCount.Length);
                return;
            }
            uint tick = ++s_localTick;
            SendSeatFrame(Players.LocalSeat, Characters.Sein, tick);
            foreach (Puppet puppet in Players.Remotes)
            {
                if (!puppet.IsLocal || puppet.Sein == null) continue;
                puppet.Input.Swap();
                try { SendSeatFrame(puppet.Seat, puppet.Sein, tick); }
                finally { puppet.Input.Swap(); }
            }
        }

        static void SendSeatFrame(int seat, SeinCharacter p1, uint tick)
        {
            GameController gc = GameController.Instance;
            bool map = seat == Players.LocalSeat && SessionRules.LiveMap;
            bool neutral = map || gc == null || gc.GameplaySuspended || gc.GameplaySuspendedForUI || gc.InputLocked || ForcedSync.IsLocked(p1)
                || p1.Controller == null || !p1.Controller.CanMove || p1.Controller.LockMovementInput || p1.Controller.IgnoreControllerInput;

            Frame f = new Frame();
            f.Tick = tick;
            if (map) f.Flags |= FlagMap;
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
            f.Flags |= FlagFaceKnown;
            if (p1.FaceLeft) f.Flags |= FlagFaceLeft;
            f.Pos = p1.Position;
            f.Vel = new Vector2(p1.Speed.x, p1.Speed.y);

            // Each local seat has its own redundancy history; never mix ticks/inputs between players.
            Frame[] history = s_sent;
            int count = s_sentCount;
            if (seat != Players.LocalSeat)
            {
                if (!s_extraSent.TryGetValue(seat, out history)) { history = new Frame[Redundancy]; s_extraSent[seat] = history; }
                count = s_extraCount[seat];
            }
            for (int i = Redundancy - 1; i > 0; i--) history[i] = history[i - 1];
            history[0] = f;
            if (count < Redundancy) count++;
            if (seat == Players.LocalSeat) s_sentCount = count; else s_extraCount[seat] = count;

            MemoryStream ms = new MemoryStream(s_buf);
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(MsgFrames); w.Write((byte)Players.LocalSeat); w.Write(QpcMs()); w.Write((byte)count);
            for (int i = count - 1; i >= 0; i--)
            {
                Frame s = history[i];
                w.Write(s.Tick); w.Write(s.Buttons); w.Write(s.LX); w.Write(s.LY); w.Write(s.RX); w.Write(s.RY); w.Write(s.Flags);
                w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z); w.Write(s.Vel.x); w.Write(s.Vel.y);
            }
            w.Flush();
            if (seat == Players.LocalSeat) Broadcast(s_buf, (int)ms.Position, false);
            else
            {
                byte[] body = new byte[(int)ms.Position - 2]; Array.Copy(s_buf, 2, body, 0, body.Length);
                byte[] packet = Seats.FrameStreams.Encode(Players.LocalSeat, new int[] { seat }, new byte[][] { body });
                Broadcast(packet, packet.Length, false);
            }
            if (s_hub != null) s_hub.Flush(); else Peer.Flush();
        }

        static void PlayRemote()
        {
            foreach (RemoteState remote in s_remotes.Values) PlayRemote(remote);
        }

        static void PlayRemote(RemoteState remote)
        {
            if (remote.Puppet == null || remote.Puppet.Sein == null)
            {
                // T-150: nothing plays these frames now; keep only the newest few for when the puppet appears.
                if (remote.HavePlay) remote.PlayTick = FrameBufferTrim.TrimIdle(remote.Buffer, remote.NewestTick, remote.PlayTick, FrameBufferTrim.IdleKeep);
                return;
            }
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
            if ((f.Flags & FlagFaceKnown) != 0) { remote.HaveFace = true; remote.FaceLeft = (f.Flags & FlagFaceLeft) != 0; }
            ApplyFacing(remote);
            // Replay the map lock as well as neutral buttons so an aiming puppet cancels its cast.
            remote.Puppet.MapInputLocked = (f.Flags & FlagMap) != 0;
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

        // T-199: a puppet replays its owner's inputs through the game's own states, and its facing was whatever that
        // replay produced. The replay is not exact (the puppet's wall contact, landing and animation ends differ a
        // little from the owner's), and a wall jump locks the sprite mirror while its animation plays: when the
        // puppet's lock ended later than the owner's, or it missed a wall jump, it kept facing the old way (playtest
        // 4: "the character does not turn during wall jumps"). The owner's facing now travels in every frame and is
        // put on the puppet past the mirror's lock, when the frame is played and again before each rendered frame.
        static readonly System.Reflection.FieldInfo s_fFaceLeft = HarmonyLib.AccessTools.Field(typeof(CharacterSpriteMirror), "m_faceLeft");
        internal static readonly bool s_mutateNoFacing = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_FACING") == "1";
        public static int FacingFixes;

        static void ApplyFacing(RemoteState remote)
        {
            if (s_mutateNoFacing || !remote.HaveFace || remote.Puppet == null || remote.Puppet.Sein == null) return;
            CharacterSpriteMirror mirror = remote.Puppet.Sein.PlatformBehaviour.Visuals.SpriteMirror;
            if (mirror == null || mirror.FaceLeft == remote.FaceLeft) return;
            s_fFaceLeft.SetValue(mirror, remote.FaceLeft);
            mirror.UpdateMaterial();
            FacingFixes++;
        }

        // Net test (FACE): the owner's facing in the frame the first partner's copy played last.
        internal static bool PartnerFacing(out bool left)
        {
            RemoteState remote = Primary;
            left = remote != null && remote.FaceLeft;
            return remote != null && remote.HaveFace;
        }

        // Runner.LateUpdate: after the puppets' own states ran this frame.
        public static void LateTick()
        {
            if (!Enabled) return;
            foreach (RemoteState remote in s_remotes.Values) ApplyFacing(remote);
        }

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
            string role = UsesCServer ? L10n.F("hud.role", Roles.Tag(Roles.Mine), Roles.Word(Roles.Mine))
                : IsHost ? "host P1" : "guest P" + Players.LocalSeat;
            string s = "NET " + role + " " + st;
            if (UsesSteam)
                s += s_steam == null ? (IsHost ? " (Steam: starting)" : " (Steam: waiting for an invite: accept it in Steam, or Join Game from your friends list)")
                    : !s_steam.Ready ? " (Steam: lobby starting)"
                    : (IsHost && (Peer == null || Peer.State != PeerState.Connected) ? "  STEAM lobby ready: press " + s_inviteKey.Value + " to invite a friend" : "  STEAM " + s_steam.RemoteName);
            if (Peer != null && Peer.State == PeerState.Connected)
            {
                NetStats n = Peer.Stats;
                s += string.Format("  RTT {0:F0} ms  jitter {1:F0}  spread {11:F0}  loss {2:F1}%  {3:F1}/{4:F1} KB/s  buf {5}/{6}  err {7:F2} (avg {8:F2})  snaps {9}  lost {10}",
                    n.RttMs, n.JitterMs, n.LossPercent, n.SendKBps, n.RecvKBps, (Primary == null ? 0 : Primary.Buffer.Count), TargetDelay, LastError, AvgError, Snaps, FramesMissing, LatenessSpreadMs);
                if (!RemoteInGameplay) s += UsesCServer ? "  (" + L10n.T("hud.remote_not_in_game.cs") + ")" : "  (partner not in gameplay)";
                if (RemoteHello && RemoteVersion != ModVersion) s += "  VERSION MISMATCH " + RemoteVersion;
            }
            else if (Peer != null && Peer.State == PeerState.Disconnected) s += " (" + OwnDisconnectReason(Peer, Peer.DisconnectReason) + ")";
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
            Rect rect = new Rect(10f, 6f, Screen.width - 20f, 30f);
            if (UsesCServer && NativeGui.Available)
            {
                NativeGui.BeginPage("hud.stats");
                try { NativeGui.Label("hud.stats.line", rect, StatusLine(), s_style.fontSize, TextAnchor.MiddleLeft, s_style.normal.textColor); }
                finally { NativeGui.EndPage(); }
            }
            else GUI.Label(rect, StatusLine(), s_style);
        }

        // The partner's Ori is simulated here only to look right; its owner decides what happens to it.
        // So it takes no damage here (otherwise enemies that differ between the two games would kill it).
        [HarmonyPatch(typeof(SeinDamageReciever), "OnRecieveDamage")]
        static class PuppetNoDamage
        {
            static bool Prefix(SeinDamageReciever __instance)
            {
                Puppet puppet = Players.OfInstance(__instance.GetInstanceID());
                return !(Enabled && puppet != null && !puppet.IsLocal);
            }
        }
    }
}
