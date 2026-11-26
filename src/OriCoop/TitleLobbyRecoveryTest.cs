using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using OriCoop.Authority;
using OriCoop.Net;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    public static partial class NativeModeMenu
    {
        partial class TitleJoinTest
        {
            // Opt-in title test, launched with the test Steam backend. Both menu paths use the real orb and buttons.
            IEnumerator LobbyRecovery()
            {
                if (m_role == "join") { yield return StartCoroutine(InviteRecovery()); yield break; }
                string original = Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER");
                var factory = NetPlay.TransportFactory;
                try
                {
                    Environment.SetEnvironmentVariable("ORICOOP_NET_CSERVER", "127.0.0.1:1");
                    s_transport = "cserver";
                    string address = CServerConfig.Address;
                    var room = new CsRoomListEntry { Code = "888888", Online = 1 };
                    int generation = s_csRooms.BeginFetch(address, Time.realtimeSinceStartup, 300);
                    Check(generation >= 0 && s_csRooms.Complete(generation, new List<CsRoomListEntry> { room }), "recovery: synthetic server returned one room");
                    GoPage(PageRole, 0);
                    LobbyPressed(0);   // Create a room and feed it the same rejection the C-server handles.
                    NetPlay.HoldRetry("server.err.version");
                    RefreshTexts(true);
                    Check(LastPage == "hosting" && HasKey(LastLabels, "common.retry"), "T211 host creation refusal offers retry");
                    bool create = false;
                    string destination = null;
                    NetPlay.TransportFactory = delegate(string transport, bool host, string target)
                    { create = transport == "cserver" && host; destination = target; return null; };
                    int retry = LastLabels.FindIndex(label => HasKey(new List<string> { label }, "common.retry"));
                    if (retry >= 0) LobbyPressed(retry);
                    yield return StartCoroutine(WaitFor(() => destination != null, 1f));
                    Check(!NetPlay.RetryHeld && create && destination == address + "|",
                        "T211 host retry resubmits creation to the same server (create=" + create + ", target=" + destination + ")");
                    NetPlay.End("recovery fixture: next menu action");
                    s_entryKind = 4;   // Last editor was a server port, independent of the room that is selected now.
                    GoPage(PageRole, 0);
                    yield return null;
                    LobbyPressed(2);
                    Check(LastPage == "joining" && s_roomEntry == RoomEntryKind.ListJoin && s_joinTarget == address + "|" + room.Code,
                        "recovery: selected the room from its rendered list row");
                    NetPlay.HoldRetry("server.err.noroom");
                    LobbyBack();
                    Check(LastPage == "role" && CsRoomListPage && !NetPlay.Enabled, "T211 Back from a refused listed room returns directly to its room list");
                }
                finally
                {
                    NetPlay.End("recovery fixture done");
                    NetPlay.TransportFactory = factory;
                    Environment.SetEnvironmentVariable("ORICOOP_NET_CSERVER", original);
                }
            }

            IEnumerator InviteRecovery()
            {
                Check(SteamApi.Current is FakeSteamApi, "recovery: isolated Steam backend");
                if (!(SteamApi.Current is FakeSteamApi)) yield break;
                yield return StartCoroutine(RecoverInvite(88888001));
                if (m_fails != 0) yield break;
                yield return StartCoroutine(Row(TitleStartRow("steam", false), 0.3f));
                AccessTools.Field(typeof(SteamInviteHook), "s_startedAt").SetValue(null, Time.realtimeSinceStartup - 61f);
                SteamInviteHook.Tick();
                Check(OnSteamJoinPage && SteamBrowsing && NetPlay.Enabled && !SteamInviteHook.CanRetry,
                    "recovery: the idle Steam friend browser waits without timing out");
                yield return StartCoroutine(RecoverInvite(88888002));
            }

            IEnumerator RecoverInvite(ulong lobby)
            {
                AccessTools.Method(typeof(SteamTransport), "OnInvite").Invoke(null, new object[] { lobby, (ulong)1001 });
                SteamInviteHook.Tick();
                yield return StartCoroutine(WaitFor(() => NetPlay.SteamLobby == lobby && SteamTransport.PendingInviteRaw == 0, 4f));
                Check(OnSteamJoinPage && NetPlay.SteamLobby == lobby && SteamTransport.PendingInviteRaw == 0,
                    "recovery: title consumed the invite and attempted its specific lobby");
                AccessTools.Field(typeof(SteamInviteHook), "s_startedAt").SetValue(null, Time.realtimeSinceStartup - 61f);
                SteamInviteHook.Tick();
                AccessTools.Field(typeof(SteamInviteHook), "s_failUntil").SetValue(null, Time.realtimeSinceStartup - 1f);
                RefreshTexts(true);
                Check(!NetPlay.Enabled && SteamInviteHook.CanRetry && LastPage == "joining"
                    && HasKey(LastLabels, "common.retry") && HasKey(LastLabels, "common.back"),
                    "T211 the timed-out Steam invite offers retry and Back after its banner expires");
                LobbyPressed(0);
                yield return StartCoroutine(WaitFor(() => NetPlay.SteamLobby == lobby, 2f));
                Check(NetPlay.Enabled && !SteamInviteHook.CanRetry && NetPlay.SteamLobby == lobby,
                    "T211 retry rejoins the lobby of the consumed Steam invite");
                AccessTools.Field(typeof(SteamInviteHook), "s_startedAt").SetValue(null, Time.realtimeSinceStartup - 61f);
                SteamInviteHook.Tick();
                RefreshTexts(true);
                Check(SteamInviteHook.CanRetry && HasKey(LastLabels, "common.back"), "recovery: Back is available after the retry times out");
                LobbyPressed(1);
                Check(!NetPlay.Enabled && !SteamInviteHook.CanRetry && LastPage == "joinmethod",
                    "T211 Back from a Steam invite returns to title join methods");
            }
        }
    }
}
