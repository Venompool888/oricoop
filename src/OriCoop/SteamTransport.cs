using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace OriCoop.Net
{
    // Main-thread only. Ori's Steamworks.Update pumps the wrapper's events.
    // Poll PendingInviteLobby every menu frame, including before Host/Join is used.
    // Only one live transport/uncorrelated lobby operation may exist at a time.
    public sealed class SteamTransport : IDatagramTransport
    {
        private const string Unavailable = "Steam is not running or not logged in";
        private const string NotConnected = " (Steam is not connected to Valve's servers; is this account playing on another PC?)";
        private const string ProtocolTag = "1";
        private const int Channel = 0;
        private const int MaxDatagram = 1200;
        private const int MaxDiscardSize = 1024 * 1024;
        private static double TimeoutSeconds = 30.0;
        internal static void SetTimeoutForTests(double seconds) { TimeoutSeconds = seconds; }
        private static readonly bool MutateNoAccept = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STEAM_NO_ACCEPT") == "1";
        private static readonly EndPoint VirtualRemote = new IPEndPoint(IPAddress.Parse("10.255.254.1"), 1);
        private static ISteamApi hookedSteam;
        private static SteamTransport active;
        // CreateLobby/JoinLobby return void and their result events have no call handle.
        // Keep a retired operation until its result arrives, then leave any late lobby.
        private static SteamTransport pendingOperation;
        // T-216: operations that were given up (timed out, closed) and replaced by a new one before their result
        // came. A late result is recognised through these and its lobby is left; before, the retired operation
        // stayed in pendingOperation and every later Host/Join failed until the game was restarted.
        private static int retiredCreates;
        private static readonly List<ulong> retiredJoins = new List<ulong>();
        private static readonly bool MutateKeepPending = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STEAM_KEEP_PENDING") == "1";
        private static ulong pendingInvite;
        private static ulong pendingInviteFrom;
        private static readonly Stopwatch inviteClock = Stopwatch.StartNew();
        private static double pendingInviteAt;
        private static bool launchParsed;
        private static int wrapperErrorsLogged;
        private static readonly List<string> wrapperErrorsSeen = new List<string>();
        // Diagnostics for the two-PC Steam test (NetPlay forwards these to the BepInEx log as "net: Steam: ...").
        public static event Action<string> Trace;
        private static void Log(string message)
        {
            Action<string> t = Trace;
            if (t == null) return;
            try { t(message); } catch (Exception) { }
        }

        private readonly bool hosting;
        private readonly ulong joinTarget;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly byte[] receiveScratch = new byte[MaxDatagram];
        private readonly List<ulong> sessionRequests = new List<ulong>();
        private int rejectsLogged;
        private bool sentLogged;
        private int skippedLogged;
        private ISteamApi steam;
        private ulong localId;
        private ulong lobbyId;
        private ulong remoteId;
        private ulong sessionId;
        private bool ready;
        private bool disposed;
        private bool leftLobby;
        private bool receivedPeer;
        private bool updating;
        private string error;
        private double operationStarted;
        private double peerStarted = -1.0;

        private SteamTransport(bool hosting, ulong lobby)
        {
            this.hosting = hosting;
            joinTarget = lobby;
        }

        public static void PumpFake()
        {
            if (!SteamApi.Fake) return;
            ISteamApi api = SteamApi.Current;
            if (api != null) api.Pump();
        }

        internal static void ResetForTests()
        {
            if (active != null) active.Dispose();
            EnsureHooks(null);
            EnsureInviteHook(null);
            active = null;
            pendingOperation = null;
            retiredCreates = 0;
            retiredJoins.Clear();
            TimeoutSeconds = 30.0;
            pendingInvite = 0;
            pendingInviteFrom = 0;
            pendingInviteAt = 0;
            inviteClock.Reset();
            inviteClock.Start();
            launchParsed = false;
            wrapperErrorsLogged = 0;
            wrapperErrorsSeen.Clear();
            Trace = null;
        }

        public static SteamTransport Host() { return Start(true, 0); }
        public static SteamTransport Join(ulong lobbyId) { return Start(false, lobbyId); }
        public bool Ready { get { return ready; } }
        public string Error { get { return error; } }
        public ulong LobbyId { get { return lobbyId; } }
        public ulong RemoteSteamId { get { return remoteId; } }
        public EndPoint RemoteEndPoint { get { return remoteId == 0 ? null : VirtualRemote; } }
        // Virtual transport: no local UDP socket or listening port exists.
        public int LocalPort { get { return 0; } }
        private double Now { get { return clock.Elapsed.TotalSeconds; } }

        public string RemoteName
        {
            get
            {
                if (remoteId == 0 || disposed || steam == null) return string.Empty;
                try { return steam.FriendName(remoteId) ?? string.Empty; }
                catch (Exception) { return string.Empty; }
            }
        }

        public static ulong PendingInviteLobby
        {
            get
            {
                ReadLaunchInvite();
                // Only the invite callback is registered this early (polled from the title screen on). Registering
                // all lobby/P2P/error callbacks at game start broke local co-op (run-test revive/teleport/enemy,
                // a CutsceneTrigger exception); those wait for a Steam session (Start -> EnsureHooks).
                try { EnsureInviteHook(SteamApi.Current); }
                catch (Exception) { }
                return pendingInvite;
            }
        }

        // The pending invite without touching Steam (no hook check): safe to read every frame in gameplay.
        public static ulong PendingInviteRaw { get { return pendingInvite; } }

        // Steam ID of the friend whose invite is pending (0 for a +connect_lobby launch).
        public static ulong PendingInviteFrom { get { return pendingInvite == 0 ? 0 : pendingInviteFrom; } }
        public static double PendingInviteAgeSeconds { get { return pendingInvite == 0 ? 0.0 : inviteClock.Elapsed.TotalSeconds - pendingInviteAt; } }

        // Consumes the pending invite (the caller starts the join with it).
        public static ulong TakePendingInvite()
        {
            ulong lobby = PendingInviteLobby;
            pendingInvite = 0;
            pendingInviteFrom = 0;
            return lobby;
        }

        public static void ClearPendingInvite(string why)
        {
            if (pendingInvite == 0) return;
            Log("invite for lobby " + pendingInvite + " dropped (" + why + ")");
            pendingInvite = 0;
            pendingInviteFrom = 0;
        }

        public static string FriendName(ulong id)
        {
            if (id == 0) return string.Empty;
            try
            {
                ISteamApi s = SteamApi.Current;
                return s == null ? string.Empty : (s.FriendName(id) ?? string.Empty);
            }
            catch (Exception) { return string.Empty; }
        }

        // This PC's Steam account (0 / "" when Ori has not started Steam). Main thread only.
        // RelayTransport sends these to the relay server for its operator's join log.
        public static ulong LocalSteamId
        {
            get
            {
                try
                {
                    ISteamApi s = SteamApi.Current;
                    return s == null ? 0 : s.LocalId;
                }
                catch (Exception) { return 0; }
            }
        }

        public static string LocalPersonaName
        {
            get
            {
                try
                {
                    ISteamApi s = SteamApi.Current;
                    return s == null ? string.Empty : (s.PersonaName ?? string.Empty);
                }
                catch (Exception) { return string.Empty; }
            }
        }

        private static void ReadLaunchInvite()
        {
            if (launchParsed) return;
            launchParsed = true;
            try
            {
                ulong lobby = ParseLaunchLobby(Environment.GetCommandLineArgs());
                if (lobby != 0)
                {
                    pendingInvite = lobby;
                    pendingInviteFrom = 0;
                    pendingInviteAt = inviteClock.Elapsed.TotalSeconds;
                    Log("launched with +connect_lobby " + lobby);
                }
            }
            catch (Exception) { }
        }

        private static ulong ParseLaunchLobby(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                ulong id;
                if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0)
                    return id;
            }
            return 0;
        }

        private static SteamTransport Start(bool hosting, ulong lobby)
        {
            ReadLaunchInvite();
            SteamTransport transport = new SteamTransport(hosting, lobby);
            try
            {
                transport.steam = SteamApi.Current;
                if (transport.steam == null || !transport.steam.LoggedOn)
                {
                    transport.Fail(Unavailable + (transport.steam == null ? " (Ori has not started Steam yet)" : NotConnected));
                    return transport;
                }
                EnsureHooks(transport.steam);
                if (active != null) transport.Fail("Dispose the active Steam transport first");
                else if (pendingOperation != null && MutateKeepPending) transport.Fail("A Steam lobby result is still pending; wait for it before retrying");
                else if (!hosting && lobby == 0) transport.Fail("Invalid Steam lobby ID");
                if (transport.error != null) return transport;
                transport.localId = transport.steam.LocalId;
                if (transport.localId == 0) transport.Fail(Unavailable);
                else if (!transport.steam.AllowP2PPacketRelay(true))
                    transport.Fail("Steam could not enable P2P packet relay");
                if (transport.error != null) return transport;
                active = transport;
                RetirePending();
                pendingOperation = transport;
                transport.operationStarted = transport.Now;
                Log((hosting ? "CreateLobby (friends-only, 2 players)" : "JoinLobby " + lobby) + " as " + transport.localId);
                if (hosting) transport.steam.CreateLobby(2);
                else transport.steam.JoinLobby(lobby);
            }
            catch (Exception e)
            {
                if (pendingOperation == transport) pendingOperation = null;
                transport.Fail("Steam lobby operation failed: " + e.Message);
            }
            return transport;
        }

        // T-216: no transport is live here (Start checked `active`), so an operation still in pendingOperation was
        // given up and its result has not come. Remember what it asked for and make room for the new operation.
        private static void RetirePending()
        {
            SteamTransport old = pendingOperation;
            pendingOperation = null;
            if (old == null) return;
            if (old.hosting) { if (retiredCreates < 16) retiredCreates++; }
            // One entry per given-up join, also for the same lobby twice: each late result uses up one, so a late
            // failure of the first cannot hide the late success of the second (this PC would sit in that lobby).
            else if (old.joinTarget != 0 && retiredJoins.Count < 16) retiredJoins.Add(old.joinTarget);
            Log("the earlier " + (old.hosting ? "CreateLobby" : "JoinLobby " + old.joinTarget) + " never got its result; a late one will be dropped");
        }

        // A lobby nobody asked for any more. Never the lobby of the live transport.
        private static void LeaveStray(ulong lobby, string why)
        {
            if (lobby == 0 || (active != null && active.lobbyId == lobby)) return;
            Log("leaving lobby " + lobby + " (" + why + " of an operation that was given up)");
            try { if (hookedSteam != null) hookedSteam.LeaveLobby(lobby); }
            catch (Exception) { }
        }

        // A LobbyEnter result that no live operation waits for. If a given-up JoinLobby asked for this lobby, leave
        // it, unless the live transport is in that same lobby (a retry of the same join took the earlier result).
        private static void LateEnter(ulong entered, bool ok, bool ioFailure)
        {
            if (entered == 0 || !retiredJoins.Remove(entered)) return;
            if (ok && !ioFailure) LeaveStray(entered, "late JoinLobby result");
        }

        private static ISteamApi inviteHookedSteam;

        private static void EnsureInviteHook(ISteamApi value)
        {
            if (object.ReferenceEquals(inviteHookedSteam, value)) return;
            if (inviteHookedSteam != null)
            {
                try { inviteHookedSteam.JoinRequested -= OnInvite; } catch (Exception) { }
            }
            inviteHookedSteam = value;
            if (value == null) return;
            value.JoinRequested += OnInvite;
            Log("invite callback hooked");
        }

        private static void EnsureHooks(ISteamApi value)
        {
            if (object.ReferenceEquals(hookedSteam, value)) return;
            if (active != null) active.Fail(Unavailable + " (Steam interface changed)");
            pendingOperation = null;
            // The old interface's results never come through the new one: nothing of it is awaited any more.
            retiredCreates = 0;
            retiredJoins.Clear();
            if (hookedSteam != null)
            {
                try
                {
                    hookedSteam.LobbyCreated -= OnCreated;
                    hookedSteam.LobbyEntered -= OnEntered;
                    hookedSteam.LobbyMemberChanged -= OnMemberChange;
                    hookedSteam.P2PSessionRequested -= OnSessionRequest;
                    hookedSteam.P2PSessionFailed -= OnSessionFailure;
                    hookedSteam.WrapperError -= OnWrapperError;
                }
                catch (Exception) { } // the old interface may already be shut down
            }
            hookedSteam = value;
            if (value == null) return;
            // The wrapper drops a callback when nothing is subscribed at that moment (SteamService.InvokeEvents);
            // the invite callback is registered from the title screen on (EnsureInviteHook), the rest from here.
            EnsureInviteHook(value);
            value.LobbyCreated += OnCreated;
            value.LobbyEntered += OnEntered;
            value.LobbyMemberChanged += OnMemberChange;
            value.P2PSessionRequested += OnSessionRequest;
            value.P2PSessionFailed += OnSessionFailure;
            // Ori's own handler is empty, so callback struct size mismatches etc. were invisible before.
            value.WrapperError += OnWrapperError;
            ulong me = 0;
            try { me = value.LocalId; } catch (Exception) { }
            Log("callbacks hooked (invites, lobby, P2P); this account " + me + ", logged on " + value.LoggedOn);
        }

        private static void OnWrapperError(Exception e)
        {
            if (e == null || wrapperErrorsLogged >= 20) return;
            string message = e.GetType().Name + ": " + e.Message;
            if (wrapperErrorsSeen.Contains(message)) return;
            wrapperErrorsSeen.Add(message);
            wrapperErrorsLogged++;
            Log("Steamworks wrapper error: " + message);
        }

        private static void OnInvite(ulong lobby, ulong friend)
        {
            ReadLaunchInvite();
            pendingInvite = lobby;
            pendingInviteFrom = friend;
            pendingInviteAt = inviteClock.Elapsed.TotalSeconds;
            Log("invite accepted in Steam: lobby " + pendingInvite + " from " + FriendName(pendingInviteFrom) + " (" + pendingInviteFrom + ")" +
                (active != null ? "; a Steam transport is already active (" + (active.hosting ? "host" : "guest") + ")" : ""));
            // An invite does not automatically interrupt an existing NetPeer session.
        }

        private static void OnMemberChange(ulong lobby, ulong who, string change)
        {
            SteamTransport transport = active;
            if (transport == null || transport.lobbyId == 0 || lobby != transport.lobbyId) return;
            Log("lobby member " + FriendName(who) + " (" + who + ") " + change);
        }

        private static void OnCreated(bool ok, string result, ulong lobby, bool ioFailure)
        {
            SteamTransport transport = pendingOperation;
            if (transport == null || !transport.hosting)
            {
                // T-216: no host operation waits for this: it is the late result of one that was given up.
                if (retiredCreates > 0)
                {
                    retiredCreates--;
                    if (ok) LeaveStray(lobby, "late CreateLobby result");
                }
                return;
            }
            pendingOperation = null;
            try
            {
                if (!ok)
                {
                    transport.Fail("Steam lobby creation failed: " + result);
                    return;
                }
                transport.lobbyId = lobby;
                if (transport.disposed || transport.error != null) { transport.Cleanup(); return; }
                if (!transport.steam.SetLobbyData(lobby, "oricoop", ProtocolTag))
                {
                    transport.Fail("Steam could not set the OriCoop lobby protocol");
                    return;
                }
                transport.ready = true;
                Log("lobby created " + transport.lobbyId + " (owner " + transport.localId + ")");
            }
            catch (Exception e) { transport.Fail("Steam lobby creation failed: " + e.Message); }
        }

        private static void OnEntered(ulong entered, bool ok, string response, bool ioFailure)
        {
            SteamTransport transport = pendingOperation;
            // Steam also emits LobbyEnter while creating a host lobby; ignore that result.
            if (transport == null || transport.hosting) { LateEnter(entered, ok, ioFailure); return; }
            if (entered != transport.joinTarget && !(ioFailure && entered == 0)) { LateEnter(entered, ok, ioFailure); return; }
            pendingOperation = null;
            try
            {
                Log("LobbyEnter result for " + entered + ": " + response + (ioFailure ? " (I/O failure)" : ""));
                if (ioFailure || !ok)
                {
                    transport.Fail("Steam lobby join failed: " + response);
                    return;
                }
                transport.lobbyId = entered;
                if (transport.disposed || transport.error != null) { transport.Cleanup(); return; }
                string tag = transport.steam.GetLobbyData(entered, "oricoop");
                if (tag != ProtocolTag)
                {
                    transport.Fail("This lobby is not compatible with OriCoop protocol " + ProtocolTag + " (lobby says '" + tag + "')");
                    return;
                }
                transport.remoteId = transport.steam.GetLobbyOwner(entered);
                if (!transport.IsAllowedPeer(transport.remoteId))
                {
                    transport.Fail("The Steam lobby host is unavailable (owner " + transport.remoteId + ", " +
                        transport.steam.GetNumLobbyMembers(entered) + " member(s))");
                    return;
                }
                transport.ready = true;
                Log("in lobby " + entered + "; host " + transport.RemoteName + " (" + transport.remoteId + "), " +
                    transport.steam.GetNumLobbyMembers(entered) + " member(s)");
                if (pendingInvite == entered) pendingInvite = 0;
            }
            catch (Exception e) { transport.Fail("Steam lobby join failed: " + e.Message); }
        }

        private static void OnSessionRequest(ulong who)
        {
            SteamTransport transport = active;
            if (transport == null) { Log("P2P session request from " + who + " ignored: no active Steam transport"); return; }
            for (int i = 0; i < transport.sessionRequests.Count; i++)
                if (transport.sessionRequests[i] == who) return;
            Log("P2P session request from " + FriendName(who) + " (" + who + ")");
            // Bound work/memory even if unrelated Steam users send requests.
            if (transport.sessionRequests.Count < 16) transport.sessionRequests.Add(who);
        }

        private static void OnSessionFailure(ulong who, string reason)
        {
            SteamTransport transport = active;
            Log("P2P session with " + who + " failed: " + reason);
            if (transport == null || who == 0 || (who != transport.remoteId && who != transport.sessionId)) return;
            // A host keeps its lobby (and the friend's invite stays valid); only the guest gives up.
            if (transport.hosting) transport.DropPeer("P2P session failed: " + reason);
            else transport.Fail("Steam P2P session failed: " + reason);
        }

        // Host only: forget the current guest but keep the lobby open, so the friend can rejoin (or another
        // friend can be invited) without a new lobby ID. NetPeer notices the silence and listens again.
        private void DropPeer(string why)
        {
            ulong peer = remoteId != 0 ? remoteId : sessionId;
            Log("guest " + peer + " dropped (" + why + "); lobby " + lobbyId + " stays open");
            CloseSession(peer);
            remoteId = 0;
            sessionId = 0;
            receivedPeer = false;
            sentLogged = false;
            peerStarted = -1.0;
        }

        private bool IsAllowedPeer(ulong id)
        {
            if (id == 0 || id == localId || lobbyId == 0) return false;
            if (remoteId != 0 && remoteId != id) return false;
            int count = steam.GetNumLobbyMembers(lobbyId);
            for (int i = 0; i < count; i++)
                if (steam.GetLobbyMemberByIndex(lobbyId, i) == id) return true;
            return false;
        }

        public void Update()
        {
            if (disposed || error != null || updating) return;
            updating = true;
            try
            {
                if (!object.ReferenceEquals(steam, SteamApi.Current) || steam == null || !steam.LoggedOn)
                {
                    Fail(Unavailable + (!object.ReferenceEquals(steam, SteamApi.Current)
                        ? (SteamApi.Current == null ? " (Ori shut Steam down)" : " (Ori restarted Steam)")
                        : NotConnected));
                    return;
                }
                if (!ready)
                {
                    if (Now - operationStarted >= TimeoutSeconds) Fail("Timed out waiting for the Steam lobby");
                    return;
                }
                ulong peer = remoteId != 0 ? remoteId : sessionId;
                if (peer != 0 && hosting && !IsAllowedPeer(peer))
                    DropPeer("the guest left the lobby");
                else if (peer != 0 && (!IsAllowedPeer(peer) ||
                    (!hosting && steam.GetLobbyOwner(lobbyId) != remoteId)))
                {
                    Fail("The Steam peer left the lobby or its owner changed");
                    return;
                }
                for (int i = 0; i < sessionRequests.Count; i++)
                {
                    ulong request = sessionRequests[i];
                    if (!IsAllowedPeer(request))
                    {
                        // Usually a stranger. It can also be the guest's first packet racing the lobby member
                        // list; the guest keeps sending, and Steam raises a new request after the close.
                        if (rejectsLogged++ < 5)
                            Log("P2P session request from " + request + " rejected: not a member of lobby " + lobbyId +
                                " (" + steam.GetNumLobbyMembers(lobbyId) + " member(s))");
                        CloseSession(request);
                        continue;
                    }
                    if (hosting && MutateNoAccept && SteamApi.Fake) continue;
                    if (!steam.AcceptP2PSession(request))
                    {
                        Fail("Steam could not accept the peer session");
                        return;
                    }
                    Log("P2P session accepted with " + FriendName(request) + " (" + request + ")");
                    sessionId = request;
                    if (peerStarted < 0) peerStarted = Now;
                }
                sessionRequests.Clear();
                if (!receivedPeer && peerStarted >= 0 && Now - peerStarted >= TimeoutSeconds)
                {
                    if (hosting) DropPeer("no packet from the guest for " + TimeoutSeconds + " s");
                    else Fail("Timed out connecting to the Steam peer");
                }
            }
            catch (Exception e) { Fail("Steam transport update failed: " + e.Message); }
            finally { updating = false; }
        }

        public bool OpenInviteOverlay()
        {
            Update();
            if (!ready) return true;
            bool enabled = steam.OverlayEnabled;
            try { steam.OpenInviteDialog(lobbyId); }
            catch (Exception e)
            {
                // T-218: a broken invite window is no reason to close the lobby (before: Fail, the lobby was torn
                // down and rebuilt 5 s later with a new id). The caller tells the player how to invite without it.
                Log("Steam invite overlay failed: " + e.Message);
                return false;
            }
            return enabled;
        }

        public void SendTo(byte[] data, int offset, int length, EndPoint to)
        {
            if (disposed) return;
            if (data == null) throw new ArgumentNullException("data");
            if (offset < 0 || length < 0 || offset > data.Length - length)
                throw new ArgumentOutOfRangeException("length");
            if (length > MaxDatagram) throw new ArgumentOutOfRangeException("length", "Steam datagrams must be at most 1200 bytes");
            Update();
            if (!ready || remoteId == 0 || length == 0)
            {
                if (skippedLogged++ < 3) Log("send skipped (" + length + " bytes): ready " + ready + ", remote " + remoteId);
                return;
            }
            if (to == null || !VirtualRemote.Equals(to)) throw new ArgumentException("Unknown Steam endpoint", "to");
            try
            {
                sessionId = remoteId;
                if (peerStarted < 0) peerStarted = Now;
                bool sent = steam.SendP2PPacket(remoteId, data, offset, length, Channel);
                if (!sentLogged) { sentLogged = true; Log("first packet sent to " + remoteId + " (" + length + " bytes, accepted by Steam: " + sent + ")"); }
            }
            catch (Exception e) { Fail("Steam packet send failed: " + e.Message); }
        }

        public int Receive(byte[] buffer, out EndPoint from)
        {
            from = null;
            if (disposed) return 0;
            if (buffer == null) throw new ArgumentNullException("buffer");
            Update();
            if (!ready) return 0;
            try
            {
                // Bound rejected-packet work per call; no waits, threads, or callback pumping.
                for (int i = 0; i < 32; i++)
                {
                    uint available;
                    if (!steam.IsP2PPacketAvailable(out available, Channel)) return 0;
                    if (available > MaxDiscardSize) { Fail("Steam packet exceeds the receive safety limit"); return 0; }
                    // Consume oversized packets completely so they cannot block the queue.
                    byte[] scratch = available <= MaxDatagram ? receiveScratch : new byte[(int)available];
                    uint size;
                    ulong sender;
                    if (!steam.ReadP2PPacket(scratch, out size, out sender, Channel)) return 0;
                    if (!IsAllowedPeer(sender))
                    {
                        if (rejectsLogged++ < 5) Log("packet from " + sender + " dropped: not a member of lobby " + lobbyId);
                        CloseSession(sender);
                        continue;
                    }
                    if (size == 0 || size > MaxDatagram || size > buffer.Length) continue;
                    remoteId = sender;
                    sessionId = remoteId;
                    if (!receivedPeer) Log("first packet received from " + RemoteName + " (" + remoteId + ", " + size + " bytes)");
                    receivedPeer = true;
                    Buffer.BlockCopy(scratch, 0, buffer, 0, (int)size);
                    from = VirtualRemote;
                    return (int)size;
                }
            }
            catch (Exception e) { Fail("Steam packet receive failed: " + e.Message); }
            return 0;
        }

        private void Fail(string message)
        {
            if (error == null) error = message;
            ready = false;
            Cleanup();
            if (active == this) active = null;
        }

        private void CloseSession(ulong id)
        {
            if (id == 0 || steam == null) return;
            try { steam.CloseP2PSession(id); }
            catch (Exception) { } // Steam may already have shut down.
        }

        private void Cleanup()
        {
            CloseSession(sessionId != 0 ? sessionId : remoteId);
            sessionId = 0;
            sessionRequests.Clear();
            if (steam == null || lobbyId == 0 || leftLobby) return;
            Log("leaving lobby " + lobbyId + (error != null ? " (" + error + ")" : disposed ? " (closed)" : ""));
            try { steam.LeaveLobby(lobbyId); }
            catch (Exception) { }
            leftLobby = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ready = false;
            Cleanup();
            if (active == this) active = null;
            // Keep static invite hooks alive in the menu, and retire late lobby results.
        }
    }
}
