using System;
using System.Collections.Generic;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop
{
    internal static class SteamRoomMenu
    {
        static SteamFriendRow[] rooms = new SteamFriendRow[0], friends = new SteamFriendRow[0];
        static float nextRooms, nextFriends, nextPresence;
        static ulong publishedLobby;
        static string publishedStage = "", hostName = "";
        static bool hostStarted;
        static int publishedUsed, publishedLimit;
        static readonly List<string> members = new List<string>();
        internal static IList<string> Members { get { return members; } }
        internal static bool HasSpace { get; private set; }
        internal static bool HostStarted { get { return hostStarted || NetPlay.RemoteInGameplay; } }
        internal static string HostName { get { return hostName.Length > 0 ? hostName : L10n.T("steam.friend"); } }

        internal static string Status(bool zh)
        {
            ISteamApi api = SteamApi.Current;
            if (api == null) return L10n.T("steam.unavailable", zh);
            try { return api.LoggedOn ? string.Format(L10n.T("steam.account", zh), api.PersonaName) : L10n.T("steam.offline", zh); }
            catch (Exception) { return L10n.T("steam.offline", zh); }
        }

        internal static SteamFriendRow[] List(bool openRooms)
        {
            float now = Time.realtimeSinceStartup;
            if (now < (openRooms ? nextRooms : nextFriends)) return openRooms ? rooms : friends;
            if (openRooms) nextRooms = now + 1.5f; else nextFriends = now + 1.5f;
            SteamFriendRow[] rows;
            try { rows = SteamDirectory.List(SteamApi.Current, openRooms, NetPlay.ModVersion); }
            catch (Exception e) { Plugin.Log.LogWarning("steam: could not refresh friends: " + e.Message); rows = new SteamFriendRow[0]; }
            if (openRooms) rooms = rows; else friends = rows;
            return rows;
        }

        internal static bool Invite(ulong user)
        {
            ISteamFriends api = SteamApi.Current as ISteamFriends;
            try { return api != null && NetPlay.IsHost && NetPlay.SteamReady && api.Invite(NetPlay.ActiveSteamLobby, user); }
            catch (Exception e) { Plugin.Log.LogWarning("steam: direct invite failed: " + e.Message); return false; }
        }

        internal static void Reset()
        {
            nextPresence = 0; publishedLobby = 0; publishedStage = ""; hostName = "";
            publishedUsed = publishedLimit = -1; HasSpace = hostStarted = false; members.Clear();
        }

        internal static void Tick()
        {
            if (!NetPlay.Enabled || !NetPlay.UsesSteam || !NetPlay.SteamReady) return;
            if (Time.realtimeSinceStartup < nextPresence) return;
            nextPresence = Time.realtimeSinceStartup + 0.75f;
            ISteamApi api = SteamApi.Current;
            ISteamFriends directory = api as ISteamFriends;
            ulong lobby = NetPlay.ActiveSteamLobby;
            if (directory == null || lobby == 0) return;
            if (publishedLobby != lobby) { publishedStage = ""; publishedUsed = publishedLimit = -1; hostStarted = false; publishedLobby = lobby; }
            ulong owner = api.GetLobbyOwner(lobby);
            hostName = owner == 0 ? "" : api.FriendName(owner) ?? "";
            int count = api.GetNumLobbyMembers(lobby), limit = directory.MemberLimit(lobby);
            int used = count;
            if (NetPlay.IsHost)
            {
                int seats = 0;
                for (int seat = 1; seat <= Players.MaxSeats; seat++) if (NetPlay.SeatOwner(seat) != 0) seats++;
                used = Math.Max(used, seats);
                if (publishedUsed != used || publishedLimit != limit)
                {
                    if (api.SetLobbyData(lobby, "build", NetPlay.ModVersion) && api.SetLobbyData(lobby, "capacity", limit.ToString()) &&
                        api.SetLobbyData(lobby, "occupied", used.ToString())) { publishedUsed = used; publishedLimit = limit; }
                }
            }
            else int.TryParse(api.GetLobbyData(lobby, "occupied"), out used);
            HasSpace = Math.Max(count, used) < limit;
            string stage = NetPlay.LocalInGameplay() && !SessionScreens.Blocking ? "game" :
                OnlineRoomScreen.Confirmed ? "loading" : NetPlay.PartnerHere || NetPlay.IsHost ? "waiting" : "connecting";
            if (stage != publishedStage) { directory.SetMemberState(lobby, stage); publishedStage = stage; }
            members.Clear();
            for (int i = 0; i < count; i++)
            {
                ulong user = api.GetLobbyMemberByIndex(lobby, i);
                if (user == 0) continue;
                string state = user == api.LocalId ? stage : directory.MemberState(lobby, user);
                if (user == owner && (state == "game" || state == "loading")) hostStarted = true;
                string status = state == "game" ? "steam.in.game" : state == "loading" ? "steam.loading" :
                    state == "waiting" ? "lobby.connected" : "lobby.connecting";
                members.Add((user == api.LocalId ? api.PersonaName : api.FriendName(user)) + " · " + L10n.T(status));
            }
            // Older kits never publish room progress. Their existing save handshake still waits for the host in game.
            if (!NetPlay.IsHost && NetPlay.PartnerHere && string.IsNullOrEmpty(api.GetLobbyData(lobby, "build"))) hostStarted = true;
        }
    }
}
