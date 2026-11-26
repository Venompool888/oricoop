using System;

namespace OriCoop.Net
{
    // T-217: everything SteamTransport needs from Steam, with plain argument types (no ManagedSteam types), so a
    // test-only fake can stand in. One object = one Steam interface instance of the game; when Ori restarts Steam,
    // SteamApi.Current returns a different object (SteamTransport compares references to notice that).
    //
    // The real implementation (RealSteamApi) forwards ONE-TO-ONE to the game's wrapper (ManagedSteam.Steam, reached
    // through global::Steamworks.SteamInterface): same call, same arguments, same order as SteamTransport used before
    // T-217. It cannot be covered by an automated test (one PC = one Steam account), so it must stay trivially
    // comparable with the old code. Exceptions from the wrapper are NOT caught in the real implementation unless the
    // member says so: SteamTransport keeps its own try/catch blocks exactly where they were.
    public interface ISteamApi
    {
        // ---- account
        // User.IsLoggedOn(); false when the call throws (old LoggedOn(Steam) helper).
        bool LoggedOn { get; }
        // User.GetSteamID().AsUInt64
        ulong LocalId { get; }
        // Friends.GetPersonaName()
        string PersonaName { get; }
        // Friends.GetFriendPersonaName(new SteamID(id))
        string FriendName(ulong id);

        // ---- lobby
        // Matchmaking.CreateLobby(LobbyType.FriendsOnly, maxMembers). Returns at once; the result comes as LobbyCreated.
        void CreateLobby(int maxMembers);
        // Matchmaking.JoinLobby(new SteamID(lobby)). Returns at once; the result comes as LobbyEntered.
        void JoinLobby(ulong lobby);
        // Matchmaking.LeaveLobby(new SteamID(lobby))
        void LeaveLobby(ulong lobby);
        bool SetLobbyData(ulong lobby, string key, string value);
        string GetLobbyData(ulong lobby, string key);
        ulong GetLobbyOwner(ulong lobby);
        int GetNumLobbyMembers(ulong lobby);
        ulong GetLobbyMemberByIndex(ulong lobby, int index);
        // Utils.IsOverlayEnabled(); true when the call throws.
        bool OverlayEnabled { get; }
        // Friends.ActivateGameOverlayInviteDialog(new SteamID(lobby))
        void OpenInviteDialog(ulong lobby);

        // ---- P2P (legacy ISteamNetworking; this game's Steam API has nothing newer)
        bool AllowP2PPacketRelay(bool allow);
        bool AcceptP2PSession(ulong user);
        bool CloseP2PSession(ulong user);
        // Networking.SendP2PPacket(user, pinned data + offset, length, P2PSend.Unreliable, channel). The real
        // implementation pins `data` for the call only (GCHandle), as SteamTransport.SendTo did.
        bool SendP2PPacket(ulong user, byte[] data, int offset, int length, int channel);
        bool IsP2PPacketAvailable(out uint size, int channel);
        bool ReadP2PPacket(byte[] buffer, out uint size, out ulong sender, int channel);

        // ---- events (raised on the main thread; the real ones from Ori's own Steamworks.Update)
        // GameLobbyJoinRequested: the player accepted an invite / pressed Join Game. (lobby, friend)
        event Action<ulong, ulong> JoinRequested;
        // LobbyCreatedResult. (ok = !ioFailure && Result == OK && lobby != 0, result text = Result.ToString(), lobby, ioFailure)
        event Action<bool, string, ulong, bool> LobbyCreated;
        // LobbyEnterResult. (lobby, ok = ChatRoomEnterResponse == Success, response text = ChatRoomEnterResponse.ToString(), ioFailure)
        event Action<ulong, bool, string, bool> LobbyEntered;
        // LobbyChatUpdate. (lobby, user that changed, change text = ChatMemberStateChange.ToString())
        event Action<ulong, ulong, string> LobbyMemberChanged;
        // P2PSessionRequest. (remote user)
        event Action<ulong> P2PSessionRequested;
        // P2PSessionConnectFail. (remote user, error text = SessionError.ToString())
        event Action<ulong, string> P2PSessionFailed;
        // Steam.ExceptionThrown: the wrapper swallowed an exception (callback struct size mismatch etc.).
        event Action<Exception> WrapperError;

        // Lets a fake deliver its queued events and packets. The real implementation does nothing (Ori pumps Steam).
        void Pump();
    }

    // Optional on transports used only for protocol tests. Real Steam and the loopback fake implement it.
    internal interface ISteamFriends
    {
        ulong[] FriendIds();
        ulong FriendLobby(ulong friend);
        void RequestDetails(ulong lobby);
        int MemberLimit(ulong lobby);
        bool Invite(ulong lobby, ulong friend);
        string MemberState(ulong lobby, ulong user);
        void SetMemberState(ulong lobby, string state);
    }

    internal sealed class SteamFriendRow
    {
        internal ulong User, Lobby;
        internal string Name, Reason;
    }

    internal static class SteamDirectory
    {
        internal static SteamFriendRow[] List(ISteamApi api, bool rooms, string version)
        {
            var rows = new System.Collections.Generic.List<SteamFriendRow>();
            var seen = new System.Collections.Generic.HashSet<ulong>();
            ISteamFriends friends = api as ISteamFriends;
            if (friends == null || !api.LoggedOn) return rows.ToArray();
            foreach (ulong user in friends.FriendIds())
            {
                if (user == 0 || user == api.LocalId) continue;
                ulong lobby = rooms ? friends.FriendLobby(user) : 0;
                string reason = "";
                if (rooms)
                {
                    if (lobby == 0 || !seen.Add(lobby)) continue;
                    friends.RequestDetails(lobby);
                    string tag = api.GetLobbyData(lobby, "oricoop");
                    string build = api.GetLobbyData(lobby, "build");
                    int used, limit;
                    if (string.IsNullOrEmpty(tag)) reason = "steam.details";
                    else if (tag != "1" || (!string.IsNullOrEmpty(build) && build != version)) reason = "server.err.version";
                    else if (int.TryParse(api.GetLobbyData(lobby, "capacity"), out limit) &&
                             int.TryParse(api.GetLobbyData(lobby, "occupied"), out used) && limit > 0 && used >= limit)
                        reason = "lobby.error.full";
                    else if (friends.MemberLimit(lobby) > 0 && api.GetNumLobbyMembers(lobby) >= friends.MemberLimit(lobby))
                        reason = "lobby.error.full";
                }
                rows.Add(new SteamFriendRow { User = user, Lobby = lobby, Name = api.FriendName(user), Reason = reason });
            }
            rows.Sort(delegate(SteamFriendRow a, SteamFriendRow b) {
                int cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : a.User.CompareTo(b.User);
            });
            return rows.ToArray();
        }
    }

    public static class SteamApi
    {
        // Read once: Current and Fake are asked several times per frame, and a kit never sets it.
        private static readonly string FakeDirectory = Environment.GetEnvironmentVariable("ORICOOP_FAKE_STEAM_DIR");
        private static ISteamApi testApi;
        private static FakeSteamApi fakeApi;

        internal static void SetForTests(ISteamApi api) { testApi = api; }

        // The Steam interface to use right now, or null when Ori has not started Steam.
        //  - ORICOOP_FAKE_STEAM_DIR set (tests only): the one FakeSteamApi of this process.
        //  - else: a RealSteamApi for global::Steamworks.SteamInterface. The SAME wrapper object is returned while the
        //    game's interface object is the same, a NEW one when the game replaced it, null when it is null.
        // Main thread only. Must not throw.
        public static ISteamApi Current
        {
            get
            {
                if (testApi != null) return testApi;
                try
                {
                    if (string.IsNullOrEmpty(FakeDirectory)) return RealSteamApi.GetCurrent();
                    if (fakeApi == null) fakeApi = FakeSteamApi.FromEnvironment(FakeDirectory);
                    return fakeApi;
                }
                catch (Exception) { return null; }
            }
        }

        // True when the fake is in use (logs and tests say so; a kit never sets the variable).
        public static bool Fake
        {
            get { return testApi != null || !string.IsNullOrEmpty(FakeDirectory); }
        }
    }
}
