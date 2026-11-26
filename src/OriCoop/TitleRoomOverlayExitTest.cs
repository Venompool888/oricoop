using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop
{
    public static partial class NativeModeMenu
    {
        partial class TitleJoinTest
        {
            // Observe the text that the legacy IMGUI lobby actually paints, including its address field and caret.
            [HarmonyPatch(typeof(OnlineLobbyOverlay), "DrawShadowedText")]
            static class OverlayPaint
            {
                public static string Wanted;
                public static bool Seen;
                static bool Prepare() { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_ROOM_EXIT") == "1"; }
                static void Postfix(string text)
                {
                    if (Wanted != null && Event.current.type == EventType.Repaint && (text == Wanted || text == Wanted + "|")) Seen = true;
                }
                public static void Expect(string text) { Wanted = text; Seen = false; }
            }

            IEnumerator OverlayRoomExit()
            {
                bool host = m_role == "host", steam = m_tr == "steam";
                if (steam && !(SteamApi.Current is FakeSteamApi)) { Check(false, "overlay needs the local Steam test backend"); yield break; }
                string target = "127.0.0.1:" + NetPlay.ConfigPort;
                GoPage(1, steam ? 2 : 1);
                HideUnderOverlay();
                OverlayPaint.Expect(L10n.T("lobby.host"));
                OnlineLobby.Open(m_tr);
                OnlineLobbyOverlay overlay = UnityEngine.Object.FindObjectOfType<OnlineLobbyOverlay>();
                try
                {
                    yield return StartCoroutine(WaitFor(() => OverlayPaint.Seen, 8f));
                    Check(OverlayPaint.Seen && !s_mgr.IsVisible, "overlay renders its own role chooser over the hidden native menu");
                    if (host)
                    {
                        yield return StartCoroutine(WaitFor(() => File.Exists(SteamWaitingFile), 40f));
                        AccessTools.Method(typeof(OnlineLobbyOverlay), "ActivateRole").Invoke(overlay, new object[] { 0 });
                        yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Visible, 20f));
                        if (steam) yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Click("room.invite"), 8f));
                        yield return StartCoroutine(WaitFor(() => File.Exists(GuestDoneFile), 80f));
                        OverlayPaint.Expect(L10n.T("lobby.host"));
                        yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Click("room.leave"), 5f));
                        yield return StartCoroutine(WaitFor(() => OverlayPaint.Seen, 5f));
                        Check(File.Exists(GuestDoneFile) && OverlayPaint.Seen && OnlineLobby.IsOpen && !NetPlay.Enabled,
                            "T211 overlay host returns to its rendered host and join choices");
                        yield break;
                    }

                    AccessTools.Method(typeof(OnlineLobbyOverlay), "ActivateRole").Invoke(overlay, new object[] { 1 });
                    if (!steam)
                    {
                        AccessTools.Field(typeof(OnlineLobbyOverlay), "m_directAddressInput").SetValue(overlay, target);
                        AccessTools.Method(typeof(OnlineLobbyOverlay), "TryConnectDirect").Invoke(overlay, null);
                    }
                    File.WriteAllText(SteamWaitingFile, "ready");
                    yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Visible && NetPlay.PartnerHere, 40f));
                    bool joined = OnlineRoomScreen.Visible && NetPlay.PartnerHere;
                    Check(joined, "overlay guest opens the room after connecting to the local test host");
                    if (!joined) yield break;
                    ulong lobby = NetPlay.SteamLobby;
                    yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Click("room.leave"), 5f));
                    OverlayPaint.Expect(steam ? L10n.T("lobby.wait_invite") : target);
                    yield return StartCoroutine(WaitFor(() => OverlayPaint.Seen, 6f));
                    bool returned = OverlayPaint.Seen && OnlineLobby.IsOpen && !OnlineRoomScreen.Visible && !NetPlay.Enabled && !s_mgr.IsVisible;
                    Check(returned, "T211 overlay guest returns to the rendered " + (steam ? "Steam invitation entry" : "direct address and port"));
                    if (!returned) yield break;

                    if (steam)
                    {
                        AccessTools.Method(typeof(OnlineLobbyOverlay), "ActivateRole").Invoke(overlay, new object[] { 1 });
                        // The test backend automatically delivers its first invitation only. Accept the same host again.
                        AccessTools.Method(typeof(SteamTransport), "OnInvite").Invoke(null, new object[] { lobby, (ulong)1001 });
                    }
                    else AccessTools.Method(typeof(OnlineLobbyOverlay), "TryConnectDirect").Invoke(overlay, null);
                    yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Click("room.continue"), 30f));
                    Check(OnlineRoomScreen.Confirmed && !OnlineRoomScreen.Visible && !OnlineLobby.IsOpen && NetPlay.PartnerHere && GuestFromTitle,
                        "T211 overlay guest Continue closes the lobby and starts the host save transfer");
                }
                finally
                {
                    OverlayPaint.Wanted = null;
                    if (!host) File.WriteAllText(GuestDoneFile, "done");
                    OnlineLobby.Close();
                    NetPlay.End("overlay room test done");
                }
            }
        }
    }
}
