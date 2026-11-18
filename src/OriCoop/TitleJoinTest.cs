using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Real menu test of the title online flow (src\run-titlejoin.ps1; ORICOOP_TITLEJOIN = host|join, transport in
    // ORICOOP_TITLEJOIN_TRANSPORT = direct|relay|cserver). Not the normal harness (ORICOOP_TEST stays off), so both games go
    // through the menus exactly as a player does:
    //   host:  title -> 多人游玩 -> transport -> 开房 -> save slot -> host page -> 开始游戏 -> in game, waits for the guest
    //   guest: (appdata without any save) title -> 多人游玩 -> transport -> 加入 -> address / room code -> 连接 ->
    //          the host's save loads with no slot pick -> placed beside the host; a co-op copy is kept in a slot.
    // Each writes PASS/FAIL lines and "RESULT PASS n/n" to ORICOOP_TEST_OUT, screenshots next to it, then quits.
    public static partial class NativeModeMenu
    {
        static string TitleJoinRole { get { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN") ?? ""; } }
        static bool s_titleJoinStarted;

        static void MaybeStartTitleJoinTest()
        {
            if (s_titleJoinStarted || TitleJoinRole.Length == 0 || TestHarness.Enabled) return;
            s_titleJoinStarted = true;
            GameObject go = new GameObject("OriCoop_TitleJoinTest");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TitleJoinTest>();
        }

        partial class TitleJoinTest : MonoBehaviour
        {
            readonly List<string> m_lines = new List<string>();
            int m_checks, m_fails;
            float m_t0;
            string m_out, m_role, m_tr;
            string m_cserverAddress;

            void Note(string s) { string l = string.Format("[{0,7:F2}] {1}", Time.realtimeSinceStartup - m_t0, s); m_lines.Add(l); Plugin.Log.LogInfo("TITLEJOIN " + s); }
            void Check(bool ok, string what) { m_checks++; if (!ok) m_fails++; Note((ok ? "PASS " : "FAIL ") + what); }
            void Shot(string name) { TShot("titlejoin_" + m_role + "_" + name); }
            void Update() { Application.runInBackground = true; }

            void Start()
            {
                m_t0 = Time.realtimeSinceStartup;
                m_role = TitleJoinRole;
                m_tr = Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_TRANSPORT") ?? "direct";
                m_out = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT") ?? Path.Combine(Application.dataPath, "titlejoin.txt");
                s_tHost = this;
                StartCoroutine(Run());
            }

            static string SavesDir { get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Ori and the Blind Forest DE"); } }
            static int SaveFiles()
            {
                try { return Directory.Exists(SavesDir) ? Directory.GetFiles(SavesDir, "saveFile*.sav").Length : -1; } catch (Exception) { return -1; }
            }

            string RoomFile { get { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_out)), "titlejoin-room.txt"); } }

            static bool CServerSavedOnDisk(string address)
            {
                try
                {
                    bool net = false;
                    foreach (string raw in File.ReadAllLines(Plugin.Instance.Config.ConfigFilePath))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("[")) net = line == "[Net]";
                        else if (net)
                        {
                            int equals = line.IndexOf('=');
                            if (equals > 0 && line.Substring(0, equals).Trim() == "CServerList")
                                return new List<string>(line.Substring(equals + 1).Trim().Split(';')).Contains(address);
                        }
                    }
                }
                catch (Exception) { }
                return false;
            }

            IEnumerator Run()
            {
                Note("up: " + m_role + " via " + m_tr + ", saves in appdata: " + SaveFiles());
                int savesAtStart = SaveFiles();
                string relay = Environment.GetEnvironmentVariable("ORICOOP_NET_RELAY") ?? "";
                if (m_tr == "relay" && relay.Length > 0) NetPlay.SetRelayServer(relay);

                // Title main menu ("press any key" first).
                float end = Time.realtimeSinceStartup + 120f; int lastPulse = -1;
                while (Time.realtimeSinceStartup < end && !(TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu && TitleItemsReady))
                {
                    int tick = (int)(Time.realtimeSinceStartup * 1f);
                    if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart && tick != lastPulse) { lastPulse = tick; TestHarness.PulseMenu = true; }
                    yield return null;
                }
                Check(TitleItemsReady, "title items up (screen=" + TitleScreenManager.CurrentScreen + ")");
                if (!TitleItemsReady) { Finish(); yield break; }
                PauseScreen pause = PauseScreen.Instance;
                Check(pause == null || pause.NavigationManager == null || !pause.NavigationManager.MenuItems.Exists(
                    delegate(CleverMenuItem item) { return item != null && item.name == "OriCoop_PauseSettings"; }),
                    "title: pause settings have not mutated the native menu during startup");
                LocalSeatsAdapter seats = new LocalSeatsAdapter();
                int p1 = Plugin.Instance.P1Pad, p2 = Plugin.Instance.P2Pad, mode = PlayMode.Current;
                InputDevice pad = new InputDevice(InputDevice.DeviceKind.Pad, (p2 + 1) % 4);
                bool changed = seats.SetInput(1, pad);
                bool added = seats.AddLocalPlayer(pad);
                seats.RemoveLocalPlayer(2);
                Check(!changed && !added && Plugin.Instance.P1Pad == p1 && Plugin.Instance.P2Pad == p2 &&
                    PlayMode.Current == mode && P2Context.P2 == null, "title: local seat operations are inert");
                if (m_fails != 0) { Finish(); yield break; }
                yield return StartCoroutine(TWait(1.5f));
                yield return StartCoroutine(PressCoop());
                int row = Array.IndexOf(TitleTransports, m_tr == "cserver" ? "relay" : m_tr);
                yield return StartCoroutine(Row(row, 1f));
                if (m_tr == "relay" || m_tr == "cserver")
                {
                    Check(TitleOrbOpen && LastPage == "servermode" && LastLabels.Count == 2 && s_mgr.Index == 0
                        && HasKey(LastLabels, "server.cserver") && HasKey(LastLabels, "lobby.rooms") && !HasKey(LastLabels, "server.relay"),
                        m_tr + ": 自建服务器 page lists server mode and My rooms (" + Labels + ")");
                    if (m_tr == "cserver") yield return StartCoroutine(Row(0, 1f));
                    else { LobbyOpen("relay"); yield return StartCoroutine(TWait(1f)); }   // the relay host / join pages are no longer in the menu (T-045): tests open them directly
                }
                if (m_tr == "cserver") yield return StartCoroutine(CheckCServerEditor());
                bool roleReady = TitleOrbOpen && LastPage == "role" && HasKey(LastLabels, "lobby.host") && HasKey(LastLabels, "lobby.join");
                Check(roleReady, m_tr + ": host / join page on the title (" + Labels + ")");
                if (!roleReady || m_fails != 0) { Finish(); yield break; }
                if (m_role == "host") yield return StartCoroutine(Host());
                else yield return StartCoroutine(Join(savesAtStart));
                if (SignMode && m_fails == 0) yield return StartCoroutine(SignOnline());          // TitleInputTest.cs
                if (LeaveMode.Length > 0 && m_fails == 0) yield return StartCoroutine(Leave());
                Finish();
            }

            IEnumerator CheckCServerEditor()
            {
                Check(CServerListLabelsMatch(), "cserver: list always shows Add / Remove and every saved address (" + Labels + ")");
                int before = OriCoop.Authority.CServerConfig.Servers.Count;
                yield return StartCoroutine(Row(0, 0.5f));
                Check(LastPage == "serveradd", "cserver: Add opens the address / port editor");
                string prefilled = OriCoop.Authority.CServerConfig.Address;
                string supplied = (Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER") ?? "").Trim();
                if (supplied.Length == 0) supplied = OriCoop.Authority.CServerConfig.Configured;
                Check(OriCoop.Authority.CServerConfig.IsValidAddress(supplied) && prefilled == supplied,
                    "cserver: runner supplied a valid endpoint (" + supplied + ")");
                string host = s_cserverHost, port = s_cserverPort;
                m_cserverAddress = supplied;
                Check(LastLabels.Count == 3 && LastLabels[0].Contains(host) && LastLabels[1].Contains(port) && host + ":" + port == supplied,
                    "cserver: address and port rows prefilled from " + prefilled);
                foreach (bool zh in new[] { true, false })
                {
                    string[][] rows = LobbyRows(zh);
                    Check(rows.Length == 3 && rows[0][0] == L10n.T("cserver.addr", zh) + ": [" + host + "]" &&
                        rows[1][0] == L10n.T("cserver.port", zh) + ": " + port && rows[2][0] == L10n.T("common.save", zh),
                        "cserver: localized add rows " + (zh ? "zh" : "en"));
                }
                s_cserverHost = "";
                yield return StartCoroutine(Row(2, 0.4f));
                Check(LastPage == "serveradd" && s_toastKey == "cserver.addr.invalid" && !NetPlay.Enabled &&
                    OriCoop.Authority.CServerConfig.Servers.Count == before, "cserver: invalid address cannot be added");
                s_cserverHost = host;
                RefreshTexts(true);

                yield return StartCoroutine(Row(0, 0.5f));
                Check(LastPage == "entry" && s_entryKind == 3 && s_entry == host, "cserver: address row opens its prefilled editor");
                if (LastPage != "entry" || s_entryKind != 3) yield break;
                yield return StartCoroutine(Row(0, 0.4f));
                int lastDot = s_entry.LastIndexOf('.');
                Check(s_editing, "cserver: address editor accepts keyboard input");
                if (!s_editing) yield break;
                s_cursor = lastDot + 1;
                CServerEditInput("0");
                string edited = s_entry;
                Check(edited != host && edited == host.Insert(lastDot + 1, "0"), "cserver: typed address changes the editor");
                CServerEditInput("\b");
                Check(s_entry == host, "cserver: Backspace removes the typed character");
                CServerEditInput("0\r");
                Check(LastPage == "serveradd" && s_cserverHost == edited && OriCoop.Authority.CServerConfig.Servers.Count == before,
                    "cserver: Enter accepts the host field without adding a partial server");
                yield return StartCoroutine(TWait(0.5f));

                // The edit above tests saving a changed field, not a different server. Restore through the editor
                // before adding/selecting: both clients must use the exact endpoint provided by the runner.
                yield return StartCoroutine(Row(0, 0.5f));
                yield return StartCoroutine(Row(0, 0.4f));
                s_cursor = lastDot + 2;
                CServerEditInput("\b\r");
                Check(LastPage == "serveradd" && s_cserverHost == host && s_cserverHost + ":" + s_cserverPort == supplied &&
                    OriCoop.Authority.CServerConfig.Servers.Count == before,
                    "cserver: editor restores the supplied endpoint before saving the list");
                yield return StartCoroutine(TWait(0.5f));

                yield return StartCoroutine(Row(1, 0.5f));
                yield return StartCoroutine(Row(0, 0.4f));
                if (s_page == PageEntry && s_entryKind == 4 && s_editing)
                {
                    CServerEditInput("x");
                    Check(s_entry == port, "cserver: port editor rejects letters");
                    CServerEditInput("0\u001b");
                    Check(LastPage == "serveradd" && s_cserverPort == port,
                        "cserver: Escape cancels a port edit");
                    yield return StartCoroutine(TWait(0.5f));
                }
                else Check(false, "cserver: port row opens its editor");
                yield return StartCoroutine(Row(2, 0.5f));
                List<string> saved = OriCoop.Authority.CServerConfig.Servers;
                Check(CServerListLabelsMatch() && saved.Count == before + 1 && saved[saved.Count - 1] == m_cserverAddress &&
                    CServerSavedOnDisk(m_cserverAddress), "cserver: Save appends to the persisted list");
                yield return StartCoroutine(Row(saved.Count + 1, 0.5f));
                Check(LastPage == "role" && LastLabels.Count == 2 && OriCoop.Authority.CServerConfig.Address == m_cserverAddress &&
                    OriCoop.Authority.CServerConfig.Configured == m_cserverAddress,
                    "cserver: picking the saved row selects its address and opens Host / Join");
            }

            IEnumerator Host()
            {
                yield return StartCoroutine(Row(0, 0.2f));                          // 开房 -> save slots
                yield return StartCoroutine(WaitFor(() => SlotsReady, 8f));
                yield return StartCoroutine(TWait(0.8f));
                Check(SlotsReady && SlotPickPlan == "host:" + m_tr, "host: 开房 -> save slots (" + SlotPickPlan + ")");
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                if (ui == null || !SlotsReady) yield break;
                int slot = 1; int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_SLOT") ?? "1", out slot);
                ui.SetCurrentItem(slot);
                ui.UsedSaveSlotSelected();
                float end = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < end && !(LastPage == "hosting" && (NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected)
                    && ((m_tr != "relay" && m_tr != "cserver") || !string.IsNullOrEmpty(NetPlay.RoomCode))))
                {
                    if (m_tr == "cserver" && OriCoop.Authority.CServerLink.LastFailure.Length > 0) break;
                    yield return null;
                }
                Check(IsOpen && LastPage == "hosting", "host: slot pick opens the host page (" + Labels + ", net=" + NetPlay.CurrentPhase + ")");
                Check((NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected) &&
                    (m_tr != "cserver" || OriCoop.Authority.CServerLink.LastFailure.Length == 0),
                    "host: server accepted the session (net=" + NetPlay.CurrentPhase + ", error=" +
                    (m_tr == "cserver" ? OriCoop.Authority.CServerLink.LastFailure : NetPlay.Problem) + ")");
                if (m_tr == "cserver") Check(s_cserverBeginTarget == m_cserverAddress + "|", "host: Begin target " + s_cserverBeginTarget);
                if (m_tr == "relay" || m_tr == "cserver")
                {
                    Check(!string.IsNullOrEmpty(NetPlay.RoomCode), "host: room code " + NetPlay.RoomCode);
                    if (string.IsNullOrEmpty(NetPlay.RoomCode)) yield break;   // Run writes RESULT FAIL, not a timeout.
                    try { File.WriteAllText(RoomFile, NetPlay.RoomCode); } catch (Exception e) { Note("room file: " + e.Message); }
                }
                if (m_fails != 0) yield break;
                yield return StartCoroutine(TWait(1f));
                Shot("host_page");
                yield return StartCoroutine(TWait(0.5f));
                yield return StartCoroutine(Row(1, 0.5f));                          // 开始游戏
                end = Time.realtimeSinceStartup + 90f;
                while (Time.realtimeSinceStartup < end && !NetPlay.LocalInGameplay()) { DismissResume(); yield return null; }
                Check(NetPlay.LocalInGameplay(), "host: in game after 开始游戏");
                Check(m_roomSeen, "host: room screen shown before the game starts");
                end = Time.realtimeSinceStartup + 150f;
                while (Time.realtimeSinceStartup < end && !(NetPlay.PartnerHere && SaveSync.Ready)) { DismissResume(); yield return null; }
                Check(NetPlay.PartnerHere && SaveSync.Ready, "host: guest joined and loaded (net=" + NetPlay.CurrentPhase + ")");
                yield return StartCoroutine(TWait(4f));
                Vector3 me = Characters.Sein != null ? Characters.Sein.Position : Vector3.zero;
                Check(NetPlay.HaveRemotePos && Vector3.Distance(NetPlay.LatestRemotePos, me) < 4f,
                    "host: guest Ori beside the host (distance " + (NetPlay.HaveRemotePos ? Vector3.Distance(NetPlay.LatestRemotePos, me).ToString("F2") : "?") + ")");
                Shot("in_game");
                yield return StartCoroutine(TWait(8f));                             // let the guest finish its checks
                if (m_tr == "cserver")
                {
                    // T-031: the server-mode room's arbiter role must end with the session. It used to survive NetPlay.End,
                    // so the next Steam join (an invite at the title) ran as a host and refused the invite.
                    Check(NetPlay.HostOverride, "host: seat 1 is the server-mode arbiter while the session runs");
                    NetPlay.End("title join test: session end check");
                    yield return null;
                    Check(!NetPlay.HostOverride && !NetPlay.IsHost && OriCoop.Authority.CServerLink.Client == null,
                        "host: ending the session clears the arbiter role and the server link (HostOverride=" + NetPlay.HostOverride +
                        ", link=" + (OriCoop.Authority.CServerLink.Client != null) + ")");
                }
            }

            // Digit keys on the focused dial row, then Enter, through the lobby's own key handling (NativeLobby test hooks).
            IEnumerator DialType(string digits)
            {
                foreach (char c in digits)
                {
                    TestDialKey = c - '0';
                    yield return StartCoroutine(TWait(0.2f));   // the edit ignores 0.15 s after it starts
                }
                TestDialConfirm = true;
                yield return StartCoroutine(TWait(0.4f));
            }

            IEnumerator Join(int savesAtStart)
            {
                bool full = Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_FULL") == "1";
                if (full) Check(savesAtStart >= 10, "guest: every save slot is full (" + savesAtStart + " files)");
                else Check(savesAtStart == 0, "guest: appdata has no saves (" + savesAtStart + ")");
                yield return StartCoroutine(Row(1, 1.2f));                          // 加入 -> entry page
                Check(TitleOrbOpen && LastPage == "entry" && !SlotsReady, "guest: join page on the title, no save slot (" + Labels + ")");
                if (m_tr == "direct")
                {
                    LoadAddress("", NetPlay.ConfigPort);
                    // T-046: the IP dial, typed with digit keys as a player does: loopback, four groups of three digits.
                    s_mgr.SetCurrentItem(0);
                    yield return StartCoroutine(TWait(0.4f));
                    yield return StartCoroutine(DialType("127000000001"));
                    Check(!s_editing && IpText == "127.0.0.1", "guest: IP typed on the digit dial (" + IpText + ")");
                }
                else
                {
                    string code = "";
                    float wait = Time.realtimeSinceStartup + 90f;
                    while (Time.realtimeSinceStartup < wait && code.Length != 6)
                    {
                        try { if (File.Exists(RoomFile)) code = File.ReadAllText(RoomFile).Trim(); } catch (Exception) { }
                        yield return StartCoroutine(TWait(0.5f));
                    }
                    Check(code.Length == 6, "guest: got the host's room code '" + code + "'");
                    // T-046: servers issue six-digit codes, typed on the room-code dial with digit keys as a player does.
                    bool digits = code.Length == 6; foreach (char c in code) if (c < '0' || c > '9') digits = false;
                    Check(digits, "guest: the room code is six digits ('" + code + "')");
                    s_mgr.SetCurrentItem(0);
                    yield return StartCoroutine(TWait(0.4f));
                    yield return StartCoroutine(DialType(code));
                    Check(!s_editing && s_entry == code, "guest: room code typed on the digit dial ('" + s_entry + "')");
                    // T-046: older letter codes still enter: a letter switches the dial to text entry, whose alphabet keeps letters.
                    bool legacyBefore = s_legacyCode; s_legacyCode = true;
                    Check(HasLegacyLetter("AB12") && !HasLegacyLetter("1234") && Filter("ab2z") == "AB2Z" && !DialMode,
                        "guest: older letter room codes still enter (Filter ab2z -> " + Filter("ab2z") + ")");
                    s_legacyCode = legacyBefore;
                }
                RefreshTexts(true);
                yield return StartCoroutine(TWait(0.5f));
                s_mgr.SetCurrentItem(m_tr == "direct" ? 2 : 1);                     // Connect: shows the save note
                yield return StartCoroutine(TWait(0.5f));
                Check(LastTooltip.Contains(L10n.T("lobby.save.note", true)) || LastTooltip.Contains(L10n.T("lobby.save.note", false)), "guest: join page says the co-op copy is saved automatically");
                Shot("join_page");
                yield return StartCoroutine(TWait(0.5f));
                yield return StartCoroutine(Row(m_tr == "direct" ? 2 : 1, 0.5f));  // 连接
                Check(LastPage == "joining" || NetPlay.Enabled, "guest: connecting (" + Labels + ", net=" + NetPlay.CurrentPhase + ")");
                if (m_tr == "cserver") Check(s_cserverBeginTarget == m_cserverAddress + "|" + s_entry.ToUpperInvariant(), "guest: Begin target " + s_cserverBeginTarget);
                float end = Time.realtimeSinceStartup + 150f;
                bool sawStart = false, picked = false;
                while (Time.realtimeSinceStartup < end && !(NetPlay.LocalInGameplay() && SaveSync.Ready))
                {
                    if (GuestFromTitle) sawStart = true;
                    if (full && !picked && GuestPickingSlot && SlotsReady)
                    {
                        // All slots full: the save slots came up to choose the slot for the co-op copy.
                        yield return StartCoroutine(TWait(4.5f));     // after the "joined" toast: the slot prompt shows
                        Shot("pick_slot");
                        yield return StartCoroutine(TWait(0.5f));
                        SaveSlotsUI ui = SaveSlotsUI.Instance;
                        ui.SetCurrentItem(2);
                        ui.UsedSaveSlotSelected();
                        picked = true;
                        Note("picked slot 2 for the co-op copy");
                    }
                    DismissResume();
                    yield return null;
                }
                if (full)
                {
                    Check(picked, "guest: all slots full -> asked to pick a slot (NeedSlotChoice=" + GuestJoin.NeedSlotChoice + ")");
                    Check(LastSlotPick == "guest-slot-2", "guest: the picked slot keeps the co-op copy (" + LastSlotPick + ")");
                    string bk = Path.Combine(SavesDir, "OriCoop-backups");
                    int nb = 0; try { if (Directory.Exists(bk)) nb = Directory.GetFiles(bk).Length; } catch (Exception) { }
                    Check(nb >= 1, "guest: the overwritten save was backed up first (" + nb + " file(s) in OriCoop-backups)");
                }
                Check(sawStart || GuestStatus.Length > 0, "guest: no-slot join started (" + GuestStatus + ")");
                Check(m_roomSeen, "guest: room screen shown before the join starts");
                Check(NetPlay.LocalInGameplay() && SaveSync.Ready, "guest: in game with the host's save (net=" + NetPlay.CurrentPhase + ", status=" + GuestStatus + ", aborts=" + GuestJoin.Aborts + " " + GuestJoin.LastAbort + ")");
                Check(SaveSync.JoinedHost && SaveSync.GuestJoinDistance < 3f, "guest: placed beside the host (distance " + SaveSync.GuestJoinDistance.ToString("F2") + ")");
                if (!full) Check(TitleChoice == TitleNone && !LastSlotPick.StartsWith("title-") && !LastSlotPick.StartsWith("guest-slot"), "guest: no save slot was picked (" + LastSlotPick + ")");
                yield return StartCoroutine(TWait(4f));
                Vector3 me = Characters.Sein != null ? Characters.Sein.Position : Vector3.zero;
                Check(NetPlay.HaveRemotePos && Vector3.Distance(NetPlay.LatestRemotePos, me) < 4f,
                    "guest: still beside the host (distance " + (NetPlay.HaveRemotePos ? Vector3.Distance(NetPlay.LatestRemotePos, me).ToString("F2") : "?") + ")");
                Check(CoopSaves.GuestSlot == (full ? 2 : CoopSaves.GuestSlot) && CoopSaves.GuestSlot >= 0 && SaveFiles() >= 1, "guest: co-op copy kept in slot " + CoopSaves.GuestSlot + " (" + SaveFiles() + " save file(s))");
                // T-053: the one-time controls guide is on screen in the game's language, and the game keeps running.
                float guideEnd = Time.realtimeSinceStartup + 6f;
                while (Time.realtimeSinceStartup < guideEnd && ControlsGuideHud.FramesDrawn == 0) yield return null;
                string jumpWord = L10n.T("guide.key.space");
                Check(ControlsGuideHud.FramesDrawn > 0 && ControlsGuideHud.LastText.Contains(jumpWord),
                    "guest: controls guide on screen (" + ControlsGuideHud.FramesDrawn + " frames: " + ControlsGuideHud.LastText + ")");
                // Paused = timeScale 0; the game's own slow-motion moments (0.5) still count as running.
                Check(ControlsGuideHud.LastTimeScale > 0.01f && Time.timeScale > 0.01f && NetPlay.LocalInGameplay(),
                    "guest: guide does not pause the game (timeScale " + ControlsGuideHud.LastTimeScale.ToString("F2") + ")");
                Shot("in_game");
            }

            void DismissResume()
            {
                // T-059: the room page comes up before the game starts; press its drawn continue / start button.
                if (OnlineRoomScreen.Visible && OnlineRoomScreen.Click("room.continue") && !m_roomSeen) { m_roomSeen = true; Note("room screen: continue pressed"); }
                if (UI.MainMenuVisible && GameController.Instance != null && GameController.Instance.IsLoadingGame) { UI.Menu.HideResumeScreen(); UI.Menu.HideMenuScreen(true); }
            }

            bool m_finished, m_roomSeen;
            void Finish()
            {
                if (m_finished) return;
                m_finished = true;
                Note("RESULT " + (m_fails == 0 && m_checks > 0 ? "PASS" : "FAIL") + " " + (m_checks - m_fails) + "/" + m_checks);
                try { File.WriteAllText(m_out, string.Join("\n", m_lines.ToArray()) + "\n"); } catch (Exception e) { Plugin.Log.LogError(e); }
                if (NetPlay.Enabled) NetPlay.End("title join test done");
                Invoke("Quit", 1.5f);
            }

            void Quit() { Application.Quit(); }
        }
    }
}
