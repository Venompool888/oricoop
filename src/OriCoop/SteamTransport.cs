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
        // T-253: how long a P2P session request from a Steam user that is not (yet) in this PC's lobby member list is
        // held before it is refused. The guest's first packet can reach the host before Steam has updated the host's
        // member list; refusing it at once (CloseP2PSessionWithUser) left the handshake to Steam raising a NEW request
        // for the guest's next packet. 10 s = how long the guest's game asks before it gives up ("no answer").
        private static double MemberGraceSeconds = 10.0;
        internal static void SetMemberGraceForTests(double seconds) { MemberGraceSeconds = seconds; }
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
        private sealed class SessionRequest
        {
            public ulong Id;
            public double Since;
            public bool Held;
        }
        private readonly List<SessionRequest> sessionRequests = new List<SessionRequest>();
        // A separate endpoint object for each accepted session, including a new object on rejoin.
        // A delayed send from a retired PeerHub route can never reach the replacement guest.
        private sealed class GuestRoute : EndPoint
        {
            public readonly ulong Id;
            public readonly double Started;
            public bool Received;
            public GuestRoute(ulong id, double started) { Id = id; Started = started; }
            public override string ToString() { return "Steam " + Id; }
        }
        private readonly List<GuestRoute> guests = new List<GuestRoute>(3);
        internal event Action<EndPoint, string> GuestLeft;
        private double nextMemberCheck;
        private int rejectsLogged;
        private bool sentLogged;
        private bool sendRefusedLogged;
        private int skippedLogged;
        // T-253: what the Steam side of the handshake saw, for HandshakeStep / HandshakeReport (host: reset per guest).
        private int requestsSeen, requestsHeld, requestsAccepted, requestsRefused;
        private int packetsSent, sendsRefused, packetsReceived, packetsDropped;
        private ISteamApi steam;
        private ulong localId;
        private ulong lobbyId;
        private ulong remoteId;
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
            MemberGraceSeconds = 10.0;
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
        public ulong RemoteSteamId { get { return hosting ? (guests.Count == 0 ? 0 : guests[0].Id) : remoteId; } }
        public EndPoint RemoteEndPoint { get { return hosting ? (guests.Count == 0 ? null : guests[0]) : remoteId == 0 ? null : VirtualRemote; } }
        // Virtual transport: no local UDP socket or listening port exists.
        public int LocalPort { get { return 0; } }
        private double Now { get { return clock.Elapsed.TotalSeconds; } }

        public string RemoteName
        {
            get
            {
                if (RemoteSteamId == 0 || disposed || steam == null) return string.Empty;
                try { return steam.FriendName(RemoteSteamId) ?? string.Empty; }
                catch (Exception) { return string.Empty; }
            }
        }

        // Lobby metadata is written by the owner. Guests can look up each other's names without a direct P2P link.
        internal void SetSeatMember(int seat, EndPoint endpoint)
        {
            if (!hosting || !ready) return;
            GuestRoute guest = endpoint as GuestRoute;
            try { steam.SetLobbyData(lobbyId, "oricoop.seat" + seat, guest == null ? "" : guest.Id.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception e) { Log("could not publish the Steam member for seat " + seat + ": " + e.Message); }
        }

        internal string SeatName(int seat)
        {
            if (!ready) return "";
            try
            {
                ulong id;
                if (seat == 1) id = steam.GetLobbyOwner(lobbyId);
                else if (!ulong.TryParse(steam.GetLobbyData(lobbyId, "oricoop.seat" + seat), out id)) return "";
                return id == localId ? LocalPersonaName : FriendName(id);
            }
            catch (Exception) { return ""; }
        }

        // T-253: the furthest step of the Steam side of the handshake, i.e. where it stopped when the game handshake
        // got no answer. Host: per guest (DropPeer starts over). Guest: since the lobby join.
        public string HandshakeStep
        {
            get
            {
                if (error != null) return "failed: " + error;
                if (!ready) return hosting ? "creating the lobby" : "joining the lobby";
                if (hosting)
                {
                    if (packetsReceived == 0)
                    {
                        if (requestsAccepted > 0) return "session accepted, no packet received from the guest";
                        for (int i = 0; i < sessionRequests.Count; i++)
                            if (sessionRequests[i].Held) return "session request held: the guest is not in this PC's lobby member list yet";
                        if (requestsRefused > 0) return "session request refused: the guest was not in this PC's lobby member list";
                        if (requestsSeen > 0) return "session request received, not accepted";
                        return "no session request from a guest";
                    }
                    if (packetsSent == 0) return "packets received from the guest, no reply sent";
                    if (sendsRefused == packetsSent) return "packets received from the guest, every reply refused by Steam";
                    return "packets both ways";
                }
                if (packetsSent == 0) return "in the lobby, nothing sent to the host";
                if (sendsRefused == packetsSent) return "every packet to the host refused by Steam";
                if (packetsReceived == 0) return "packets sent to the host, nothing received from it";
                return "packets both ways";
            }
        }

        // T-253: one line with HandshakeStep and the counts behind it, for the log when the game handshake fails.
        public string HandshakeReport
        {
            get
            {
                return (hosting ? "host" : "guest") + " lobby " + lobbyId + ", peer " + RemoteSteamId + ", channel " + Channel +
                    ": " + HandshakeStep + " (session requests " + requestsSeen + " seen, " + requestsHeld + " held, " +
                    requestsAccepted + " accepted, " + requestsRefused + " refused; packets " + packetsSent + " sent, " +
                    sendsRefused + " refused by Steam, " + packetsReceived + " received, " + packetsDropped + " dropped from non-members)";
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
                Log((hosting ? "CreateLobby (friends-only, 4 players)" : "JoinLobby " + lobby) + " as " + transport.localId);
                if (hosting) transport.steam.CreateLobby(4);
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
            transport.nextMemberCheck = 0;
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
                if (transport.sessionRequests[i].Id == who) return;
            transport.requestsSeen++;
            Log("P2P session request from " + FriendName(who) + " (" + who + ")");
            // Bound work/memory even if unrelated Steam users send requests.
            if (transport.sessionRequests.Count < 16)
                transport.sessionRequests.Add(new SessionRequest { Id = who, Since = transport.Now });
        }

        private static void OnSessionFailure(ulong who, string reason)
        {
            SteamTransport transport = active;
            Log("P2P session with " + who + " failed: " + reason);
            if (transport == null || who == 0) return;
            if (transport.hosting) transport.DropGuest(transport.FindGuest(who), "P2P session failed: " + reason);
            else if (who == transport.remoteId) transport.Fail("Steam P2P session failed: " + reason);
        }

        private GuestRoute FindGuest(ulong id)
        {
            for (int i = 0; i < guests.Count; i++) if (guests[i].Id == id) return guests[i];
            return null;
        }

        private void DropGuest(GuestRoute guest, string why)
        {
            if (guest == null || !guests.Remove(guest)) return;
            Log("guest " + guest.Id + " dropped (" + why + "); lobby " + lobbyId + " stays open");
            CloseSession(guest.Id);
            if (GuestLeft != null) GuestLeft(guest, why);
            if (guests.Count != 0) return;
            sentLogged = false;
            sendRefusedLogged = false;
            requestsSeen = requestsHeld = requestsAccepted = requestsRefused = 0;
            packetsSent = sendsRefused = packetsReceived = packetsDropped = 0;
        }

        private bool IsAllowedPeer(ulong id)
        {
            if (id == 0 || id == localId || lobbyId == 0) return false;
            if (!hosting && remoteId != id) return false;
            int count = steam.GetNumLobbyMembers(lobbyId);
            for (int i = 0; i < count; i++)
                if (steam.GetLobbyMemberByIndex(lobbyId, i) == id) return true;
            return false;
        }

        // Membership may lag the request for any of the three guests. A guest only ever talks to the owner.
        private bool CouldBecomePeer(ulong id)
        {
            return id != 0 && id != localId && lobbyId != 0 && (hosting ? guests.Count < 3 : remoteId == id);
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
                if (hosting && Now >= nextMemberCheck)
                {
                    nextMemberCheck = Now + 0.1;
                    for (int i = guests.Count - 1; i >= 0; i--)
                    {
                        GuestRoute guest = guests[i];
                        if (!IsAllowedPeer(guest.Id)) DropGuest(guest, "the guest left the lobby");
                        else if (!guest.Received && Now - guest.Started >= TimeoutSeconds)
                            DropGuest(guest, "no packet from the guest for " + TimeoutSeconds + " s");
                    }
                }
                else if (!hosting && (!IsAllowedPeer(remoteId) || steam.GetLobbyOwner(lobbyId) != remoteId))
                {
                    Fail("The Steam peer left the lobby or its owner changed");
                    return;
                }
                for (int i = 0; i < sessionRequests.Count; )
                {
                    SessionRequest entry = sessionRequests[i];
                    ulong request = entry.Id;
                    if (!IsAllowedPeer(request))
                    {
                        // T-253: the guest's first packet can race the lobby member list (Steam tells the host about
                        // the new member separately). Such a request is held, not closed, until Steam lists the
                        // member; only a user that cannot become this transport's peer, or one still not listed
                        // after MemberGraceSeconds, is refused. Before, every such request was closed at once and the
                        // handshake then depended on Steam raising a new request for the guest's next packet.
                        double waited = Now - entry.Since;
                        if (CouldBecomePeer(request) && waited < MemberGraceSeconds)
                        {
                            if (!entry.Held)
                            {
                                entry.Held = true;
                                requestsHeld++;
                                Log("P2P session request from " + request + " held: not (yet) a member of lobby " + lobbyId +
                                    " (" + steam.GetNumLobbyMembers(lobbyId) + " member(s)); accepted as soon as Steam lists it, refused after " +
                                    MemberGraceSeconds + " s");
                            }
                            i++;
                            continue;
                        }
                        requestsRefused++;
                        if (rejectsLogged++ < 5)
                            Log("P2P session request from " + request + " refused: not a member of lobby " + lobbyId +
                                " (" + steam.GetNumLobbyMembers(lobbyId) + " member(s))" +
                                (entry.Held ? " after waiting " + waited.ToString("0.0", CultureInfo.InvariantCulture) + " s" : ""));
                        CloseSession(request);
                        sessionRequests.RemoveAt(i);
                        continue;
                    }
                    if (hosting && MutateNoAccept && SteamApi.Fake) { sessionRequests.RemoveAt(i); continue; }
                    if (!steam.AcceptP2PSession(request))
                    {
                        string failure = "Steam could not accept the peer session with " + request;
                        if (!hosting) { Fail(failure); return; }
                        Log(failure);
                        CloseSession(request);
                        sessionRequests.RemoveAt(i);
                        continue;
                    }
                    requestsAccepted++;
                    Log("P2P session accepted with " + FriendName(request) + " (" + request + ")" +
                        (entry.Held ? " after " + (Now - entry.Since).ToString("0.0", CultureInfo.InvariantCulture) + " s waiting for the lobby member list" : ""));
                    if (hosting)
                    {
                        if (FindGuest(request) == null) guests.Add(new GuestRoute(request, Now));
                    }
                    else if (peerStarted < 0) peerStarted = Now;
                    sessionRequests.RemoveAt(i);
                }
                if (!hosting && !receivedPeer && peerStarted >= 0 && Now - peerStarted >= TimeoutSeconds)
                    Fail("Timed out connecting to the Steam peer");
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
            GuestRoute route = to as GuestRoute;
            ulong target = hosting ? (route != null && guests.Contains(route) ? route.Id : 0) : remoteId;
            if (!ready || target == 0 || length == 0)
            {
                if (skippedLogged++ < 3) Log("send skipped (" + length + " bytes): ready " + ready + ", remote " + target);
                return;
            }
            if (!hosting && !VirtualRemote.Equals(to)) throw new ArgumentException("Unknown Steam endpoint", "to");
            try
            {
                if (!hosting && peerStarted < 0) peerStarted = Now;
                bool sent = steam.SendP2PPacket(target, data, offset, length, Channel);
                packetsSent++;
                if (!sent) sendsRefused++;
                if (!sentLogged) { sentLogged = true; sendRefusedLogged = !sent; Log("first packet sent to " + target + " on channel " + Channel + " (" + length + " bytes, accepted by Steam: " + sent + ")"); }
                else if (!sent && !sendRefusedLogged) { sendRefusedLogged = true; Log("Steam refused a packet to " + target + " on channel " + Channel + " (" + length + " bytes; " + sendsRefused + " of " + packetsSent + " sends refused)"); }
            }
            catch (Exception e)
            {
                if (hosting) DropGuest(route, "Steam packet send failed: " + e.Message);
                else Fail("Steam packet send failed: " + e.Message);
            }
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
                        packetsDropped++;
                        if (rejectsLogged++ < 5) Log("packet from " + sender + " dropped: not a member of lobby " + lobbyId);
                        CloseSession(sender);
                        continue;
                    }
                    if (size == 0 || size > MaxDatagram || size > buffer.Length) continue;
                    GuestRoute route = hosting ? FindGuest(sender) : null;
                    if (hosting && route == null) continue;
                    packetsReceived++;
                    if (hosting ? !route.Received : !receivedPeer)
                        Log("first packet received from " + FriendName(sender) + " (" + sender + ") on channel " + Channel + " (" + size + " bytes)");
                    if (hosting) route.Received = true; else receivedPeer = true;
                    Buffer.BlockCopy(scratch, 0, buffer, 0, (int)size);
                    from = hosting ? route : VirtualRemote;
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
            if (hosting) { foreach (GuestRoute guest in guests) CloseSession(guest.Id); guests.Clear(); }
            else CloseSession(remoteId);
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
