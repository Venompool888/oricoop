using System;
using System.Runtime.InteropServices;
using ManagedSteam;
using ManagedSteam.CallbackStructures;
using ManagedSteam.SteamTypes;

namespace OriCoop.Net
{
    internal sealed class RealSteamApi : ISteamApi
    {
        private static RealSteamApi current;
        private readonly Steam steam;

        private RealSteamApi(Steam steam) { this.steam = steam; }

        internal static ISteamApi GetCurrent()
        {
            Steam value = global::Steamworks.SteamInterface;
            if (value == null) current = null;
            else if (current == null || !object.ReferenceEquals(current.steam, value)) current = new RealSteamApi(value);
            return current;
        }

        public bool LoggedOn
        {
            get { try { return steam.User.IsLoggedOn(); } catch (Exception) { return false; } }
        }

        public ulong LocalId { get { return steam.User.GetSteamID().AsUInt64; } }
        public string PersonaName { get { return steam.Friends.GetPersonaName(); } }
        public string FriendName(ulong id) { return steam.Friends.GetFriendPersonaName(new SteamID(id)); }
        public void CreateLobby(int maxMembers) { steam.Matchmaking.CreateLobby(LobbyType.FriendsOnly, maxMembers); }
        public void JoinLobby(ulong lobby) { steam.Matchmaking.JoinLobby(new SteamID(lobby)); }
        public void LeaveLobby(ulong lobby) { steam.Matchmaking.LeaveLobby(new SteamID(lobby)); }
        public bool SetLobbyData(ulong lobby, string key, string value) { return steam.Matchmaking.SetLobbyData(new SteamID(lobby), key, value); }
        public string GetLobbyData(ulong lobby, string key) { return steam.Matchmaking.GetLobbyData(new SteamID(lobby), key); }
        public ulong GetLobbyOwner(ulong lobby) { return steam.Matchmaking.GetLobbyOwner(new SteamID(lobby)).AsUInt64; }
        public int GetNumLobbyMembers(ulong lobby) { return steam.Matchmaking.GetNumLobbyMembers(new SteamID(lobby)); }
        public ulong GetLobbyMemberByIndex(ulong lobby, int index) { return steam.Matchmaking.GetLobbyMemberByIndex(new SteamID(lobby), index).AsUInt64; }
        public bool OverlayEnabled
        {
            get { try { return steam.Utils.IsOverlayEnabled(); } catch (Exception) { return true; } }
        }
        public void OpenInviteDialog(ulong lobby) { steam.Friends.ActivateGameOverlayInviteDialog(new SteamID(lobby)); }
        public bool AllowP2PPacketRelay(bool allow) { return steam.Networking.AllowP2PPacketRelay(allow); }
        public bool AcceptP2PSession(ulong user) { return steam.Networking.AcceptP2PSessionWithUser(new SteamID(user)); }
        public bool CloseP2PSession(ulong user) { return steam.Networking.CloseP2PSessionWithUser(new SteamID(user)); }

        public bool SendP2PPacket(ulong user, byte[] data, int offset, int length, int channel)
        {
            GCHandle pinned = default(GCHandle);
            try
            {
                pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
                // Unreliable queues briefly while Steam opens the session; UnreliableNoDelay drops those packets.
                return steam.Networking.SendP2PPacket(new SteamID(user), pinned.AddrOfPinnedObject(),
                    (uint)length, (uint)offset, P2PSend.Unreliable, channel);
            }
            finally { if (pinned.IsAllocated) pinned.Free(); }
        }

        public bool IsP2PPacketAvailable(out uint size, int channel) { return steam.Networking.IsP2PPacketAvailable(out size, channel); }

        public bool ReadP2PPacket(byte[] buffer, out uint size, out ulong sender, int channel)
        {
            SteamID user;
            bool read = steam.Networking.ReadP2PPacket(buffer, out size, out user, channel);
            sender = user.AsUInt64;
            return read;
        }

        // Hook each wrapper event only for the lifetime of its subscribers.
        private Action<ulong, ulong> joinRequested;
        public event Action<ulong, ulong> JoinRequested
        {
            add { if (joinRequested == null && value != null) steam.Friends.GameLobbyJoinRequested += OnInvite; joinRequested += value; }
            remove { if (joinRequested == null) return; joinRequested -= value; if (joinRequested == null) steam.Friends.GameLobbyJoinRequested -= OnInvite; }
        }
        private void OnInvite(GameLobbyJoinRequested value)
        {
            if (joinRequested != null) joinRequested(value.LobbyID.AsUInt64, value.FriendID.AsUInt64);
        }

        private Action<bool, string, ulong, bool> lobbyCreated;
        public event Action<bool, string, ulong, bool> LobbyCreated
        {
            add { if (lobbyCreated == null && value != null) steam.Matchmaking.LobbyCreatedResult += OnCreated; lobbyCreated += value; }
            remove { if (lobbyCreated == null) return; lobbyCreated -= value; if (lobbyCreated == null) steam.Matchmaking.LobbyCreatedResult -= OnCreated; }
        }
        private void OnCreated(LobbyCreated value, bool ioFailure)
        {
            ulong lobby = value.SteamIDLobby.AsUInt64;
            if (lobbyCreated != null) lobbyCreated(!ioFailure && value.Result == Result.OK && lobby != 0, value.Result.ToString(), lobby, ioFailure);
        }

        private Action<ulong, bool, string, bool> lobbyEntered;
        public event Action<ulong, bool, string, bool> LobbyEntered
        {
            add { if (lobbyEntered == null && value != null) steam.Matchmaking.LobbyEnterResult += OnEntered; lobbyEntered += value; }
            remove { if (lobbyEntered == null) return; lobbyEntered -= value; if (lobbyEntered == null) steam.Matchmaking.LobbyEnterResult -= OnEntered; }
        }
        private void OnEntered(LobbyEnter value, bool ioFailure)
        {
            if (lobbyEntered != null) lobbyEntered(value.SteamIDLobby.AsUInt64,
                value.ChatRoomEnterResponse == ChatRoomEnterResponse.Success, value.ChatRoomEnterResponse.ToString(), ioFailure);
        }

        private Action<ulong, ulong, string> lobbyMemberChanged;
        public event Action<ulong, ulong, string> LobbyMemberChanged
        {
            add { if (lobbyMemberChanged == null && value != null) steam.Matchmaking.LobbyChatUpdate += OnMemberChange; lobbyMemberChanged += value; }
            remove { if (lobbyMemberChanged == null) return; lobbyMemberChanged -= value; if (lobbyMemberChanged == null) steam.Matchmaking.LobbyChatUpdate -= OnMemberChange; }
        }
        private void OnMemberChange(LobbyChatUpdate value)
        {
            if (lobbyMemberChanged != null) lobbyMemberChanged(value.SteamIDLobby.AsUInt64, value.SteamIDUserChanged.AsUInt64, value.ChatMemberStateChange.ToString());
        }

        private Action<ulong> p2pSessionRequested;
        public event Action<ulong> P2PSessionRequested
        {
            add { if (p2pSessionRequested == null && value != null) steam.Networking.P2PSessionRequest += OnSessionRequest; p2pSessionRequested += value; }
            remove { if (p2pSessionRequested == null) return; p2pSessionRequested -= value; if (p2pSessionRequested == null) steam.Networking.P2PSessionRequest -= OnSessionRequest; }
        }
        private void OnSessionRequest(P2PSessionRequest value)
        {
            if (p2pSessionRequested != null) p2pSessionRequested(value.SteamIDRemote.AsUInt64);
        }

        private Action<ulong, string> p2pSessionFailed;
        public event Action<ulong, string> P2PSessionFailed
        {
            add { if (p2pSessionFailed == null && value != null) steam.Networking.P2PSessionConnectFail += OnSessionFailure; p2pSessionFailed += value; }
            remove { if (p2pSessionFailed == null) return; p2pSessionFailed -= value; if (p2pSessionFailed == null) steam.Networking.P2PSessionConnectFail -= OnSessionFailure; }
        }
        private void OnSessionFailure(P2PSessionConnectFail value)
        {
            if (p2pSessionFailed != null) p2pSessionFailed(value.SteamIDRemote.AsUInt64, value.SessionError.ToString());
        }

        private Action<Exception> wrapperError;
        public event Action<Exception> WrapperError
        {
            add { if (wrapperError == null && value != null) steam.ExceptionThrown += OnWrapperError; wrapperError += value; }
            remove { if (wrapperError == null) return; wrapperError -= value; if (wrapperError == null) steam.ExceptionThrown -= OnWrapperError; }
        }
        private void OnWrapperError(Exception value) { if (wrapperError != null) wrapperError(value); }

        public void Pump() { }
    }
}
