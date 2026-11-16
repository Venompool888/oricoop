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
                yield return StartCoroutine(TWait(1.5f));
                yield return StartCoroutine(PressCoop());
                int row = Array.IndexOf(TitleTransports, m_tr == "cserver" ? "relay" : m_tr);
                yield return StartCoroutine(Row(row, 1f));
                if (m_tr == "relay" || m_tr == "cserver")
                {
                    Check(TitleOrbOpen && LastPage == "servermode" && LastLabels.Count == 2 && s_mgr.Index == 0,
                        m_tr + ": server mode choice defaults to equal mode (" + Labels + ")");
                    yield return StartCoroutine(Row(m_tr == "cserver" ? 0 : 1, 1f));
                }
                Check(TitleOrbOpen && LastPage == "role", m_tr + ": host / join page on the title (" + Labels + ")");
                if (m_role == "host") yield return StartCoroutine(Host());
                else yield return StartCoroutine(Join(savesAtStart));
                if (SignMode && m_fails == 0) yield return StartCoroutine(SignOnline());          // TitleInputTest.cs
                if (LeaveMode.Length > 0 && m_fails == 0) yield return StartCoroutine(Leave());
                Finish();
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
                    && ((m_tr != "relay" && m_tr != "cserver") || !string.IsNullOrEmpty(NetPlay.RoomCode)))) yield return null;
                Check(IsOpen && LastPage == "hosting", "host: slot pick opens the host page (" + Labels + ", net=" + NetPlay.CurrentPhase + ")");
                if (m_tr == "relay" || m_tr == "cserver")
                {
                    Check(!string.IsNullOrEmpty(NetPlay.RoomCode), "host: room code " + NetPlay.RoomCode);
                    try { File.WriteAllText(RoomFile, NetPlay.RoomCode ?? ""); } catch (Exception e) { Note("room file: " + e.Message); }
                }
                yield return StartCoroutine(TWait(1f));
                Shot("host_page");
                yield return StartCoroutine(TWait(0.5f));
                yield return StartCoroutine(Row(1, 0.5f));                          // 开始游戏
                end = Time.realtimeSinceStartup + 90f;
                while (Time.realtimeSinceStartup < end && !NetPlay.LocalInGameplay()) { DismissResume(); yield return null; }
                Check(NetPlay.LocalInGameplay(), "host: in game after 开始游戏");
                end = Time.realtimeSinceStartup + 150f;
                while (Time.realtimeSinceStartup < end && !(NetPlay.PartnerHere && SaveSync.Ready)) { DismissResume(); yield return null; }
                Check(NetPlay.PartnerHere && SaveSync.Ready, "host: guest joined and loaded (net=" + NetPlay.CurrentPhase + ")");
                yield return StartCoroutine(TWait(4f));
                Vector3 me = Characters.Sein != null ? Characters.Sein.Position : Vector3.zero;
                Check(NetPlay.HaveRemotePos && Vector3.Distance(NetPlay.LatestRemotePos, me) < 4f,
                    "host: guest Ori beside the host (distance " + (NetPlay.HaveRemotePos ? Vector3.Distance(NetPlay.LatestRemotePos, me).ToString("F2") : "?") + ")");
                Shot("in_game");
                yield return StartCoroutine(TWait(8f));                             // let the guest finish its checks
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
                    LoadAddress("127.0.0.1", NetPlay.ConfigPort);
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
                    s_entry = code; s_cursor = Math.Min(code.Length, EntryMax - 1);
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
                Check(NetPlay.LocalInGameplay() && SaveSync.Ready, "guest: in game with the host's save (net=" + NetPlay.CurrentPhase + ", status=" + GuestStatus + ", aborts=" + GuestJoin.Aborts + " " + GuestJoin.LastAbort + ")");
                Check(SaveSync.JoinedHost && SaveSync.GuestJoinDistance < 3f, "guest: placed beside the host (distance " + SaveSync.GuestJoinDistance.ToString("F2") + ")");
                if (!full) Check(TitleChoice == TitleNone && !LastSlotPick.StartsWith("title-") && !LastSlotPick.StartsWith("guest-slot"), "guest: no save slot was picked (" + LastSlotPick + ")");
                yield return StartCoroutine(TWait(4f));
                Vector3 me = Characters.Sein != null ? Characters.Sein.Position : Vector3.zero;
                Check(NetPlay.HaveRemotePos && Vector3.Distance(NetPlay.LatestRemotePos, me) < 4f,
                    "guest: still beside the host (distance " + (NetPlay.HaveRemotePos ? Vector3.Distance(NetPlay.LatestRemotePos, me).ToString("F2") : "?") + ")");
                Check(CoopSaves.GuestSlot == (full ? 2 : CoopSaves.GuestSlot) && CoopSaves.GuestSlot >= 0 && SaveFiles() >= 1, "guest: co-op copy kept in slot " + CoopSaves.GuestSlot + " (" + SaveFiles() + " save file(s))");
                Shot("in_game");
            }

            void DismissResume()
            {
                if (UI.MainMenuVisible && GameController.Instance != null && GameController.Instance.IsLoadingGame) { UI.Menu.HideResumeScreen(); UI.Menu.HideMenuScreen(true); }
            }

            bool m_finished;
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
