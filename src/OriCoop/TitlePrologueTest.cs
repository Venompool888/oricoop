using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game;
using UnityEngine;

namespace OriCoop
{
    // T-203: the shared new-game prologue, with two real clients through the real menus
    // (src\run-titlejoin.ps1 -Prologue skip|leave; ORICOOP_TITLEJOIN_PROLOGUE). LAN (direct), and for skip also the
    // C-server (-Transport cserver: the room page's start sends both PCs in at the same moment, the playtest-4 path).
    //   host:  多人游玩 -> 开房 -> an EMPTY slot -> difficulty -> host page -> 开始游戏 once the guest waits
    //   guest: 多人游玩 -> 加入 -> connects, waits on the title for the host's save (C-server: ready on the room page)
    // skip:  the guest plays the prologue along (not the waiting screen), in step with the host; its own input moves
    //        nothing, the host's input moves Naru on both PCs; the guest cannot skip; the host's skip ends it for
    //        both and both are in the first playable scene with the host's save.
    // leave: the guest leaves from its pause menu mid-prologue (back on the title, the host goes on); it joins again:
    //        now it is late, keeps the waiting screen (which says why) past the "no save" limit; the host skips and
    //        the late guest loads the host's save.
    // The expected values come from docs\tickets\T-203.md (设计), not from the code under test.
    public static partial class NativeModeMenu
    {
        partial class TitleJoinTest
        {
            static string PrologueMode { get { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_PROLOGUE") ?? ""; } }

            // In step: the same milestone on both PCs within this many seconds (ticket: 一起播放; no timeline seek, so a
            // PC that stalls on loading may trail by a moment). The start itself: within StartLimit.
            const float StepLimit = 3f, StartLimit = 1.5f;
            const string FirstScene = "swallowsNestStartOfLeafJourney", GameScene = "sunkenGladesRunaway";

            // ---- values shared between the two games (files next to the result file) ------------------------------
            readonly Dictionary<string, string> m_pOwn = new Dictionary<string, string>();
            string PFile(string role) { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_out)), "titlejoin-prologue-" + role + ".txt"); }
            string PeerRole { get { return m_role == "host" ? "join" : "host"; } }
            static double Utc() { return DateTime.UtcNow.Ticks / 1e7; }
            static string Num(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
            // The other game reads this file every frame while it waits: a write can collide with its read (seen once: a
            // sharing violation lost a value and both games waited for each other). So: the whole set is written to a
            // temporary file and moved into place, a failed write is tried again every frame until it is through, and the
            // reader opens the file without locking the writer out.
            bool m_pRetrying;
            void Put(string key, string value)
            {
                m_pOwn[key] = value;
                if (!WriteOwn() && !m_pRetrying) StartCoroutine(RetryWrite());
            }
            bool WriteOwn()
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> p in m_pOwn) lines.Add(p.Key + "=" + p.Value);
                try
                {
                    string path = PFile(m_role), tmp = path + ".tmp";
                    File.WriteAllText(tmp, string.Join("\n", lines.ToArray()) + "\n");
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return true;
                }
                catch (Exception) { return false; }
            }
            IEnumerator RetryWrite()
            {
                m_pRetrying = true;
                float give = Time.realtimeSinceStartup + 20f;
                while (!WriteOwn() && Time.realtimeSinceStartup < give) yield return null;
                if (Time.realtimeSinceStartup >= give) Note("prologue file: could not be written for 20 s");
                m_pRetrying = false;
            }
            void Put(string key, double value) { Put(key, Num(value)); }
            string Peer(string key)
            {
                try
                {
                    if (!File.Exists(PFile(PeerRole))) return null;
                    string text;
                    using (FileStream fs = new FileStream(PFile(PeerRole), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(fs)) text = reader.ReadToEnd();
                    foreach (string line in text.Split('\n'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && line.Substring(0, eq) == key) return line.Substring(eq + 1);
                    }
                }
                catch (Exception) { }   // the other game is replacing it right now: read again next frame
                return null;
            }
            double PeerNum(string key)
            {
                string s = Peer(key); double v;
                return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
            }
            IEnumerator WaitPeer(string key, float timeout)
            {
                float end = Time.realtimeSinceStartup + timeout;
                while (Time.realtimeSinceStartup < end && Peer(key) == null) yield return null;
                if (Peer(key) == null) Note("prologue: the " + PeerRole + " never reported '" + key + "' (" + timeout + " s)");
            }
            // Both games reach the same point: each reports it, then waits for the other.
            IEnumerator Together(string name, float timeout) { Put(name, Utc()); yield return StartCoroutine(WaitPeer(name, timeout)); }

            static bool InPrologueState { get { return GameStateMachine.Instance != null && GameStateMachine.Instance.CurrentState == GameStateMachine.State.Prologue; } }
            static string SceneNow { get { return Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null ? Core.Scenes.Manager.CurrentScene.Scene : ""; } }
            static bool NaruPlayable { get { return Characters.Naru != null && object.ReferenceEquals(Characters.Current, Characters.Naru); } }
            static float NaruX { get { return Characters.Naru != null ? Characters.Naru.Position.x : float.NaN; } }

            // Reports when this game reaches each milestone (UTC seconds), for the other game to compare with.
            IEnumerator Milestones(float timeout, bool untilNaru)
            {
                float end = Time.realtimeSinceStartup + timeout;
                bool state = false, scene = false, naru = false;
                while (Time.realtimeSinceStartup < end && !(state && scene && (naru || !untilNaru)))
                {
                    if (!state && InPrologueState) { state = true; Put("m.state", Utc()); Note("prologue state reached"); }
                    if (!scene && SceneNow == FirstScene) { scene = true; Put("m.scene", Utc()); Note("scene " + FirstScene); }
                    if (!naru && NaruPlayable) { naru = true; Put("m.naru", Utc()); Note("Naru is the current character at x " + NaruX.ToString("F2")); }
                    yield return null;
                }
            }

            void CheckStep(string key, string what)
            {
                double mine = m_pOwn.ContainsKey(key) ? double.Parse(m_pOwn[key], CultureInfo.InvariantCulture) : double.NaN, theirs = PeerNum(key);
                double gap = Math.Abs(mine - theirs);
                Check(!double.IsNaN(gap) && gap <= StepLimit, "guest: " + what + " within " + StepLimit + " s of the host ("
                    + (double.IsNaN(gap) ? "missing: guest " + mine + ", host " + theirs : (mine - theirs).ToString("F2") + " s after it") + ")");
            }

            // ---- host ------------------------------------------------------------------------------------------

            IEnumerator PrologueHostRun()
            {
                string mode = PrologueMode;
                Note("--- shared prologue: " + mode + " (host)");
                KeyboardOnlyP1.SetMenuDeviceForTest(HostMenuPad);
                yield return StartCoroutine(Row(0, 0.2f));                          // 开房 -> save slots
                yield return StartCoroutine(WaitFor(() => SlotsReady, 8f));
                yield return StartCoroutine(TWait(0.8f));
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                int slot = Saves.CoopSaveFolder.FirstEmptyCoopSlot();
                Check(SlotsReady && ui != null && slot >= 0 && !SaveSlotsManager.SlotExists(slot), "host: 开房 -> save slots, slot " + slot + " is empty");
                if (ui == null || !SlotsReady || slot < 0) yield break;
                ui.SetCurrentItem(slot);
                ui.EmptySaveSlotSelected();                                        // A on an empty slot: the game's difficulty screen
                yield return StartCoroutine(TWait(0.6f));
                Check(ui.SelectingDifficulty, "host: the empty slot asks for the difficulty");
                ui.SetDifficulty(DifficultyMode.Easy);                             // the difficulty item's action (SetDifficultyAction)
                yield return StartCoroutine(WaitFor(() => LastPage == "hosting" && (NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected), 20f));
                Check(IsOpen && LastPage == "hosting" && SharedPrologue.HostStarts == 0 && !InPrologueState,
                    "host: new game on an empty slot opens the host page, nothing plays yet (" + Labels + ", net=" + NetPlay.CurrentPhase + ")");
                if (LastPage != "hosting") yield break;
                if (m_tr == "cserver")
                {
                    yield return StartCoroutine(WaitFor(() => !string.IsNullOrEmpty(NetPlay.RoomCode), 20f));
                    Check(!string.IsNullOrEmpty(NetPlay.RoomCode), "host: the C-server gave a room code");
                    if (string.IsNullOrEmpty(NetPlay.RoomCode)) yield break;
                    try { File.WriteAllText(RoomFile, NetPlay.RoomCode); } catch (Exception e) { Note("room file: " + e.Message); }
                }
                yield return StartCoroutine(Row(1, 0.5f));                          // 开始游戏 -> the room screen, until the guest is there
                yield return StartCoroutine(WaitFor(() => OnlineRoomScreen.Visible, 5f));
                yield return StartCoroutine(WaitPeer("waiting", 180f));
                Check(Peer("waiting") != null && NetPlay.PartnerHere && OnlineRoomScreen.Visible,
                    "host: the guest is connected and " + (m_tr == "cserver" ? "ready on the room page" : "waits for the host's save"));
                if (Peer("waiting") == null) yield break;
                yield return StartCoroutine(TWait(1f));
                float end = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < end && OnlineRoomScreen.Visible) { OnlineRoomScreen.Click("room.continue"); yield return null; }
                Put("start", Utc());
                yield return null;
                Check(SharedPrologue.HostStarts == 1 && SharedPrologue.HostAnnouncing, "host: the new game announces its prologue at once (starts " + SharedPrologue.HostStarts + ")");
                yield return StartCoroutine(Milestones(150f, mode == "skip"));
                Check(InPrologueState, "host: the state machine is in the Prologue state");
                // Ticket 11: the prologue is not co-op gameplay; no checkpoint leaves the host in the middle of it.
                Check(!NetPlay.LocalInGameplay() && !SaveSync.HaveCheckpointPosition, "host: not 'in gameplay' during the prologue, no checkpoint sent");
                if (!InPrologueState) yield break;
                if (mode == "hostquit") { yield return StartCoroutine(PrologueHostQuits()); yield break; }
                if (mode == "leave") yield return StartCoroutine(PrologueHostLeave());
                else yield return StartCoroutine(PrologueHostControl());
                if (m_fails != 0) yield break;

                // ---- only P1 skips: the pause menu's skip item performs SkipCutsceneController.SkipCutscene
                yield return StartCoroutine(Together("skip.ready", 120f));
                Check(SkipCutsceneController.Instance != null && SkipCutsceneController.Instance.SkippingAvailable, "host: P1 can skip the prologue");
                Put("skip", Utc());
                SkipCutsceneController.Instance.SkipCutscene();
                end = Time.realtimeSinceStartup + 90f;
                while (Time.realtimeSinceStartup < end && !NetPlay.LocalInGameplay()) { DismissResume(); yield return null; }
                Check(NetPlay.LocalInGameplay() && !InPrologueState, "host: in game after its skip");
                Check(!SharedPrologue.HostAnnouncing && SharedPrologue.HostStarts == 1, "host: the prologue is announced as over (one prologue in this session)");
                end = Time.realtimeSinceStartup + 150f;
                while (Time.realtimeSinceStartup < end && !(NetPlay.PartnerHere && SaveSync.Ready)) { DismissResume(); yield return null; }
                Check(NetPlay.PartnerHere && SaveSync.Ready, "host: the guest joined and loaded the host's save (net=" + NetPlay.CurrentPhase + ")");
                Check(SceneNow == GameScene, "host: first playable scene (" + SceneNow + ")");
                yield return StartCoroutine(TWait(4f));
                Vector3 me = Characters.Sein != null ? Characters.Sein.Position : Vector3.zero;
                Check(NetPlay.HaveRemotePos && Vector3.Distance(NetPlay.LatestRemotePos, me) < 6f,
                    "host: the guest's Ori is beside the host (distance " + (NetPlay.HaveRemotePos ? Vector3.Distance(NetPlay.LatestRemotePos, me).ToString("F2") : "?") + ")");
                // A brand-new game has no save file yet ("commit (no file yet)"): the host's first save point writes the
                // room's slot and commits it to the guest. Then both ends can name the same save.
                float until = Time.realtimeSinceStartup + 8f;
                while (Time.realtimeSinceStartup < until && string.IsNullOrEmpty(CoopSaves.Timeline)) yield return null;
                if (string.IsNullOrEmpty(CoopSaves.Timeline)) GameController.Instance.SaveGameController.PerformSave();
                until = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < until && string.IsNullOrEmpty(CoopSaves.Timeline)) yield return null;
                Check(!string.IsNullOrEmpty(CoopSaves.Timeline) && CoopSaves.CoopId.Length == 32 && SaveSlotsManager.SlotExists(slot),
                    "host: the new game's save is in the slot it picked (slot " + slot + ", lineage " + CoopSaves.CoopId + ", hash " + CoopSaves.Timeline + ")");
                Put("coop", CoopSaves.CoopId ?? "");
                Put("timeline", CoopSaves.Timeline ?? "");
                Shot("prologue_in_game");
                yield return StartCoroutine(WaitPeer("done", 60f));                 // the guest finishes its checks while the session is up
            }

            // The playable part: the host's input moves Naru here (and, checked by the guest, on the guest's PC).
            IEnumerator PrologueHostControl()
            {
                Check(NaruPlayable, "host: Naru is playable");
                if (!NaruPlayable) yield break;
                // First the guest presses its own keys while the host's are neutral; the guest checks nothing moves there.
                yield return StartCoroutine(Together("own.start", 60f));
                yield return StartCoroutine(WaitPeer("own.done", 30f));
                // Then the host walks: hold a direction until Naru has moved (control is handed over a few seconds after
                // the camera; the cliff allows only one direction).
                yield return StartCoroutine(Together("walk.start", 60f));
                float x0 = NaruX, moved = 0f;
                float end = Time.realtimeSinceStartup + 25f; int dir = 1; float flipAt = Time.realtimeSinceStartup + 4f;
                SharedPrologue.TestInput = true;
                while (Time.realtimeSinceStartup < end && Math.Abs(moved) < 4f)
                {
                    if (Time.realtimeSinceStartup >= flipAt && Math.Abs(moved) < 0.5f) { dir = -dir; flipAt = Time.realtimeSinceStartup + 4f; }
                    SharedPrologue.TestHorizontal = dir;
                    moved = NaruX - x0;
                    yield return null;
                }
                SharedPrologue.TestHorizontal = 0f;
                yield return StartCoroutine(TWait(2.5f));                           // at rest: the guest's copy settles on the same spot
                SharedPrologue.TestInput = false;
                Check(Math.Abs(NaruX - x0) > 1.5f && InPrologueState, "host: P1's input walks Naru (" + (NaruX - x0).ToString("F2") + " units, direction " + dir + ")");
                Put("walk.from", x0); Put("walk.x", NaruX); Put("walk.done", Utc());
                Check(SharedPrologue.InputsSent > 0, "host: its input was sent to the guests (" + SharedPrologue.InputsSent + " messages)");
                Shot("prologue_naru");
                yield return StartCoroutine(WaitPeer("walk.checked", 30f));
            }

            // The host quits to the title from its pause menu while the guest plays along.
            IEnumerator PrologueHostQuits()
            {
                yield return StartCoroutine(WaitPeer("watching", 60f));
                Check(Peer("watching") != null && NetPlay.PartnerHere, "hostquit: the guest plays along before the host quits");
                yield return StartCoroutine(TWait(3f));
                Put("quit", Utc());
                yield return StartCoroutine(QuitFromPause());                       // pause menu -> quit to the title, as a player does
                yield return StartCoroutine(CheckTitleInput("host quit its prologue"));
                Check(!SharedPrologue.HostAnnouncing && !InPrologueState && SharedPrologue.HostStarts == 1, "hostquit: nothing of the prologue is left on the host's title");
                yield return StartCoroutine(WaitPeer("done", 60f));
            }

            // The guest leaves mid-prologue and joins again late: the host just goes on.
            IEnumerator PrologueHostLeave()
            {
                yield return StartCoroutine(WaitPeer("left", 120f));
                yield return StartCoroutine(WaitFor(() => !NetPlay.PartnerHere, 20f));
                yield return StartCoroutine(TWait(2f));
                Check(Peer("left") != null && !NetPlay.PartnerHere && InPrologueState && NetPlay.Enabled && SharedPrologue.HostAnnouncing && !NetPlay.LocalInGameplay(),
                    "leave: the guest left; the host's prologue goes on, still hosting (partner " + NetPlay.PartnerHere + ", net=" + NetPlay.CurrentPhase + ")");
                yield return StartCoroutine(WaitPeer("late.waiting", 180f));
                Check(Peer("late.waiting") != null && NetPlay.PartnerHere && InPrologueState, "leave: the guest is back in the session while the prologue still plays");
                yield return StartCoroutine(WaitPeer("late.checked", 60f));           // the late guest waits past its no-save limit
                Check(InPrologueState && !SaveSync.HaveCheckpointPosition, "leave: still in the prologue, still no checkpoint sent");
            }

            // ---- guest -----------------------------------------------------------------------------------------

            // This guest is where it stays until the host starts (or, for a game already running, until its save comes).
            // LAN: it pressed the room screen's continue and waits on the title for the host's save. C-server: it is ready
            // on the room page (房间管理页); the manager's start sends every PC in at the same moment, as in playtest 4.
            bool m_pGuestParked;

            // From the host / join page: 加入, the address (LAN: loopback) or the room code (C-server), 连接, the room screen.
            IEnumerator PrologueConnect(string who, bool gameRunning)
            {
                KeyboardOnlyP1.SetMenuDeviceForTest(-1);
                m_pGuestParked = false;
                bool server = m_tr == "cserver";
                string code = "";
                if (server)
                {
                    float wait = Time.realtimeSinceStartup + 120f;
                    while (Time.realtimeSinceStartup < wait && code.Length != 6)
                    {
                        try { if (File.Exists(RoomFile)) code = File.ReadAllText(RoomFile).Trim(); } catch (Exception) { }
                        yield return StartCoroutine(TWait(0.5f));
                    }
                    Check(code.Length == 6, who + ": got the host's room code");
                    if (code.Length != 6) yield break;
                }
                yield return StartCoroutine(Row(1, 1.2f));                          // 加入 -> entry page
                Check(TitleOrbOpen && LastPage == "entry", who + ": join page on the title (" + Labels + ")");
                if (LastPage != "entry") yield break;
                if (!server) LoadAddress("", NetPlay.ConfigPort);
                s_mgr.SetCurrentItem(0);
                yield return StartCoroutine(TWait(0.4f));
                yield return StartCoroutine(DialType(server ? code : "127000000001"));
                RefreshTexts(true);
                yield return StartCoroutine(TWait(0.5f));
                yield return StartCoroutine(Row(server ? 1 : 2, 0.5f));             // 连接
                float end = Time.realtimeSinceStartup + 90f;
                // A game already running (the late join): the C-server page lets this PC in on its own, like the LAN continue.
                bool untilWaiting = !server || gameRunning;
                while (Time.realtimeSinceStartup < end && !(untilWaiting ? GuestJoin.Waiting : CsRoomPage.IsOpen && CsLobby.LocalReady && NetPlay.PartnerHere))
                {
                    if (OnlineRoomScreen.Visible) OnlineRoomScreen.Click("room.continue");
                    yield return null;
                }
                m_pGuestParked = untilWaiting ? GuestJoin.Waiting : CsRoomPage.IsOpen && CsLobby.LocalReady;
                Check(m_pGuestParked && NetPlay.PartnerHere, who + ": connected, " + (untilWaiting ? "waiting on the title for the host's save" : "ready on the room page")
                    + " (net=" + NetPlay.CurrentPhase + ")");
            }

            IEnumerator PrologueJoinRun(int savesAtStart)
            {
                string mode = PrologueMode;
                Note("--- shared prologue: " + mode + " (guest)");
                Check(savesAtStart == 0, "guest: appdata has no saves (" + savesAtStart + ")");
                yield return StartCoroutine(PrologueConnect("guest", false));
                if (!m_pGuestParked) yield break;
                int titleReturns = TitleReturn.SessionsEnded;
                Check(SharedPrologue.LocalStarts == 0 && !SharedPrologue.HostPlaying, "guest: nothing plays before the host starts");
                Put("waiting", Utc());

                // ---- the host starts its new game: the same prologue plays here, not the waiting screen
                yield return StartCoroutine(WaitPeer("start", 120f));
                yield return StartCoroutine(WaitFor(() => SharedPrologue.Watching, 5f));
                yield return StartCoroutine(TWait(0.5f));                           // the transfer screen's phase follows on the next tick
                Check(SharedPrologue.Watching && SharedPrologue.LocalStarts == 1,
                    "guest: plays the host's prologue along (copies started " + SharedPrologue.LocalStarts + ", host in prologue " + SharedPrologue.HostPlaying + ")");
                Check(SessionScreens.Phase == SessionScreens.SavePhase.None && !SessionScreens.Blocking,
                    "guest: no 'waiting for the host's save' screen while it plays (" + SessionScreens.Phase + ")");
                Check(GuestJoin.Waiting && NetPlay.Enabled && GuestJoin.Aborts == 0, "guest: the join itself is still waiting for the host's save");
                if (!SharedPrologue.Watching) yield break;
                Check(SharedPrologue.LastStartDelay >= 0f && SharedPrologue.LastStartDelay <= StartLimit,
                    "guest: its copy started " + SharedPrologue.LastStartDelay.ToString("F2") + " s after the host's (limit " + StartLimit + " s)");
                Check(DifficultyController.Instance.Difficulty == DifficultyMode.Easy, "guest: its copy has the host's difficulty (" + DifficultyController.Instance.Difficulty + ")");
                yield return StartCoroutine(Milestones(150f, mode == "skip"));
                yield return StartCoroutine(WaitPeer("m.scene", 30f));
                Check(InPrologueState && SharedPrologue.Watching, "guest: its state machine is in the Prologue state");
                CheckStep("m.state", "the Prologue state");
                CheckStep("m.scene", "scene " + FirstScene);
                Check(!NetPlay.LocalInGameplay() && Characters.Sein == null, "guest: no Ori and not 'in gameplay' during the prologue");
                Shot("prologue_playing");
                if (m_fails != 0) yield break;
                if (mode == "hostquit") { yield return StartCoroutine(PrologueJoinHostQuits()); yield break; }
                if (mode == "leave") { yield return StartCoroutine(PrologueJoinLeave()); yield break; }

                yield return StartCoroutine(WaitPeer("m.naru", 30f));
                CheckStep("m.naru", "Naru playable");
                if (!NaruPlayable) yield break;

                // ---- P2 cannot control: this PC's own keys (right + jump) for 3 s move nothing
                yield return StartCoroutine(Together("own.start", 60f));
                yield return StartCoroutine(TWait(6f));                             // control has been handed over (a few seconds after the camera)
                float x0 = NaruX, y0 = Characters.Naru.Position.y, maxDx = 0f, maxDy = 0f;
                SharedPrologue.TestInput = true; SharedPrologue.TestHorizontal = 1f;
                float end = Time.realtimeSinceStartup + 3f; int flip = 0;
                while (Time.realtimeSinceStartup < end)
                {
                    SharedPrologue.TestJump = (flip++ / 20) % 2 == 0;              // press and release, so new presses keep coming
                    maxDx = Math.Max(maxDx, Math.Abs(NaruX - x0)); maxDy = Math.Max(maxDy, Math.Abs(Characters.Naru.Position.y - y0));
                    yield return null;
                }
                SharedPrologue.TestInput = false; SharedPrologue.TestHorizontal = 0f; SharedPrologue.TestJump = false;
                Check(maxDx < 0.3f && maxDy < 0.3f, "guest: its own right + jump keys do not move Naru (moved " + maxDx.ToString("F2") + " / " + maxDy.ToString("F2") + " units)");
                Put("own.done", Utc());

                // ---- the host walks Naru: the guest's Naru follows and ends on the same spot
                yield return StartCoroutine(Together("walk.start", 60f));
                float before = NaruX;
                yield return StartCoroutine(WaitPeer("walk.done", 60f));
                yield return StartCoroutine(TWait(1f));
                double from = PeerNum("walk.from"), hostX = PeerNum("walk.x");
                Check(!double.IsNaN(hostX) && Math.Abs(NaruX - before) > 1.5f && Math.Abs(NaruX - hostX) < 1f,
                    "guest: Naru follows the host's input (host " + from.ToString("F2") + " -> " + hostX.ToString("F2") + ", here " + before.ToString("F2") + " -> " + NaruX.ToString("F2")
                    + ", inputs applied " + SharedPrologue.InputsApplied + ", position corrections " + SharedPrologue.Snaps + ")");
                Put("walk.checked", Utc());

                // ---- P2 cannot skip; its own pause menu opens and the prologue goes on behind it
                Check(SkipCutsceneController.Instance != null && !SkipCutsceneController.Instance.SkippingAvailable, "guest: skipping is not available to P2");
                int refused = SharedPrologue.SkipsRefused;
                SkipCutsceneController.Instance.SkipCutscene();                    // what the pause menu's skip item performs
                yield return StartCoroutine(TWait(1.5f));
                Check(SharedPrologue.SkipsRefused == refused + 1 && SharedPrologue.Watching && InPrologueState && SharedPrologue.LocalStops == 0,
                    "guest: its skip is refused, the prologue plays on (refused " + (SharedPrologue.SkipsRefused - refused) + ", watching " + SharedPrologue.Watching + ")");
                UI.Menu.ShowMenuScreen(true);
                yield return StartCoroutine(TWait(1.5f));
                Check(UI.MainMenuVisible && SharedPrologue.Watching && Time.timeScale > 0.01f, "guest: its own pause menu opens and the prologue is not paused (menu " + UI.MainMenuVisible + ")");
                UI.Menu.HideMenuScreen(true);
                yield return StartCoroutine(TWait(1f));
                if (m_fails != 0) yield break;

                // ---- the host skips: the copy here ends, the join loads the host's save as always
                yield return StartCoroutine(Together("skip.ready", 120f));
                yield return StartCoroutine(PrologueGuestEnds("guest", titleReturns, 0, 1, 1));
            }

            // After the host's skip: the copy is put away, the session stays, the host's save loads from the title.
            // aborts: joins given up before this one; copies / stops: prologue copies started / ended on this PC by now.
            IEnumerator PrologueGuestEnds(string who, int titleReturns, int aborts, int copies, int stops)
            {
                bool watched = SharedPrologue.Watching;
                yield return StartCoroutine(WaitPeer("skip", 60f));
                if (watched)
                {
                    yield return StartCoroutine(WaitFor(() => !SharedPrologue.Watching, 10f));
                    double after = Utc() - PeerNum("skip");
                    // The host's skip item runs its own 2 s fade before the state changes (playtest trace); then one network hop.
                    Check(!SharedPrologue.Watching && after < 6.0 && SharedPrologue.LastStop == "the host's prologue ended",
                        who + ": the host's skip ends the copy here (" + after.ToString("F1") + " s after the host pressed skip; '" + SharedPrologue.LastStop + "')");
                    yield return StartCoroutine(TWait(0.5f));
                    Check(SessionScreens.Blocking, who + ": the transfer screen is back while the host's save comes (" + SessionScreens.Phase + ")");
                }
                float end = Time.realtimeSinceStartup + 120f;
                while (Time.realtimeSinceStartup < end && !(NetPlay.LocalInGameplay() && SaveSync.Ready)) { DismissResume(); yield return null; }
                Check(NetPlay.LocalInGameplay() && SaveSync.Ready, who + ": in game with the host's save (net=" + NetPlay.CurrentPhase + ", aborts=" + GuestJoin.Aborts + " " + GuestJoin.LastAbort
                    + ", screen " + SessionScreens.Phase + ", busy " + SharedPrologue.Busy + ")");
                Check(NetPlay.Enabled && NetPlay.PartnerHere && TitleReturn.SessionsEnded == titleReturns,
                    who + ": the session was kept through the return to the title (sessions ended by a title return: " + (TitleReturn.SessionsEnded - titleReturns) + ")");
                Check(GuestJoin.Aborts == aborts, who + ": this join was never given up (" + (GuestJoin.Aborts - aborts) + " " + GuestJoin.LastAbort + ")");
                Check(SaveSync.JoinedHost && SaveSync.GuestJoinDistance < 3f, who + ": placed beside the host (distance " + SaveSync.GuestJoinDistance.ToString("F2") + ")");
                Check(SceneNow == GameScene && !InPrologueState, who + ": first playable scene (" + SceneNow + ")");
                Check(SharedPrologue.LocalStarts == copies && SharedPrologue.LocalStops == stops && !SharedPrologue.Busy,
                    who + ": copies of the prologue played here: " + SharedPrologue.LocalStarts + " started, " + SharedPrologue.LocalStops + " ended (expected "
                    + copies + " / " + stops + "), none left");
                if (watched) Note(who + ": putting the copy away took " + SharedPrologue.LastReturnSeconds.ToString("F1") + " s");
                yield return StartCoroutine(WaitPeer("timeline", 60f));
                string hostLine = Peer("timeline") ?? "", hostCoop = Peer("coop") ?? "";
                float until = Time.realtimeSinceStartup + 20f;                      // the host's save commit reaches the guest a moment later
                while (Time.realtimeSinceStartup < until && ((CoopSaves.Timeline ?? "") != hostLine || CoopSaves.GuestSlot < 0)) yield return null;
                Check(hostLine.Length > 0 && CoopSaves.Timeline == hostLine && hostCoop.Length == 32 && CoopSaves.CoopId == hostCoop,
                    who + ": the same save as the host (hash " + CoopSaves.Timeline + " / host " + hostLine + ", lineage " + CoopSaves.CoopId + " / host " + hostCoop + ")");
                Check(CoopSaves.GuestSlot >= 0 && SaveFiles() >= 1, who + ": its co-op copy of that save is kept in slot " + CoopSaves.GuestSlot + " (" + SaveFiles() + " save file(s))");
                yield return StartCoroutine(TWait(3f));
                Vector3 me = Characters.Sein != null ? Characters.Sein.Position : Vector3.zero;
                Check(NetPlay.HaveRemotePos && Vector3.Distance(NetPlay.LatestRemotePos, me) < 6f,
                    who + ": still beside the host (distance " + (NetPlay.HaveRemotePos ? Vector3.Distance(NetPlay.LatestRemotePos, me).ToString("F2") : "?") + ")");
                Shot("prologue_in_game");
                Put("done", Utc());
            }

            // The host quits mid-prologue (ticket 8): on a LAN that is a lost link, so the copy plays on for the 10 s
            // link-drop grace, then ends; the join is given up with "the host left" and this PC is back on a usable title.
            IEnumerator PrologueJoinHostQuits()
            {
                int aborts = GuestJoin.Aborts;
                Put("watching", Utc());
                yield return StartCoroutine(WaitPeer("quit", 120f));
                yield return StartCoroutine(WaitFor(() => !NetPlay.Connected, 15f));
                double lost = Utc();
                Check(!NetPlay.Connected && SharedPrologue.Watching, "hostquit: the link to the host is gone, the copy still plays (grace)");
                yield return StartCoroutine(TWait(5f));
                Check(SharedPrologue.Watching && GuestJoin.Aborts == aborts, "hostquit: 5 s without the host: still playing (a short link drop ends nothing)");
                yield return StartCoroutine(WaitFor(() => !SharedPrologue.Watching, 15f));
                double grace = Utc() - lost;
                Check(!SharedPrologue.Watching && grace >= 9.0 && grace <= 13.0 && SharedPrologue.LastStop == "the host left",
                    "hostquit: the copy ends 10 s after the host was lost (" + grace.ToString("F1") + " s; '" + SharedPrologue.LastStop + "')");
                yield return null;
                Check(GuestJoin.Aborts == aborts + 1 && GuestJoin.LastAbort.Contains("host left") && !GuestJoin.Waiting,
                    "hostquit: the join is given up because the host left (" + (GuestJoin.Aborts - aborts) + " '" + GuestJoin.LastAbort + "')");
                yield return StartCoroutine(CheckTitleInput("guest after the host left its prologue"));
                Check(!SharedPrologue.Busy && SessionScreens.Phase == SessionScreens.SavePhase.None && !InPrologueState && Characters.Naru == null,
                    "hostquit: on the title nothing of the prologue or the transfer screen is left (busy " + SharedPrologue.Busy + ", screen " + SessionScreens.Phase + ")");
                Check(SharedPrologue.LocalStarts == 1 && SharedPrologue.LocalStops == 1, "hostquit: one copy started and ended (" + SharedPrologue.LocalStarts + " / " + SharedPrologue.LocalStops + ")");
                Put("done", Utc());
            }

            // Leave from the pause menu mid-prologue, then join again: late.
            IEnumerator PrologueJoinLeave()
            {
                yield return StartCoroutine(TWait(4f));                             // well inside the prologue
                Check(SharedPrologue.Watching && InPrologueState, "leave: the guest is in the prologue before leaving");
                yield return StartCoroutine(QuitFromPause());                       // pause menu -> quit to the title, as a player does
                Put("left", Utc());
                yield return StartCoroutine(CheckTitleInput("guest left the prologue"));
                Check(!SharedPrologue.Busy && !SharedPrologue.Watching && SessionScreens.Phase == SessionScreens.SavePhase.None && !GuestJoin.Waiting && !InPrologueState,
                    "leave: on the title nothing of the prologue or the join is left (busy " + SharedPrologue.Busy + ", screen " + SessionScreens.Phase + ", waiting " + GuestJoin.Waiting + ")");
                if (m_fails != 0) yield break;

                // ---- join again while the host's prologue still plays: too late to play along
                int titleReturns = TitleReturn.SessionsEnded, aborts = GuestJoin.Aborts;
                GuestJoin.TestWaitLimit = 8f;                                       // the "no save" limit, shortened from 90 s for this check
                yield return StartCoroutine(PressCoop());
                yield return StartCoroutine(Row(Array.IndexOf(TitleTransports, m_tr), 1f));
                yield return StartCoroutine(PrologueConnect("late guest", true));
                if (!m_pGuestParked) yield break;
                Put("late.waiting", Utc());
                yield return StartCoroutine(WaitFor(() => SharedPrologue.HostPlaying, 5f));
                yield return StartCoroutine(TWait(2f));
                Check(SharedPrologue.HostPlaying && !SharedPrologue.Watching && SharedPrologue.LocalStarts == 1,
                    "late guest: the host is in its prologue; no copy starts here (copies " + SharedPrologue.LocalStarts + ")");
                Check(SessionScreens.Phase == SessionScreens.SavePhase.Waiting && SessionScreens.WaitingText == L10n.T("session.waiting_prologue"),
                    "late guest: the waiting screen says the host is watching the opening (" + SessionScreens.Phase + ": '" + SessionScreens.WaitingText + "')");
                Shot("prologue_late_waiting");
                yield return StartCoroutine(TWait(GuestJoin.TestWaitLimit + 4f));   // past the (shortened) no-save limit
                Check(GuestJoin.Waiting && GuestJoin.Aborts == aborts && NetPlay.Enabled,
                    "late guest: still waiting after " + (GuestJoin.TestWaitLimit + 6f) + " s: no 'no save' failure while the host is in its prologue (aborts " + (GuestJoin.Aborts - aborts) + " " + GuestJoin.LastAbort + ")");
                Put("late.checked", Utc());
                yield return StartCoroutine(Together("skip.ready", 120f));
                GuestJoin.TestWaitLimit = 0f;
                yield return StartCoroutine(PrologueGuestEnds("late guest", titleReturns, aborts, 1, 1));
            }
        }
    }
}
