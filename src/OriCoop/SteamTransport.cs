using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using ManagedSteam;
using ManagedSteam.CallbackStructures;
using ManagedSteam.SteamTypes;

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
        private const double TimeoutSeconds = 30.0;
        private static readonly EndPoint VirtualRemote = new IPEndPoint(IPAddress.Parse("10.255.254.1"), 1);
        private static Steam hookedSteam;
        private static SteamTransport active;
        // CreateLobby/JoinLobby return void and their result events have no call handle.
        // Keep a retired operation until its result arrives, then leave any late lobby.
        private static SteamTransport pendingOperation;
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
        private readonly List<SteamID> sessionRequests = new List<SteamID>();
        private int rejectsLogged;
        private bool sentLogged;
        private int skippedLogged;
        private Steam steam;
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
                try { return steam.Friends.GetFriendPersonaName(new SteamID(remoteId)) ?? string.Empty; }
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
                try { EnsureInviteHook(global::Steamworks.SteamInterface); }
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
                Steam s = global::Steamworks.SteamInterface;
                return s == null ? string.Empty : (s.Friends.GetFriendPersonaName(new SteamID(id)) ?? string.Empty);
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
                    Steam s = global::Steamworks.SteamInterface;
                    return s == null ? 0 : s.User.GetSteamID().AsUInt64;
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
                    Steam s = global::Steamworks.SteamInterface;
                    return s == null ? string.Empty : (s.Friends.GetPersonaName() ?? string.Empty);
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
                transport.steam = global::Steamworks.SteamInterface;
                if (transport.steam == null || !LoggedOn(transport.steam))
                {
                    transport.Fail(Unavailable + (transport.steam == null ? " (Ori has not started Steam yet)" : NotConnected));
                    return transport;
                }
                EnsureHooks(transport.steam);
                if (active != null) transport.Fail("Dispose the active Steam transport first");
                else if (pendingOperation != null) transport.Fail("A Steam lobby result is still pending; wait for it before retrying");
                else if (!hosting && lobby == 0) transport.Fail("Invalid Steam lobby ID");
                if (transport.error != null) return transport;
                transport.localId = transport.steam.User.GetSteamID().AsUInt64;
                if (transport.localId == 0) transport.Fail(Unavailable);
                else if (!transport.steam.Networking.AllowP2PPacketRelay(true))
                    transport.Fail("Steam could not enable P2P packet relay");
                if (transport.error != null) return transport;
                active = transport;
                pendingOperation = transport;
                transport.operationStarted = transport.Now;
                Log((hosting ? "CreateLobby (friends-only, 2 players)" : "JoinLobby " + lobby) + " as " + transport.localId);
                if (hosting) transport.steam.Matchmaking.CreateLobby(LobbyType.FriendsOnly, 2);
                else transport.steam.Matchmaking.JoinLobby(new SteamID(lobby));
            }
            catch (Exception e)
            {
                if (pendingOperation == transport) pendingOperation = null;
                transport.Fail("Steam lobby operation failed: " + e.Message);
            }
            return transport;
        }

        // Live check: false while the Steam client is disconnected from Valve's servers, e.g. when the same
        // account is playing on another PC ("Logged In Elsewhere"); lobbies then fail with NoConnection.
        private static bool LoggedOn(Steam s)
        {
            if (s == null) return false;
            try { return s.User.IsLoggedOn(); } catch (Exception) { return false; }
        }

        private static Steam inviteHookedSteam;

        private static void EnsureInviteHook(Steam value)
        {
            if (object.ReferenceEquals(inviteHookedSteam, value)) return;
            if (inviteHookedSteam != null)
            {
                try { inviteHookedSteam.Friends.GameLobbyJoinRequested -= OnInvite; } catch (Exception) { }
            }
            inviteHookedSteam = value;
            if (value == null) return;
            value.Friends.GameLobbyJoinRequested += OnInvite;
            Log("invite callback hooked");
        }

        private static void EnsureHooks(Steam value)
        {
            if (object.ReferenceEquals(hookedSteam, value)) return;
            if (active != null) active.Fail(Unavailable + " (Steam interface changed)");
            pendingOperation = null;
            if (hookedSteam != null)
            {
                try
                {
                    hookedSteam.Matchmaking.LobbyCreatedResult -= OnCreated;
                    hookedSteam.Matchmaking.LobbyEnterResult -= OnEntered;
                    hookedSteam.Matchmaking.LobbyChatUpdate -= OnMemberChange;
                    hookedSteam.Networking.P2PSessionRequest -= OnSessionRequest;
                    hookedSteam.Networking.P2PSessionConnectFail -= OnSessionFailure;
                    hookedSteam.ExceptionThrown -= OnWrapperError;
                }
                catch (Exception) { } // the old interface may already be shut down
            }
            hookedSteam = value;
            if (value == null) return;
            // The wrapper drops a callback when nothing is subscribed at that moment (SteamService.InvokeEvents);
            // the invite callback is registered from the title screen on (EnsureInviteHook), the rest from here.
            EnsureInviteHook(value);
            value.Matchmaking.LobbyCreatedResult += OnCreated;
            value.Matchmaking.LobbyEnterResult += OnEntered;
            value.Matchmaking.LobbyChatUpdate += OnMemberChange;
            value.Networking.P2PSessionRequest += OnSessionRequest;
            value.Networking.P2PSessionConnectFail += OnSessionFailure;
            // Ori's own handler is empty, so callback struct size mismatches etc. were invisible before.
            value.ExceptionThrown += OnWrapperError;
            ulong me = 0;
            try { me = value.User.GetSteamID().AsUInt64; } catch (Exception) { }
            Log("callbacks hooked (invites, lobby, P2P); this account " + me + ", logged on " + LoggedOn(value));
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

        private static void OnInvite(GameLobbyJoinRequested value)
        {
            ReadLaunchInvite();
            pendingInvite = value.LobbyID.AsUInt64;
            pendingInviteFrom = value.FriendID.AsUInt64;
            pendingInviteAt = inviteClock.Elapsed.TotalSeconds;
            Log("invite accepted in Steam: lobby " + pendingInvite + " from " + FriendName(pendingInviteFrom) + " (" + pendingInviteFrom + ")" +
                (active != null ? "; a Steam transport is already active (" + (active.hosting ? "host" : "guest") + ")" : ""));
            // An invite does not automatically interrupt an existing NetPeer session.
        }

        private static void OnMemberChange(LobbyChatUpdate value)
        {
            SteamTransport transport = active;
            if (transport == null || transport.lobbyId == 0 || value.SteamIDLobby.AsUInt64 != transport.lobbyId) return;
            ulong who = value.SteamIDUserChanged.AsUInt64;
            Log("lobby member " + FriendName(who) + " (" + who + ") " + value.ChatMemberStateChange);
        }

        private static void OnCreated(LobbyCreated value, bool ioFailure)
        {
            SteamTransport transport = pendingOperation;
            if (transport == null || !transport.hosting) return;
            pendingOperation = null;
            try
            {
                if (ioFailure || value.Result != ManagedSteam.SteamTypes.Result.OK || value.SteamIDLobby.AsUInt64 == 0)
                {
                    transport.Fail("Steam lobby creation failed: " + value.Result);
                    return;
                }
                transport.lobbyId = value.SteamIDLobby.AsUInt64;
                if (transport.disposed || transport.error != null) { transport.Cleanup(); return; }
                if (!transport.steam.Matchmaking.SetLobbyData(value.SteamIDLobby, "oricoop", ProtocolTag))
                {
                    transport.Fail("Steam could not set the OriCoop lobby protocol");
                    return;
                }
                transport.ready = true;
                Log("lobby created " + transport.lobbyId + " (owner " + transport.localId + ")");
            }
            catch (Exception e) { transport.Fail("Steam lobby creation failed: " + e.Message); }
        }

        private static void OnEntered(LobbyEnter value, bool ioFailure)
        {
            SteamTransport transport = pendingOperation;
            // Steam also emits LobbyEnter while creating a host lobby; ignore that result.
            if (transport == null || transport.hosting) return;
            ulong entered = value.SteamIDLobby.AsUInt64;
            if (entered != transport.joinTarget && !(ioFailure && entered == 0)) return;
            pendingOperation = null;
            try
            {
                Log("LobbyEnter result for " + entered + ": " + value.ChatRoomEnterResponse + (ioFailure ? " (I/O failure)" : ""));
                if (ioFailure || value.ChatRoomEnterResponse != ChatRoomEnterResponse.Success)
                {
                    transport.Fail("Steam lobby join failed: " + value.ChatRoomEnterResponse);
                    return;
                }
                transport.lobbyId = entered;
                if (transport.disposed || transport.error != null) { transport.Cleanup(); return; }
                string tag = transport.steam.Matchmaking.GetLobbyData(value.SteamIDLobby, "oricoop");
                if (tag != ProtocolTag)
                {
                    transport.Fail("This lobby is not compatible with OriCoop protocol " + ProtocolTag + " (lobby says '" + tag + "')");
                    return;
                }
                transport.remoteId = transport.steam.Matchmaking.GetLobbyOwner(value.SteamIDLobby).AsUInt64;
                if (!transport.IsAllowedPeer(transport.remoteId))
                {
                    transport.Fail("The Steam lobby host is unavailable (owner " + transport.remoteId + ", " +
                        transport.steam.Matchmaking.GetNumLobbyMembers(value.SteamIDLobby) + " member(s))");
                    return;
                }
                transport.ready = true;
                Log("in lobby " + entered + "; host " + transport.RemoteName + " (" + transport.remoteId + "), " +
                    transport.steam.Matchmaking.GetNumLobbyMembers(value.SteamIDLobby) + " member(s)");
                if (pendingInvite == entered) pendingInvite = 0;
            }
            catch (Exception e) { transport.Fail("Steam lobby join failed: " + e.Message); }
        }

        private static void OnSessionRequest(P2PSessionRequest value)
        {
            SteamTransport transport = active;
            if (transport == null) { Log("P2P session request from " + value.SteamIDRemote.AsUInt64 + " ignored: no active Steam transport"); return; }
            for (int i = 0; i < transport.sessionRequests.Count; i++)
                if (transport.sessionRequests[i].AsUInt64 == value.SteamIDRemote.AsUInt64) return;
            Log("P2P session request from " + FriendName(value.SteamIDRemote.AsUInt64) + " (" + value.SteamIDRemote.AsUInt64 + ")");
            // Bound work/memory even if unrelated Steam users send requests.
            if (transport.sessionRequests.Count < 16) transport.sessionRequests.Add(value.SteamIDRemote);
        }

        private static void OnSessionFailure(P2PSessionConnectFail value)
        {
            SteamTransport transport = active;
            ulong who = value.SteamIDRemote.AsUInt64;
            Log("P2P session with " + who + " failed: " + value.SessionError);
            if (transport == null || who == 0 || (who != transport.remoteId && who != transport.sessionId)) return;
            // A host keeps its lobby (and the friend's invite stays valid); only the guest gives up.
            if (transport.hosting) transport.DropPeer("P2P session failed: " + value.SessionError);
            else transport.Fail("Steam P2P session failed: " + value.SessionError);
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
            SteamID lobby = new SteamID(lobbyId);
            int count = steam.Matchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
                if (steam.Matchmaking.GetLobbyMemberByIndex(lobby, i).AsUInt64 == id) return true;
            return false;
        }

        public void Update()
        {
            if (disposed || error != null || updating) return;
            updating = true;
            try
            {
                if (!object.ReferenceEquals(steam, global::Steamworks.SteamInterface) || !LoggedOn(steam))
                {
                    Fail(Unavailable + (!object.ReferenceEquals(steam, global::Steamworks.SteamInterface)
                        ? (global::Steamworks.SteamInterface == null ? " (Ori shut Steam down)" : " (Ori restarted Steam)")
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
                    (!hosting && steam.Matchmaking.GetLobbyOwner(new SteamID(lobbyId)).AsUInt64 != remoteId)))
                {
                    Fail("The Steam peer left the lobby or its owner changed");
                    return;
                }
                for (int i = 0; i < sessionRequests.Count; i++)
                {
                    SteamID request = sessionRequests[i];
                    if (!IsAllowedPeer(request.AsUInt64))
                    {
                        // Usually a stranger. It can also be the guest's first packet racing the lobby member
                        // list; the guest keeps sending, and Steam raises a new request after the close.
                        if (rejectsLogged++ < 5)
                            Log("P2P session request from " + request.AsUInt64 + " rejected: not a member of lobby " + lobbyId +
                                " (" + steam.Matchmaking.GetNumLobbyMembers(new SteamID(lobbyId)) + " member(s))");
                        CloseSession(request.AsUInt64);
                        continue;
                    }
                    if (!steam.Networking.AcceptP2PSessionWithUser(request))
                    {
                        Fail("Steam could not accept the peer session");
                        return;
                    }
                    Log("P2P session accepted with " + FriendName(request.AsUInt64) + " (" + request.AsUInt64 + ")");
                    sessionId = request.AsUInt64;
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

        public void OpenInviteOverlay()
        {
            Update();
            if (!ready) return;
            try { steam.Friends.ActivateGameOverlayInviteDialog(new SteamID(lobbyId)); }
            catch (Exception e) { Fail("Steam invite overlay failed: " + e.Message); }
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
            GCHandle pinned = default(GCHandle);
            try
            {
                sessionId = remoteId;
                if (peerStarted < 0) peerStarted = Now;
                // This wrapper version only exposes IntPtr sends. Pin for this call only.
                pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
                // Unreliable, not UnreliableNoDelay: NoDelay throws a packet away while the P2P connection is
                // not up yet (Steamworks docs), and every packet of NetPeer's handshake is sent in exactly that
                // window. Unreliable lets Steam open the session and queue briefly. False = datagram loss; NetPeer retries.
                bool sent = steam.Networking.SendP2PPacket(new SteamID(remoteId), pinned.AddrOfPinnedObject(),
                    (uint)length, (uint)offset, P2PSend.Unreliable, Channel);
                if (!sentLogged) { sentLogged = true; Log("first packet sent to " + remoteId + " (" + length + " bytes, accepted by Steam: " + sent + ")"); }
            }
            catch (Exception e) { Fail("Steam packet send failed: " + e.Message); }
            finally { if (pinned.IsAllocated) pinned.Free(); }
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
                    if (!steam.Networking.IsP2PPacketAvailable(out available, Channel)) return 0;
                    if (available > MaxDiscardSize) { Fail("Steam packet exceeds the receive safety limit"); return 0; }
                    // Consume oversized packets completely so they cannot block the queue.
                    byte[] scratch = available <= MaxDatagram ? receiveScratch : new byte[(int)available];
                    uint size;
                    SteamID sender;
                    if (!steam.Networking.ReadP2PPacket(scratch, out size, out sender, Channel)) return 0;
                    if (!IsAllowedPeer(sender.AsUInt64))
                    {
                        if (rejectsLogged++ < 5) Log("packet from " + sender.AsUInt64 + " dropped: not a member of lobby " + lobbyId);
                        CloseSession(sender.AsUInt64);
                        continue;
                    }
                    if (size == 0 || size > MaxDatagram || size > buffer.Length) continue;
                    remoteId = sender.AsUInt64;
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
            try { steam.Networking.CloseP2PSessionWithUser(new SteamID(id)); }
            catch (Exception) { } // Steam may already have shut down.
        }

        private void Cleanup()
        {
            CloseSession(sessionId != 0 ? sessionId : remoteId);
            sessionId = 0;
            sessionRequests.Clear();
            if (steam == null || lobbyId == 0 || leftLobby) return;
            Log("leaving lobby " + lobbyId + (error != null ? " (" + error + ")" : disposed ? " (closed)" : ""));
            try { steam.Matchmaking.LeaveLobby(new SteamID(lobbyId)); }
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
