using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using HarmonyLib;
using OriCoop.Net;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    public static partial class NativeModeMenu
    {
        partial class TitleJoinTest
        {
            IEnumerator RoomExitChecks()
            {
                // Use real local datagram peers before opening the UI session. The two ends of a
                // goodbye see different reasons: the host initiated it; the guests saw the partner leave.
                using (PeerHub hub = new PeerHub(0))
                using (UdpTransport first = new UdpTransport(0))
                using (UdpTransport second = new UdpTransport(0))
                {
                    List<string> local = new List<string>();
                    hub.Disconnected += (peer, reason) => local.Add(NetPlay.OwnDisconnectReason(peer, reason));
                    NetPeer a = new NetPeer(first), b = new NetPeer(second);
                    IPEndPoint endpoint = new IPEndPoint(IPAddress.Loopback, hub.LocalPort);
                    a.Connect(endpoint); b.Connect(endpoint);
                    float by = Time.realtimeSinceStartup + 5f;
                    while ((a.State != PeerState.Connected || b.State != PeerState.Connected) && Time.realtimeSinceStartup < by)
                    { hub.Poll(); hub.Flush(); a.Poll(); b.Poll(); a.Flush(); b.Flush(); yield return null; }
                    FieldInfo activeHub = AccessTools.Field(typeof(NetPlay), "s_hub");
                    if (activeHub.GetValue(null) != null) throw new InvalidOperationException("an active room owns the test hub");
                    try { activeHub.SetValue(null, hub); NetPlay.Shutdown(); hub.Poll(); a.Poll(); b.Poll(); }
                    finally { activeHub.SetValue(null, null); }
                    Check(local.Count == 2 && local.TrueForAll(reason => reason == "left: closed the game")
                        && a.DisconnectReason == SessionMachine.PartnerGoodbye && b.DisconnectReason == SessionMachine.PartnerGoodbye,
                        "T211 local host close is recorded for both peers and guests receive a partner goodbye");
                }

                OnlineLobby.Open("direct");
                try
                {
                    OnlineLobbyOverlay overlay = UnityEngine.Object.FindObjectOfType<OnlineLobbyOverlay>();
                    FieldInfo field = AccessTools.Field(typeof(OnlineLobbyOverlay), "m_directAddressInput");
                    FieldInfo key = AccessTools.Field(AccessTools.TypeByName("OriCoop.TestQuiet.InputBlock"), "FakeKeyDown");
                    AccessTools.Field(typeof(OnlineLobbyOverlay), "m_page").SetValue(overlay, 1);
                    field.SetValue(overlay, "1234");
                    try
                    {
                        key.SetValue(null, KeyCode.Backspace);
                        AccessTools.Method(typeof(OnlineLobbyOverlay), "UpdateJoinInput").Invoke(overlay, null);
                        overlay.HandleTypingEvents(Event.KeyboardEvent("backspace"));
                    }
                    finally { key.SetValue(null, KeyCode.None); }
                    Check((string)field.GetValue(overlay) == "123", "T211 one physical backspace removes one address character");
                }
                finally { OnlineLobby.Close(); }

                bool host = m_role == "host";
                s_transport = m_tr; s_roomEntry = RoomEntryKind.None;
                GoPage(host ? PageHost : PageJoin, 1);
                NetPlay.Begin(host, m_tr, host || m_tr == "steam" ? "" : IPAddress.Loopback.ToString());
                OnlineRoomScreen.Show(null);
                yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Click("room.leave"), 8f));
                RoomLeaveDestination expected = RoomLeaveFlow.DirectOrSteam(host, m_tr == "steam", !host && s_onTitle);
                int page = host ? PageRole : m_tr == "direct" ? PageEntry : PageJoinMethod;
                Check(!OnlineRoomScreen.Visible && !NetPlay.Enabled && IsOpen && s_page == page,
                    "T211 one room cancel leaves the waiting page (" + expected + ")");
            }
        }
    }
}
