using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // Automated in-game test, only active when ORICOOP_TEST=1.
    // Title -> loads save slot ORICOOP_SLOT -> spawns P2 -> runs scenarios (movement, pause, P2 death,
    // area teleport, P1 death) with a scripted P2 bot -> writes ORICOOP_TEST_OUT and quits.
    // ORICOOP_SCENARIOS (optional) = comma list of: move,pause,p2death,teleport,p1death
    public class TestHarness : MonoBehaviour
    {
        public static bool Enabled { get { return Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1"; } }
        public static bool PulseMenu;
        // In-game run limit (s); ORICOOP_HARNESS_SEC raises it for long opt-in scenarios (P3 lever / story sweep).
        static float RunLimit { get { float s; return float.TryParse(Environment.GetEnvironmentVariable("ORICOOP_HARNESS_SEC"), out s) && s > 0 ? s : 420f; } }
        public static int RestoreCheckpointCalls;

        string m_out, m_scenarios;
        int m_slot = 1;
        float m_t, m_inGameT = -1f, m_lastDismiss = -10f;
        bool m_slotChosen, m_started, m_done;
        int m_errors, m_steamLeaderboardErrors, m_hidErrors, m_lastPulse = -1, m_lastDiag = -1;
        readonly List<string> m_lines = new List<string>();
        readonly List<string> m_fails = new List<string>();
        int m_checks;
        internal string ResultPath { get { return m_out; } }
        internal int ErrorCount { get { return m_errors; } }

        void Awake()
        {
            m_out = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT") ?? "oricoop_test.txt";
            m_scenarios = Environment.GetEnvironmentVariable("ORICOOP_SCENARIOS") ?? "lifecycle,interact,menu,visual,bottomhud,companion,move,camera,platform,pause,p2death,revive,teleport,pool,enemy,pickup,p1death,bothdown,title";
            int s; if (int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_SLOT"), out s)) m_slot = s;
            Application.logMessageReceived += OnLog;
            P2Input.BotActive = true;
            Note("test harness up, slot " + m_slot + ", scenarios " + m_scenarios);
        }

        void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
            {
                // Vanilla game bug, unrelated to the mod: Steam leaderboard callback with an unknown board name
                // (stack is Steamworks.Update -> Steamworks.LeaderboardFindResult). Count it, don't fail on it.
                if (stack != null && stack.Contains("Steamworks.LeaderboardFindResult")) { m_steamLeaderboardErrors++; return; }
                // Unity's raw-input layer failing to read a HID device (e.g. a Bluetooth pad): device/driver noise, not the mod.
                if (msg != null && msg.StartsWith("<RI.Hid>")) { m_hidErrors++; return; }
                m_errors++;
                if (m_errors <= 20) Note("UNITY " + type + ": " + msg + " @ " + (stack ?? "").Split('\n')[0]);
            }
        }

        internal void Note(string s) { string l = string.Format("[{0,7:F2}] {1}", m_t, s); m_lines.Add(l); Plugin.Log.LogInfo("TEST " + s); }
        internal void Check(bool ok, string what) { m_checks++; Note((ok ? "PASS " : "FAIL ") + what); if (!ok) m_fails.Add(what); }
        bool Want(string s) { return ("," + m_scenarios + ",").Contains("," + s + ","); }
        static string F(float f) { return f.ToString("F2"); }

        void Update()
        {
            m_t += Time.unscaledDeltaTime;
            Application.runInBackground = true;
            if (m_done) return;
            if (m_t > (m_started ? RunLimit : 150f)) { Check(false, "timed out (inGame=" + (m_inGameT >= 0) + " started=" + m_started + ")"); Finish(); return; }
            if (m_started) return;

            SeinCharacter sein = Characters.Sein;
            bool inGame = sein != null && sein.gameObject.activeInHierarchy && GameController.Instance != null && !GameController.Instance.InputLocked;

            if (m_inGameT < 0f)
            {
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                bool noSlot = Environment.GetEnvironmentVariable("ORICOOP_NEWGAME") == "noslot";
                TitleScreenManager.Screen screen = TitleScreenManager.CurrentScreen;
                if (!m_slotChosen && noSlot && NetPlay.PartnerHere
                    && (screen == TitleScreenManager.Screen.MainMenu || screen == TitleScreenManager.Screen.SaveSlots) && GuestJoin.BeginNoSlot())
                {
                    // run-nettest.ps1 -GuestNoSlot: the title 加入 path without a slot pick (GuestJoin.BeginNoSlot),
                    // started where a player would: the title menu is up.
                    m_slotChosen = true;
                    Note("guest join without a save slot started (title " + screen + ", slot list " + (ui != null) + ")");
                }
                else if (!m_slotChosen && !noSlot && ui != null && ui.IsVisible && ui.Active && ui.Items != null && ui.Items.Count > m_slot)
                {
                    ui.SetCurrentItem(m_slot);
                    if (Environment.GetEnvironmentVariable("ORICOOP_NEWGAME") == "1")
                    {
                        // Empty slot -> difficulty -> new game (run-nettest.ps1 -GuestNewGame: an online guest must
                        // load the host's save from here instead of starting the prologue; GuestJoin.cs).
                        ui.EmptySaveSlotSelected();
                        ui.SetDifficulty(DifficultyMode.Normal);
                        Note("selected EMPTY save slot " + m_slot + " -> Normal -> new game (guest join intercepted=" + GuestJoin.Waiting + ")");
                    }
                    else ui.UsedSaveSlotSelected();
                    m_slotChosen = true;
                    Note("selected save slot " + m_slot);
                }
                else if (!m_slotChosen)
                {
                    int diag = (int)(m_t / 2f);
                    if (diag != m_lastDiag)
                    {
                        m_lastDiag = diag;
                        if (m_t > 8f && (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart || TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu))
                        {
                            TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
                            Note("forced title screen -> SaveSlots");
                        }
                    }
                    int tick = (int)(m_t / 2f);
                    if (tick != m_lastPulse && m_t > 6f) { m_lastPulse = tick; PulseMenu = true; }
                }
                if (inGame) { m_inGameT = m_t; Note("in gameplay, P1 at " + sein.Position); }
                else if (m_slotChosen)
                {
                    // The game opens its Resume screen by itself when the window size changes (windowed launch)
                    // or focus changes; during the test's load phase, dismiss it.
                    if (UI.MainMenuVisible && m_t - m_lastDismiss > 1f)
                    {
                        m_lastDismiss = m_t;
                        UI.Menu.HideResumeScreen();
                        UI.Menu.HideMenuScreen(true);
                        Note("dismissed a Resume screen the game opened during loading");
                    }
                    int diag = (int)(m_t / 2f);
                    if (diag != m_lastDiag)
                    {
                        m_lastDiag = diag;
                        GameController gc = GameController.Instance;
                        Note("loading: state=" + (GameStateMachine.Instance == null ? "?" : GameStateMachine.Instance.CurrentState.ToString())
                            + " sein=" + (sein != null) + (sein != null ? " seinActive=" + sein.gameObject.activeInHierarchy : "")
                            + " inputLocked=" + (gc != null && gc.InputLocked) + " lockInput=" + (gc != null && gc.LockInput) + " lockByAction=" + (gc != null && gc.LockInputByAction)
                            + " scenesLoading=" + (Core.Scenes.Manager != null && Core.Scenes.Manager.IsLoadingScenes)
                            + " timeScale=" + Time.timeScale + " frame=" + Time.frameCount + " focused=" + GameController.IsFocused);
                        if (diag % 15 == 0) Shot("loading_" + diag);
                    }
                }
                return;
            }

            if (m_t - m_inGameT < 4f) return; // let the scene settle
            m_started = true;
            StartCoroutine(Run());
        }

        // --- bot helpers -------------------------------------------------------------------
        static void Bot(float lx, int buttons) { P2Input.BotLX = lx; P2Input.BotButtons = buttons; }

        IEnumerator Wait(float s) { float end = Time.unscaledTime + s; while (Time.unscaledTime < end) yield return null; }

        SeinCharacter P1 { get { return P2Context.P1; } }
        SeinCharacter P2 { get { return P2Context.P2; } }

        IEnumerator Run()
        {
            if (NetPlay.Enabled)
            {
                yield return StartCoroutine(NetTestHarness.Run(this));
                Finish();
                yield break;
            }
            int p1SaveEntries0 = CountSaveEntriesUnder(P2Context.P1 != null ? P2Context.P1.transform : Characters.Sein.transform);
            int total0 = SaveSceneManager.Master.SaveData.Count;
            // Join the way a player does: press Start on P2's controller (bot input goes through P2Input.Sample).
            P2Input.BotButtons = P2Input.BTN_START;
            yield return StartCoroutine(Wait(0.2f));
            P2Input.BotButtons = 0;
            yield return StartCoroutine(Wait(0.5f));
            Check(P2 != null, "P2 joins by pressing Start on the controller");
            if (P2 == null) { Finish(); yield break; }
            Check(P2 != P1, "P2 is a separate SeinCharacter");
            Check(Characters.Sein == P1, "Characters.Sein restored to P1 after spawn");
            // T-032: the build is named (the build scripts embed the commit) and F8 opens the log folder.
            Check(OriCoop.Ui.BuildInfo.Commit != OriCoop.Ui.BuildInfo.Unknown, "build info: DLL names its commit (" + OriCoop.Ui.BuildInfo.Commit + ")");
            string opened = null;
            CoopMenu.OpenFolderHook = delegate(string dir) { opened = dir; };
            CoopMenu.Open();
            yield return StartCoroutine(Wait(0.5f));
            Check(CoopMenu.SettingsLogsRow == 2 + 3 && CoopMenu.SettingsCloseRow == 2 + 4, "F8: two seats, then cutscenes / remove P2 / mode / log folder / close (rows "
                + CoopMenu.SettingsLogsRow + ", " + CoopMenu.SettingsCloseRow + ")");
            CoopMenu.Activate(CoopMenu.SettingsLogsRow);
            Check(opened != null && System.IO.File.Exists(System.IO.Path.Combine(opened, "LogOutput.log")),
                "F8: 打开日志文件夹 opens the folder with LogOutput.log (" + (opened == null ? "not opened" : System.IO.File.Exists(System.IO.Path.Combine(opened, "LogOutput.log")) ? "LogOutput.log found" : "no LogOutput.log there") + ")");
            Check(CoopMenu.IsOpen, "F8: panel stays open after opening the log folder");
            CoopMenu.Close();
            CoopMenu.OpenFolderHook = null;
            yield return StartCoroutine(Wait(1f));
            // Save isolation: P2 must not take over any of P1's checkpoint save entries.
            int p1SaveEntries1 = CountSaveEntriesUnder(P1.transform), p2Entries = CountSaveEntriesUnder(P2.transform);
            Note("save: P1 entries " + p1SaveEntries0 + " -> " + p1SaveEntries1 + ", P2 entries " + p2Entries + ", total " + total0 + " -> "
                + SaveSceneManager.Master.SaveData.Count + ", blocked " + P2Lifecycle.BlockedSaveRegistrations);
            Check(p2Entries == 0, "save: P2 is not registered in the save system");
            Check(p1SaveEntries1 == p1SaveEntries0, "save: P1 keeps all its save entries after P2 joins");
            // Abilities: P2 gets the same ability components as P1 (double jump, bash, glide...).
            string mismatch = "";
            foreach (System.Reflection.FieldInfo f in typeof(SeinPrefabFactory).GetFields())
            {
                if (f.FieldType != typeof(SeinNestedPrefab)) continue;
                SeinNestedPrefab a = (SeinNestedPrefab)f.GetValue(P1.Prefabs), b = (SeinNestedPrefab)f.GetValue(P2.Prefabs);
                if (a != null && b != null && a.IsInstantiated != b.IsInstantiated) mismatch += f.Name + "(P1 " + a.IsInstantiated + ") ";
            }
            Check(mismatch == "", "abilities: P2 has the same ability components as P1 " + mismatch);
            if (Want("lifecycle")) yield return StartCoroutine(Lifecycle());

            if (Want("interact")) yield return StartCoroutine(Interact());
            if (Want("diag")) yield return StartCoroutine(Diag());
            if (Want("menu")) yield return StartCoroutine(Menu());
            if (Want("visual")) yield return StartCoroutine(Visual());
            if (Want("bottomhud")) yield return StartCoroutine(BottomHudScenario());
            if (Want("companion")) yield return StartCoroutine(CompanionStates());
            if (Want("move")) yield return StartCoroutine(Move());
            if (Want("camera")) yield return StartCoroutine(CameraAndEdges());
            if (Want("platform")) yield return StartCoroutine(Platforms());
            if (Want("pause")) yield return StartCoroutine(Pause());
            // p2death / p1death cover the classic rules ([Death] ReviveMode = false); "revive" covers the default.
            if (Want("p2death")) { bool rv = Revive.Enabled; Revive.Enabled = false; yield return StartCoroutine(P2Death()); Revive.Enabled = rv; }
            if (Want("revive")) yield return StartCoroutine(ReviveScenario());
            if (Want("teleport")) yield return StartCoroutine(Teleport());
            if (Want("pool")) yield return StartCoroutine(Pool());
            if (Want("enemy")) yield return StartCoroutine(Enemy());
            if (Want("enemytrigger")) yield return StartCoroutine(EnemyTrigger());
            if (Want("pickup")) yield return StartCoroutine(Pickup());
            if (Want("p1death")) { bool rv = Revive.Enabled; Revive.Enabled = false; yield return StartCoroutine(P1Death()); Revive.Enabled = rv; }
            if (Want("bothdown")) yield return StartCoroutine(BothDown());
            if (Want("title")) yield return StartCoroutine(ReturnToTitle());
            if (Want("signpost")) yield return StartCoroutine(SignpostTest.Run(this));

            Check(P2 != null, "P2 still exists at end");
            Check(!P2Context.Inside, "context stack balanced at end");
            Check(VisualInvariants.Violations == m_visualBaseline, "visual self-check: no P2 visual problems in any scenario (" + (VisualInvariants.Violations - m_visualBaseline) + ": " + string.Join(", ", VisualInvariants.Kinds.ToArray()) + ")");
            Check(m_errors == 0, "no Unity errors/exceptions in whole run (" + m_errors + ")");
            Note("ignored vanilla Steam leaderboard exceptions: " + m_steamLeaderboardErrors + ", Unity HID read errors: " + m_hidErrors);
            Finish();
        }

        IEnumerator Move()
        {
            Note("--- scenario: move");
            int err0 = m_errors;
            Vector3 p1Start = P1.Position, p2Start = P2.Position;
            float maxX = float.MinValue, minX = float.MaxValue, maxY = p2Start.y;
            float t0 = Time.unscaledTime;
            bool shot1 = false, shot2 = false;
            Vector3 afterRight = Vector3.zero;
            while (Time.unscaledTime - t0 < 7f)
            {
                float bt = Time.unscaledTime - t0;
                Bot(0f, 0);
                if (bt < 2f) P2Input.BotLX = 1f;
                if (bt > 1f && bt < 1.25f) P2Input.BotButtons = P2Input.BTN_A;
                if (bt > 2.5f && bt < 4.5f) P2Input.BotLX = -1f;
                if (bt > 3.5f && bt < 3.75f) P2Input.BotButtons = P2Input.BTN_A;
                if (bt > 5f && bt < 5.1f) P2Input.BotButtons = P2Input.BTN_X;
                maxY = Mathf.Max(maxY, P2.Position.y);
                if (bt < 2.2f) maxX = Mathf.Max(maxX, P2.Position.x);
                else if (bt < 4.7f) { if (afterRight == Vector3.zero) afterRight = P2.Position; minX = Mathf.Min(minX, P2.Position.x); }
                if (bt > 1.6f && !shot1) { shot1 = true; Shot("move_right"); }
                if (bt > 5.3f && !shot2) { shot2 = true; Shot("move_flame"); }
                yield return null;
            }
            Bot(0f, 0);
            Check(maxX - p2Start.x > 2f, "move: P2 moved right (peak dx=" + F(maxX - p2Start.x) + ")");
            Check(afterRight.x - minX > 2f, "move: P2 moved left (peak dx=" + F(minX - afterRight.x) + ")");
            Check(maxY - p2Start.y > 0.8f, "move: P2 jumped (rise=" + F(maxY - p2Start.y) + ")");
            Check(Mathf.Abs(P1.Position.x - p1Start.x) < 0.5f, "move: P1 unaffected by P2 input (dx=" + F(P1.Position.x - p1Start.x) + ")");
            Check(Characters.Sein == P1, "move: Characters.Sein is P1 outside P2 callbacks");
            Check(m_errors == err0, "move: no errors");
        }

        IEnumerator Pause()
        {
            Note("--- scenario: pause");
            int err0 = m_errors;
            UI.Menu.ShowMenuScreen();
            yield return StartCoroutine(Wait(1.5f));
            Shot("pause_menu");
            Vector3 p2a = P2.Position, p1a = P1.Position;
            Bot(1f, 0);
            yield return StartCoroutine(Wait(1.5f));
            Bot(0f, 0);
            float dxPaused = P2.Position.x - p2a.x;
            Check(Mathf.Abs(dxPaused) < 0.3f, "pause: P2 frozen while pause menu is open (dx=" + F(dxPaused) + ")");
            Check(Vector3.Distance(P1.Position, p1a) < 0.3f, "pause: P1 frozen while pause menu is open");
            UI.Menu.HideMenuScreen();
            yield return StartCoroutine(Wait(1.5f));
            Vector3 p2b = P2.Position;
            Bot(-1f, 0);
            yield return StartCoroutine(Wait(0.8f));
            Bot(0f, 0);
            Check(Mathf.Abs(P2.Position.x - p2b.x) > 1f, "pause: P2 moves again after resume (dx=" + F(P2.Position.x - p2b.x) + ")");
            Check(m_errors == err0, "pause: no errors");
        }

        IEnumerator P2Death()
        {
            Note("--- scenario: p2death");
            int err0 = m_errors, restores0 = RestoreCheckpointCalls;
            float p1Health = P1.Mortality.Health.Amount;
            // Walk P2 away from P1 first so "respawned next to P1" is meaningful.
            Bot(1f, 0);
            yield return StartCoroutine(Wait(1f));
            Bot(0f, 0);
            yield return StartCoroutine(Wait(0.5f));
            Note("p2death: P2 at " + P2.Position + " P1 at " + P1.Position + " before damage");
            P2.Mortality.DamageReciever.OnRecieveDamage(new Damage(1000f, Vector2.zero, P2.Position, DamageType.Spikes, null));
            yield return StartCoroutine(Wait(4f));
            Check(P2 != null && P2.gameObject.activeInHierarchy, "p2death: P2 alive and active after lethal damage");
            Check(RestoreCheckpointCalls == restores0, "p2death: no checkpoint reload (" + (RestoreCheckpointCalls - restores0) + ")");
            Check(P2.Mortality.Health.Amount >= P2.Mortality.Health.MaxHealth - 0.01f, "p2death: P2 health restored (" + F(P2.Mortality.Health.Amount) + "/" + P2.Mortality.Health.MaxHealth + ")");
            Check(Mathf.Abs(P1.Mortality.Health.Amount - p1Health) < 0.01f, "p2death: P1 health unchanged");
            Check(Vector3.Distance(P1.Position, P2.Position) < 4f, "p2death: P2 respawned next to P1 (d=" + F(Vector3.Distance(P1.Position, P2.Position)) + ")");
            Check(m_errors == err0, "p2death: no errors");
        }

        IEnumerator Teleport()
        {
            Note("--- scenario: teleport");
            int err0 = m_errors;
            RuntimeSceneMetaData target = null;
            float best = float.MaxValue;
            Vector2 here = P1.Position;
            foreach (RuntimeSceneMetaData s in Core.Scenes.Manager.AllScenes)
            {
                if (s == null || s.PlaceholderPosition == Vector2.zero) continue;
                float d = Vector2.Distance(s.PlaceholderPosition, here);
                if (d > 150f && d < best) { best = d; target = s; }
            }
            if (target == null) { Check(false, "teleport: found a scene placeholder 150+ units away"); yield break; }
            Note("teleport: P1 -> scene " + target.Scene + " at " + target.PlaceholderPosition + " (" + F(best) + " away)");
            Plugin.Teleport(P1, target.PlaceholderPosition);
            yield return StartCoroutine(Wait(8f));
            Shot("teleport");
            float d12 = Vector3.Distance(P1.Position, P2.Position);
            Check(d12 < 30f, "teleport: P2 followed P1 to the new area (d=" + F(d12) + ")");
            Check(P1.Position.y > target.PlaceholderPosition.y - 25f, "teleport: P1 did not fall out of the world (y=" + F(P1.Position.y) + ")");
            Check(P2.Position.y > target.PlaceholderPosition.y - 25f, "teleport: P2 did not fall out of the world (y=" + F(P2.Position.y) + ")");
            Check(m_errors == err0, "teleport: no errors");
        }

        void Skip(string what) { Note("SKIP " + what); m_skips.Add(what); }
        readonly List<string> m_skips = new List<string>();

        IEnumerator Pool()
        {
            Note("--- scenario: pool (shared keystones / map stones / energy)");
            int err0 = m_errors;
            yield return null;
            int k0 = P1.Inventory.Keystones;
            float e0 = P1.Energy.Current;
            P1.Inventory.Keystones += 2;
            yield return null; yield return null;
            Check(P2.Inventory.Keystones == k0 + 2, "pool: P1 gaining 2 keystones shows on P2 (" + P2.Inventory.Keystones + ")");
            P2.Inventory.Keystones -= 1;
            yield return null; yield return null;
            Check(P1.Inventory.Keystones == k0 + 1, "pool: P2 spending 1 keystone is taken from P1 (" + P1.Inventory.Keystones + ")");
            P1.Energy.Current = Mathf.Min(P1.Energy.Max, 2f);
            yield return null; yield return null;
            float before = P1.Energy.Current;
            P2.Energy.Spend(1f);
            yield return null; yield return null;
            Check(Mathf.Abs(P1.Energy.Current - (before - 1f)) < 0.01f && Mathf.Abs(P2.Energy.Current - P1.Energy.Current) < 0.01f,
                "pool: P2 spending 1 energy comes out of the shared pool (P1 " + F(P1.Energy.Current) + ", P2 " + F(P2.Energy.Current) + ")");
            P1.Inventory.Keystones = k0; P1.Energy.Current = e0;
            yield return null; yield return null;
            Check(m_errors == err0, "pool: no errors");
        }

        // Older trigger-based variant; kept for areas whose enemies use NearSeinTrigger (scenario "enemytrigger").
        IEnumerator EnemyTrigger()
        {
            Note("--- scenario: enemytrigger (enemy NearSeinTrigger reacts to the nearest Ori)");
            int err0 = m_errors;
            // An enemy's NearSeinTrigger fires when "Sein" is within TriggerDistance. Put P2 inside that range and
            // P1 outside it: without the mod the trigger can only ever see P1, so it would stay off.
            System.Reflection.FieldInfo trig = AccessTools.Field(typeof(NearSeinTrigger), "m_eventTriggered");
            System.Reflection.FieldInfo recvField = AccessTools.Field(typeof(NearSeinTrigger), "m_recievers");
            int allTriggers = 0;
            foreach (NearSeinTrigger t in FindObjectsOfType<NearSeinTrigger>()) allTriggers++;
            Note("enemy: " + allTriggers + " NearSeinTriggers loaded, " + FindObjectsOfType<EntityController>().Length + " EntityControllers loaded");
            NearSeinTrigger best = null; float bestD = float.MaxValue;
            // Try the loaded area first, then hop through up to 8 other areas until an enemy is loaded.
            List<RuntimeSceneMetaData> hops = new List<RuntimeSceneMetaData>();
            foreach (RuntimeSceneMetaData s in Core.Scenes.Manager.AllScenes)
                if (s != null && s.PlaceholderPosition != Vector2.zero && Vector2.Distance(s.PlaceholderPosition, P1.Position) > 60f) hops.Add(s);
            Vector2 origin = P1.Position;
            hops.Sort((a, b) => Vector2.Distance(a.PlaceholderPosition, origin).CompareTo(Vector2.Distance(b.PlaceholderPosition, origin)));
            for (int hop = 0; hop <= 8 && best == null; hop++)
            {
                if (hop > 0)
                {
                    if (hop - 1 >= hops.Count) break;
                    RuntimeSceneMetaData s = hops[hop - 1];
                    Note("enemy: hopping to " + s.Scene + " to look for an enemy");
                    Plugin.Teleport(P1, s.PlaceholderPosition);
                    Plugin.Teleport(P2, s.PlaceholderPosition);
                    yield return StartCoroutine(Wait(4f));
                    Note("enemy: now loaded " + FindObjectsOfType<NearSeinTrigger>().Length + " NearSeinTriggers, " + FindObjectsOfType<EntityController>().Length
                        + " EntityControllers, " + FindObjectsOfType<Entity>().Length + " Entities, " + Resources.FindObjectsOfTypeAll<EntityController>().Length + " EntityControllers incl. inactive");
                }
                foreach (NearSeinTrigger t in FindObjectsOfType<NearSeinTrigger>())
                {
                    if (t != null)
                    {
                        Component[] rv = (Component[])recvField.GetValue(t);
                        string types = "";
                        if (rv != null) foreach (Component r in rv) types += (r == null ? "null" : r.GetType().Name) + " ";
                        Note("enemy: candidate " + t.name + " range=" + F(t.TriggerDistance) + " triggered=" + trig.GetValue(t) + " fish=" + (t.GetComponentInParent<FishEnemy>() != null) + " receivers=[" + types.Trim() + "]");
                    }
                    if (t == null || !t.isActiveAndEnabled || t.TriggerDistance < 2f || t.TriggerDistance > 30f) continue;
                    if (t.GetComponentInParent<FishEnemy>() != null) continue; // underwater; P1/P2 would be swimming
                    // Must notify an enemy: look at the trigger's actual receiver list.
                    Component[] recv = (Component[])recvField.GetValue(t);
                    bool enemy = false;
                    if (recv != null) foreach (Component r in recv) if (r is EntityController) { enemy = true; break; }
                    if (!enemy) continue;
                    if ((bool)trig.GetValue(t)) continue;                   // already triggered by P1
                    float d = Vector3.Distance(t.transform.position, P1.Position);
                    if (d < bestD) { bestD = d; best = t; }
                }
            }
            if (best == null) { Skip("enemy: no idle land enemy found in 9 areas"); yield break; }
            Plugin.LeashOverride = 200f;
            float range = best.TriggerDistance;
            Vector3 tp = best.transform.position;
            Note("enemy: trigger on " + best.transform.root.name + "/" + best.name + " range " + F(range) + ", " + F(bestD) + " from P1");
            bool fired = false, p1Inside = false;
            float end = Time.unscaledTime + 3f;
            while (Time.unscaledTime < end && !fired)
            {
                // Hold the positions every frame (gravity/AI would move them otherwise).
                Plugin.Teleport(P1, tp + new Vector3(range + 6f, 0f, 0f));
                Plugin.Teleport(P2, tp + new Vector3(0.5f, 0f, 0f));
                if (Vector3.Distance(P1.Position, tp) < range) p1Inside = true;
                fired = best != null && (bool)trig.GetValue(best);
                yield return null;
            }
            Shot("enemy");
            Plugin.LeashOverride = -1f;
            if (p1Inside) Skip("enemy: P1 drifted inside the trigger range; result not meaningful");
            else Check(fired, "enemy: enemy detected P2 while P1 was out of range");
            Check(Characters.Sein == P1 && !P2Context.Inside, "enemy: globals restored after enemy callbacks");
            Check(m_errors == err0, "enemy: no errors");
        }

        IEnumerator Enemy()
        {
            Note("--- scenario: enemy (enemy AI sees the nearest Ori as its target)");
            int err0 = m_errors;
            // This checks who the enemy targets, not damage. With revive rules an Ori shot dead here would stay downed
            // for the rest of the run (the spitter fires for seconds), so both start healthy and invincible.
            P1.Mortality.Health.RestoreAllHealth(); P2.Mortality.Health.RestoreAllHealth();
            P1.Mortality.DamageReciever.MakeInvincible(25f); P2.Mortality.DamageReciever.MakeInvincible(25f);
            List<RuntimeSceneMetaData> hops = new List<RuntimeSceneMetaData>();
            foreach (RuntimeSceneMetaData s in Core.Scenes.Manager.AllScenes)
                if (s != null && s.PlaceholderPosition != Vector2.zero && Vector2.Distance(s.PlaceholderPosition, P1.Position) > 60f) hops.Add(s);
            Vector2 origin = P1.Position;
            hops.Sort((a, b) => Vector2.Distance(a.PlaceholderPosition, origin).CompareTo(Vector2.Distance(b.PlaceholderPosition, origin)));
            EntityController enemy = null;
            for (int hop = 0; hop <= 8 && enemy == null; hop++)
            {
                if (hop > 0)
                {
                    if (hop - 1 >= hops.Count) break;
                    RuntimeSceneMetaData s = hops[hop - 1];
                    Note("enemy: hopping to " + s.Scene + " to look for an enemy");
                    Plugin.Teleport(P1, s.PlaceholderPosition);
                    Plugin.Teleport(P2, s.PlaceholderPosition);
                    yield return StartCoroutine(Wait(4f));
                }
                float bestD = float.MaxValue;
                foreach (EntityController ec in FindObjectsOfType<EntityController>())
                {
                    if (ec == null || !ec.isActiveAndEnabled || ec.Entity == null || ec.Entity is FishEnemy) continue;
                    float d = Vector3.Distance(ec.transform.position, P1.Position);
                    if (d < bestD) { bestD = d; enemy = ec; }
                }
            }
            if (enemy == null) { Skip("enemy: no active land enemy found in 9 areas"); yield break; }
            Note("enemy: using " + enemy.Entity.GetType().Name + " '" + enemy.name + "'");
            Plugin.LeashOverride = 200f;
            int id = enemy.GetInstanceID();

            // Phase A: P2 beside the enemy, P1 15 units away -> the enemy's update must see P2.
            SeenByEnemy.Remove(id);
            int sawP2 = 0, sawP1 = 0, frames = 0;
            float end = Time.unscaledTime + 1.5f;
            while (Time.unscaledTime < end && enemy != null)
            {
                Vector3 ep = enemy.transform.position;
                Plugin.Teleport(P2, ep + new Vector3(1f, 0f, 0f));
                Plugin.Teleport(P1, ep + new Vector3(-15f, 0f, 0f));
                yield return new WaitForFixedUpdate();
                SeinCharacter seen;
                if (SeenByEnemy.TryGetValue(id, out seen)) { frames++; if (seen == P2) sawP2++; else if (seen == P1) sawP1++; }
            }
            Shot("enemy_p2_near");
            Note("enemy phase A (P2 near): saw P2 " + sawP2 + ", saw P1 " + sawP1 + " of " + frames + " updates");
            Check(frames > 0 && sawP2 > frames * 0.8f, "enemy: AI targets P2 when P2 is the nearer Ori");

            // Phase B: swap them -> the enemy must see P1 again.
            sawP2 = 0; sawP1 = 0; frames = 0;
            end = Time.unscaledTime + 1.5f;
            while (Time.unscaledTime < end && enemy != null)
            {
                Vector3 ep = enemy.transform.position;
                Plugin.Teleport(P1, ep + new Vector3(1f, 0f, 0f));
                Plugin.Teleport(P2, ep + new Vector3(-15f, 0f, 0f));
                yield return new WaitForFixedUpdate();
                SeinCharacter seen;
                if (SeenByEnemy.TryGetValue(id, out seen)) { frames++; if (seen == P2) sawP2++; else if (seen == P1) sawP1++; }
            }
            Note("enemy phase B (P1 near): saw P1 " + sawP1 + ", saw P2 " + sawP2 + " of " + frames + " updates");
            Check(frames > 0 && sawP1 > frames * 0.8f, "enemy: AI targets P1 when P1 is the nearer Ori");
            Plugin.LeashOverride = -1f;
            Plugin.Teleport(P2, P1.Position);
            yield return StartCoroutine(Wait(0.5f));
            Check(Characters.Sein == P1 && !P2Context.Inside, "enemy: globals restored after enemy callbacks");
            Check(m_errors == err0, "enemy: no errors");
        }

        IEnumerator Pickup()
        {
            Note("--- scenario: pickup (P2 collects for the shared save)");
            int err0 = m_errors, byP2 = SharedProgress.PickupsByP2;
            // This runs in the enemy scenario's area (a ranged enemy nearby). Under revive rules a death would leave the
            // Ori downed, which is not what this scenario tests: start healthy and briefly invincible.
            P1.Mortality.Health.RestoreAllHealth(); P2.Mortality.Health.RestoreAllHealth();
            P1.Mortality.DamageReciever.MakeInvincible(10f); P2.Mortality.DamageReciever.MakeInvincible(10f);
            PickupBase best = null; float bestD = float.MaxValue;
            // Include inactive ones: frustum-optimized pickups switch themselves off while off-screen. Skip template
            // clones parked at the origin and anything far away: when the area had not streamed in yet, the old search
            // took an inactive "healthContainerPickup(Clone)" at (0,0,0) ~290 units off and failed (2026-11-18, 1 run
            // in 5). Wait a few seconds for a real one; otherwise report SKIP, not FAIL.
            float searchEnd = Time.unscaledTime + 8f;
            while (best == null)
            {
                foreach (PickupBase pb in Resources.FindObjectsOfTypeAll<PickupBase>())
                {
                    if (pb == null || pb.IsCollected || !pb.gameObject.scene.IsValid() || !pb.gameObject.scene.isLoaded) continue;
                    if (pb.transform.parent != null && !pb.transform.parent.gameObject.activeInHierarchy) continue;
                    if (pb.transform.position == Vector3.zero) continue;
                    float d = Vector3.Distance(pb.transform.position, P1.Position);
                    if (d < 60f && d < bestD) { bestD = d; best = pb; }
                }
                if (best != null || Time.unscaledTime >= searchEnd) break;
                float retryAt = Time.unscaledTime + 0.5f; while (Time.unscaledTime < retryAt) yield return null;
            }
            if (best == null) { Skip("pickup: no uncollected pickup within 60 units of P1 in loaded scenes"); yield break; }
            Note("pickup: using " + best.name + " (" + best.GetType().Name + ") " + F(bestD) + " from P1");
            // P1 stays where it is (it must not touch the pickup); the leash is loosened so P2 can reach it.
            Vector3 pp = best.transform.position;
            Vector3 p1Pos = P1.Position;
            Plugin.LeashOverride = 200f;
            float end = Time.unscaledTime + 3f;
            int frame = 0;
            while (Time.unscaledTime < end && best != null && !best.IsCollected)
            {
                if (best.gameObject.activeInHierarchy && best.transform.position != Vector3.zero) pp = best.transform.position;
                Plugin.Teleport(P2, pp - new Vector3(0f, 0.5f, 0f));
                Plugin.Teleport(P1, p1Pos - new Vector3(0f, 0.5f, 0f));
                if (frame++ % 15 == 0)
                    Note("pickup diag: P2 " + P2.Position + " pickup " + best.transform.position + " d=" + F(Vector3.Distance(P2.Position, best.transform.position))
                        + " r=" + F(best.Radius) + " active=" + best.gameObject.activeInHierarchy + " enabled=" + best.enabled + " P2active=" + P2.gameObject.activeInHierarchy);
                yield return null;
            }
            Shot("pickup");
            Plugin.LeashOverride = -1f;
            Check(Vector3.Distance(P1.Position, pp) > 5f, "pickup: P1 was nowhere near the pickup (" + F(Vector3.Distance(P1.Position, pp)) + ")");
            Check(best == null || best.IsCollected, "pickup: P2 touching the pickup collected it");
            Check(SharedProgress.PickupsByP2 > byP2, "pickup: counted as collected by P2 (" + (SharedProgress.PickupsByP2 - byP2) + ")");
            Check(m_errors == err0, "pickup: no errors");
        }

        public static bool P1HoldSpiritFlame;

        static void Hurt(SeinCharacter s, float amount)
        {
            s.Mortality.DamageReciever.OnRecieveDamage(new Damage(amount, Vector2.zero, s.Position, DamageType.Spikes, null));
        }

        IEnumerator HoldP1(float seconds) { P1HoldSpiritFlame = true; yield return StartCoroutine(Wait(seconds)); P1HoldSpiritFlame = false; yield return StartCoroutine(Wait(0.1f)); }
        IEnumerator HoldP2(float seconds) { Bot(0f, P2Input.BTN_X); yield return StartCoroutine(Wait(seconds)); Bot(0f, 0); yield return StartCoroutine(Wait(0.1f)); }

        // Downed / revive, the owner's rules. Each step checks one rule.
        IEnumerator ReviveScenario()
        {
            Note("--- scenario: revive (downed skull, hold Spirit Flame to revive with half your health)");
            int err0 = m_errors, restores0 = RestoreCheckpointCalls, revives0 = Revive.Revives;
            float saveLeash = Plugin.LeashOverride;
            Plugin.LeashOverride = 200f;
            Plugin.Teleport(P2, P1.Position + new Vector3(1.5f, 0f, 0f));
            P1.Mortality.Health.RestoreAllHealth(); P2.Mortality.Health.RestoreAllHealth();
            yield return StartCoroutine(Wait(1.5f));

            // 1) P2 dies while P1 is up: downed, skull, no reload.
            Vector3 p2Ground = P2.Position;
            Hurt(P2, 1000f);
            yield return StartCoroutine(Wait(0.6f));
            Revive.Downed d2 = Revive.DownedOf(2);
            Check(d2 != null && !P2.gameObject.activeSelf, "revive: P2's death leaves P2 downed (not respawned at once)");
            Check(RestoreCheckpointCalls == restores0, "revive: no checkpoint reload while P1 is up");
            Check(ReviveHud.SkullShown(2), "revive: a skull marker is shown for P2");
            Shot("revive_skull");
            if (d2 == null) { Plugin.LeashOverride = saveLeash; yield break; }
            Check(Vector3.Distance(d2.Marker, p2Ground) < 2f, "revive: skull is at P2's last solid ground (d=" + F(Vector3.Distance(d2.Marker, p2Ground)) + ")");

            // 2) Holding far from the skull does nothing.
            Plugin.Teleport(P1, d2.Marker + new Vector3(8f, 0f, 0f));
            yield return StartCoroutine(Wait(0.8f));
            yield return StartCoroutine(HoldP1(0.6f));
            Check(d2.Progress == 0f, "revive: holding Spirit Flame away from the skull does nothing");

            // 3) Hold near the skull for half the time: ~50%, health drained by ~1/4 of the start health; release keeps it.
            Plugin.Teleport(P1, d2.Marker + new Vector3(0.8f, 0f, 0f));
            yield return StartCoroutine(Wait(0.8f));
            float h0 = P1.Mortality.Health.Amount;
            Check(ReviveHud.LastHintText.Length > 0, "revive: a hold-to-revive hint is shown near the skull ('" + ReviveHud.LastHintText + "')");
            yield return StartCoroutine(HoldP1(0.75f));
            float pHalf = d2.Progress, hHalf = P1.Mortality.Health.Amount;
            Shot("revive_progress");
            Note("revive: after 0.75 s hold: progress " + F(pHalf * 100f) + "%, P1 health " + F(h0) + " -> " + F(hHalf) + " (" + ReviveHud.LastPercentText + ")");
            Check(pHalf > 0.35f && pHalf < 0.65f, "revive: progress advances while holding (about half after half the time)");
            Check(Mathf.Abs((h0 - hHalf) - h0 / 2f * pHalf) < 0.6f, "revive: saver's health drains in step with progress (H0/2 x progress)");
            yield return StartCoroutine(Wait(0.6f));
            Check(Mathf.Abs(d2.Progress - pHalf) < 0.01f && Mathf.Abs(P1.Mortality.Health.Amount - hHalf) < 0.01f, "revive: releasing pauses; progress and health stay");

            // 4) Taking damage while holding interrupts; progress and drained health stay; holding on does nothing.
            P1HoldSpiritFlame = true;
            yield return StartCoroutine(Wait(0.2f));
            int int0 = Revive.Interrupts;
            Hurt(P1, 4f);
            yield return StartCoroutine(Wait(0.1f));
            float pHit = d2.Progress;
            yield return StartCoroutine(Wait(0.4f));
            P1HoldSpiritFlame = false;
            yield return StartCoroutine(Wait(0.1f));
            Check(Revive.Interrupts == int0 + 1, "revive: damage to the saver interrupts the revive");
            Check(Mathf.Abs(d2.Progress - pHit) < 0.01f && d2.Progress > pHalf, "revive: after the hit, holding on doesn't continue; progress is kept (" + F(d2.Progress * 100f) + "%)");

            // 5) Press again and hold to 100%: P2 back next to P1, with the drained health; P1 paid half its start health.
            float givenBefore = d2.Given;
            yield return StartCoroutine(HoldP1(1.5f));
            Check(Revive.DownedOf(2) == null && P2.gameObject.activeSelf, "revive: holding again completes the revive");
            Check(Revive.Revives == revives0 + 1, "revive: counted one revive");
            float given = d2.Given;
            Note("revive: total health given " + F(given) + " (H0/2 = " + F(h0 / 2f) + "), P2 health " + F(P2.Mortality.Health.Amount) + ", P1 health " + F(P1.Mortality.Health.Amount));
            Check(Mathf.Abs(given - h0 / 2f) < 0.6f, "revive: total drained = half the saver's health at the start");
            Check(Mathf.Abs(P2.Mortality.Health.Amount - Mathf.Max(1f, given)) < 0.6f, "revive: revived P2 has the drained health");
            Check(Vector3.Distance(P2.Position, P1.Position) < 2.5f, "revive: P2 comes back next to the saver");
            yield return StartCoroutine(Wait(1.5f));

            // 6) P1 dies while P2 is up: P1 downed (hidden, immune, frozen); camera frames P2; P2 revives P1 with X.
            P1.Mortality.Health.RestoreAllHealth(); P2.Mortality.Health.RestoreAllHealth();
            Plugin.Teleport(P2, P1.Position + new Vector3(1.5f, 0f, 0f));
            yield return StartCoroutine(Wait(1.5f));
            Hurt(P1, 1000f);
            yield return StartCoroutine(Wait(0.6f));
            Revive.Downed d1 = Revive.DownedOf(1);
            Check(d1 != null && RestoreCheckpointCalls == restores0, "revive: P1's death while P2 is up leaves P1 downed (no reload)");
            if (d1 != null)
            {
                int visible = 0; foreach (Renderer r in P1.GetComponentsInChildren<Renderer>()) if (r.enabled) visible++;
                Check(visible == 0, "revive: downed P1 is hidden (" + visible + " renderers on)");
                Hurt(P1, 1000f);
                yield return StartCoroutine(Wait(0.3f));
                Check(Revive.DownedOf(1) != null && RestoreCheckpointCalls == restores0, "revive: downed P1 ignores further damage");
                Check(CoopCamera.FocusOverride == P2, "revive: camera frames the living P2 while P1 is down");
                Check(P2IsNearerProbe(), "revive: enemies and proximity triggers see P2 while P1 is down");
                Plugin.Teleport(P2, d1.Marker + new Vector3(0.8f, 0f, 0f));
                yield return StartCoroutine(Wait(0.8f));
                Bot(0f, P2Input.BTN_X);
                yield return StartCoroutine(Wait(0.4f));
                Note("revive diag: P2 " + P2.Position + " marker " + d1.Marker + " near=" + Revive.SaverNear(P2, d1) + " holdingInput=" + Revive.SaverHolding(P2) + " P2active=" + P2.gameObject.activeInHierarchy + " P2health=" + F(P2.Mortality.Health.Amount) + " progress=" + F(d1.Progress) + " needRelease=" + d1.NeedRelease + " suspended=" + GameController.Instance.GameplaySuspended);
                yield return StartCoroutine(Wait(1.3f));
                Bot(0f, 0); yield return StartCoroutine(Wait(0.1f));
                Check(Revive.DownedOf(1) == null, "revive: P2 revives P1 by holding X (progress " + F(d1.Progress * 100f) + "%)");
                Check(PartnerFx.RevivesPlayed >= 2 && PartnerFx.ReviveEffects >= 2 && PartnerFx.ReviveSounds >= 2, "revive: the game's respawn glow and a native sound play on revive (" + PartnerFx.LastReviveEffect + ", " + PartnerFx.LastReviveSound + ")");
                yield return StartCoroutine(Wait(PartnerFx.GlowSeconds + PartnerFx.FadeSeconds)); // glow, then the Ori reappears
                int vis2 = 0; foreach (Renderer r in P1.GetComponentsInChildren<Renderer>()) if (r.enabled) vis2++;
                Check(vis2 > 0 && P1.Mortality.Health.Amount >= 1f && !P1.Controller.IsSuspended, "revive: revived P1 is visible, has health and isn't frozen");
                Check(CoopCamera.FocusOverride == null, "revive: camera frames both again");
            }
            yield return StartCoroutine(Wait(1f));
            P1.Mortality.Health.RestoreAllHealth(); P2.Mortality.Health.RestoreAllHealth(); // later scenarios start healthy
            Plugin.LeashOverride = saveLeash;
            Check(m_errors == err0, "revive: no errors");
        }

        static bool P2IsNearerProbe() { return P2Context.P2IsNearer(P2Context.P1.Position); }

        // Both down -> the game's normal death and checkpoint reload, from a checkpoint P2 created; both come back.
        IEnumerator BothDown()
        {
            Note("--- scenario: bothdown (both down -> reload at the last checkpoint either touched)");
            if (DifficultyController.Instance.Difficulty == DifficultyMode.OneLife) { Note("bothdown: skipped on One Life difficulty"); yield break; }
            int err0 = m_errors, restores0 = RestoreCheckpointCalls, cps0 = Revive.CheckpointsByP2;
            float saveLeash = Plugin.LeashOverride;
            Plugin.LeashOverride = 200f;
            P1.Mortality.Health.RestoreAllHealth(); P2.Mortality.Health.RestoreAllHealth();
            // P2 walks off and a checkpoint is created by P2 (as a soul link / spirit well / checkpoint zone would).
            Bot(1f, 0); yield return StartCoroutine(Wait(1.2f)); Bot(0f, 0);
            yield return StartCoroutine(Wait(1f));
            Vector3 p2Spot = P2.Position, p1Spot = P1.Position;
            P2Context.Enter();
            try { GameController.Instance.CreateCheckpoint(); } finally { P2Context.Exit(); }
            Check(Revive.CheckpointsByP2 == cps0 + 1 && Vector3.Distance(P1.Position, p1Spot) < 0.1f, "checkpoint: one created by P2 (P1 put back after the snapshot)");
            Note("bothdown: checkpoint at P2 " + p2Spot + ", P1 was at " + p1Spot + " (" + F(Vector3.Distance(p1Spot, p2Spot)) + " apart)");
            yield return StartCoroutine(Wait(0.5f));
            Hurt(P2, 1000f);
            yield return StartCoroutine(Wait(0.6f));
            Check(Revive.DownedOf(2) != null && RestoreCheckpointCalls == restores0, "bothdown: first death -> downed, no reload");
            Hurt(P1, 1000f);
            float end = Time.unscaledTime + 25f;
            while (Time.unscaledTime < end && RestoreCheckpointCalls == restores0) yield return null;
            Check(RestoreCheckpointCalls > restores0, "bothdown: second death -> the game's checkpoint reload");
            end = Time.unscaledTime + 25f;
            while (Time.unscaledTime < end && (Characters.Sein == null || GameController.Instance.InputLocked || !Characters.Sein.gameObject.activeInHierarchy)) yield return null;
            yield return StartCoroutine(Wait(3f));
            Check(!Revive.AnyDowned && P1 != null && P2 != null && P2.gameObject.activeInHierarchy, "bothdown: both Oris back after the reload");
            if (P1 != null && P2 != null)
            {
                Note("bothdown: P1 respawned at " + P1.Position + " (checkpoint spot " + p2Spot + ")");
                Check(Vector3.Distance(P1.Position, p2Spot) < 2.5f, "bothdown: respawn at the checkpoint P2 created (d=" + F(Vector3.Distance(P1.Position, p2Spot)) + ")");
                Check(P2.Mortality.Health.Amount >= P2.Mortality.Health.MaxHealth - 0.01f && Vector3.Distance(P1.Position, P2.Position) < 3f, "bothdown: P2 back next to P1 with full health");
            }
            Plugin.LeashOverride = saveLeash;
            Check(m_errors == err0, "bothdown: no errors");
        }

        IEnumerator P1Death()
        {
            Note("--- scenario: p1death");
            if (DifficultyController.Instance.Difficulty == DifficultyMode.OneLife) { Note("p1death: skipped on One Life difficulty"); yield break; }
            int err0 = m_errors, restores0 = RestoreCheckpointCalls;
            P1.Mortality.DamageReciever.OnRecieveDamage(new Damage(1000f, Vector2.zero, P1.Position, DamageType.Spikes, null));
            float end = Time.unscaledTime + 25f;
            while (Time.unscaledTime < end && RestoreCheckpointCalls == restores0) yield return null;
            Check(RestoreCheckpointCalls > restores0, "p1death: P1 death reloads the checkpoint");
            // Wait for the reload to finish and gameplay to resume.
            end = Time.unscaledTime + 25f;
            while (Time.unscaledTime < end && (Characters.Sein == null || GameController.Instance.InputLocked || !Characters.Sein.gameObject.activeInHierarchy)) yield return null;
            yield return StartCoroutine(Wait(4f));
            Shot("p1death_after");
            Check(Characters.Sein != null && Characters.Sein == P1, "p1death: Characters.Sein is P1 after reload");
            Check(P2 != null && P2.gameObject.activeInHierarchy, "p1death: P2 still present after reload");
            if (P1 != null && P2 != null)
                Check(Vector3.Distance(P1.Position, P2.Position) < 30f, "p1death: P2 near P1 after reload (d=" + F(Vector3.Distance(P1.Position, P2.Position)) + ")");
            Check(m_errors == err0, "p1death: no errors");
        }

        // Records who "the" Sein was when a game trigger performs its action.
        public class RecorderAction : ActionMethod
        {
            public List<string> Log = new List<string>();
            public override void Perform(IContext context)
            {
                Log.Add(Characters.Sein == P2Context.P2 ? "P2" : Characters.Sein == P2Context.P1 ? "P1" : "?");
            }
        }

        IEnumerator Interact()
        {
            Note("--- scenario: interact (companion, touch context, shared zones, cutscene, bash, join guard)");
            int err0 = m_errors;

            // Companion: P2 has its own, it follows P2, P1 keeps the original.
            Ori comp = P2Context.P2Companion;
            Check(comp != null && comp != Characters.Ori, "companion: P2 has its own spirit companion");
            if (comp != null)
            {
                Plugin.Teleport(P2, P1.Position + new Vector3(8f, 0f, 0f));
                yield return StartCoroutine(Wait(2.5f));
                float dP2 = Vector3.Distance(comp.transform.position, P2.Position), dP1 = Vector3.Distance(Characters.Ori.transform.position, P1.Position);
                Note("companion: P2's companion " + F(dP2) + " from P2, P1's companion " + F(dP1) + " from P1");
                Check(dP2 < 5f, "companion: P2's companion follows P2");
                Check(dP1 < 5f, "companion: P1's companion still follows P1");
                Plugin.Teleport(P2, P1.Position + new Vector3(1f, 0f, 0f));
                yield return StartCoroutine(Wait(0.5f));
            }

            // Touch context + shared enter/exit zone, using the game's own SeinEnterExitTrigger.
            GameObject zone = new GameObject("oricoop_test_zone");
            Collider p2col = P2.GetComponentInChildren<Collider>();
            int oriLayer = p2col != null ? p2col.gameObject.layer : P1.gameObject.layer, zl = -1;
            foreach (Collider sc in UnityEngine.Object.FindObjectsOfType<Collider>()) if (sc.isTrigger && sc.gameObject.layer != oriLayer && !Physics.GetIgnoreLayerCollision(sc.gameObject.layer, oriLayer) && sc.GetComponent<SeinEnterExitTrigger>() != null) { zl = sc.gameObject.layer; break; }
            if (zl < 0) foreach (Collider sc in UnityEngine.Object.FindObjectsOfType<Collider>()) if (sc.isTrigger && !sc.transform.IsChildOf(P1.transform) && !sc.transform.IsChildOf(P2.transform) && !Physics.GetIgnoreLayerCollision(sc.gameObject.layer, oriLayer)) { zl = sc.gameObject.layer; break; }
            if (zl < 0) zl = 0;
            zone.layer = zl;
            Note("zone: using layer " + zl);
            Rigidbody zrb = zone.AddComponent<Rigidbody>(); zrb.isKinematic = true; zrb.useGravity = false;
            Note("zone: P2 collider " + (p2col == null ? "none" : p2col.GetType().Name + " on " + p2col.gameObject.name + " layer " + p2col.gameObject.layer + " tag " + p2col.tag + " trigger=" + p2col.isTrigger));
            BoxCollider box = zone.AddComponent<BoxCollider>();
            box.isTrigger = true; box.size = new Vector3(3f, 3f, 20f);
            Vector3 zonePos = P1.Position + new Vector3(-12f, 0f, 0f);
            zone.transform.position = zonePos;
            RecorderAction onEnter = zone.AddComponent<RecorderAction>(), onExit = zone.AddComponent<RecorderAction>();
            SeinEnterExitTrigger trig = zone.AddComponent<SeinEnterExitTrigger>();
            trig.OnEnterAction = onEnter; trig.OnExitAction = onExit;
            yield return StartCoroutine(Wait(0.3f));
            Vector3 p1Home = P1.Position;
            yield return StartCoroutine(HoldFor(P2, zonePos, 0.5f));
            if (onEnter.Log.Count == 0) { Skip("zone: test trigger got no physics events on this layer"); }
            else
            {
                Check(onEnter.Log.Count == 1 && onEnter.Log[0] == "P2", "touch: a game trigger entered by P2 runs as P2 (" + string.Join(",", onEnter.Log.ToArray()) + ")");
                Vector3 p1In = zonePos + new Vector3(0.6f, 0f, 0f);
                yield return StartCoroutine(HoldBoth(zonePos, p1In, 0.5f));
                Check(onEnter.Log.Count == 1, "zone: second Ori entering doesn't fire enter again");
                Vector3 p2Out = zonePos + new Vector3(0f, 8f, 0f);
                yield return StartCoroutine(HoldBoth(p2Out, p1In, 0.5f));
                Check(onExit.Log.Count == 0, "zone: P2 leaving while P1 is inside doesn't fire exit");
                yield return StartCoroutine(HoldBoth(p2Out, p1Home, 0.5f));
                yield return StartCoroutine(Wait(0.5f));
                Note("zone: enter " + onEnter.Log.Count + " exit " + onExit.Log.Count + " occupants " + P2Extras.OccupantsOf(trig) + " P1 now " + P1.Position + " zone " + zonePos);
                Check(onExit.Log.Count == 1, "zone: exit fires when the last Ori leaves");
                Plugin.Teleport(P2, P1.Position + new Vector3(1f, 0f, 0f));
            }
            UnityEngine.Object.Destroy(zone);
            yield return StartCoroutine(Wait(0.5f));

            // Cutscene trigger: P2 doesn't start story cutscenes.
            GameObject cz = new GameObject("oricoop_test_cutscene");
            cz.AddComponent<BoxCollider>().isTrigger = true;
            CutsceneTrigger ct = cz.AddComponent<CutsceneTrigger>();
            int ign0 = P2Extras.CutscenesIgnored;
            Vector3 p2Before = P2.Position;
            // This checks the default (option off) whatever the config says.
            bool cutOpt = Plugin.Instance.P2CanTriggerCutscenes;
            Plugin.Instance.P2CanTriggerCutscenes = false;
            try { ct.OnTriggerEnter(P2.GetComponentInChildren<Collider>()); }
            finally { Plugin.Instance.P2CanTriggerCutscenes = cutOpt; }
            Check(P2Extras.CutscenesIgnored == ign0 + 1 && Vector3.Distance(P2.Position, p2Before) < 0.01f, "cutscene: P2 touching a cutscene trigger is ignored");
            UnityEngine.Object.Destroy(cz);

            // Bash minigame spawned by P2 belongs to P2 (its stick/button reads use P2's input).
            bool hadBash = P1.PlayerAbilities.Bash.HasAbility;
            if (!hadBash) { P1.PlayerAbilities.SetAbility(AbilityType.Bash, true); yield return StartCoroutine(Wait(1.2f)); }
            Note("bash: P1 has=" + P1.PlayerAbilities.Bash.HasAbility + " P2 has=" + P2.PlayerAbilities.Bash.HasAbility + " P1 comp=" + (P1.Abilities.Bash != null) + " P2 comp=" + (P2.Abilities.Bash != null) + " P2 prefab=" + P2.Prefabs.Bash.IsInstantiated);
            SeinBashAttack bash = P2.Abilities.Bash;
            if (bash == null || bash.BashAttackGamePrefab == null) Skip("bash: P2 has no Bash ability on this save");
            else
            {
                P2Context.Enter();
                GameObject g = (GameObject)InstantiateUtility.Instantiate(bash.BashAttackGamePrefab);
                P2Context.Exit();
                Check(P2Context.BelongsToP2(g.transform), "bash: the aiming minigame P2 opens belongs to P2");
                UnityEngine.Object.Destroy(g);
            }

            // Join guard: no joining while the game holds input (cutscene/loading); joining works afterwards.
            Plugin.DespawnNow("test: join guard");
            yield return StartCoroutine(Wait(0.5f));
            GameController.Instance.LockInput = true;
            Bot(0f, P2Input.BTN_A); yield return StartCoroutine(Wait(0.2f)); Bot(0f, 0); yield return StartCoroutine(Wait(0.4f));
            Check(P2 == null, "join: can't join while the game holds input");
            GameController.Instance.LockInput = false;
            yield return StartCoroutine(Wait(0.3f));
            Bot(0f, P2Input.BTN_A); yield return StartCoroutine(Wait(0.2f)); Bot(0f, 0); yield return StartCoroutine(Wait(0.6f));
            Check(P2 != null, "join: joins again once gameplay resumes");
            Check(m_errors == err0, "interact: no errors");
        }

        // In-game co-op menu: opens and freezes the game, controller assignment by button press (with swap),
        // story-cutscene option, closes and restores the game.
        IEnumerator Menu()
        {
            Note("--- scenario: menu (F8 co-op settings)");
            int err0 = m_errors;
            int p1Was = Plugin.Instance.P1Pad, p2Was = Plugin.Instance.P2Pad;
            bool cutWas = Plugin.Instance.P2CanTriggerCutscenes;
            int joins0 = P2 != null ? 1 : 0;
            // Native pause entry: use the real manager press, keep its pause debt, then return to it.
            UI.Menu.ShowMenuScreen();
            yield return null;
            CleverMenuItemSelectionManager pauseNav = PauseScreen.Instance.NavigationManager;
            CleverMenuItem pauseEntry = pauseNav.MenuItems.Find(delegate(CleverMenuItem item) { return item != null && item.name == "OriCoop_PauseSettings"; });
            Check(pauseEntry != null && pauseEntry.Pressed == null && pauseEntry.Activated == null && pauseEntry.Visible == null,
                "menu: native pause settings entry has no copied press action or conditions");
            if (pauseEntry != null)
            {
                float pauseScale = Time.timeScale;
                pauseNav.SetCurrentMenuItem(pauseEntry);
                pauseNav.PressCurrentItem();
                yield return null;
                // "Without adding a pause": the panel must not freeze the game itself (the native pause keeps its own). Not an exact
                // timeScale compare: the game's SyncFramesTest.Update writes 1 / 1.5 / 0.5 every frame to pace fixed updates
                // (decomp SyncFramesTest.cs:28-50), so two frames' values differ by design.
                Check(CoopMenu.IsOpen && UI.MainMenuVisible && !pauseNav.IsVisible && pauseNav.IsLocked && !CoopMenu.FrozeGame && Time.timeScale > 0f,
                    "menu: pause entry opens settings without adding a pause or releasing the native pause (open=" + CoopMenu.IsOpen + " page=" + CoopMenu.Page
                    + " menuVisible=" + UI.MainMenuVisible + " pauseVisible=" + pauseNav.IsVisible + " pauseLocked=" + pauseNav.IsLocked
                    + " froze=" + CoopMenu.FrozeGame + " timeScale=" + Time.timeScale + " was " + pauseScale + ")");
                CoopMenu.Close();
                yield return StartCoroutine(Wait(0.4f));
                Check(!CoopMenu.IsOpen && UI.MainMenuVisible && pauseNav.IsVisible && pauseNav.IsActive && !pauseNav.IsLocked && pauseNav.CurrentMenuItem == pauseEntry,
                    "menu: closing settings returns focus to the pause entry");
            }
            UI.Menu.HideMenuScreen(true);
            CoopMenu.TestPadButtons = new int[4];
            CoopMenu.Open();
            yield return null; yield return null;
            Check(CoopMenu.IsOpen && Time.timeScale == 0f, "menu: opens and freezes the game");
            Shot("menu");
            yield return StartCoroutine(Wait(0.5f));

            // P2 <- controller 3 (index 2): choose, then press A on that controller.
            CoopMenu.Activate(1);
            yield return null;
            CoopMenu.TestPadButtons[2] = 0x1000; yield return null; yield return null;
            CoopMenu.TestPadButtons[2] = 0; yield return null;
            Check(Plugin.Instance.P2Pad == 2, "menu: P2 assigned by pressing a button on its controller (" + Plugin.Instance.P2Pad + ")");
            // P1 <- the same controller: the two swap, never both on one controller.
            CoopMenu.Assign(1, 2);
            Check(Plugin.Instance.P1Pad == 2 && Plugin.Instance.P2Pad != 2, "menu: giving P2's controller to P1 swaps them (P1 " + Plugin.Instance.P1Pad + ", P2 " + Plugin.Instance.P2Pad + ")");
            CoopMenu.Assign(1, -1);
            Check(Plugin.Instance.P1Pad == -1, "menu: P1 can be keyboard/mouse only");

            CoopMenu.Activate(2);
            Check(Plugin.Instance.P2CanTriggerCutscenes != cutWas, "menu: story cutscene option toggles");
            CoopMenu.Activate(CoopMenu.SettingsCloseRow);
            yield return null;
            Check(!CoopMenu.IsOpen && Time.timeScale > 0f, "menu: closes and the game runs again");
            Check((P2 != null ? 1 : 0) == joins0, "menu: buttons pressed in the menu don't add/remove P2");

            // Play mode (asked once a save is loaded; the harness always plays as 2 players, so open it by hand).
            // P1 is keyboard/mouse only here.
            Check(PlayMode.Current == PlayMode.Coop, "mode: the test run plays as 2 players");
            KeyboardOnlyP1.HoldUntilReleased = false; KeyboardOnlyP1.Refresh();
            Check(KeyboardOnlyP1.GamePad >= 0 && KeyboardOnlyP1.StartOnly, "mode: keyboard P1 in co-op gameplay -> the game reads only Start from the controller (" + KeyboardOnlyP1.State + ")");
            CoopMenu.OpenModeSelect(); yield return null;
            Check(CoopMenu.IsOpen && CoopMenu.Page == CoopMenu.PageModeSelect && Time.timeScale == 0f, "mode: mode-select screen opens and freezes the game");
            KeyboardOnlyP1.Refresh();
            Check(KeyboardOnlyP1.GamePad < 0, "mode: the game gets no controller input while the co-op screen is open (" + KeyboardOnlyP1.State + ")");
            Shot("mode-select");
            CoopMenu.Activate(0); yield return null; yield return null;
            Check(!CoopMenu.IsOpen && PlayMode.Current == PlayMode.Single && P2 == null, "mode: single player closes the screen and removes P2");
            KeyboardOnlyP1.HoldUntilReleased = false; KeyboardOnlyP1.Refresh();
            Check(KeyboardOnlyP1.GamePad >= 0 && !KeyboardOnlyP1.StartOnly, "mode: single player -> P1 gets the controller as well (" + KeyboardOnlyP1.State + ")");
            Bot(0f, P2Input.BTN_A); yield return StartCoroutine(Wait(0.2f)); Bot(0f, 0); yield return StartCoroutine(Wait(0.4f));
            Check(P2 == null, "mode: in single player a controller button doesn't add P2");
            CoopMenu.OpenModeSelect(); yield return null;
            CoopMenu.Activate(1); yield return null;
            Check(CoopMenu.IsOpen && CoopMenu.Page == CoopMenu.PageSetup, "mode: 2 players opens the controller setup");
            Shot("mode-setup");
            CoopMenu.Activate(4); yield return null;
            Check(CoopMenu.IsOpen && CoopMenu.Page == CoopMenu.PageModeSelect, "mode: Back on the setup returns to the mode choice");
            CoopMenu.Activate(1); yield return null;
            CoopMenu.Activate(3); yield return StartCoroutine(Wait(0.6f));
            Check(!CoopMenu.IsOpen && PlayMode.Current == PlayMode.Coop && P2 != null, "mode: Start 2 players closes the setup and P2 joins");
            CoopMenu.TestPadButtons = null;

            // Option on: P2 touching a cutscene trigger brings P1 over and runs the trigger as P1.
            Plugin.Instance.P2CanTriggerCutscenes = true;
            Plugin.Teleport(P2, P1.Position + new Vector3(6f, 0f, 0f));
            yield return StartCoroutine(Wait(0.3f));
            GameObject cz = new GameObject("oricoop_test_cutscene2");
            cz.transform.position = P2.Position; // a real trigger is where P2 walks into it
            cz.AddComponent<BoxCollider>().isTrigger = true;
            CutsceneTrigger ct = cz.AddComponent<CutsceneTrigger>();
            int by0 = P2Extras.CutscenesByP2;
            try { ct.OnTriggerEnter(P2.GetComponent<Collider>()); } catch (Exception) { } // test trigger has no cutscene attached
            Check(P2Extras.CutscenesByP2 == by0 + 1, "cutscene option: P2 can start a cutscene when allowed");
            Check(Vector3.Distance(P1.Position, P2.Position) < 3f, "cutscene option: P1 is brought to P2 first (d=" + F(Vector3.Distance(P1.Position, P2.Position)) + ")");
            UnityEngine.Object.Destroy(cz);

            // Put the owner's settings back.
            Plugin.Instance.P2CanTriggerCutscenes = cutWas;
            if (p1Was >= 0) Plugin.Instance.AssignPad(1, p1Was); else Plugin.Instance.AssignPad(1, -1);
            Plugin.Instance.AssignPad(2, p2Was);
            if (p1Was >= 0) Plugin.Instance.AssignPad(1, p1Was);
            Check(Plugin.Instance.P1Pad == p1Was && Plugin.Instance.P2Pad == p2Was && Plugin.Instance.P2CanTriggerCutscenes == cutWas, "menu: owner's settings restored after the test");
            Check(m_errors == err0, "menu: no errors");
        }

        static string DescribeRenderers(GameObject root)
        {
            if (root == null) return "null";
            int n = 0, on = 0, vis = 0;
            string first = "";
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                n++;
                if (r.enabled && r.gameObject.activeInHierarchy) on++;
                if (r.isVisible) vis++;
                if (first.Length < 600) first += r.GetType().Name + ":" + r.gameObject.name + "(L" + r.gameObject.layer + (r.enabled ? "" : ",off") + (r.gameObject.activeInHierarchy ? "" : ",inactive") + (r.isVisible ? ",vis" : "") + ") ";
            }
            return "active=" + root.activeInHierarchy + " pos=" + root.transform.position + " scale=" + root.transform.lossyScale + " renderers " + n + " enabled " + on + " visible " + vis + " :: " + first;
        }

        // What players see: P2's HUD lives in the world and follows the game's own HUD visibility; P2's companion is
        // shown like P1's; the runtime visual self-check catches and repairs a hidden companion.
        IEnumerator Visual()
        {
            Note("--- scenario: visual (HUD layering, companion)");
            int err0 = m_errors;
            Plugin.Teleport(P2, P1.Position + new Vector3(1.5f, 0f, 0f));
            yield return StartCoroutine(Wait(2f));
            Check(P2Hud.Visible, "hud: P2 health shown in gameplay");
            Ori comp = P2Context.P2Companion;
            Check(comp != null && comp.gameObject.activeInHierarchy, "companion: P2 has an active companion");
            // Companion on screen, from real pixels: P2's companion renderers off vs on, count new bright blue-white pixels.
            if (comp != null)
            {
                Vector3 compScreen = UI.Cameras.Current.Camera.WorldToScreenPoint(comp.transform.position);
                Renderer[] cr = comp.GetComponentsInChildren<Renderer>();
                foreach (Renderer r in cr) r.enabled = false;
                yield return StartCoroutine(Wait(0.2f));
                string cOff = Shot("visual_companion_hidden"); yield return StartCoroutine(Wait(0.3f));
                foreach (Renderer r in cr) r.enabled = true;
                yield return StartCoroutine(Wait(0.2f));
                string cOn = Shot("visual_companion"); yield return StartCoroutine(Wait(0.3f));
                int glow = NewPixels(cOff, cOn, compScreen, 40, c => c.b > 0.7f && c.g > 0.6f && c.b >= c.r);
                Note("companion pixels: " + glow + " new bright blue-white pixels around screen " + compScreen);
                Check(glow >= 60, "companion: P2's companion is actually visible on screen (" + glow + " pixels)");
            }
            // On-screen check from real pixels: screenshot with P2's HUD hidden and shown at the same spot; count pixels
            // that are HUD-yellow only in the shown one (background animation can't fake that).
            Vector3 hudScreen = UI.Cameras.Current.Camera.WorldToScreenPoint(P2.Position + new Vector3(0f, 1.7f, 0f));
            P2HudTestHide = true; yield return StartCoroutine(Wait(0.3f));
            string shotOff = Shot("visual_hud_hidden"); yield return StartCoroutine(Wait(0.4f));
            P2HudTestHide = false; yield return StartCoroutine(Wait(0.3f));
            string shotOn = Shot("visual_gameplay"); yield return StartCoroutine(Wait(0.4f)); // taken at end of frame
            string hudRegion = Path.Combine(Path.GetDirectoryName(shotOn), "visual_hud_region.png");
            int newYellow = NewPixels(shotOff, shotOn, hudScreen, 90, c => c.r > 0.75f && c.g > 0.55f && c.b < 0.45f && c.r - c.b > 0.35f, hudRegion);
            Note("screenshot " + hudRegion);
            Note("hud pixels: " + newYellow + " new HUD-yellow pixels around screen " + hudScreen + " (layer " + (P2Hud.Renderer != null ? P2Hud.Renderer.gameObject.layer : -1) + ")");
            // Background animation alone produced 50-85 such pixels in this spot; readable text gives several hundred.
            Check(newYellow >= 200, "hud: P2 health is actually visible on screen (" + newYellow + " pixels)");

            UI.Menu.ShowMenuScreen();
            yield return StartCoroutine(Wait(1.2f));
            Check(!P2Hud.Visible, "hud: hidden while the pause menu is open");
            Shot("visual_pause");
            UI.Menu.HideMenuScreen();
            yield return StartCoroutine(Wait(2f));
            Check(P2Hud.Visible, "hud: back after resuming");

            Letterbox.ShowLetterboxes = true;   // what cutscenes do
            yield return StartCoroutine(Wait(2f));
            Check(!P2Hud.Visible, "hud: fades out with the game's HUD during a cutscene letterbox");
            Letterbox.ShowLetterboxes = false;
            yield return StartCoroutine(Wait(2f));
            Check(P2Hud.Visible, "hud: back after the cutscene");

            // Self-check: hide P2's companion behind the mod's back; it must be noticed and repaired.
            int v0 = VisualInvariants.Violations;
            if (comp != null)
            {
                comp.gameObject.SetActive(false);
                yield return StartCoroutine(Wait(2.5f));
                Check(comp.gameObject.activeInHierarchy, "self-check: a hidden P2 companion comes back (the game's own logic or the self-check)");
                VisualInvariants.Kinds.Remove("companion hidden");
            }
            m_visualBaseline = VisualInvariants.Violations;
            Check(m_errors == err0, "visual: no errors");
        }

        int m_visualBaseline;

        // T-061: a status bar per teammate along the bottom, next to the game's own HUD (a copy of it, the teammate's values).
        // Oracles from the ticket: P2's bar equals P2 within 1 s after damage / energy use; the game's own HUD keeps
        // showing P1; downed = dimmed, gone = hidden; 3 bars do not overlap each other or the native HUD; every bar is
        // really drawn (pixels).
        IEnumerator BottomHudScenario()
        {
            Note("--- scenario: bottomhud (T-061: a status bar per teammate at the bottom)");
            int err0 = m_errors;
            float saveLeash = Plugin.LeashOverride;
            Plugin.LeashOverride = 200f;
            Plugin.Teleport(P2, P1.Position + new Vector3(1.5f, 0f, 0f));
            P1.Mortality.Health.RestoreAllHealth(); P2.Mortality.Health.RestoreAllHealth();
            yield return StartCoroutine(Wait(2f));
            Check(BottomHud.Count == 1 && BottomHud.Visible(2), "bottomhud: one bar, for P2, shown in gameplay (count " + BottomHud.Count + ")");
            Check(BottomHud.LabelText(2) == Roles.Tag(2), "bottomhud: P2's bar is labelled " + Roles.Tag(2) + " ('" + BottomHud.LabelText(2) + "')");
            float h, mh, e, me, nh, nmh, ne, nme;
            bool shown = BottomHud.TryShown(2, out h, out mh, out e, out me);
            Note("bottomhud: P2 bar shows health " + F(h) + "/" + F(mh) + " energy " + F(e) + "/" + F(me) + "; P2 has " + F(P2.Mortality.Health.Amount) + "/" + P2.Mortality.Health.MaxHealth + ", " + F(P2.Energy.Current) + "/" + F(P2.Energy.Max));
            Check(shown && Mathf.Abs(h - P2.Mortality.Health.Amount) < 0.01f && Mathf.Abs(mh - P2.Mortality.Health.MaxHealth) < 0.01f
                && Mathf.Abs(e - P2.Energy.Current) < 0.01f && Mathf.Abs(me - P2.Energy.Max) < 0.01f, "bottomhud: P2's bar shows P2's health and energy");

            // Damage P2 only: its bar follows within 1 s, the game's own HUD keeps P1's health.
            Hurt(P2, 3f);
            float t0 = Time.unscaledTime, took = -1f;
            while (Time.unscaledTime - t0 < 1.5f)
            {
                yield return null;
                if (BottomHud.TryShown(2, out h, out mh, out e, out me) && Mathf.Abs(h - P2.Mortality.Health.Amount) < 0.01f) { took = Time.unscaledTime - t0; break; }
            }
            Note("bottomhud: after damage P2 has " + F(P2.Mortality.Health.Amount) + ", bar " + F(h) + " after " + F(took) + " s");
            Check(P2.Mortality.Health.Amount < P2.Mortality.Health.MaxHealth, "bottomhud: the test damage reached P2");
            Check(took >= 0f && took <= 1f, "bottomhud: P2's bar shows P2's health within 1 s of damage (" + F(took) + " s)");
            BottomHud.TryNativeShown(out nh, out nmh, out ne, out nme);
            Check(Mathf.Abs(nh - P1.Mortality.Health.VisualMinAmount) < 0.01f && Mathf.Abs(nh - P2.Mortality.Health.Amount) > 0.01f,
                "bottomhud: the game's own HUD still shows P1's health (" + F(nh) + " vs P1 " + F(P1.Mortality.Health.Amount) + ")");

            // Energy use (energy is one pool in local co-op): the bar follows within 1 s.
            float eWas = P1.Energy.Current;
            P2.Energy.Current = 0f;
            t0 = Time.unscaledTime; took = -1f;
            while (Time.unscaledTime - t0 < 1.5f)
            {
                yield return null;
                if (BottomHud.TryShown(2, out h, out mh, out e, out me) && Mathf.Abs(e - P2.Energy.Current) < 0.01f && P2.Energy.Current < 0.01f) { took = Time.unscaledTime - t0; break; }
            }
            Note("bottomhud: after energy use P2 has " + F(P2.Energy.Current) + ", bar " + F(e) + " after " + F(took) + " s");
            Check(took >= 0f && took <= 1f, "bottomhud: P2's bar shows P2's energy within 1 s of use (" + F(took) + " s)");
            P1.Energy.Current = eWas; P2.Energy.Current = eWas;

            // Layout: P2's bar beside the native HUD, not over it, on screen.
            Rect native, bar;
            yield return StartCoroutine(Wait(0.5f));
            bool geo = BottomHud.NativeScreenRect(out native) & BottomHud.ScreenRect(2, out bar);
            Note("bottomhud: native HUD " + native + ", P2 bar " + bar + ", screen " + Screen.width + "x" + Screen.height);
            Check(geo && !bar.Overlaps(native) && bar.xMin >= 0f && bar.xMax <= Screen.width && bar.yMin >= 0f, "bottomhud: P2's bar is on screen and does not cover the game's HUD");

            // Pixels: the bar is really drawn (bright HUD colours appear where it is when it is shown).
            Func<Color, bool> hudColour = c => Mathf.Max(c.r, Mathf.Max(c.g, c.b)) > 0.5f && Mathf.Max(c.r, Mathf.Max(c.g, c.b)) - Mathf.Min(c.r, Mathf.Min(c.g, c.b)) > 0.18f;
            BottomHud.TestHide = true; yield return StartCoroutine(Wait(0.3f));
            string off = Shot("bottomhud_hidden"); yield return StartCoroutine(Wait(0.3f));
            BottomHud.TestHide = false; yield return StartCoroutine(Wait(0.3f));
            string on = Shot("bottomhud_2p"); yield return StartCoroutine(Wait(0.3f));
            int px = NewPixels(off, on, new Vector3(bar.center.x, bar.center.y + bar.height * 0.4f, 0f), (int)(Mathf.Max(bar.width, bar.height * 2f) / 2f + 6f), hudColour,
                Path.Combine(Path.GetDirectoryName(on), "bottomhud_region_p2.png"));
            int nativePx = NewPixels(off, on, new Vector3(native.center.x, native.center.y, 0f), (int)(native.height / 2f), hudColour);
            Note("bottomhud: " + px + " new HUD-coloured pixels at P2's bar, " + nativePx + " at the native HUD's orb");
            Check(px >= 60, "bottomhud: P2's bar is actually visible on screen (" + px + " pixels)");
            // The orb's own sparkle and glow animate between the two shots (seen: 21 pixels); a bar drawn over the orb adds
            // about as many as P2's bar itself (300+). So the limit is a fifth of the bar's count, at least 40.
            int nativeLimit = Mathf.Max(40, px / 5);
            Check(nativePx <= nativeLimit, "bottomhud: showing P2's bar does not change the game's own HUD (" + nativePx + " pixels, limit " + nativeLimit + ")");
            // The "P2" label: above P2's bar, readable size, drawn in P2's name-tag colour.
            Rect label;
            bool haveLabel = BottomHud.LabelScreenRect(2, out label);
            Note("bottomhud: P2 label " + label);
            Check(haveLabel && label.yMin >= bar.yMax - 1f && label.xMin >= bar.xMin && label.xMax <= bar.xMax && !label.Overlaps(native)
                && label.height >= Screen.height * 0.015f, "bottomhud: P2's label sits above P2's bar, readable, off the game's HUD");
            Color tag = Roles.ColorOf(2);
            int labelPx = NewPixels(off, on, new Vector3(label.center.x, label.center.y, 0f), (int)(Mathf.Max(label.width, label.height) / 2f + 3f),
                c => Mathf.Abs(c.r - tag.r) < 0.25f && Mathf.Abs(c.g - tag.g) < 0.25f && Mathf.Abs(c.b - tag.b) < 0.3f && c.r > 0.5f,
                Path.Combine(Path.GetDirectoryName(on), "bottomhud_region_label.png"));
            Note("bottomhud: " + labelPx + " new P2-coloured pixels at the label");
            Check(labelPx >= 15, "bottomhud: P2's label is visible in P2's colour (" + labelPx + " pixels)");

            // 3 teammates (seats 3 and 4 as test bars with their own values): no overlaps, all visible.
            BottomHud.TestFakeSeats.Add(3); BottomHud.TestFakeSeats.Add(4);
            yield return StartCoroutine(Wait(0.3f));
            VitalsSample v3 = new VitalsSample { Health = 8f, MaxHealth = 12, Energy = 2f, MaxEnergy = 3f, SoulFlame = 1f };
            VitalsSample v4 = new VitalsSample { Health = 4f, MaxHealth = 12, Energy = 1f, MaxEnergy = 3f, SoulFlame = 1f };
            BottomHud.SetTestVitals(3, v3); BottomHud.SetTestVitals(4, v4);
            yield return StartCoroutine(Wait(1.5f));
            Check(BottomHud.Count == 3 && BottomHud.Visible(2) && BottomHud.Visible(3) && BottomHud.Visible(4), "bottomhud: 3 teammates -> 3 bars shown (" + BottomHud.Count + ")");
            Rect[] rs = new Rect[5];
            bool all = true;
            for (int s = 2; s <= 4; s++) all &= BottomHud.ScreenRect(s, out rs[s]);
            string overlaps = "";
            for (int s = 2; s <= 4; s++)
            {
                if (rs[s].Overlaps(native)) overlaps += "P" + s + "/native ";
                if (rs[s].xMin < 0f || rs[s].xMax > Screen.width || rs[s].yMin < 0f || rs[s].yMax > Screen.height) overlaps += "P" + s + "/offscreen ";
                for (int o = 2; o < s; o++) if (rs[s].Overlaps(rs[o])) overlaps += "P" + s + "/P" + o + " ";
            }
            Note("bottomhud 4p: P2 " + rs[2] + " P3 " + rs[3] + " P4 " + rs[4]);
            Check(all && overlaps == "", "bottomhud: 3 bars do not overlap each other or the game's HUD and stay on screen (" + overlaps + ")");
            float h3, mh3, e3, me3;
            Check(BottomHud.TryShown(3, out h3, out mh3, out e3, out me3) && Mathf.Abs(h3 - 8f) < 0.01f && Mathf.Abs(e3 - 2f) < 0.01f,
                "bottomhud: P3's bar shows P3's own values (health " + F(h3) + ", energy " + F(e3) + ")");
            BottomHud.TestHide = true; yield return StartCoroutine(Wait(0.3f));
            string off4 = Shot("bottomhud_4p_hidden"); yield return StartCoroutine(Wait(0.3f));
            BottomHud.TestHide = false; yield return StartCoroutine(Wait(0.3f));
            string on4 = Shot("bottomhud_4p"); yield return StartCoroutine(Wait(0.3f));
            for (int s = 2; s <= 4; s++)
            {
                int n = NewPixels(off4, on4, new Vector3(rs[s].center.x, rs[s].center.y + rs[s].height * 0.4f, 0f), (int)(Mathf.Max(rs[s].width, rs[s].height * 2f) / 2f + 6f), hudColour);
                Check(n >= 60, "bottomhud: P" + s + "'s bar is actually visible with 4 players (" + n + " pixels)");
            }
            BottomHud.TestFakeSeats.Clear();
            yield return StartCoroutine(Wait(0.3f));
            Check(BottomHud.Count == 1, "bottomhud: test bars removed again (" + BottomHud.Count + ")");

            // Hidden with the game's HUD: pause menu.
            UI.Menu.ShowMenuScreen();
            yield return StartCoroutine(Wait(1.2f));
            Check(!BottomHud.Visible(2), "bottomhud: hidden while the pause menu is open");
            UI.Menu.HideMenuScreen();
            yield return StartCoroutine(Wait(2f));
            Check(BottomHud.Visible(2), "bottomhud: back after resuming");

            // Downed: dimmed; revived: full again.
            float full = BottomHud.Opacity(2);
            Hurt(P2, 1000f);
            yield return StartCoroutine(Wait(0.8f));
            Revive.Downed d2 = Revive.DownedOf(2);
            Check(d2 != null && BottomHud.Visible(2) && BottomHud.Opacity(2) < full * 0.5f, "bottomhud: downed P2's bar stays, dimmed (" + F(BottomHud.Opacity(2)) + " vs " + F(full) + ")");
            Shot("bottomhud_downed");
            if (d2 != null)
            {
                Plugin.Teleport(P1, d2.Marker + new Vector3(0.8f, 0f, 0f));
                yield return StartCoroutine(Wait(0.8f));
                yield return StartCoroutine(HoldP1(2.5f));
                yield return StartCoroutine(Wait(1f));
                Check(Revive.DownedOf(2) == null && BottomHud.Opacity(2) > full * 0.9f, "bottomhud: revived P2's bar is full again (" + F(BottomHud.Opacity(2)) + ")");
            }

            // Gone: P2 leaves -> no bar; joins again -> bar back.
            Plugin.DespawnNow("test: bottomhud leave");
            yield return StartCoroutine(Wait(0.5f));
            Check(BottomHud.Count == 0 && !BottomHud.Visible(2), "bottomhud: P2 left -> P2's bar is gone");
            Bot(0f, P2Input.BTN_A); yield return StartCoroutine(Wait(0.2f)); Bot(0f, 0);
            yield return StartCoroutine(Wait(2.5f));
            Check(P2 != null && BottomHud.Visible(2), "bottomhud: P2 joined again -> P2's bar is back");
            Plugin.LeashOverride = saveLeash;
            Check(m_errors == err0, "bottomhud: no errors");
        }

        // P2 joins while P1's companion is on a scripted move (the owner's case: P2's companion flew to (0,0,0)),
        // and P2's companion gets stuck on a move nobody calls it back from.
        IEnumerator CompanionStates()
        {
            Note("--- scenario: companion (join during P1 companion move, stuck move, camera)");
            int err0 = m_errors;
            Ori c1 = Characters.Ori;
            Plugin.DespawnNow("test: rejoin during companion move");
            yield return StartCoroutine(Wait(0.5f));
            c1.MoveOriToPosition(Characters.Sein.Position + new Vector3(-6f, 4f, 0f), 3f);   // like flying to an interest zone (P1 shortcut is empty while P2 is out)
            yield return StartCoroutine(Wait(0.3f));
            Check(c1.CurrentState == Ori.State.MoveToPosition, "companion: P1's companion is on a scripted move before P2 joins");
            Bot(0f, P2Input.BTN_A); yield return StartCoroutine(Wait(0.2f)); Bot(0f, 0);
            yield return StartCoroutine(Wait(2f));
            Ori c2 = P2Context.P2Companion;
            Check(P2 != null && c2 != null, "companion: P2 rejoined with a companion");
            if (c2 != null)
            {
                float d = Vector3.Distance(c2.transform.position, P2.Position);
                Note("companion: P2's companion state " + c2.CurrentState + ", " + F(d) + " from P2");
                Check(c2.CurrentState == Ori.State.Hovering && d < 5f, "companion: P2's companion follows P2 even if P1's was mid-move at join (d=" + F(d) + ")");
                Check(UI.Cameras.Current.Target != P2.Transform, "camera: still shared (not switched to P2) after P2's companion reset");
            }
            c1.BackToPlayerController();
            yield return StartCoroutine(Wait(1f));

            // P2's companion sent on a move as P2, never called back: the self-check returns it.
            if (c2 != null)
            {
                int v0 = VisualInvariants.Violations;
                P2Context.Enter();
                c2.MoveOriToPosition(P2.Position + new Vector3(5f, 3f, 0f), 1f);
                P2Context.Exit();
                yield return StartCoroutine(Wait(6f));
                float d = Vector3.Distance(c2.transform.position, P2.Position);
                Check(c2.CurrentState == Ori.State.Hovering && d < 5f, "companion: a P2 companion left on a scripted move comes back (state " + c2.CurrentState + ", d=" + F(d) + ")");
                if (VisualInvariants.Violations > v0) VisualInvariants.Kinds.Remove("companion stuck in a scripted move");
                Check(UI.Cameras.Current.Target != P2.Transform, "camera: still shared after the self-check returned the companion");
            }
            m_visualBaseline = VisualInvariants.Violations;
            Check(m_errors == err0, "companion: no errors");
        }

        IEnumerator Diag()
        {
            Note("--- diag: companions");
            yield return StartCoroutine(Wait(1.5f));
            Note("P1 companion: " + DescribeRenderers(Characters.Ori != null ? Characters.Ori.gameObject : null));
            Note("P2 companion: " + DescribeRenderers(P2Context.P2Companion != null ? P2Context.P2Companion.gameObject : null));
            Shot("diag_companion");
        }

        IEnumerator HoldFor(SeinCharacter s, Vector3 p, float t) { float end = Time.unscaledTime + t; while (Time.unscaledTime < end) { s.Position = p; s.Speed = Vector3.zero; yield return null; } }
        IEnumerator HoldBoth(Vector3 p2, Vector3 p1, float t) { float end = Time.unscaledTime + t; while (Time.unscaledTime < end) { P2.Position = p2; P2.Speed = Vector3.zero; P1.Position = p1; P1.Speed = Vector3.zero; yield return null; } }

        static int CountSaveEntriesUnder(Transform root)
        {
            int n = 0;
            foreach (SaveSceneManager.SaveId s in SaveSceneManager.Master.SaveData)
            {
                Component c = s.Save as Component;
                if (c != null && c.transform.IsChildOf(root)) n++;
            }
            return n;
        }

        // P2 follows P1's availability: hidden while P1 is hidden by a cutscene, held while P1 is held.
        IEnumerator Lifecycle()
        {
            Note("--- scenario: lifecycle (cutscene hide / movement lock)");
            int err0 = m_errors;
            P1.gameObject.SetActive(false);
            yield return StartCoroutine(Wait(0.5f));
            Check(!P2.gameObject.activeSelf, "lifecycle: P2 hidden while a cutscene hides P1");
            P1.gameObject.SetActive(true);
            yield return StartCoroutine(Wait(0.5f));
            Check(P2.gameObject.activeSelf, "lifecycle: P2 back when P1 is back");
            Check(Vector3.Distance(P1.Position, P2.Position) < 2f, "lifecycle: P2 reappears next to P1 (d=" + F(Vector3.Distance(P1.Position, P2.Position)) + ")");
            yield return StartCoroutine(Wait(1f));
            P1.Controller.LockMovementInput = true;
            yield return StartCoroutine(Wait(0.3f));
            float x0 = P2.Position.x;
            Bot(1f, 0);
            yield return StartCoroutine(Wait(1f));
            Bot(0f, 0);
            Check(Mathf.Abs(P2.Position.x - x0) < 0.5f, "lifecycle: P2 held while a script holds P1 (dx=" + F(P2.Position.x - x0) + ")");
            P1.Controller.LockMovementInput = false;
            yield return StartCoroutine(Wait(0.3f));
            x0 = P2.Position.x;
            Bot(1f, 0);
            TestClock.Window run = new TestClock.Window(0.6f);   // game seconds (T-071): movement is game-timed
            while (run.Open) yield return null;
            Bot(0f, 0);
            Check(P2.Position.x - x0 > 1f, "lifecycle: P2 moves again after the hold (dx=" + F(P2.Position.x - x0) + " game=" + F(run.GameElapsed) + "s real=" + F(run.RealElapsed) + "s)");
            Check(m_errors == err0, "lifecycle: no errors");
        }

        static float CamDist() { return -UI.Cameras.Current.OffsetController.Offset.z; }

        // Drop-through platforms are per Ori: one Ori's "pass through" state must not make the other fall through.
        // Uses the game's own GoThroughPlatform on platforms built in open air near the start.
        GameObject MakePlatform(Vector3 center)
        {
            GameObject g = new GameObject("oricoop_test_platform");
            g.SetActive(false); // GoThroughPlatform.Awake needs Colliders set before it runs
            g.transform.position = center;
            BoxCollider box = g.AddComponent<BoxCollider>();
            box.size = new Vector3(3f, 0.4f, 4f);
            GoThroughPlatform gp = g.AddComponent<GoThroughPlatform>();
            gp.Colliders = new Collider[] { box };
            gp.Transforms = new Transform[] { g.transform };
            g.SetActive(true);
            return g;
        }

        bool OpenAir(Vector3 c) { return !Physics.CheckBox(c + new Vector3(0f, 1.5f, 0f), new Vector3(2f, 2.5f, 1f)); }

        IEnumerator StandOn(SeinCharacter s, Vector3 platformCenter)
        {
            Plugin.Teleport(s, platformCenter + new Vector3(0f, 1.2f, 0f));
            yield return null;
        }

        IEnumerator Platforms()
        {
            Note("--- scenario: platform (drop-through platforms per Ori)");
            int err0 = m_errors;
            float saveLeash = Plugin.LeashOverride;
            Plugin.LeashOverride = 200f; // positions are set by the test; no edge walls / regroup meanwhile
            // Find two open-air spots above the start area.
            Vector3 a = Vector3.zero, b = Vector3.zero; bool found = false;
            for (float up = 4f; up <= 16f && !found; up += 2f)
                for (float dx = -12f; dx <= 12f && !found; dx += 3f)
                {
                    Vector3 pa = P1.Position + new Vector3(dx, up, 0f), pb = pa + new Vector3(6f, 0f, 0f);
                    if (OpenAir(pa) && OpenAir(pb)) { a = pa; b = pb; found = true; }
                }
            if (!found) { Skip("platform: no open air near the start for test platforms"); Plugin.LeashOverride = saveLeash; yield break; }
            GameObject pa1 = MakePlatform(a), pb1 = MakePlatform(b);
            float topA = a.y + 0.2f, topB = b.y + 0.2f;
            Vector3 groundP1 = P1.Position, groundP2 = P2.Position;

            // 1) P1 on a platform, P2 on the ground (the owner's report: P1 fell through).
            yield return StartCoroutine(StandOn(P1, a));
            Plugin.Teleport(P2, groundP1 + new Vector3(1f, 0f, 0f));
            yield return StartCoroutine(Wait(1.5f));
            Note("platform: P1 y " + F(P1.Position.y) + " vs platform top " + F(topA) + ", P2 y " + F(P2.Position.y));
            Check(P1.Position.y > topA - 0.4f && P1.Position.y < topA + 1.5f, "platform: P1 stands on a drop-through platform while P2 is on the ground");

            // 2) P2 on a platform, P1 on the ground.
            Plugin.Teleport(P1, groundP1);
            yield return StartCoroutine(StandOn(P2, b));
            yield return StartCoroutine(Wait(1.5f));
            Check(P2.Position.y > topB - 0.4f && P2.Position.y < topB + 1.5f, "platform: P2 stands on a drop-through platform while P1 is on the ground");

            // 3) Both on platforms; P2 drops through with Down + Jump, P1 stays.
            yield return StartCoroutine(StandOn(P1, a));
            yield return StartCoroutine(Wait(1.5f));
            // As a player does it: hold Down to crouch first, then press Jump (the game's crouch-jump drops through).
            P2Input.BotLY = -1f; Bot(0f, 0);
            yield return StartCoroutine(Wait(0.5f));
            Bot(0f, P2Input.BTN_A);
            yield return StartCoroutine(Wait(0.15f));
            Bot(0f, 0);
            yield return StartCoroutine(Wait(0.25f));
            P2Input.BotLY = 0f;
            yield return StartCoroutine(Wait(0.8f));
            Note("platform: after P2 drop, P2 y " + F(P2.Position.y) + " (top " + F(topB) + "), P1 y " + F(P1.Position.y) + " (top " + F(topA) + ")");
            Check(P2.Position.y < topB - 0.5f, "platform: P2 drops through with Down + Jump");
            Check(P1.Position.y > topA - 0.4f, "platform: P1 stays on its platform while P2 drops through");

            UnityEngine.Object.Destroy(pa1); UnityEngine.Object.Destroy(pb1);
            Plugin.Teleport(P1, groundP1); Plugin.Teleport(P2, groundP1 + new Vector3(1f, 0f, 0f));
            yield return StartCoroutine(Wait(1f));
            Plugin.LeashOverride = saveLeash;
            Check(m_errors == err0, "platform: no errors");
        }

        // Shared-screen camera and edges: zoom out when apart, edges stop outward movement without dragging the
        // other player, inward movement still works, and an off-screen player glides back instead of snapping.
        IEnumerator CameraAndEdges()
        {
            Note("--- scenario: camera (zoom, screen-edge walls, glide back)");
            int err0 = m_errors;
            float saveMax = CoopCamera.MaxZoomOut, saveMargin = CoopCamera.EdgeMargin;

            // 1) Zoom out as P2 walks away.
            // The camera starts opening once a player is in the outer ~18% of the normal view, i.e. at a spread of about
            // 64% of the view width. Walk P2 out until it is clearly past that (right, else left if terrain blocks),
            // instead of walking a fixed time: how far P2 gets in a fixed time depends on where this map spot is.
            Plugin.Teleport(P2, P1.Position + new Vector3(0.5f, 0f, 0f));
            yield return StartCoroutine(Wait(2f));
            float d0 = CamDist();
            Vector2 vNow, vMax; CoopCamera.ViewSize(out vNow, out vMax);
            float need = vNow.x * 0.64f + 4f, dMax = d0, reached = 0f;
            for (int dir = 1; dir >= -1 && reached < need; dir -= 2)
            {
                if (dir < 0) { Plugin.Teleport(P2, P1.Position + new Vector3(-0.5f, 0f, 0f)); yield return StartCoroutine(Wait(1.5f)); }
                Bot(dir, 0);
                float end0 = Time.unscaledTime + 5f;
                while (Time.unscaledTime < end0 && Mathf.Abs(P2.Position.x - P1.Position.x) < need) { dMax = Mathf.Max(dMax, CamDist()); yield return null; }
                Bot(0f, 0);
                reached = Mathf.Abs(P2.Position.x - P1.Position.x);
            }
            float end = Time.unscaledTime + 1.5f; // let the camera settle at this spread
            while (Time.unscaledTime < end) { dMax = Mathf.Max(dMax, CamDist()); yield return null; }
            Note("camera: distance " + F(d0) + " -> " + F(dMax) + ", spread reached " + F(reached) + " (needed " + F(need) + "), view " + F(vNow.x) + "x" + F(vNow.y) + ", extra " + F(CoopCamera.Extra));
            if (reached < need) Skip("camera: terrain here blocks P2 before the zoom-out spread (" + F(reached) + " < " + F(need) + ")");
            else Check(dMax > d0 * 1.05f, "camera: zooms out when the players separate (" + F(d0) + " -> " + F(dMax) + ")");
            Check(dMax <= d0 * CoopCamera.MaxZoomOut + 0.5f, "camera: zoom-out stays within the cap");
            yield return StartCoroutine(Wait(0.5f));

            // 2) Edge wall. Shrink the allowed spread so the wall is reachable on this map's open ground.
            Plugin.Teleport(P2, P1.Position + new Vector3(0.5f, 0f, 0f));
            yield return StartCoroutine(Wait(1.5f));
            CoopCamera.MaxZoomOut = 1f;
            Vector2 now, atMax;
            CoopCamera.ViewSize(out now, out atMax);
            CoopCamera.EdgeMargin = Mathf.Max(saveMargin, (atMax.x - 7f) * 0.5f); // allowed spread ~7 units (open ground here reaches ~9.7)
            float allowed = Mathf.Max(4f, atMax.x - 2f * CoopCamera.EdgeMargin);
            Vector3 p1Start = P1.Position;
            int blocks0 = CoopCamera.EdgeBlocks;
            Bot(1f, 0);
            float maxSep = 0f, minSepLate = 999f;
            end = Time.unscaledTime + 3.5f;
            while (Time.unscaledTime < end)
            {
                float sep = P2.Position.x - P1.Position.x;
                maxSep = Mathf.Max(maxSep, sep);
                if (end - Time.unscaledTime < 1f) minSepLate = Mathf.Min(minSepLate, sep);
                yield return null;
            }
            Shot("camera_edge");
            Note("edge: allowed " + F(allowed) + ", max sep " + F(maxSep) + ", sep in last second >= " + F(minSepLate) + ", blocks " + (CoopCamera.EdgeBlocks - blocks0));
            if (CoopCamera.EdgeBlocks == blocks0 && maxSep < allowed - 0.5f)
                Skip("edge: terrain stopped P2 before the screen edge here (" + F(maxSep) + " < " + F(allowed) + ")");
            else Check(CoopCamera.EdgeBlocks > blocks0, "edge: the screen edge stopped P2");
            Check(maxSep <= allowed + 1f, "edge: P2 can't walk past the screen edge (" + F(maxSep) + " <= " + F(allowed) + ")");
            Check(minSepLate > allowed - 2f, "edge: P2 stays at the edge (no snap back to P1)");
            Check(Mathf.Abs(P1.Position.x - p1Start.x) < 0.5f, "edge: P1 is not dragged by P2 pushing the edge (dx=" + F(P1.Position.x - p1Start.x) + ")");
            // Walking back inward is always allowed.
            float before = P2.Position.x;
            Bot(-1f, 0);
            yield return StartCoroutine(Wait(0.8f));
            Bot(0f, 0);
            Check(P2.Position.x < before - 1.5f, "edge: P2 can walk back inward (dx=" + F(P2.Position.x - before) + ")");
            CoopCamera.MaxZoomOut = saveMax; CoopCamera.EdgeMargin = saveMargin;
            yield return StartCoroutine(Wait(0.5f));

            // 3) Off-screen vertically for too long -> glides back, no single-frame snap.
            int regroups0 = CoopCamera.Regroups;
            CoopCamera.ViewSize(out now, out atMax);
            Vector3 high = P1.Position + new Vector3(0f, atMax.y + 6f, 0f);
            end = Time.unscaledTime + CoopCamera.RegroupAfter + 2f;
            while (Time.unscaledTime < end && CoopCamera.Regroups == regroups0) { P2.Position = high; P2.Speed = Vector3.zero; yield return null; }
            Check(CoopCamera.Regroups > regroups0, "regroup: off-screen P2 starts gliding back");
            float maxStep = 0f; Vector3 last = P2.Position;
            end = Time.unscaledTime + 3f;
            while (Time.unscaledTime < end && CoopCamera.Regrouping) { maxStep = Mathf.Max(maxStep, Vector3.Distance(P2.Position, last)); last = P2.Position; yield return null; }
            yield return StartCoroutine(Wait(0.3f));
            float dist = Vector3.Distance(P1.Position, P2.Position);
            Note("regroup: largest per-frame step " + F(maxStep) + ", final distance " + F(dist));
            Check(dist < 3f, "regroup: P2 ends next to P1");
            Check(maxStep < atMax.y * 0.25f, "regroup: smooth glide, not an instant snap");
            Check(m_errors == err0, "camera: no errors");
        }

        // Quit to the title screen with P2 in the game, then use the title menus again and rejoin P2.
        IEnumerator ReturnToTitle()
        {
            Note("--- scenario: title (return to main menu with P2 in game)");
            int err0 = m_errors;
            // Like the owner's session (2026-11-15): F8 menu opened and closed, an online host still open, then
            // pause menu -> quit to the title.
            CoopMenu.Open(); yield return StartCoroutine(Wait(0.5f)); CoopMenu.Close();
            NetPlay.Begin(true, "direct", "");
            yield return StartCoroutine(Wait(1.5f));
            int ended0 = TitleReturn.SessionsEnded;
            UI.Menu.ShowMenuScreen(true);
            yield return StartCoroutine(Wait(1f));
            GameController.Instance.RestartGame();
            float end = Time.unscaledTime + 40f;
            while (Time.unscaledTime < end && (Characters.Sein != null || GameController.Instance.InputLocked)) yield return null;
            yield return StartCoroutine(Wait(3f));
            Shot("title_after_quit");
            Check(!NetPlay.Enabled && TitleReturn.SessionsEnded == ended0 + 1, "title: returning to the title ends the online session (net=" + NetPlay.CurrentPhase + ")");
            Check(NativeModeMenu.AsksOnSlotPick, "title: picking a save slot asks single / co-op / online again");
            // "Press any key" through the game's own input (PlayerInput -> Core.Input.AnyStart), not the menu pulse.
            end = Time.unscaledTime + 10f;
            while (Time.unscaledTime < end && TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.PressStart) yield return null;
            int callbacks0 = PressStartCallbacks;
            FakeAnyKeySteps = 3;
            end = Time.unscaledTime + 5f;
            while (Time.unscaledTime < end && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart) yield return null;
            Check(PressStartCallbacks > callbacks0 && TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.PressStart,
                "title: a key press on \"press any key\" moves on (" + TitleDiag() + ")");
            Check(Characters.Sein == null, "title: P1 removed by the game");
            Check(P2 == null, "title: P2 removed with P1");
            Check(UnityEngine.Object.FindObjectsOfType<SeinCharacter>().Length == 0,
                "title: no Ori left on the title screen (" + UnityEngine.Object.FindObjectsOfType<SeinCharacter>().Length + ")");
            Check(!P2Context.Inside, "title: not stuck in P2 context");

            // Title menu Single Player / Co-op (TitleMenu.cs): every path, ending on Local Co-op -> save slots.
            yield return StartCoroutine(NativeModeMenu.TitleChecks(this, Check, Note, m_slot));
            // ORICOOP_L10N_PROBE=1: every label drawn so far (title pages, F8, toast, my rooms) in one language.
            yield return StartCoroutine(L10nProbe.Checks(this, Check, Note));

            // The title menu must still work: reach the save slots and load the save again.
            end = Time.unscaledTime + 40f;
            bool chosen = false;
            int lastForce = -1;
            while (Time.unscaledTime < end && !chosen)
            {
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                if (ui != null && ui.IsVisible && ui.Active && ui.Items != null && ui.Items.Count > m_slot)
                {
                    ui.SetCurrentItem(m_slot);
                    ui.UsedSaveSlotSelected();
                    chosen = true;
                }
                else
                {
                    int tick = (int)(Time.unscaledTime / 2f);
                    if (tick != lastForce)
                    {
                        lastForce = tick;
                        // Press confirm only on "press start"; on the main menu it would pick Continue and load
                        // the save without ever showing the slot list.
                        if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart) PulseMenu = true;
                        if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu)
                            TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
                        if (tick % 5 == 0) Note("title: screen=" + TitleScreenManager.CurrentScreen + " slotsUI=" + (ui != null) + (ui != null ? " visible=" + ui.IsVisible + " active=" + ui.Active : "") + " sein=" + (Characters.Sein != null));
                    }
                    yield return null;
                }
            }
            Check(chosen, "title: save slot menu usable after quitting with P2");
            Check(NativeModeMenu.LastSlotPick == "title-local" && !NativeModeMenu.IsOpen,
                "title menu: Local Co-op slot pick loads without the \"how to play\" page (" + NativeModeMenu.LastSlotPick + ")");
            end = Time.unscaledTime + 60f;
            float lastDismiss = -10f;
            while (Time.unscaledTime < end && (Characters.Sein == null || !Characters.Sein.gameObject.activeInHierarchy || GameController.Instance.InputLocked))
            {
                if (UI.MainMenuVisible && Time.unscaledTime - lastDismiss > 1f) { lastDismiss = Time.unscaledTime; UI.Menu.HideResumeScreen(); UI.Menu.HideMenuScreen(true); }
                yield return null;
            }
            yield return StartCoroutine(Wait(4f));
            Check(Characters.Sein != null && Characters.Sein.gameObject.activeInHierarchy, "title: save loads again");
            Check(UnityEngine.Object.FindObjectsOfType<SeinCharacter>().Length == 1, "title: exactly one Ori after reload");

            Bot(0f, P2Input.BTN_A);
            yield return StartCoroutine(Wait(0.2f));
            Bot(0f, 0);
            yield return StartCoroutine(Wait(0.5f));
            Check(P2 != null && P2 != Characters.Sein, "title: P2 rejoins with a button press after reload");
            if (P2 != null)
            {
                Vector3 p0 = P2.Position;
                Bot(1f, 0);
                yield return StartCoroutine(Wait(1f));
                Bot(0f, 0);
                Check(P2.Position.x - p0.x > 2f, "title: rejoined P2 can move (dx=" + F(P2.Position.x - p0.x) + ")");
                Check(Characters.Sein == P1, "title: Characters.Sein is P1 after rejoin");
            }
            Check(m_errors == err0, "title: no errors");
        }

        string Shot(string name)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(m_out));
            string p = Path.Combine(dir, name + ".png");
            Application.CaptureScreenshot(p);
            Note("screenshot " + p);
            return p;
        }

        public static bool P2HudTestHide { get { return P2Hud.TestHide; } set { P2Hud.TestHide = value; } }

        // Pixels matching `match` in b but not in a, in a square of +-radius around a screen point.
        static int NewPixels(string a, string b, Vector3 screen, int radius, Func<Color, bool> match, string regionPath = null)
        {
            if (!File.Exists(a) || !File.Exists(b)) return -1;
            Texture2D ta = new Texture2D(2, 2), tb = new Texture2D(2, 2);
            ta.LoadImage(File.ReadAllBytes(a)); tb.LoadImage(File.ReadAllBytes(b));
            float sx = ta.width / (float)Screen.width, sy = ta.height / (float)Screen.height;
            int cx = (int)(screen.x * sx), cy = (int)(screen.y * sy), n = 0;
            int x0 = Mathf.Max(0, cx - radius), x1 = Mathf.Min(ta.width, cx + radius);
            int y0 = Mathf.Max(0, cy - radius), y1 = Mathf.Min(ta.height, cy + radius);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (match(tb.GetPixel(x, y)) && !match(ta.GetPixel(x, y))) n++;
            if (regionPath != null)
            {
                Texture2D region = new Texture2D(x1 - x0, y1 - y0, TextureFormat.RGB24, false);
                region.SetPixels(tb.GetPixels(x0, y0, x1 - x0, y1 - y0));
                region.Apply();
                File.WriteAllBytes(regionPath, region.EncodeToPNG());
                UnityEngine.Object.Destroy(region);
            }
            UnityEngine.Object.Destroy(ta); UnityEngine.Object.Destroy(tb);
            return n;
        }

        // Pixels that differ clearly between two screenshots in a square of +-radius around a screen point.
        static int DiffPixels(string a, string b, Vector3 screen, int radius)
        {
            if (!File.Exists(a) || !File.Exists(b)) return -1;
            Texture2D ta = new Texture2D(2, 2), tb = new Texture2D(2, 2);
            ta.LoadImage(File.ReadAllBytes(a)); tb.LoadImage(File.ReadAllBytes(b));
            float sx = ta.width / (float)Screen.width, sy = ta.height / (float)Screen.height;
            int cx = (int)(screen.x * sx), cy = (int)(screen.y * sy), n = 0;
            for (int y = Mathf.Max(0, cy - radius); y < Mathf.Min(ta.height, cy + radius); y++)
                for (int x = Mathf.Max(0, cx - radius); x < Mathf.Min(ta.width, cx + radius); x++)
                {
                    Color p = ta.GetPixel(x, y), q = tb.GetPixel(x, y);
                    if (Mathf.Abs(p.r - q.r) + Mathf.Abs(p.g - q.g) + Mathf.Abs(p.b - q.b) > 0.3f) n++;
                }
            UnityEngine.Object.Destroy(ta); UnityEngine.Object.Destroy(tb);
            return n;
        }

        // Counts pixels matching `match` in a square of +-radius around a screen point (screen y is bottom-up).
        static int CountPixels(string png, Vector3 screen, int radius, Func<Color, bool> match)
        {
            if (!File.Exists(png)) return -1;
            Texture2D t = new Texture2D(2, 2);
            t.LoadImage(File.ReadAllBytes(png));
            float sx = t.width / (float)Screen.width, sy = t.height / (float)Screen.height;
            int cx = (int)(screen.x * sx), cy = (int)(screen.y * sy), n = 0;
            for (int y = Mathf.Max(0, cy - radius); y < Mathf.Min(t.height, cy + radius); y++)
                for (int x = Mathf.Max(0, cx - radius); x < Mathf.Min(t.width, cx + radius); x++)
                    if (match(t.GetPixel(x, y))) n++;
            UnityEngine.Object.Destroy(t);
            return n;
        }

        void Finish()
        {
            if (m_done) return;
            m_done = true;
            Bot(0f, 0);
            List<string> throwing = new List<string>();
            foreach (KeyValuePair<string, int> count in SubsystemGuard.Counts)
                if (count.Value > 5) throwing.Add(count.Key + "=" + count.Value);
            Check(throwing.Count == 0, "subsystem throws <= 5" + (throwing.Count > 0 ? ": " + string.Join(", ", throwing.ToArray()) : ""));
            if (NetPlay.Enabled)
            {
                NetTestHarness.Stop();
                Note(m_fails.Count == 0 ? "RESULT PASS " + m_checks + "/" + m_checks : "RESULT FAIL " + m_fails.Count + "/" + m_checks);
            }
            else Note((m_fails.Count == 0 ? "RESULT PASS" : "RESULT FAIL (" + m_fails.Count + ")") + (m_skips.Count > 0 ? " with " + m_skips.Count + " skipped" : ""));
            try { File.WriteAllText(m_out, string.Join("\n", m_lines.ToArray()) + "\n"); } catch (Exception e) { Plugin.Log.LogError(e); }
            if (NetPlay.Enabled) { Application.Quit(); return; }
            Invoke("Quit", 1.5f);
        }

        void Quit() { Application.Quit(); }

        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class PulsePatch
        {
            static void Postfix()
            {
                // Test: player 1 holds the Spirit Flame button (goes through P1's real Core.Input, like a key press).
                if (P1HoldSpiritFlame) Core.Input.SpiritFlame.Update(true);
                if (!PulseMenu) return;
                PulseMenu = false; // exactly one fixed step of "pressed"
                Core.Input.AnyStart.Update(true);
                Core.Input.ActionButtonA.Update(true);
                Core.Input.Start.Update(true);
            }
        }

        // Fake "any key" for N fixed steps inside PlayerInput itself: only works while PlayerInput is active and
        // FixedUpdate runs, like a real key press.
        public static int FakeAnyKeySteps;
        [HarmonyPatch(typeof(PlayerInput), "IsAnyStartPressed")]
        static class FakeAnyKeyPatch { static void Postfix(ref bool __result) { if (FakeAnyKeySteps > 0) { FakeAnyKeySteps--; __result = true; } } }

        public static int PressStartTicks, PressStartCallbacks, PlayerInputTicks;
        [HarmonyPatch(typeof(TitleScreenPressStartLogic), "FixedUpdate")]
        static class PressStartTickPatch { static void Prefix() { PressStartTicks++; } }
        [HarmonyPatch(typeof(TitleScreenPressStartLogic), "OnStartPressedCallback")]
        static class PressStartCallbackPatch { static void Prefix() { PressStartCallbacks++; } }
        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class PlayerInputTickPatch { static void Prefix(PlayerInput __instance) { if (__instance.Active) PlayerInputTicks++; } }

        static string TitleDiag()
        {
            GameController gc = GameController.Instance;
            TitleScreenPressStartLogic ps = UnityEngine.Object.FindObjectOfType<TitleScreenPressStartLogic>();
            return "screen=" + TitleScreenManager.CurrentScreen + " timeScale=" + Time.timeScale
                + " playerInput.Active=" + (PlayerInput.Instance != null ? PlayerInput.Instance.Active.ToString() : "null")
                + " piTicks=" + PlayerInputTicks + " psTicks=" + PressStartTicks + " psCallbacks=" + PressStartCallbacks
                + " psLogic=" + (ps == null ? "none" : ps.enabled + "/" + ps.gameObject.activeInHierarchy)
                + " lockInput=" + (gc != null && gc.LockInput) + " inputLocked=" + (gc != null && gc.InputLocked)
                + " suspended=" + (gc != null && gc.GameplaySuspended) + " mainMenuVisible=" + UI.MainMenuVisible
                + " coopMenu=" + CoopMenu.IsOpen + " modeMenu=" + NativeModeMenu.IsOpen + " net=" + NetPlay.CurrentPhase
                + " localLocked=" + SessionRules.LocalLocked + " pad=" + KeyboardOnlyP1.State
                + " extTitle=" + (GameStateMachine.Instance != null && GameStateMachine.Instance.IsInExtendedTitleScreen())
                + " gsm=" + (GameStateMachine.Instance != null ? GameStateMachine.Instance.CurrentState.ToString() : "null");
        }

        // Test mode only: keep running when the window loses focus (otherwise the game pauses itself and
        // shows the resume screen whenever the owner clicks another window during a test run).
        [HarmonyPatch(typeof(GameController), "OnApplicationFocus")]
        static class FocusPatch
        {
            static bool Prefix() { if (!Enabled) return true; Application.runInBackground = true; return false; }
        }

        // Test probe: a postfix runs before the proximity finalizer restores globals, so it sees the "Sein"
        // the enemy's own FixedUpdate just used.
        public static readonly Dictionary<int, SeinCharacter> SeenByEnemy = new Dictionary<int, SeinCharacter>();

        [HarmonyPatch(typeof(EntityController), "FixedUpdate")]
        static class EnemySeesPatch
        {
            static void Postfix(EntityController __instance) { if (Enabled) SeenByEnemy[__instance.GetInstanceID()] = Characters.Sein; }
        }

        [HarmonyPatch(typeof(GameController), "RestoreCheckpoint")]
        static class RestoreCountPatch
        {
            static void Prefix() { if (Enabled) RestoreCheckpointCalls++; }
        }
    }
}
