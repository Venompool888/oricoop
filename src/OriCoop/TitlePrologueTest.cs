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
    // late:  (T-205) the host starts alone. The guest joins about 20 s in (a cinematic part): within seconds its copy
    //        shows the host's moment (the same timeline within 2 s), not the start and not the waiting screen. It
    //        leaves from its pause menu (title usable, the host goes on) and joins again while the host's Naru stands
    //        in the playable part, past a trigger: same scene, Naru on the host's spot, the trigger done here too.
    //        Then as in skip: own keys move nothing, the host walks Naru on both, the host's skip ends it for both.
    // hostquit: the host quits mid-prologue.
    // The expected values come from docs\tickets\T-203.md and T-205.md (设计), not from the code under test.
    public static partial class NativeModeMenu
    {
        partial class TitleJoinTest
        {
            static string PrologueMode { get { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_PROLOGUE") ?? ""; } }

            // In step: the same milestone on both PCs within this many seconds (ticket: 一起播放; no timeline seek, so a
            // PC that stalls on loading may trail by a moment). The start itself: within StartLimit.
            const float StepLimit = 3f, StartLimit = 1.5f;
            const string FirstScene = "swallowsNestStartOfLeafJourney", GameScene = "sunkenGladesRunaway";
            // T-205 (ticket 验收 1-2): a late guest shows the host's moment within JoinLimit seconds of joining, no more
            // than MomentLimit seconds of story apart; in a playable part its character is on the host's spot.
            const float JoinLimit = 8f, MomentLimit = 2f, SpotLimit = 1f;
            // ... a guest joining in the prologue's last scene (about nine minutes of story to make up; ticket 设计
            // "玩家看到什么"): level within DeepJoinLimit; its character, which the host moves, within the 3 units the
            // position correction allows while the host walks.
            const float DeepJoinLimit = 60f, DeepSpotLimit = 3f;
            // The area-name trigger of the first playable scene (probe: swallowsNestTheGift/areaTextZone/triggerCollider).
            const float AreaTriggerX = -2244.5f;
            const string AreaTrigger = "areaTextZone/triggerCollider";

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
            // What the other game has reported so far. A read can fall into the moment the other game replaces its file
            // (it is gone for an instant between delete and move; T-205: the host now writes five times a second): what
            // was read before stays known, so a value that was there never seems to disappear.
            readonly Dictionary<string, string> m_pPeer = new Dictionary<string, string>();
            string Peer(string key)
            {
                try
                {
                    if (File.Exists(PFile(PeerRole)))
                    {
                        string text;
                        using (FileStream fs = new FileStream(PFile(PeerRole), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (StreamReader reader = new StreamReader(fs)) text = reader.ReadToEnd();
                        foreach (string line in text.Split('\n'))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0) m_pPeer[line.Substring(0, eq)] = line.Substring(eq + 1);
                        }
                    }
                }
                catch (Exception) { }   // the other game is replacing it right now: read again next frame
                string value;
                return m_pPeer.TryGetValue(key, out value) ? value : null;
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

            // ---- T-205: what the game shows right now (its own state: scene, character, the longest timeline that plays)
            static string ActorNow
            {
                get
                {
                    ICharacter c = Characters.Current;
                    return c == null ? "none" : object.ReferenceEquals(c, Characters.Naru) ? "Naru" : object.ReferenceEquals(c, Characters.BabySein) ? "BabySein"
                        : object.ReferenceEquals(c, Characters.Sein) ? "Sein" : "other";
                }
            }
            static TimelineSequence PlayingTimeline(string name)
            {
                TimelineSequence best = null; float longest = 0f;
                foreach (TimelineSequence s in UnityEngine.Object.FindObjectsOfType<TimelineSequence>())
                {
                    if (!s.AnimatorDriver.IsPlaying || name != null && s.name != name) continue;
                    float d = s.Duration;
                    if (d > longest) { longest = d; best = s; }
                }
                return best;
            }
            // The host reports it five times a second (key "s") for as long as its test runs.
            IEnumerator PublishMoments()
            {
                while (!m_finished)
                {
                    TimelineSequence t = PlayingTimeline(null);
                    if (t != null && t.Duration < 5f) t = null;   // a short effect may start a frame apart on the two PCs
                    Vector3 p = Characters.Current != null ? Characters.Current.Position : Vector3.zero;
                    Put("s", string.Join("|", new[] { Num(Utc()), SceneNow, ActorNow, Num(p.x), Num(p.y), t == null ? "" : t.name,
                        Num(t == null ? 0.0 : t.AnimatorDriver.CurrentTime), SharedPrologue.HostStep.ToString() }));
                    yield return StartCoroutine(TWait(0.2f));
                }
            }
            // The character this copy controls is drawn (its object is active and at least one renderer is on).
            static bool CharacterVisible
            {
                get
                {
                    ICharacter c = Characters.Current;
                    if (c == null || c.GameObject == null || !c.GameObject.activeInHierarchy) return false;
                    foreach (Renderer r in c.GameObject.GetComponentsInChildren<Renderer>()) if (r.enabled) return true;
                    return false;
                }
            }

            // Guest: does this copy show what the host's latest report shows? Scene and character equal; a playing
            // timeline is the same one and within `limit` seconds; a character stands within `spot` of the host's.
            bool SameMoment(float limit, out string detail) { return SameMoment(limit, SpotLimit, out detail); }
            bool SameMoment(float limit, float spot, out string detail)
            {
                string raw = Peer("s");
                detail = "no report from the host";
                if (raw == null) return false;
                string[] p = raw.Split('|');
                if (p.Length < 8) return false;
                double age = Utc() - double.Parse(p[0], CultureInfo.InvariantCulture);
                float hx = float.Parse(p[3], CultureInfo.InvariantCulture), hy = float.Parse(p[4], CultureInfo.InvariantCulture), ht = float.Parse(p[6], CultureInfo.InvariantCulture);
                detail = "host: scene " + p[1] + ", character " + p[2] + (p[5].Length > 0 ? ", timeline " + p[5] + " at " + ht.ToString("F1") + " s" : "")
                    + "; here: scene " + SceneNow + ", character " + ActorNow;
                if (age > 1.0) { detail += "; the host's report is " + age.ToString("F1") + " s old"; return false; }
                if (p[1] != SceneNow || p[2] != ActorNow) return false;
                if (p[5].Length > 0)
                {
                    TimelineSequence mine = PlayingTimeline(p[5]);
                    if (mine == null) { detail += ", that timeline does not play here"; return false; }
                    float apart = mine.AnimatorDriver.CurrentTime - (ht + (float)age);
                    detail += ", the same timeline " + apart.ToString("F2") + " s apart";
                    if (Math.Abs(apart) > limit) return false;
                }
                if (Characters.Current != null)
                {
                    Vector3 here = Characters.Current.Position;
                    float off = Math.Max(Math.Abs(here.x - hx), Math.Abs(here.y - hy));
                    detail += ", the character " + off.ToString("F2") + " units from the host's";
                    if (off > spot) return false;
                }
                return true;
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
                yield return StartCoroutine(Row(TitleStartRow(m_tr, true), 0.2f));  // 开房 / the method row -> save slots
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
                if (mode == "late" || mode == "deep" || mode == "probe" && ProbeJoin > 0f)   // T-205: the host starts alone; the guest connects later
                    Check(OnlineRoomScreen.Visible && !NetPlay.PartnerHere, "host: starts its new game alone, nobody has joined yet");
                else
                {
                    yield return StartCoroutine(WaitPeer("waiting", 180f));
                    Check(Peer("waiting") != null && NetPlay.PartnerHere && OnlineRoomScreen.Visible,
                        "host: the guest is connected and " + (m_tr == "cserver" ? "ready on the room page" : "waits for the host's save"));
                    if (Peer("waiting") == null) yield break;
                }
                yield return StartCoroutine(TWait(1f));
                float end = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < end && OnlineRoomScreen.Visible) { OnlineRoomScreen.Click("room.continue"); yield return null; }
                Put("start", Utc());
                yield return null;
                Check(SharedPrologue.HostStarts == 1 && SharedPrologue.HostAnnouncing, "host: the new game announces its prologue at once (starts " + SharedPrologue.HostStarts + ")");
                if (mode == "probe") { yield return StartCoroutine(PrologueProbe(1000f, true)); yield return StartCoroutine(TWait(3f)); yield break; }
                StartCoroutine(PublishMoments());
                if (mode == "deep")
                {
                    // T-205: the host plays the whole prologue (the probe's driver walks it; its cinematic parts are
                    // fast-forwarded up to the point where the guest joins, in the last scene) to its own end.
                    yield return StartCoroutine(PrologueProbe(700f, true));
                    Check(!InPrologueState && SharedPrologue.EventsLogged >= 40,
                        "host: played the whole prologue to its own end (" + (SharedPrologue.HostStep / 60f).ToString("F0") + " s of story, " + SharedPrologue.EventsLogged
                        + " player points logged, state " + GameStateMachine.Instance.CurrentState + ")");
                    Put("natural.end", Utc());
                    if (m_fails != 0) yield break;
                    yield return StartCoroutine(PrologueHostInGame(slot, "the prologue's own end"));
                    yield break;
                }
                yield return StartCoroutine(Milestones(150f, mode == "skip" || mode == "late"));
                Check(InPrologueState, "host: the state machine is in the Prologue state");
                // Ticket 11: the prologue is not co-op gameplay; no checkpoint leaves the host in the middle of it.
                Check(!NetPlay.LocalInGameplay() && !SaveSync.HaveCheckpointPosition, "host: not 'in gameplay' during the prologue, no checkpoint sent");
                if (!InPrologueState) yield break;
                if (mode == "hostquit") { yield return StartCoroutine(PrologueHostQuits()); yield break; }
                if (mode == "late") yield return StartCoroutine(PrologueHostLate());
                if (m_fails != 0) yield break;
                yield return StartCoroutine(PrologueHostControl());
                if (m_fails != 0) yield break;

                // ---- only P1 skips: the pause menu's skip item performs SkipCutsceneController.SkipCutscene
                yield return StartCoroutine(Together("skip.ready", 120f));
                Check(SkipCutsceneController.Instance != null && SkipCutsceneController.Instance.SkippingAvailable, "host: P1 can skip the prologue");
                Check(SharedPrologue.RequestsShown == 1, "host: two member prologue skips produced one request");
                Put("skip", Utc());
                SkipCutsceneController.Instance.SkipCutscene();
                yield return StartCoroutine(PrologueHostInGame(slot, "its skip"));
            }

            // The host's prologue is over (skipped, or played to its end): it is in the game, the guest loads its save
            // and stands beside it, and the new game's save is in the slot the host picked.
            IEnumerator PrologueHostInGame(int slot, string after)
            {
                float end = Time.realtimeSinceStartup + 90f;
                while (Time.realtimeSinceStartup < end && !NetPlay.LocalInGameplay()) { DismissResume(); yield return null; }
                Check(NetPlay.LocalInGameplay() && !InPrologueState, "host: in game after " + after);
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
                // T-209 (1): P1 plays on a controller (the test's menu device is pad 0). A key held on this PC's keyboard
                // (a second local player's device) must not walk Naru: only P1 controls the prologue.
                yield return StartCoroutine(TWait(6f));                             // control is handed over a few seconds after the camera
                float keyFrom = NaruX, keyMoved = 0f;
                KeyboardOnlyP1.TestKeyHeld = KeyCode.D;                             // the game's default "right" key (PlayerInputRebinding)
                float keyUntil = Time.realtimeSinceStartup + 2.5f;
                while (Time.realtimeSinceStartup < keyUntil) { keyMoved = Math.Max(keyMoved, Math.Abs(NaruX - keyFrom)); yield return null; }
                KeyboardOnlyP1.TestKeyHeld = KeyCode.None;
                Check(OnlineLocalPlayers.InputOf(Players.LocalSeat).Kind == InputDevice.DeviceKind.Pad && keyMoved < 0.3f,
                    "host: P1 is on a controller (" + OnlineLocalPlayers.InputOf(Players.LocalSeat).Kind + "): a held keyboard direction key does not move Naru (moved "
                    + keyMoved.ToString("F2") + " units)");
                yield return StartCoroutine(TWait(1f));
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

            // T-205: the guest joined during the opening cinematic, checked what it sees and left again (pause menu):
            // the host just goes on. Alone, it walks Naru through the area-name trigger and stands still; the guest
            // joins again there.
            IEnumerator PrologueHostLate()
            {
                yield return StartCoroutine(WaitPeer("left", 150f));
                yield return StartCoroutine(WaitFor(() => !NetPlay.PartnerHere, 20f));
                yield return StartCoroutine(TWait(2f));
                Check(Peer("left") != null && !NetPlay.PartnerHere && InPrologueState && NetPlay.Enabled && SharedPrologue.HostAnnouncing && !NetPlay.LocalInGameplay(),
                    "late: the guest joined, then left; the host's prologue goes on, still hosting (partner " + NetPlay.PartnerHere + ", net=" + NetPlay.CurrentPhase + ")");
                Check(NaruPlayable, "late: the host's Naru is playable");
                if (!NaruPlayable) yield break;
                yield return StartCoroutine(TWait(6f));                             // control is handed over a few seconds after the camera
                float x0 = NaruX; int logged = SharedPrologue.EventsLogged;
                SharedPrologue.TestInput = true; SharedPrologue.TestHorizontal = 1f;
                float end = Time.realtimeSinceStartup + 40f;
                while (Time.realtimeSinceStartup < end && NaruX < AreaTriggerX + 1.5f) yield return null;
                SharedPrologue.TestHorizontal = 0f;
                yield return StartCoroutine(TWait(2.5f));                           // at rest
                SharedPrologue.TestInput = false;
                Check(NaruX > AreaTriggerX + 1f && SharedPrologue.EventsLogged > logged && InPrologueState,
                    "late: alone, the host walked Naru through the area-name trigger (x " + x0.ToString("F1") + " -> " + NaruX.ToString("F1") + ", player points logged "
                    + logged + " -> " + SharedPrologue.EventsLogged + ", story step " + SharedPrologue.HostStep + ")");
                Put("walk1.x", NaruX); Put("walk1.y", Characters.Naru.Position.y); Put("walk1.done", Utc());
                yield return StartCoroutine(WaitPeer("rejoined.checked", 150f));
                Check(Peer("rejoined.checked") != null && NetPlay.PartnerHere && InPrologueState, "late: the guest is back in the session while Naru stands in the playable part");
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
                yield return StartCoroutine(Row(TitleStartRow(m_tr, false), 1.2f)); // 加入 / the method row -> entry page
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

            // T-209 (2): the other way in (ORICOOP_TITLEJOIN_GUESTSLOT=1, run-titlejoin -GuestSlot): the guest is connected
            // and picks an EMPTY save slot (difficulty -> new game). The new game is not started (GuestJoin.Intercept);
            // that slot waits for the host's save. A player gets there from the save-slot screen's own mode menu; the
            // test connects directly, as the net harness does for -GuestNewGame.
            static bool GuestSlotPath { get { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_GUESTSLOT") == "1"; } }
            IEnumerator PrologueConnectBySlot(string who)
            {
                KeyboardOnlyP1.SetMenuDeviceForTest(-1);
                m_pGuestParked = false;
                int intercepts = GuestJoin.Intercepts;
                if (TitleOrbOpen) NativeModeMenu.Destroy();
                NetPlay.Begin(false, "direct", "127.0.0.1:" + NetPlay.ConfigPort);
                TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
                yield return StartCoroutine(WaitFor(() => SlotsReady && NetPlay.PartnerHere, 90f));
                Check(SlotsReady && NetPlay.PartnerHere && !GuestJoin.Waiting, who + ": connected, on the save-slot screen (net=" + NetPlay.CurrentPhase + ")");
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                if (ui == null || !SlotsReady || !NetPlay.PartnerHere) yield break;
                yield return StartCoroutine(TWait(0.8f));
                ui.SetCurrentItem(0);
                ui.EmptySaveSlotSelected();                                        // A on an empty slot: the game's difficulty screen
                yield return StartCoroutine(TWait(0.6f));
                ui.SetDifficulty(DifficultyMode.Easy);
                yield return StartCoroutine(TWait(0.5f));
                m_pGuestParked = GuestJoin.Waiting && GuestJoin.Intercepts == intercepts + 1;
                Check(m_pGuestParked && !InPrologueState && NetPlay.PartnerHere,
                    who + ": the empty slot starts no new game here; it waits for the host's save (intercepted " + (GuestJoin.Intercepts - intercepts) + ", waiting " + GuestJoin.Waiting + ")");
            }

            IEnumerator PrologueJoinRun(int savesAtStart)
            {
                string mode = PrologueMode;
                Note("--- shared prologue: " + mode + " (guest" + (GuestSlotPath ? ", joining through an empty save slot" : "") + ")");
                Check(savesAtStart == 0, "guest: appdata has no saves (" + savesAtStart + ")");
                if (mode == "late") { yield return StartCoroutine(PrologueJoinLate()); yield break; }
                if (mode == "deep") { yield return StartCoroutine(PrologueJoinDeep()); yield break; }
                if (mode == "probe" && ProbeJoin > 0f)
                {
                    yield return StartCoroutine(WaitPeer("start", 180f));
                    double hostStarted = PeerNum("start");
                    yield return StartCoroutine(WaitPeer("probe.ready", 900f));   // the host's story is ProbeJoin seconds in
                    yield return StartCoroutine(PrologueConnect("late probe guest", true));
                    double joinedAt = Utc();
                    Note("late probe guest: joined " + (joinedAt - hostStarted).ToString("F1") + " s after the host's start");
                    yield return StartCoroutine(WaitFor(() => SharedPrologue.Watching && SharedPrologue.CatchUps > 0 && !SharedPrologue.CatchingUp, 120f));
                    Note("late probe guest: level with the host " + (Utc() - joinedAt).ToString("F1") + " s after joining (fast-forward " + SharedPrologue.LastCatchUpSeconds.ToString("F1")
                        + " s for " + (SharedPrologue.LastCatchUpSteps / 60f).ToString("F1") + " s of story; points done " + SharedPrologue.EventsApplied + ", left out " + SharedPrologue.EventsSkipped + ")");
                    yield return StartCoroutine(PrologueProbe(1000f, false));
                    yield break;
                }
                if (GuestSlotPath) yield return StartCoroutine(PrologueConnectBySlot("guest"));
                else yield return StartCoroutine(PrologueConnect("guest", false));
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
                if (mode == "probe") { yield return StartCoroutine(PrologueProbe(1000f, false)); yield break; }
                yield return StartCoroutine(Milestones(150f, mode == "skip"));
                yield return StartCoroutine(WaitPeer("m.scene", 30f));
                Check(InPrologueState && SharedPrologue.Watching, "guest: its state machine is in the Prologue state");
                CheckStep("m.state", "the Prologue state");
                CheckStep("m.scene", "scene " + FirstScene);
                Check(!NetPlay.LocalInGameplay() && Characters.Sein == null, "guest: no Ori and not 'in gameplay' during the prologue");
                Shot("prologue_playing");
                if (m_fails != 0) yield break;
                if (mode == "hostquit") { yield return StartCoroutine(PrologueJoinHostQuits()); yield break; }

                yield return StartCoroutine(WaitPeer("m.naru", 30f));
                CheckStep("m.naru", "Naru playable");
                if (!NaruPlayable) yield break;
                // T-205: an on-time guest follows the host's story too (its points come from the host's log, its
                // speed from the host's clock); nothing was left out and it never needed the cover.
                Check(SharedPrologue.FollowsStory && SharedPrologue.EventsSkipped == 0 && SharedPrologue.CatchUps == 0 && Math.Abs(SharedPrologue.LastLag) <= 120,
                    "guest: follows the host's story position (" + (SharedPrologue.LastLag / 60f).ToString("F2") + " s behind now, points left out " + SharedPrologue.EventsSkipped
                    + ", covered catch-ups " + SharedPrologue.CatchUps + ")");
                yield return StartCoroutine(PrologueGuestControl());
                if (m_fails != 0) yield break;

                // ---- the host skips: the copy here ends, the join loads the host's save as always
                yield return StartCoroutine(Together("skip.ready", 120f));
                yield return StartCoroutine(PrologueGuestEnds("guest", titleReturns, 0, 1, 1));
            }

            // Naru is playable on both PCs: this PC's keys move nothing, the host's input walks Naru here too, this
            // PC cannot skip, its own pause menu opens and pauses nothing.
            IEnumerator PrologueGuestControl()
            {
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

                // ---- T-205: a "press a button" wait of the story is the host's to answer. The game's own condition class
                // (the one the sleeping-Ori wait uses, any button) is asked while a button of this PC is held that the
                // host's input never replaces: it must not be answered here.
                if (SharedPrologue.FollowsStory)
                {
                    GameObject waitObject = new GameObject("oricoopTestButtonWait");
                    ButtonPressedCondition wait = waitObject.AddComponent<ButtonPressedCondition>();
                    wait.Button = Core.Input.Button.Any; wait.State = ButtonPressedCondition.ButtonStates.Pressed;
                    SharedPrologue.TestInput = true; SharedPrologue.TestOwnButton = true;
                    yield return StartCoroutine(TWait(0.5f));
                    bool held = Core.Input.Legend.IsPressed && Core.Input.AnyButtonPressed, answered = wait.Validate(null);
                    SharedPrologue.TestOwnButton = false; SharedPrologue.TestInput = false;
                    UnityEngine.Object.Destroy(waitObject);
                    Check(held && !answered, "guest: a button of this PC is held (" + held + ") and does not answer the story's 'press a button' wait (answered " + answered + ")");
                    yield return StartCoroutine(TWait(0.3f));

                    // ---- T-205 review 1: a trigger of the host's log is done only when the game really took it. The
                    // game's own trigger class, asked the way the host's log asks: switched off, on an inactive object,
                    // then usable, then used up (it fires once).
                    GameObject triggerObject = new GameObject("oricoopTestTrigger");
                    PlayerCollisionTrigger trigger = triggerObject.AddComponent<PlayerCollisionTrigger>();
                    trigger.UseTrigger = true;                                      // never by a character's position
                    trigger.TriggerOnce = true; trigger.Active = false;
                    bool off = SharedPrologue.Replay(trigger);
                    trigger.Active = true; triggerObject.SetActive(false);
                    bool inactive = SharedPrologue.Replay(trigger), kept = trigger.Active;
                    triggerObject.SetActive(true);
                    bool taken = SharedPrologue.Replay(trigger), used = !trigger.Active, again = SharedPrologue.Replay(trigger);
                    UnityEngine.Object.Destroy(triggerObject);
                    Check(!off && !inactive && kept && taken && used && !again,
                        "guest: a trigger of the host's log counts as done only when the game took it (switched off " + off + ", inactive object " + inactive
                        + " and still usable " + kept + ", usable " + taken + " and used up " + used + ", asked again " + again + ")");
                    yield return StartCoroutine(TWait(0.3f));
                }

                // ---- P2 cannot skip; its own pause menu opens and the prologue goes on behind it
                Check(SkipCutsceneController.Instance != null && SkipCutsceneController.Instance.SkippingAvailable, "guest: the prologue skip request is available");
                int refused = SharedPrologue.SkipsRefused, stops = SharedPrologue.LocalStops;
                SkipCutsceneController.Instance.SkipCutscene();                    // what the pause menu's skip item performs
                SkipCutsceneController.Instance.SkipCutscene();                    // repeated request
                yield return StartCoroutine(TWait(1.5f));
                Check(SharedPrologue.SkipsRefused == refused + 2 && SharedPrologue.Watching && InPrologueState && SharedPrologue.LocalStops == stops,
                    "guest: its skip is refused, the prologue plays on (refused " + (SharedPrologue.SkipsRefused - refused) + ", watching " + SharedPrologue.Watching + ")");
                UI.Menu.ShowMenuScreen(true);
                yield return StartCoroutine(TWait(1.5f));
                Check(UI.MainMenuVisible && SharedPrologue.Watching && Time.timeScale > 0.01f, "guest: its own pause menu opens and the prologue is not paused (menu " + UI.MainMenuVisible + ")");
                UI.Menu.HideMenuScreen(true);
                yield return StartCoroutine(TWait(1f));
            }

            // After the host's skip: the copy is put away, the session stays, the host's save loads from the title.
            // aborts: joins given up before this one; copies / stops: prologue copies started / ended on this PC by now.
            IEnumerator PrologueGuestEnds(string who, int titleReturns, int aborts, int copies, int stops)
            { return PrologueGuestEnds(who, titleReturns, aborts, copies, stops, false); }
            // natural: the host does not skip; its prologue plays to its own end (the copy here may reach it a moment sooner).
            IEnumerator PrologueGuestEnds(string who, int titleReturns, int aborts, int copies, int stops, bool natural)
            {
                bool watched = SharedPrologue.Watching;
                if (natural && watched) yield return StartCoroutine(WaitFor(() => !SharedPrologue.Watching || Peer("natural.end") != null, 300f));
                else yield return StartCoroutine(WaitPeer("skip", 60f));
                if (watched)
                {
                    yield return StartCoroutine(WaitFor(() => !SharedPrologue.Watching, natural ? 30f : 10f));
                    if (natural)
                        Check(!SharedPrologue.Watching && (SharedPrologue.LastStop == "the host's prologue ended" || SharedPrologue.LastStop == "this copy reached its end first"),
                            who + ": the copy here ends with the prologue's own end ('" + SharedPrologue.LastStop + "', points left out " + SharedPrologue.EventsSkipped + ")");
                    else
                    {
                        double after = Utc() - PeerNum("skip");
                        // The host's skip item runs its own 2 s fade before the state changes (playtest trace); then one network hop.
                        Check(!SharedPrologue.Watching && after < 6.0 && SharedPrologue.LastStop == "the host's prologue ended",
                            who + ": the host's skip ends the copy here (" + after.ToString("F1") + " s after the host pressed skip; '" + SharedPrologue.LastStop + "')");
                    }
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

            // T-205: this PC has just joined a host whose prologue is running (GuestJoin waits since `waitingAt`). Its
            // copy must show the host's moment within JoinLimit: started at once, fast-forwarded under the cover
            // screen, then the same scene / character / timeline as the host (the game's own state, not the mod's clock).
            // catchUps: SharedPrologue.CatchUps before this PC connected (the copy may start in the frame it joins).
            IEnumerator LateJoinChecks(string who, int copies, double waitingAt, int catchUps)
            { return LateJoinChecks(who, copies, waitingAt, catchUps, JoinLimit, SpotLimit); }
            IEnumerator LateJoinChecks(string who, int copies, double waitingAt, int catchUps, float joinLimit, float spot)
            {
                yield return StartCoroutine(WaitFor(() => SharedPrologue.Watching, JoinLimit));
                double copyAt = Utc();
                Check(SharedPrologue.Watching && SharedPrologue.LocalStarts == copies,
                    who + ": its own copy of the prologue starts " + (copyAt - waitingAt).ToString("F1") + " s after joining (copies started " + SharedPrologue.LocalStarts
                    + ", host in prologue " + SharedPrologue.HostPlaying + ", host story known " + SharedPrologue.EventsKnown + " points)");
                if (!SharedPrologue.Watching) yield break;
                Check(SharedPrologue.FollowsStory, who + ": the copy follows the host's story position");
                // While it fast-forwards the picture is covered, and the screen says what is going on.
                // Review 5: the pause key (what Start does in the game) is pressed every few frames while the cover is
                // up: the pause menu must never open under it.
                bool covered = false, menuUnderCover = false;
                int refused = SharedPrologue.MenusRefused, frame = 0;
                float end = Time.realtimeSinceStartup + joinLimit + 30f;
                string coverText = L10n.T(m_tr == "cserver" ? "session.catching_up.cs" : "session.catching_up");
                while (Time.realtimeSinceStartup < end && (SharedPrologue.CatchUps == catchUps || SharedPrologue.CatchingUp))
                {
                    if (SharedPrologue.CatchingUp && SessionScreens.Phase == SessionScreens.SavePhase.Waiting && SessionScreens.WaitingText == coverText)
                    {
                        covered = true;
                        if (UI.Menu != null)
                        {
                            if (UI.MainMenuVisible) { menuUnderCover = true; UI.Menu.HideMenuScreen(true); }
                            else if (frame++ % 5 == 0) UI.Menu.DoMenuKeyPress();
                        }
                    }
                    yield return null;
                }
                if (UI.Menu != null && UI.MainMenuVisible) { menuUnderCover = true; UI.Menu.HideMenuScreen(true); }
                double levelAt = Utc();
                Check(!menuUnderCover && SharedPrologue.MenusRefused > refused,
                    who + ": the pause key does nothing while the cover is up (pause menu opened under it " + menuUnderCover + ", presses refused "
                    + (SharedPrologue.MenusRefused - refused) + ")");
                Check(SharedPrologue.CatchUps == catchUps + 1 && !SharedPrologue.CatchingUp && covered,
                    who + ": catches up with the host under the cover screen (catch-ups " + (SharedPrologue.CatchUps - catchUps) + ", cover with its text seen " + covered
                    + ", still catching up " + SharedPrologue.CatchingUp + ", rate " + SharedPrologue.RateNow + ", " + (SharedPrologue.LastLag / 60f).ToString("F1") + " s behind)");
                Check(levelAt - waitingAt <= joinLimit,
                    who + ": shows the host's moment " + (levelAt - waitingAt).ToString("F1") + " s after joining (limit " + joinLimit + " s; the fast-forward took "
                    + SharedPrologue.LastCatchUpSeconds.ToString("F1") + " s for " + (SharedPrologue.LastCatchUpSteps / 60f).ToString("F1") + " s of story)");
                yield return StartCoroutine(TWait(1f));
                Check(SessionScreens.Phase == SessionScreens.SavePhase.None && !SessionScreens.Blocking && SharedPrologue.Watching && InPrologueState,
                    who + ": the cover is gone, the prologue is on screen (" + SessionScreens.Phase + ")");
                // The same moment as the host: three looks, half a second apart.
                int same = 0; string detail = "";
                for (int look = 0; look < 3; look++)
                {
                    bool ok = false;
                    float until = Time.realtimeSinceStartup + 3f;
                    while (Time.realtimeSinceStartup < until && !(ok = SameMoment(MomentLimit, spot, out detail))) yield return null;
                    if (ok) same++;
                    yield return StartCoroutine(TWait(0.5f));
                }
                Check(same == 3, who + ": shows the same moment as the host, " + same + " of 3 looks (" + detail + ")");
                double age = Utc() - copyAt;
                Check(SharedPrologue.LocalStep / 60f > age + 5.0,
                    who + ": did not play from the start: its copy is " + (SharedPrologue.LocalStep / 60f).ToString("F1") + " s into the story " + age.ToString("F1") + " s after it started");
                Check(SharedPrologue.EventsSkipped == 0 && Math.Abs(SharedPrologue.LastLag) <= MomentLimit * 60f,
                    who + ": none of the host's player points was left out (" + SharedPrologue.EventsApplied + " done, " + SharedPrologue.EventsSkipped + " left out); "
                    + (SharedPrologue.LastLag / 60f).ToString("F2") + " s behind the host's clock");
                Check(GuestJoin.Waiting && NetPlay.Enabled && Time.timeScale <= 2.01f, who + ": the join still waits for the host's save; the game runs at its own speed again (time scale "
                    + Time.timeScale.ToString("F2") + ")");
            }

            // T-205: join in the prologue's last scene (the weak Ori before the Spirit Tree), about nine minutes of
            // story in. The copy fast-forwards through every scene and every point the host's player decided; then the
            // story's character is on screen where the host's is, and both PCs play to the prologue's own end.
            IEnumerator PrologueJoinDeep()
            {
                yield return StartCoroutine(WaitPeer("start", 180f));
                if (Peer("start") == null) yield break;
                yield return StartCoroutine(WaitPeer("probe.ready", 600f));
                Check(Peer("probe.ready") != null && !NetPlay.Enabled, "deep guest: the host is deep in its prologue, " + (Utc() - PeerNum("start")).ToString("F0")
                    + " s after it started; this PC is not connected yet");
                if (Peer("probe.ready") == null) yield break;
                int titleReturns = TitleReturn.SessionsEnded, aborts = GuestJoin.Aborts, catchUps = SharedPrologue.CatchUps;
                yield return StartCoroutine(PrologueConnect("deep guest", true));
                if (!m_pGuestParked) yield break;
                double waitingAt = Utc();
                yield return StartCoroutine(LateJoinChecks("deep guest", 1, waitingAt, catchUps, DeepJoinLimit, DeepSpotLimit));
                // About 42-48 by then (triggers, the button wait, cutscene states; scene loads are not counted here).
                Check(SharedPrologue.EventsApplied >= 30, "deep guest: its copy did the points the host's player decided on the way (" + SharedPrologue.EventsApplied + " of "
                    + SharedPrologue.EventsKnown + " known)");
                // The intro hold used to take this scene's Ori for "this player's Ori": pinned where it appeared, hidden.
                Check(Characters.Sein != null && object.ReferenceEquals(Characters.Current, Characters.Sein) && CharacterVisible,
                    "deep guest: the story's character is on screen (character " + ActorNow + ", drawn " + CharacterVisible + ")");
                Shot("prologue_deep");
                if (m_fails != 0) yield break;
                yield return StartCoroutine(PrologueGuestEnds("deep guest", titleReturns, aborts, 1, 1, true));
            }

            // T-205: join late twice. First during the opening cinematic; then, after leaving from the pause menu, in
            // the playable part.
            IEnumerator PrologueJoinLate()
            {
                yield return StartCoroutine(WaitPeer("start", 180f));
                if (Peer("start") == null) yield break;
                Check(!NetPlay.Enabled && SharedPrologue.LocalStarts == 0, "late guest: not connected while the host starts its new game");
                double hostStart = PeerNum("start");
                yield return StartCoroutine(WaitFor(() => Utc() - hostStart >= 20.0, 40f));
                int catchUps = SharedPrologue.CatchUps;
                yield return StartCoroutine(PrologueConnect("late guest", true));
                if (!m_pGuestParked) yield break;
                double waitingAt = Utc();
                Note("late guest: joined " + (waitingAt - hostStart).ToString("F1") + " s after the host's start");
                yield return StartCoroutine(LateJoinChecks("late guest (cinematic)", 1, waitingAt, catchUps));
                Check(Characters.Current == null, "late guest (cinematic): a cinematic part, no character to control");
                Shot("prologue_late_cinematic");
                if (m_fails != 0) yield break;

                // ---- leave from the pause menu, as a player does: back on a usable title, the host goes on
                yield return StartCoroutine(QuitFromPause());
                Put("left", Utc());
                yield return StartCoroutine(CheckTitleInput("late guest left the prologue"));
                Check(!SharedPrologue.Busy && !SharedPrologue.Watching && SessionScreens.Phase == SessionScreens.SavePhase.None && !GuestJoin.Waiting && !InPrologueState,
                    "late guest: on the title nothing of the prologue or the join is left (busy " + SharedPrologue.Busy + ", screen " + SessionScreens.Phase + ", waiting " + GuestJoin.Waiting + ")");
                Check(SharedPrologue.LocalStops == 1 && Time.timeScale <= 2.01f, "late guest: its copy ended with the leave (" + SharedPrologue.LocalStops + "), time scale " + Time.timeScale.ToString("F2"));
                if (m_fails != 0) yield break;

                // ---- join again: the host's Naru stands in the playable part, past the area-name trigger
                yield return StartCoroutine(WaitPeer("walk1.done", 180f));
                if (Peer("walk1.done") == null) yield break;
                int titleReturns = TitleReturn.SessionsEnded, aborts = GuestJoin.Aborts, applied = SharedPrologue.EventsApplied;
                catchUps = SharedPrologue.CatchUps;
                yield return StartCoroutine(PressCoop());
                yield return StartCoroutine(Row(TitleEntryRow(m_tr, false), 1f));
                yield return StartCoroutine(PrologueConnect("rejoining guest", true));
                if (!m_pGuestParked) yield break;
                waitingAt = Utc();
                yield return StartCoroutine(LateJoinChecks("rejoining guest (playable part)", 2, waitingAt, catchUps));
                double hostX = PeerNum("walk1.x"), hostY = PeerNum("walk1.y");
                Check(NaruPlayable && Math.Abs(NaruX - hostX) <= SpotLimit && Math.Abs(Characters.Naru.Position.y - hostY) <= SpotLimit,
                    "rejoining guest: Naru is playable and stands on the host's spot (host " + hostX.ToString("F2") + ", " + hostY.ToString("F2") + "; here "
                    + (NaruPlayable ? NaruX.ToString("F2") + ", " + Characters.Naru.Position.y.ToString("F2") : "no Naru") + ")");
                // The host walked through the area-name trigger before this PC joined: the copy here did it too, from the
                // host's log (its own Naru never walked there).
                Trigger area = null;
                foreach (PlayerCollisionTrigger t in UnityEngine.Object.FindObjectsOfType<PlayerCollisionTrigger>())
                    if (t.transform.parent != null && t.transform.parent.name + "/" + t.name == AreaTrigger) area = t;
                Check(area != null && !area.Active && SharedPrologue.EventsApplied > applied,
                    "rejoining guest: the trigger the host walked through alone has fired here too (" + (area == null ? "trigger not found" : "still armed: " + area.Active)
                    + ", host points done in this copy " + (SharedPrologue.EventsApplied - applied) + ")");
                Shot("prologue_late_playable");
                Put("rejoined.checked", Utc());
                if (m_fails != 0) yield break;

                // ---- from here on like a guest that was there from the start
                yield return StartCoroutine(PrologueGuestControl());
                if (m_fails != 0) yield break;
                yield return StartCoroutine(Together("skip.ready", 120f));
                yield return StartCoroutine(PrologueGuestEnds("rejoining guest", titleReturns, aborts, 2, 2));
            }
        }
    }
}
