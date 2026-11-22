using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Real menu test of the title online flow (src\run-titlejoin.ps1; ORICOOP_TITLEJOIN = host|join, transport in
    // ORICOOP_TITLEJOIN_TRANSPORT = direct|cserver). Not the normal harness (ORICOOP_TEST stays off), so both games go
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

            static string SavesDir { get { return Saves.CoopSaveFolder.Dir; } }
            static int SaveFiles()
            {
                try { return Directory.Exists(SavesDir) ? Directory.GetFiles(SavesDir, "saveFile*.sav").Length : -1; } catch (Exception) { return -1; }
            }

            // T-043 rule 5: online saves live in OriCoop-coop; the single-player files are never touched online.
            readonly Dictionary<string, string> m_singleSaves = new Dictionary<string, string>();
            void SnapshotSinglePlayerSaves()
            {
                foreach (int slot in Saves.CoopSaveFolder.SinglePlayerSlotsWithSaves())
                {
                    string path = Saves.CoopSaveFolder.SinglePlayerPath(slot);
                    m_singleSaves[path] = CoopSaves.Hash(File.ReadAllBytes(path));
                }
                Note(m_role + ": single-player saves before going online: " + m_singleSaves.Count);
            }

            void CheckSaveIsolation(int slot)
            {
                string path = GameController.Instance.SaveGameController.GetSaveFilePath(slot);
                Check(Saves.CoopSaveFolder.Active && File.Exists(path) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                    Path.GetFullPath(Saves.CoopSaveFolder.Dir), StringComparison.OrdinalIgnoreCase),
                    m_role + ": online save file is inside OriCoop-coop (" + path + ")");
                Check(Saves.CoopSaveFolder.SinglePlayerSlotsWithSaves().Length == m_singleSaves.Count,
                    m_role + ": no single-player save file created or removed (" + m_singleSaves.Count + " -> "
                    + Saves.CoopSaveFolder.SinglePlayerSlotsWithSaves().Length + ")");
                foreach (KeyValuePair<string, string> before in m_singleSaves)
                    Check(File.Exists(before.Key) && before.Value == CoopSaves.Hash(File.ReadAllBytes(before.Key)),
                        m_role + ": single-player save byte-identical: " + Path.GetFileName(before.Key));
            }

            // T-043 rule 6: a single-player save copied in becomes a co-op slot (and the starting save of the room).
            void CheckCopyIn()
            {
                int[] singles = Saves.CoopSaveFolder.SinglePlayerSlotsWithSaves();
                int target = Saves.CoopSaveFolder.FirstEmptyCoopSlot();
                if (singles.Length == 0 || target < 0) { Note("host: copy-in check skipped (single saves " + singles.Length + ", empty co-op slot " + target + ")"); return; }
                string source = Saves.CoopSaveFolder.SinglePlayerPath(singles[0]);
                string copy = GameController.Instance.SaveGameController.GetSaveFilePath(target);
                Check(Saves.CoopSaveFolder.CopyFromSinglePlayer(singles[0], target), "host: copy single-player slot " + singles[0] + " to co-op slot " + target);
                Check(File.Exists(copy) && CoopSaves.Hash(File.ReadAllBytes(copy)) == CoopSaves.Hash(File.ReadAllBytes(source)),
                    "host: the co-op copy is byte-identical to the single-player save (" + copy + ")");
                Check(SaveSlotsManager.SlotExists(target), "host: the slot list shows the copied co-op slot " + target);
                Check(!Saves.CoopSaveFolder.CopyFromSinglePlayer(singles[0], target), "host: copy-in never overwrites a co-op slot");
            }

            string RoomFile { get { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_out)), "titlejoin-room.txt"); } }
            // T-189: the guest writes it once its pause menu checks are done; the host ends the session only after that.
            string GuestDoneFile { get { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_out)), "titlejoin-guestdone.txt"); } }

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
                SnapshotSinglePlayerSaves();

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
                GameSeatView seats = new GameSeatView();
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
                int row = Array.IndexOf(TitleTransports, m_tr);
                yield return StartCoroutine(Row(row, 1f));
                if (m_tr == "cserver")
                {
                    Check(TitleOrbOpen && ServerModeLabelsMatch() && s_mgr.Index == 0,
                        m_tr + ": 自建服务器 page lists My rooms and Server management (" + Labels + ")");
                    yield return StartCoroutine(Row(ServerModeRoomsRow + 1, 0.5f));
                    Check(ServerManageLabelsMatch(), m_tr + ": 服务器管理 lists Add / Remove / Server list (" + Labels + ")");
                    yield return StartCoroutine(Row(2, 0.5f));
                }
                if (m_tr == "cserver") yield return StartCoroutine(CheckCServerEditor());
                // T-171: on a C-server the page is the room list (创建房间 / 加入房间 / the rooms on the server).
                bool roleReady = TitleOrbOpen && LastPage == "role" && (m_tr == "cserver"
                    ? HasKey(LastLabels, "csroom.list.create") && HasKey(LastLabels, "csroom.list.join")
                    : HasKey(LastLabels, "lobby.host") && HasKey(LastLabels, "lobby.join"));
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
                Check(CServerListLabelsMatch(), "cserver: list shows every saved address (" + Labels + ")");
                int before = OriCoop.Authority.CServerConfig.Servers.Count;
                yield return StartCoroutine(Back(0.5f));
                yield return StartCoroutine(Row(0, 0.5f));
                Check(LastPage == "serveradd", "cserver: Add opens the address / port editor");
                Check(LastLabels.Count == 4 && s_cserverHost.Length == 0 && s_cserverPort.Length == 0, "cserver: Add starts with an empty address and port (" + Labels + ")");
                string prefilled = OriCoop.Authority.CServerConfig.Address;
                string supplied = (Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER") ?? "").Trim();
                if (supplied.Length == 0) supplied = OriCoop.Authority.CServerConfig.Configured;
                Check(OriCoop.Authority.CServerConfig.IsValidAddress(supplied) && prefilled == supplied,
                    "cserver: runner supplied a valid endpoint (" + supplied + ")");
                // Add starts blank (T-095): fill in the endpoint the runner supplied, as typing it would.
                int suppliedColon = supplied.LastIndexOf(':');
                s_cserverHost = supplied.Substring(0, suppliedColon); s_cserverPort = supplied.Substring(suppliedColon + 1);
                RefreshTexts(true);
                string host = s_cserverHost, port = s_cserverPort;
                m_cserverAddress = supplied;
                Check(LastLabels.Count == 4 && LastLabels[0].Contains(host) && LastLabels[1].Contains(port) && host + ":" + port == supplied,
                    "cserver: address and port rows show the entered endpoint " + supplied);
                foreach (bool zh in new[] { true, false })
                {
                    string[][] rows = LobbyRows(zh);
                    Check(rows.Length == 4 && rows[0][0] == L10n.T("cserver.addr", zh) + ": [" + host + "]" &&
                        rows[1][0] == L10n.T("cserver.port", zh) + ": " + port && rows[2][0] == L10n.T("server.test", zh) && rows[3][0] == L10n.T("common.ok", zh),
                        "cserver: localized add rows " + (zh ? "zh" : "en"));
                }
                s_cserverHost = "";
                yield return StartCoroutine(Row(3, 0.4f));
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
                // Keep the edit valid for any IPv4 address: append to a one-digit last octet, otherwise shorten it.
                System.Net.IPAddress ip;
                bool append = !System.Net.IPAddress.TryParse(host, out ip) || host.Length - lastDot - 1 == 1;
                string typed = append ? "0" : "\b";
                string undo = append ? "\b" : host.Substring(host.Length - 1);
                s_cursor = host.Length;
                CServerEditInput(typed);
                string edited = s_entry;
                string expected = append ? host + "0" : host.Substring(0, host.Length - 1);
                Check(edited != host && edited == expected, "cserver: keyboard input changes the address editor");
                CServerEditInput(undo);
                Check(s_entry == host, "cserver: reversing the edit restores the address");
                CServerEditInput(typed + "\r");
                Check(LastPage == "serveradd" && s_cserverHost == edited && OriCoop.Authority.CServerConfig.Servers.Count == before,
                    "cserver: Enter accepts the host field without adding a partial server");
                yield return StartCoroutine(TWait(0.5f));

                // The edit above tests saving a changed field, not a different server. Restore through the editor
                // before adding/selecting: both clients must use the exact endpoint provided by the runner.
                yield return StartCoroutine(Row(0, 0.5f));
                yield return StartCoroutine(Row(0, 0.4f));
                s_cursor = edited.Length;
                CServerEditInput(undo + "\r");
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
                yield return StartCoroutine(Row(3, 0.5f));
                List<string> saved = OriCoop.Authority.CServerConfig.Servers;
                Check(CServerListLabelsMatch() && saved.Count == before + 1 && saved[saved.Count - 1] == m_cserverAddress &&
                    CServerSavedOnDisk(m_cserverAddress), "cserver: Save appends to the persisted list");
                yield return StartCoroutine(Row(saved.Count - 1, 0.5f));
                // T-171: the room list = 创建房间 / 加入房间, then the rooms (or one status row).
                Check(LastPage == "role" && LastLabels.Count >= 3 && HasKey(LastLabels.GetRange(0, 2), "csroom.list.create") && HasKey(LastLabels.GetRange(0, 2), "csroom.list.join")
                    && OriCoop.Authority.CServerConfig.Address == m_cserverAddress &&
                    OriCoop.Authority.CServerConfig.Configured == m_cserverAddress,
                    "cserver: picking the saved row selects its address and opens the room list (" + Labels + ")");
            }

            // T-176: before the code is shared, leave the room page through the Esc path (byLeave), or end the session from
            // outside the way a kick does (CServerLink.EndForGood: NetPlay.End from a coroutine): the orb must close and the
            // save-slot list come back usable (old code: the dead host page stayed on the orb). Then host again from the same slot.
            IEnumerator CsLeaveAndRehost(int slot, bool byLeave)
            {
                string how = byLeave ? "T-176 leave: " : "T-176 session end: ";
                yield return StartCoroutine(WaitFor(() => CsRoomPage.IsOpen, 5f));
                Check(CsRoomPage.IsOpen, how + "host room page open before");
                if (!CsRoomPage.IsOpen) yield break;
                if (byLeave) CsRoomPage.Run(CsRoomPage.Model.Shortcut(Ui.CsRoomAction.Leave));
                else NetPlay.End("T-176 test: the session ended by itself");
                yield return StartCoroutine(TWait(1f));
                Check(!CsRoomPage.IsOpen && !OnlineRoomScreen.Visible, how + "the room page is closed");
                Check(!IsOpen, how + "the host orb is closed (" + Labels + ")");
                Check(!NetPlay.Enabled, how + "the host session is over");
                Check(SlotsReady, how + "save-slot list active on the SaveSlots screen (" + TitleScreenManager.CurrentScreen + ")");
                Check(SlotPickPlan == "host:cserver", how + "the next slot pick still hosts (" + SlotPickPlan + ")");
                if (m_fails != 0) yield break;
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                ui.SetCurrentItem(slot);
                ui.UsedSaveSlotSelected();
                yield return StartCoroutine(WaitFor(() => IsOpen && LastPage == "hosting" && !string.IsNullOrEmpty(NetPlay.RoomCode), 20f));
                Check(IsOpen && LastPage == "hosting", how + "picking the slot again opens the host page (" + Labels + ")");
                Check(!string.IsNullOrEmpty(NetPlay.RoomCode), how + "rehost room code " + NetPlay.RoomCode);
            }

            // T-188: test games have no real devices (T-085), so the device "pressed on the menus" is injected. The host
            // plays as a controller player, the guest as a keyboard/mouse player.
            const int HostMenuPad = 0;
            void CheckPrimaryInput(string who, int expected, string what)
            {
                InputDevice d = OnlineLocalPlayers.InputOf(Players.LocalSeat);
                int code = d.Kind == InputDevice.DeviceKind.KeyboardMouse ? -1 : d.Pad;
                int game = expected < 0 ? -1 : expected;
                bool gameOk = expected < 0 ? KeyboardOnlyP1.GamePad < 0 : KeyboardOnlyP1.GamePad == game && !KeyboardOnlyP1.StartOnly;
                Check(code == expected && gameOk, who + ": " + what + " (P" + Players.LocalSeat + " input " + (code < 0 ? "keyboard/mouse" : "pad " + code)
                    + ", game reads: " + KeyboardOnlyP1.State + ")");
            }

            IEnumerator Host()
            {
                try { if (File.Exists(RenameFile)) File.Delete(RenameFile); } catch (Exception e) { Note("rename file: " + e.Message); }   // T-173: no stale marker
                try { if (File.Exists(GuestDoneFile)) File.Delete(GuestDoneFile); } catch (Exception e) { Note("guest done file: " + e.Message); }   // T-189
                KeyboardOnlyP1.SetMenuDeviceForTest(HostMenuPad);                   // T-188: the host used pad 0 on the menus
                yield return StartCoroutine(Row(0, 0.2f));                          // 开房 -> save slots
                yield return StartCoroutine(WaitFor(() => SlotsReady, 8f));
                yield return StartCoroutine(TWait(0.8f));
                Check(SlotsReady && SlotPickPlan == "host:" + m_tr, "host: 开房 -> save slots (" + SlotPickPlan + ")");
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                if (ui == null || !SlotsReady) yield break;
                int slot = 1; int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_SLOT") ?? "1", out slot);
                CheckCopyIn();
                ui.SetCurrentItem(slot);
                m_pickedSlot = ui.CurrentSlotIndex;   // T-173: the room page must show this slot
                ui.UsedSaveSlotSelected();
                float end = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < end && !(LastPage == "hosting" && (NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected)
                    && (m_tr != "cserver" || !string.IsNullOrEmpty(NetPlay.RoomCode))))
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
                if (m_tr == "cserver")
                {
                    Check(!string.IsNullOrEmpty(NetPlay.RoomCode), "host: room code " + NetPlay.RoomCode);
                    if (string.IsNullOrEmpty(NetPlay.RoomCode)) yield break;   // Run writes RESULT FAIL, not a timeout.
                    yield return StartCoroutine(CsLeaveAndRehost(slot, true));    // T-176: Esc on the room page
                    if (m_fails != 0) yield break;
                    yield return StartCoroutine(CsLeaveAndRehost(slot, false));   // T-176: the session ends by itself (kick)
                    if (m_fails != 0) yield break;
                    try { File.WriteAllText(RoomFile, NetPlay.RoomCode); } catch (Exception e) { Note("room file: " + e.Message); }
                }
                if (m_fails != 0) yield break;
                yield return StartCoroutine(TWait(1f));
                Shot("host_page");
                yield return StartCoroutine(TWait(0.5f));
                yield return StartCoroutine(Row(1, 0.5f));                          // 开始游戏
                if (m_tr == "cserver") yield return StartCoroutine(CsRoomPageChecks(2));   // T-171: wait for the guest on the 房间管理页
                end = Time.realtimeSinceStartup + 90f;
                while (Time.realtimeSinceStartup < end && !NetPlay.LocalInGameplay()) { DismissResume(); yield return null; }
                Check(NetPlay.LocalInGameplay(), "host: in game after 开始游戏");
                Check(m_roomSeen, "host: room screen shown before the game starts");
                yield return StartCoroutine(TWait(1f));                             // KeyboardOnlyP1.Refresh runs in FixedUpdate
                CheckPrimaryInput("host", HostMenuPad, "T-188 the controller used on the menus still drives P1 in game");
                KeyboardOnlyP1.SetMenuDeviceForTest(-1);                            // a key pressed later (e.g. in the pause menu)
                yield return StartCoroutine(TWait(0.5f));
                CheckPrimaryInput("host", HostMenuPad, "T-188 once in game a later key press does not take the controller away");
                GameSeatView hostSeats = new GameSeatView();
                bool pickedKb = hostSeats.SetInput(Players.LocalSeat, new InputDevice(InputDevice.DeviceKind.KeyboardMouse, -1));
                yield return StartCoroutine(TWait(0.5f));
                Check(pickedKb, "host: T-188 picking keyboard/mouse only in F8 is accepted");
                CheckPrimaryInput("host", -1, "T-188 keyboard/mouse only, when picked, sticks");
                hostSeats.SetInput(Players.LocalSeat, new InputDevice(InputDevice.DeviceKind.Pad, HostMenuPad));
                end = Time.realtimeSinceStartup + 150f;
                while (Time.realtimeSinceStartup < end && !(NetPlay.PartnerHere && SaveSync.Ready)) { DismissResume(); yield return null; }
                Check(NetPlay.PartnerHere && SaveSync.Ready, "host: guest joined and loaded (net=" + NetPlay.CurrentPhase + ")");
                yield return StartCoroutine(TWait(4f));
                Vector3 me = Characters.Sein != null ? Characters.Sein.Position : Vector3.zero;
                Check(NetPlay.HaveRemotePos && Vector3.Distance(NetPlay.LatestRemotePos, me) < 4f,
                    "host: guest Ori beside the host (distance " + (NetPlay.HaveRemotePos ? Vector3.Distance(NetPlay.LatestRemotePos, me).ToString("F2") : "?") + ")");
                GameController.Instance.SaveGameController.PerformSave();   // a host save online writes the co-op slot only
                CheckSaveIsolation(slot);
                yield return StartCoroutine(PauseRoomChecks());                     // T-189: pause menu -> 房间管理
                Shot("in_game");
                yield return StartCoroutine(TWait(8f));                             // let the guest finish its checks
                float doneBy = Time.realtimeSinceStartup + 45f;                     // T-189: and its pause menu checks
                while (Time.realtimeSinceStartup < doneBy && !File.Exists(GuestDoneFile)) yield return StartCoroutine(TWait(0.5f));
                if (!File.Exists(GuestDoneFile)) Note("guest pause checks not reported done within 45 s");
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
                if (m_tr == "cserver") yield return StartCoroutine(CsRoomListed());   // T-171: the host's room is on the list
                KeyboardOnlyP1.SetMenuDeviceForTest(-1);                            // T-188: the guest used keyboard/mouse on the menus
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
                    m_joinedCode = code;   // T-173: the room page's invite line must show the code this PC joined with
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
                bool sawStart = false, picked = false, csChecked = m_tr != "cserver";
                while (Time.realtimeSinceStartup < end && !(NetPlay.LocalInGameplay() && SaveSync.Ready))
                {
                    if (GuestFromTitle) sawStart = true;
                    if (!csChecked && CsRoomPage.IsOpen) { csChecked = true; yield return StartCoroutine(CsRoomPageChecks(1)); }
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
                // T-171: on a C-server join the guest's room screen must be the 房间管理页 and its checks must have run (an old
                // LAN room panel would also set m_roomSeen and silently skip them).
                Check(csChecked, "guest: the server room page (房间管理页) opened on the joined PC and was checked (transport " + m_tr + ")");
                Check(NetPlay.LocalInGameplay() && SaveSync.Ready, "guest: in game with the host's save (net=" + NetPlay.CurrentPhase + ", status=" + GuestStatus + ", aborts=" + GuestJoin.Aborts + " " + GuestJoin.LastAbort + ")");
                Check(SaveSync.JoinedHost && SaveSync.GuestJoinDistance < 3f, "guest: placed beside the host (distance " + SaveSync.GuestJoinDistance.ToString("F2") + ")");
                if (!full) Check(TitleChoice == TitleNone && !LastSlotPick.StartsWith("title-") && !LastSlotPick.StartsWith("guest-slot"), "guest: no save slot was picked (" + LastSlotPick + ")");
                yield return StartCoroutine(TWait(4f));
                CheckPrimaryInput("guest", -1, "T-188 keyboard/mouse used on the menus stays keyboard/mouse in game");
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
                Check(Saves.CoopSaveFolder.Active, "guest: online play uses the co-op save folder");
                Check(Saves.CoopSaveFolder.SinglePlayerSlotsWithSaves().Length == m_singleSaves.Count,
                    "guest: no single-player save file created online (" + m_singleSaves.Count + " -> "
                    + Saves.CoopSaveFolder.SinglePlayerSlotsWithSaves().Length + ")");
                Shot("in_game");
                yield return StartCoroutine(PauseRoomChecks());                     // T-189: pause menu -> 房间管理
                try { File.WriteAllText(GuestDoneFile, "done"); } catch (Exception e) { Note("guest done file: " + e.Message); }
            }

            // T-189: the pause menu's 房间管理. On a C-server: the item right above the game's quit item, pressing it opens the
            // room page in the game (no save / ready / start; copy the code, 踢出 for the manager with the other player picked,
            // 离开房间 asks first), and 返回 goes back to the pause menu with 房间管理 still selected. On LAN: no such item.
            IEnumerator PauseRoomChecks()
            {
                UI.Menu.ShowMenuScreen(true);
                float until = Time.realtimeSinceStartup + 5f;
                while (Time.realtimeSinceStartup < until && !(PauseScreen.Instance != null && PauseScreen.Instance.NavigationManager != null
                    && PauseScreen.Instance.NavigationManager.IsVisible)) yield return null;
                yield return StartCoroutine(TWait(1f));
                int index, quit; string selected;
                bool shown = PauseMenuEntry.RoomEntryShown(out index, out quit, out selected);
                if (m_tr != "cserver")
                {
                    Check(!shown && index < 0, m_role + ": LAN: the pause menu has no room entry (房间管理) (index " + index + ", quit " + quit + ")");
                    UI.Menu.HideMenuScreen(true);
                    yield return StartCoroutine(TWait(1f));
                    yield break;
                }
                Check(shown && index >= 1 && index == quit - 1, m_role + ": C-server: the pause menu has 房间管理 right above the game's quit item (index " + index + ", quit " + quit + ")");
                Note("pause menu: screen " + UI.Menu.CurrentScreen + ", visible " + UI.Menu.MainMenuVisible + ", list visible " + PauseScreen.Instance.NavigationManager.IsVisible
                    + ", items " + PauseScreen.Instance.NavigationManager.MenuItems.Count + ", selected '" + selected + "'");
                yield return StartCoroutine(TWait(1f));
                Shot("pause_room_entry");
                if (!shown) { UI.Menu.HideMenuScreen(true); yield break; }
                bool pressed = PauseMenuEntry.PressRoomEntry();
                yield return StartCoroutine(TWait(1f));
                Check(pressed && CsRoomPage.InGame && !PauseScreen.Instance.NavigationManager.IsVisible,
                    m_role + ": 房间管理 opens the room page in the game over the hidden pause menu (open " + CsRoomPage.InGame + ")");
                if (!CsRoomPage.InGame) { UI.Menu.HideMenuScreen(true); yield break; }
                string code = NetPlay.RoomCode ?? "";
                Ui.CsRoomModel m = CsRoomPage.Model;
                int mine = Players.LocalSeat, other = mine == 1 ? 2 : 1;
                // The manager's bar keeps 踢出 (off until the other player is picked); a guest has none.
                string bar = m_role == "host" ? "CopyCode,Kick(off),Back,Leave" : "CopyCode,Back,Leave";
                Check(CsRoomPage.BarActions == bar, m_role + ": in-game bar is " + bar + ", no save, ready or start (" + CsRoomPage.BarActions + ")");
                Check(code.Length == 6 && CsRoomPage.CodeShown == code, m_role + ": in-game page shows the room code " + code + " ('" + CsRoomPage.CodeLineShown + "')");
                Check(CsRoomPage.IpPortShown == m_cserverAddress && PlainRoomName(CsRoomPage.NameShown),
                    m_role + ": in-game page shows the server '" + CsRoomPage.IpPortShown + "' and the room name '" + CsRoomPage.NameShown + "'");
                Check(m.Row(mine).Status == L10n.T("csroom.local") && m.Row(other).Status == L10n.T("csroom.remote") && !m.Row(other).Empty && m.Row(3).Empty,
                    m_role + ": in-game rows: own P" + mine + " '" + m.Row(mine).Status + "', other P" + other + " '" + m.Row(other).Name + " / " + m.Row(other).Status + "'");
                yield return StartCoroutine(TWait(0.5f));
                Check(NativeGui.IconsShown("csroom.bar.0") >= 1 && NativeGui.IconsShown("csroom.bar.1") >= 1,
                    m_role + ": in-game bar keys show the game's button icons (" + CsRoomPage.BarIconMarkup + ")");
                Shot("ingame_room_page");
                CoopMenu.Open();   // F8 / a View hold on top of the page: refused (one pause hold, T-189 review)
                yield return null;
                Check(!CoopMenu.IsOpen && CsRoomPage.InGame, m_role + ": the co-op settings panel does not open over the in-game room page (panel " + CoopMenu.IsOpen + ")");
                if (CoopMenu.IsOpen) CoopMenu.Close();
                if (m_role == "host")
                {
                    m.ClickRow(other);   // pick the guest, as a click on its row does: 踢出 appears (not pressed)
                    Check(CsRoomPage.BarActions == "CopyCode,Kick,Back,Leave", "host: with the guest picked the bar offers 踢出 (" + CsRoomPage.BarActions + ")");
                    m.ClickRow(mine);
                    Check(CsRoomPage.BarActions == "CopyCode,Kick(off),Back,Leave", "host: never 踢出 on its own row (" + CsRoomPage.BarActions + ")");
                    m.ResetFocus();
                }
                else
                {
                    m.ClickRow(other);   // the host's row, picked as a click does
                    Check(!m.IsManager && CsRoomPage.BarActions == "CopyCode,Back,Leave" && m.Shortcut(Ui.CsRoomAction.Kick) == Ui.CsRoomAction.None,
                        "guest: not the manager: no 踢出 even with the host's row picked (" + CsRoomPage.BarActions + ")");
                    m.ResetFocus();
                }
                // Test runs record the copy without touching the real clipboard (shared with the owner's desktop).
                CsRoomPage.Run(m.Shortcut(Ui.CsRoomAction.CopyCode));
                yield return null;
                Check(CsRoomPage.Copied == code && CsRoomPage.CopiedShown,
                    m_role + ": 复制邀请码 copies just the code '" + CsRoomPage.Copied + "' and shows 已复制 (" + CsRoomPage.CopiedShown + ")");
                int left0 = CsRoomPage.LeftFromGame;
                CsRoomPage.Run(m.Shortcut(Ui.CsRoomAction.Leave));
                yield return null;
                Check(CsRoomPage.BarActions == "ConfirmLeave,Back" && CsRoomPage.InGame && NetPlay.Enabled && CsRoomPage.LeftFromGame == left0,
                    m_role + ": 离开房间 asks first, nothing left yet (" + CsRoomPage.BarActions + ")");
                Shot("ingame_room_leave_ask");
                CsRoomPage.Run(m.Shortcut(Ui.CsRoomAction.Back));   // 取消
                yield return null;
                Check(CsRoomPage.InGame && CsRoomPage.BarActions == bar, m_role + ": 取消 keeps the page and its bar (" + CsRoomPage.BarActions + ")");
                CsRoomPage.Run(m.Shortcut(Ui.CsRoomAction.Back));   // 返回
                yield return StartCoroutine(TWait(0.8f));
                Check(!CsRoomPage.IsOpen && PauseMenuEntry.RoomEntrySelected && UI.Menu.MainMenuVisible,
                    m_role + ": 返回 closes the page and the pause menu comes back with 房间管理 selected (open " + CsRoomPage.IsOpen + ")");
                yield return StartCoroutine(TWait(0.5f));   // PauseMenuEntry gives the pause menu back 0.3 s after Return
                UI.Menu.HideMenuScreen(true);
                yield return StartCoroutine(TWait(1f));
                // The pause menu really closes (a hold left behind keeps LockClosingMenu, and HideMenuScreen does nothing).
                Check(!UI.Menu.MainMenuVisible && NetPlay.Enabled && NetPlay.LocalInGameplay(),
                    m_role + ": the pause menu closes after the room page and the game goes on online (menu " + UI.Menu.MainMenuVisible + ", net=" + NetPlay.CurrentPhase + ")");
            }

            void DismissResume()
            {
                // T-059: the room page comes up before the game starts; press its drawn continue / start button.
                if (OnlineRoomScreen.Visible && OnlineRoomScreen.Click("room.continue") && !m_roomSeen) { m_roomSeen = true; Note("room screen: continue pressed"); }
                // T-171: on a C-server the 房间管理页 readies this PC; a joined player goes in when the creator starts.
                if (CsRoomPage.IsOpen && !m_roomSeen) { m_roomSeen = true; Note("cs room page shown"); }
                if (UI.MainMenuVisible && GameController.Instance != null && GameController.Instance.IsLoadingGame) { UI.Menu.HideResumeScreen(); UI.Menu.HideMenuScreen(true); }
            }

            bool m_finished, m_roomSeen;
            int m_pickedSlot = -1;

            // T-171: the guest's server room list shows the host's room (CsList through the C-server) before joining.
            IEnumerator CsRoomListed()
            {
                string code = "";
                float wait = Time.realtimeSinceStartup + 105f;   // T-176: the host leaves and hosts again before publishing its code
                while (Time.realtimeSinceStartup < wait && code.Length != 6)
                {
                    try { if (File.Exists(RoomFile)) code = File.ReadAllText(RoomFile).Trim(); } catch (Exception) { }
                    yield return StartCoroutine(TWait(0.5f));
                }
                bool listed = false;
                wait = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < wait && !listed)
                {
                    RefreshTexts(true);
                    // The row's label is the room's English name, its description starts with the room code (T-171).
                    foreach (string[] row in LobbyRows(L10n.Zh)) if (code.Length == 6 && (row[0].Contains(code) || row[1].Contains(code))) listed = true;
                    yield return StartCoroutine(TWait(0.5f));
                }
                Check(listed, "cserver: the host's room " + code + " is on the server's room list (" + Labels + ")");
                Shot("room_list");
            }

            // T-171: the 房间管理页 with both players: the other PC's player appears, local seats say (你), nobody is 房主.
            IEnumerator CsRoomPageChecks(int otherSeat)
            {
                float wait = Time.realtimeSinceStartup + 120f;
                while (Time.realtimeSinceStartup < wait && !(CsRoomPage.IsOpen && OriCoop.Authority.CServerLink.Client != null
                    && OriCoop.Authority.CServerLink.Client.SeatOnlineNow(otherSeat)))
                    yield return null;
                Check(CsRoomPage.IsOpen, m_role + ": 房间管理页 open on the C-server (" + Labels + ")");
                yield return StartCoroutine(TWait(2f));   // the other PC's lobby state (ping, room save) arrives every 2 s
                Ui.CsRoomModel m = CsRoomPage.Model;
                int mine = Players.LocalSeat;
                Ui.CsRoomRow own = m.Row(mine), other = m.Row(otherSeat);
                Check(!other.Empty && !other.Name.Contains(L10n.T("csroom.you")) && own.Name.EndsWith(L10n.T("csroom.you")),
                    m_role + ": room page rows: own P" + mine + " '" + own.Name + "', other P" + otherSeat + " '" + other.Name + "'");
                Check(m.Row(3).Empty && m.Row(4).Empty && m.Row(3).Name == L10n.T("csroom.waiting_player"),
                    m_role + ": empty seats wait for a player ('" + m.Row(3).Name + "')");
                Check(own.Ping.EndsWith(" ms"), m_role + ": own ping shown (" + own.Ping + ")");
                Check(m.IsManager == (m_role == "host"), m_role + ": only the room's creator manages it (manager P" + m.ManagerSeat + ")");
                Check(CsLobby.CreatedRoom == (m_role == "host") && (CsLobby.LocalSummary.Length > 0) == (m_role == "host"),
                    m_role + ": only the PC that created the room publishes its save (created=" + CsLobby.CreatedRoom + ", local save '" + CsLobby.LocalSummary + "')");
                Check(Ui.CsRoomSummary.Parse(CsLobby.RoomSummary).HasSave, m_role + ": the room's save is known on the page (" + CsLobby.RoomSummary + ")");
                // Owner playtest (T-173): the orb copy showed the game's difficulty picker; it must show only the save.
                Check(CsRoomPage.OrbMenus <= 0, m_role + ": the room page orb shows only the save, no menu inside (" + CsRoomPage.OrbMenus + ")");
                // The page shows the slot the host picked, not an older SaveSlotsManager slot (owner playtest, T-173).
                if (m_role == "host")
                    Check(m_pickedSlot >= 0 && Ui.CsRoomSummary.Parse(CsLobby.RoomSummary).Slot == m_pickedSlot,
                        "host: the room page shows the picked save slot " + (m_pickedSlot + 1) + " (" + CsLobby.RoomSummary + ")");
                // T-173 layout room-v2-a: room name above the orb, IP:Port and the invite code under it, [E] 改名 for the manager.
                string name = CsRoomPage.NameShown;
                Check(PlainRoomName(name), m_role + ": the room name is shown, English (ASCII) without markup ('" + name + "')");
                Check(m_cserverAddress != null && m_cserverAddress.Length > 0 && CsRoomPage.IpPortShown == m_cserverAddress,
                    m_role + ": the address line shows exactly the server this PC plays on, no label ('" + CsRoomPage.IpPortShown + "', test server '" + m_cserverAddress + "')");
                // A C-server room from the server list: the live room code (the host's: the code it wrote to RoomFile when it
                // created the room; the guest's: the one it typed to join).
                string expected = m_joinedCode;
                if (m_role == "host")
                {
                    expected = "";
                    try { if (File.Exists(RoomFile)) expected = File.ReadAllText(RoomFile).Trim(); } catch (Exception e) { Note("room file: " + e.Message); }
                }
                Check(expected.Length == 6 && CsRoomPage.CodeShown == expected && CsRoomPage.CodeLineShown.Contains(expected),
                    m_role + ": the invite line shows the room code " + expected + " ('" + CsRoomPage.CodeLineShown + "')");
                Check(CsRoomPage.CanRenameNow == (m_role == "host"), m_role + ": only the room's manager may rename (" + CsRoomPage.CanRenameNow + ")");
                if (m_role == "host")
                {
                    // The new name always differs from the room's name so far (a random two-word name may already be
                    // "Spirit Grove"): a guest showing it can only have it from the rename.
                    string target = name == RenameTo ? RenameToAlt : RenameTo;
                    bool shown = CsRoomPage.TestRename(target);
                    Check(shown && CsRoomPage.NameShown == target && !CsRoomPage.Renaming && Ui.CsRoomSummary.Parse(CsLobby.LocalSummary).Name == target,
                        "host: renamed the room from '" + name + "' to '" + target + "' through the page's confirm path (shown '" + CsRoomPage.NameShown + "', save line '" + CsLobby.LocalSummary + "')");
                    try { File.WriteAllText(RenameFile, target + "\n" + name); } catch (Exception e) { Note("rename file: " + e.Message); }
                    // The server's room info: Tick hands the renamed line to the C-server, and the server's room list (what
                    // the 服务器房间 page shows) carries the new name for this room's code.
                    float until = Time.realtimeSinceStartup + 3f;
                    while (Time.realtimeSinceStartup < until && ServerInfoName() != target) yield return null;
                    Check(ServerInfoName() == target, "host: the renamed line went to the C-server as the room info ('" + ServerInfoName() + "')");
                    string listed = null;
                    until = Time.realtimeSinceStartup + 10f;
                    while (Time.realtimeSinceStartup < until && listed != target)
                    {
                        string[] got = { null };
                        string address = m_cserverAddress, code = NetPlay.RoomCode ?? "";
                        System.Threading.ThreadPool.QueueUserWorkItem(delegate { got[0] = ListedName(address, code); });
                        float give = Time.realtimeSinceStartup + 5f;
                        while (got[0] == null && Time.realtimeSinceStartup < give) yield return null;
                        listed = got[0];
                        if (listed != target) yield return StartCoroutine(TWait(0.5f));
                    }
                    Check(listed == target, "host: the server's room list shows room " + NetPlay.RoomCode + " as '" + target + "' after the rename ('" + listed + "')");
                }
                else
                {
                    // The host renames once it sees this PC on its page and writes "new name\nold name" to the marker; the
                    // new name (never the room's earlier name) must then come through LobbyState within ~6 s.
                    string target = "", before = "";
                    float until = Time.realtimeSinceStartup + 90f;
                    while (Time.realtimeSinceStartup < until && target.Length == 0)
                    {
                        try
                        {
                            string[] lines = File.Exists(RenameFile) ? File.ReadAllText(RenameFile).Split('\n') : new string[0];
                            if (lines.Length >= 2 && lines[0].Trim().Length > 0) { target = lines[0].Trim(); before = lines[1].Trim(); }
                        }
                        catch (Exception) { }
                        if (target.Length == 0) yield return StartCoroutine(TWait(0.25f));
                    }
                    Check(target.Length > 0 && target != before, "guest: the host renamed the room from '" + before + "' to '" + target + "' (marker " + RenameFile + " within 90 s)");
                    if (target.Length > 0)
                    {
                        until = Time.realtimeSinceStartup + 6f;
                        while (Time.realtimeSinceStartup < until && CsRoomPage.IsOpen && CsRoomPage.NameShown != target) yield return null;
                        name = CsRoomPage.NameShown;
                        Check(name == target && name != before && PlainRoomName(name), "guest: the host's new room name reached this PC within 6 s ('" + name + "', expected '" + target + "', room save '" + CsLobby.RoomSummary + "')");
                    }
                }
                // Owner playtest (T-173, 2026-11-22): the key bar is the game's own legend look, with the game's button icons
                // (keyboard keys, then the controller's buttons after switching to the controller), not drawn keycaps.
                GameSettings settings = GameSettings.Instance;
                ControlScheme scheme = settings.CurrentControlScheme;
                settings.CurrentControlScheme = settings.KeyboardScheme == ControlScheme.Controller ? ControlScheme.KeyboardAndMouse : settings.KeyboardScheme;
                yield return StartCoroutine(CsBarIcons("keyboard"));
                string keyboardIcons = CsRoomPage.BarIconMarkup;
                Shot("room_page");
                settings.CurrentControlScheme = ControlScheme.Controller;
                yield return StartCoroutine(CsBarIcons("controller"));
                Check(CsRoomPage.BarIconMarkup != keyboardIcons, m_role + ": the key bar switches to the controller's icons ('" + keyboardIcons + "' -> '" + CsRoomPage.BarIconMarkup + "')");
                Shot("room_page_pad");
                settings.CurrentControlScheme = scheme;
            }

            IEnumerator CsBarIcons(string device)
            {
                yield return StartCoroutine(TWait(0.5f));   // the bar redraws with the device's icons
                int items = CsRoomPage.BarItems, missing = 0;
                string counts = "";
                for (int i = 0; i < items; i++)
                {
                    int n = NativeGui.IconsShown("csroom.bar." + i);
                    counts += (i > 0 ? "," : "") + n;
                    if (n < 1) missing++;
                }
                Check(items >= 2 && missing == 0, m_role + ": " + device + ": every key bar item shows the game's own button icon (" + items + " items, icons shown " + counts + ", markup '" + CsRoomPage.BarIconMarkup + "')");
                if (device == "keyboard" && m_role == "host")
                    Check(CsRoomPage.RenameHintShown && NativeGui.IconsShown("csroom.rename") >= 1,
                        "host: keyboard: the rename hint shows the game's key icon (hint " + CsRoomPage.RenameHintShown + ", icons " + NativeGui.IconsShown("csroom.rename") + ")");
            }

            const string RenameTo = "Spirit Grove", RenameToAlt = "Misty Wells";
            string m_joinedCode = "";

            // The room info text this PC's link last handed the C-server, parsed: its room name ("" = none).
            static string ServerInfoName()
            {
                OriCoop.Authority.CServerClient c = OriCoop.Authority.CServerLink.Client;
                return c != null && c.RoomInfo != null ? Ui.CsRoomSummary.Parse(c.RoomInfo).Name : "";
            }

            // On a worker: the server's room list (CsList, as the 服务器房间 page fetches it), the name of the room with this
            // code. "" = not listed or no list (never null: the caller waits for non-null).
            static string ListedName(string address, string code)
            {
                try
                {
                    System.Net.EndPoint server = OriCoop.Authority.CServerLink.Resolve((address ?? "").Trim());
                    string error;
                    List<OriCoop.Authority.CsRoomListEntry> rooms = server != null ? OriCoop.Authority.CServerRoomList.Fetch(server, 3000, out error) : null;
                    if (rooms != null)
                        foreach (OriCoop.Authority.CsRoomListEntry room in rooms)
                            if (room.Code == code) return Ui.CsRoomSummary.Parse(room.Text).Name;
                }
                catch (Exception) { }
                return "";
            }
            string RenameFile { get { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_out)), "titlejoin-rename.txt"); } }

            // A room name as the page may show it: English only (printable ASCII), no MessageBox markup, already clean.
            static bool PlainRoomName(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                foreach (char c in name)
                    if (c < ' ' || c > '~' || c == '*' || c == '<' || c == '>' || c == '#' || c == ';' || c == '=') return false;
                return name == Ui.RoomNames.Clean(name);
            }
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
