using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    [BepInPlugin("local.oricoop", "Ori Local Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.2.0";   // one place: the BepInEx id, the startup log line
        public static BepInEx.Logging.ManualLogSource Log;
        public static Plugin Instance;
        public static Harmony H;

        ConfigEntry<int> m_pad;
        ConfigEntry<KeyCode> m_spawnKey, m_despawnKey;
        ConfigEntry<float> m_leash;
        ConfigEntry<bool> m_sharedCamera, m_p1Keyboard, m_p2Cutscenes;
        ConfigEntry<int> m_p1Pad;

        // ---- Settings used by the in-game co-op menu (F8) ----
        public int P1Pad { get { return m_p1Pad.Value; } }
        public int P2Pad { get { return m_pad.Value; } }
        public int P2PadInUse { get { return m_activePad >= 0 ? m_activePad : m_pad.Value; } }
        public bool P2CanTriggerCutscenes { get { return m_p2Cutscenes.Value; } set { m_p2Cutscenes.Value = value; } }
        public KeyCode SpawnKey { get { return m_spawnKey.Value; } }

        // Give `pad` (-1 = keyboard only, P1 only) to a player. If the other player had that pad, they swap.
        public void AssignPad(int player, int pad)
        {
            int p1 = m_p1Pad.Value, p2 = P2PadInUse;
            if (player == 1)
            {
                if (pad >= 0 && pad == p2) { m_pad.Value = p1 >= 0 ? p1 : FirstOtherPad(pad); }
                m_p1Pad.Value = pad;
            }
            else if (pad >= 0)
            {
                if (pad == p1) m_p1Pad.Value = p2 >= 0 && p2 != pad ? p2 : -1;
                m_pad.Value = pad;
            }
            m_activePad = -1;
            Log.LogInfo("controllers: P1 " + (m_p1Pad.Value < 0 ? "keyboard/mouse only" : "pad " + m_p1Pad.Value) + ", P2 pad " + m_pad.Value);
        }

        static int FirstOtherPad(int not)
        {
            for (int i = 0; i < 4; i++) if (i != not && P2Input.IsConnected(i)) return i;
            return not == 0 ? 1 : 0;
        }
        int m_activePad = -1;
        bool m_lastConnected, m_parked;
        float m_padScanTimer;

        internal static float LeashOverride = -1f; // test harness only
        Transform m_camTarget;
        float m_mirrorTimer;

        sealed class TaggedTestLog : BepInEx.Logging.ILogListener
        {
            readonly StreamWriter writer;
            public TaggedTestLog(string path) { writer = new StreamWriter(path, false); writer.AutoFlush = true; }
            public void LogEvent(object sender, BepInEx.Logging.LogEventArgs e)
            {
                lock (writer) writer.WriteLine(DateTime.UtcNow.ToString("o") + " [" + e.Level + ":" + e.Source.SourceName + "] " + e.Data);
            }
            public void Dispose() { lock (writer) writer.Dispose(); }
        }

        void Awake()
        {
            Log = Logger;
            Instance = this;
            CoopTrace.Init(Config, BepInEx.Paths.BepInExRootPath);
            string logTag = Environment.GetEnvironmentVariable("ORICOOP_LOG_TAG");
            if (TestHarness.Enabled && !string.IsNullOrEmpty(logTag))
            {
                string dir = @"C:\OriCoop\test"; Directory.CreateDirectory(dir);
                BepInEx.Logging.Logger.Listeners.Add(new TaggedTestLog(Path.Combine(dir, logTag + ".log")));
                Log.LogInfo("test client tag=" + logTag + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                    + " role=" + Environment.GetEnvironmentVariable("ORICOOP_NET") + " clients=" + Environment.GetEnvironmentVariable("ORICOOP_NET_CLIENTS")
                    + " appdata=" + Environment.GetEnvironmentVariable("LOCALAPPDATA") + " result=" + Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT"));
            }
            m_pad = Config.Bind("Player2", "ControllerIndex", 1, "XInput pad index (0-3) for player 2. Player 1 keeps pad 0 and keyboard.");
            m_spawnKey = Config.Bind("Player2", "SpawnKey", KeyCode.F9, "Keyboard key that spawns player 2 (P2 can also press Start).");
            m_despawnKey = Config.Bind("Player2", "DespawnKey", KeyCode.F10, "Keyboard key that removes player 2.");
            m_leash = Config.Bind("Player2", "LeashDistance", 120f, "Safety net only: if the Oris are farther apart than this (e.g. P2 fell out of the world), P2 is put next to P1. Normal play is limited by the screen edges instead.");
            var zoom = Config.Bind("Camera", "DynamicZoom", true, "Camera zooms out smoothly as the players separate.");
            var maxZoom = Config.Bind("Camera", "MaxZoomOut", 1.6f, "Most the camera zooms out, as a multiple of the game's normal distance. The screen edges stop the players at this zoom.");
            var margin = Config.Bind("Camera", "EdgeMargin", 2.5f, "Distance kept between a player and the screen edge.");
            var revive = Config.Bind("Death", "ReviveMode", true, "Local co-op: a dead Ori leaves a skull; the partner holds Spirit Flame near it for 1.5 s to revive it with half their health. Both down = checkpoint reload. false = old behaviour (P2 respawns next to P1 at once).");
            Revive.Enabled = revive.Value; revive.SettingChanged += (s2, e2) => Revive.Enabled = revive.Value;
            var regroup = Config.Bind("Player2", "RegroupAfterSeconds", 2f, "A player off-screen vertically for this long glides back to the other.");
            Action apply = () => { CoopCamera.DynamicZoom = zoom.Value; CoopCamera.MaxZoomOut = Mathf.Max(1f, maxZoom.Value); CoopCamera.EdgeMargin = margin.Value; CoopCamera.RegroupAfter = regroup.Value; };
            apply();
            Config.SettingChanged += (s, e) => apply();
            m_sharedCamera = Config.Bind("Camera", "SharedCamera", true, "Camera follows the midpoint of both Oris.");
            m_p1Keyboard = Config.Bind("Player1", "KeyboardOnly", false, "Legacy: true is the same as Player1.ControllerIndex = -1 (converted on start).");
            m_p1Pad = Config.Bind("Player1", "ControllerIndex", 0,
                "-1 = player 1 uses only keyboard/mouse (all controllers ignored for P1, e.g. one controller + one keyboard); " +
                "0-3 = player 1 uses that controller (plus keyboard/mouse). Change it in game with F8.");
            if (m_p1Keyboard.Value) { m_p1Pad.Value = -1; m_p1Keyboard.Value = false; }
            m_p2Cutscenes = Config.Bind("Story", "P2CanTriggerCutscenes", false,
                "false = only player 1 starts story cutscenes. true = player 2 can too; player 1 is brought to player 2 first.");
            var askMode = Config.Bind("Start", "AskModeOnLoad", true,
                "true = after loading a save, ask: single player (keyboard/mouse and controller both control Ori) or 2 players (choose controllers, P2 joins). false = always 2 players, P2 joins with a button / F9.");
            PlayMode.AskOnLoad = askMode.Value;
            askMode.SettingChanged += (s, e) => PlayMode.AskOnLoad = askMode.Value;
            P2Skin.Init(Config);
            NetPlay.Init(Config);
            DisplayFix.Init(Config);
            KeyboardOnlyP1.P1Pad = m_p1Pad.Value;
            m_p1Pad.SettingChanged += (s, e) => KeyboardOnlyP1.P1Pad = m_p1Pad.Value;
            H = new Harmony("local.oricoop");
            H.PatchAll(typeof(Plugin).Assembly);
            SaveSync.Init(); WorldSync.Init(); ProgressSync.Init(); EnemySync.Init();
            SessionRules.Init(); NetHud.Init();
            P2Context.PatchProximityTypes(H);
            float tp = Time.realtimeSinceStartup; P2Context.PatchTouchTypes(H); Log.LogInfo("touch patching took " + (Time.realtimeSinceStartup - tp).ToString("F2") + " s");
            Log.LogInfo("Ori Local Co-op " + Version + " loaded; P2 pad " + m_pad.Value + (KeyboardOnlyP1.Enabled ? "; P1 keyboard/mouse only" : ""));
            // The BepInEx manager object does not survive in this game, so tick from our own persistent object.
            GameObject runner = new GameObject("OriCoopRunner");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.hideFlags = HideFlags.HideAndDontSave;
            runner.AddComponent<Runner>();
            if (TestHarness.Enabled) runner.AddComponent<TestHarness>();
            if (SkinDump.Enabled) runner.AddComponent<SkinDump>();
            if (LobbyProbe.Enabled) runner.AddComponent<LobbyProbe>();
        }

        public class Runner : MonoBehaviour
        {
            void FixedUpdate() { CoopTrace.FixedTick(); Instance.DoFixedUpdate(); }
            void Update() { CoopTrace.Tick(); Instance.DoUpdate(); }
            void LateUpdate() { Instance.DoLateUpdate(); }
            void OnGUI() { Instance.DoGUI(); }
            void OnDestroy() { Log.LogWarning("OriCoopRunner destroyed"); }
            void OnApplicationQuit() { NetPlay.Shutdown(); CoopTrace.Event("scene", "quit"); CoopTrace.Flush(); }
        }

        void OnDestroy() { Log.LogInfo("BepInEx plugin component destroyed (expected in this game); runner keeps ticking"); }

        internal void DoFixedUpdate()
        {
            try { KeyboardOnlyP1.Refresh(); } catch (Exception e) { Log.LogError(e); }
            // Online: P2 is the partner's Ori, driven by their input from the network, not by a local pad.
            if (NetPlay.Enabled) { NetPlay.FixedTick(); return; }
            int pad =m_activePad >= 0 ? m_activePad : m_pad.Value;
            P2Input.Sample(pad, P2Context.P2Input);
            if (P2Input.Connected || !KeyboardOnlyP1.Enabled) return;
            // Keyboard-only P1: P1 does not need any pad, so P2 takes whichever one is plugged in.
            // Polling an empty XInput slot is slow, so scan at most once a second.
            m_padScanTimer -= Time.unscaledDeltaTime;
            if (m_padScanTimer > 0f) return;
            m_padScanTimer = 1f;
            for (int i = 0; i < 4; i++)
            {
                if (i == pad || i == KeyboardOnlyP1.P1Pad || !P2Input.IsConnected(i)) continue;
                m_activePad = i;
                Log.LogInfo("Player 2 controller: pad " + i);
                break;
            }
        }

        internal void DoUpdate()
        {
            try { NetPlay.Update(); }
            catch (Exception e) { Log.LogError(e); }
            try { SaveSync.Tick(); WorldSync.Tick(); ProgressSync.Tick(); EnemySync.Tick(); } catch (Exception e) { Log.LogError(e); }
            try { SessionRules.Tick(); NetHud.Tick(); } catch (Exception e) { Log.LogError(e); }
            try { Tick(); }
            catch (Exception e) { Log.LogError(e); }
            try { P2Skin.Tick(); }
            catch (Exception e) { Log.LogError(e); }
        }

        void Tick()
        {
            foreach (Puppet puppet in new List<Puppet>(Players.Remotes))
                if (puppet.Sein == null) DespawnPuppet(puppet);
            if (P2Context.P2 == null && P2Context.P1 != null) { Log.LogInfo("P2 gone"); Cleanup(); }
            // Lifecycle watchdog: P2 only exists while P1 does. Any path that removes P1 (title screen, One Life
            // restart, ending, debug scene jumps, or something we don't know about) removes P2 too.
            if (P2Context.P2 != null && (P2Context.P1 == null || Characters.Sein == null || Characters.Sein == P2Context.P2))
                DespawnNow("player 1 is gone");
            if (P2Context.P2 != null && Characters.Sein != P2Context.P1 && Characters.Sein != null && Characters.Sein != P2Context.P2)
            {
                // P1 was re-instantiated (debug reinstantiate / new game); follow it.
                P2Context.P1 = Characters.Sein;
            }

            if (P2Input.Connected != m_lastConnected)
            {
                m_lastConnected = P2Input.Connected;
                Log.LogInfo("Player 2 controller " + (m_lastConnected ? "connected" : "not connected") + " (pad " + (m_activePad >= 0 ? m_activePad : m_pad.Value) + ")");
            }
            CoopMenu.Tick();
            P2Extras.TickZones();
            PlayMode.Tick();
            KeyboardOnlyP1.Refresh();
            bool keySpawn = Input.GetKeyDown(m_spawnKey.Value);
            bool padSpawn = P2Input.StartLatched;
            P2Input.StartLatched = false;
            if (CoopMenu.IsOpen) keySpawn = padSpawn = false; // buttons pressed in the menu don't join
            if (NetPlay.Enabled) keySpawn = padSpawn = false;  // online: P2 appears when the partner is in gameplay
            if (PlayMode.Current != PlayMode.Coop) padSpawn = false; // single player: the controller is P1's
            if (keySpawn && P2Context.P2 == null && PlayMode.Current == PlayMode.Single) PlayMode.SetMode(PlayMode.Coop); // F9 = switch to 2 players
            if (P2Context.P2 == null && (keySpawn || padSpawn))
            {
                Log.LogInfo("P2 join requested by " + (keySpawn ? m_spawnKey.Value.ToString() : "controller button"));
                Spawn();
            }
            if (P2Context.P2 != null && Input.GetKeyDown(m_despawnKey.Value)) DespawnNow(m_despawnKey.Value + " pressed");
            if (P2Context.P2 == null) return;

            SeinCharacter p1 = P2Context.P1, p2 = P2Context.P2;
            if (p1 == null) return;

            // P2 follows P1's availability. Cutscenes that hide/disable P1, switch to another character (Naru,
            // the companion, baby Ori) or a P1 death delay park P2 out of sight; when P1 is back, P2 reappears
            // next to P1. While a script holds P1 still (cutscene walk, animation, movement lock), P2 is held too.
            bool p1Available = p1.gameObject.activeInHierarchy && (Characters.Current == null || object.ReferenceEquals(Characters.Current, p1));
            if (NetPlay.Enabled)
            {
                foreach (Puppet puppet in Players.Remotes)
                {
                    if (!p1Available && puppet.Sein.gameObject.activeSelf)
                    { puppet.Sein.gameObject.SetActive(false); P2Extras.SetCompanionActive(puppet, false); }
                    else if (p1Available && m_parked)
                    { puppet.Sein.gameObject.SetActive(true); P2Extras.SetCompanionActive(puppet, true); Teleport(puppet.Sein, p1.Position); }
                }
                m_parked = !p1Available;
                P2Input.HoldStill = !Revive.IsDowned(p1) && (!p1Available || p1.Controller == null || !p1.Controller.CanMove || p1.Controller.LockMovementInput || p1.Controller.IgnoreControllerInput);
                if (m_parked) return;
                m_mirrorTimer -= Time.unscaledDeltaTime;
                if (m_mirrorTimer <= 0f)
                {
                    m_mirrorTimer = 0.5f;
                    foreach (Puppet puppet in Players.Remotes) { MirrorProgress(p1, puppet.Sein); P2Context.RefreshIds(puppet); }
                }
                Revive.TickOnline();
                return;
            }
            if (!p1Available && p2.gameObject.activeSelf) { p2.gameObject.SetActive(false); P2Extras.SetCompanionActive(false); m_parked = true; Log.LogInfo("P2 parked (P1 unavailable)"); }
            else if (p1Available && m_parked) { m_parked = false; p2.gameObject.SetActive(true); P2Extras.SetCompanionActive(true); Teleport(p2, p1.Position); CoopCamera.Reset(); Log.LogInfo("P2 back next to P1"); }
            P2Input.HoldStill = !Revive.IsDowned(p1) && (!p1Available || p1.Controller == null || !p1.Controller.CanMove || p1.Controller.LockMovementInput || p1.Controller.IgnoreControllerInput);
            if (m_parked) return;

            // Distance limits live in CoopCamera (screen-edge walls, glide-back); this is only a last-resort safety net
            // for things like falling out of the world.
            Revive.Tick();   // local co-op downed / revive (a downed Ori stays at its skull: no leash, no edge walls)
            float leash = LeashOverride > 0f ? LeashOverride : m_leash.Value;
            if (!Revive.AnyDowned && Vector3.Distance(p1.Position, p2.Position) > leash && !CoopCamera.Regrouping) Teleport(p2, p1.Position);

            SharedProgress.Sync(p1, p2);

            m_mirrorTimer -= Time.unscaledDeltaTime;
            if (m_mirrorTimer <= 0f) { m_mirrorTimer = 0.5f; MirrorProgress(p1, p2); P2Context.RefreshIds(); }

            UpdateCamera(p1, p2);
        }

        internal void DoLateUpdate()
        {
            if (P2Context.P2 == null || P2Context.P1 == null) return;
            if (NetPlay.Enabled)
            {
                try
                {
                    foreach (Puppet puppet in Players.Remotes)
                    {
                        P2Context.Enter(puppet);
                        try { if (!m_parked) VisualInvariants.Check(P2Context.P1, puppet.Sein); }
                        finally { P2Context.Exit(); }
                        P2Hud.Update(puppet);
                    }
                    ReviveHud.Update();
                    var cam = UI.Cameras.Current; // the game may retarget "the character" (P2 in P2 context); keep ours
                    if (cam != null)
                        foreach (Puppet puppet in Players.Remotes)
                            if (cam.Target == puppet.Sein.Transform) cam.Target = P2Context.P1.Transform;
                }
                catch (Exception e) { Log.LogError(e); }
                return;
            }
            try
            {
                CoopCamera.FocusOverride = Revive.OnlyAlive; // while one is down the camera frames the living one
                CoopCamera.Apply(P2Context.P1, P2Context.P2, LeashOverride <= 0f && !Revive.AnyDowned);
                if (!m_parked && !Revive.AnyDowned) VisualInvariants.Check(P2Context.P1, P2Context.P2);
                P2Hud.Update(P2Context.P2);
                ReviveHud.Update();
                UpdateCamera(P2Context.P1, P2Context.P2);
            }
            catch (Exception e) { Log.LogError(e); }
        }


        // Screen overlays drawn here: the co-op menu (F8) and, without P2, the join hint. P2's health pips are in the
        // game world (P2Hud), not here. The hint follows the same rule as the pips: shown only while the game shows its
        // own HUD, so menus, maps, cutscenes and fades never get a mod overlay on top.
        internal void DoGUI()
        {
            CoopMenu.Draw();
            if (CoopMenu.IsOpen) return;
            NetPlay.DrawGUI();
            try { NetHud.Draw(); } catch (Exception e) { Log.LogError(e); }
            try { SessionScreens.Draw(); } catch (Exception e) { Log.LogError(e); }
            if (P2Context.P2 == null && !TestHarness.Enabled && PlayMode.Current == PlayMode.Coop) DrawJoinHint();
        }
        GUIStyle m_hintStyle;

        // While in gameplay without P2, tell player 2 how to join (and whether their controller is seen).
        void DrawJoinHint()
        {
            SeinCharacter p1 = Characters.Sein;
            if (p1 == null || !p1.gameObject.activeInHierarchy) return;
            float a = P2Hud.GameHudOpacity();
            if (a <= 0.01f) return;
            if (m_hintStyle == null)
            {
                m_hintStyle = new GUIStyle(GUI.skin.label);
                m_hintStyle.fontSize = Mathf.Max(12, Screen.height / 50);
                m_hintStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            }
            m_hintStyle.normal.textColor = new Color(1f, 0.85f, 0.4f, a);
            string pad = L10n.T(P2Input.Connected ? "p2.pad_found" : "p2.pad_missing");
            GUI.Label(new Rect(16f, Screen.height - 40f, 900f, 30f), L10n.F("p2.join_hint", m_spawnKey.Value, pad), m_hintStyle);
        }

        void UpdateCamera(SeinCharacter p1, SeinCharacter p2)
        {
            if (!m_sharedCamera.Value || m_camTarget == null) return;
            SeinCharacter alive = Revive.OnlyAlive;
            m_camTarget.position = alive != null ? alive.Position : (p1.Position + p2.Position) * 0.5f;
            var cam = UI.Cameras.Current;
            if (cam == null) return;
            // Only take over while the game is following P1; leave cutscene/scripted targets alone.
            // Take over whenever the game is following one of the players (it retargets to "the current character"
            // in several places, which can be P2 inside P2 context); leave cutscene/scripted targets alone.
            if (cam.Target == p1.Transform || cam.Target == p2.Transform) cam.Target = m_camTarget;
        }

        internal void Spawn()
        {
            SpawnPuppet(2);
        }

        public Puppet SpawnPuppet(int seat)
        {
            SeinCharacter p1 = Characters.Sein;
            if (p1 == null || !p1.gameObject.activeInHierarchy) { Log.LogInfo("No P1 in game yet; load a save first."); return null; }
            GameController gc = GameController.Instance;
            if (gc != null && (gc.GameplaySuspended || gc.GameplaySuspendedForUI || gc.InputLocked || gc.IsLoadingGame || (Core.Scenes.Manager != null && Core.Scenes.Manager.IsLoadingScenes)))
            { Log.LogInfo("Not joining now (paused, loading or cutscene); try again in gameplay."); return null; }
            GameObject prefab = Resources.Load<GameObject>("characters/seinCharacter");
            if (prefab == null) { Log.LogError("characters/seinCharacter prefab not found"); return null; }

            ICharacter savedCurrent = Characters.Current;
            GameObject go;
            P2Lifecycle.SpawningP2 = true;
            try { go = (GameObject)UnityEngine.Object.Instantiate(prefab, p1.Position + new Vector3(1f, 0.5f, 0f), Quaternion.identity); }
            finally { P2Lifecycle.SpawningP2 = false; }
            go.name = "seinCharacter_P" + seat;
            SeinCharacter p2 = go.GetComponent<SeinCharacter>();
            // SeinCharacter.Awake made the new copy the global Sein; give that back to P1.
            Characters.Sein = p1;
            Characters.Current = savedCurrent;

            P2Context.P1 = p1;
            Puppet puppet;
            if (NetPlay.Enabled)
            {
                puppet = new Puppet { Seat = seat, Sein = p2 };
                Players.Remotes.Add(puppet);
                Players.Remotes.Sort((a, b) => a.Seat.CompareTo(b.Seat));
            }
            else { P2Context.P2 = p2; puppet = Players.First; }
            P2Context.RefreshIds(puppet);
            P2Context.PatchTypesOn(go, H);
            MirrorProgress(p1, p2, true);
            if (!NetPlay.Enabled)
            {
                SharedProgress.Reset();
                SharedProgress.Sync(p1, p2);
                CoopCamera.Reset();
            }
            VisualInvariants.Reset();
            p2.Mortality.Health.RestoreAllHealth();
            P2Extras.SpawnCompanion(puppet);
            P2Skin.Preload();

            if (!NetPlay.Enabled && m_camTarget == null)
            {
                GameObject t = new GameObject("OriCoop_CameraTarget");
                UnityEngine.Object.DontDestroyOnLoad(t);
                m_camTarget = t.transform;
            }
            Log.LogInfo("P" + seat + " spawned at " + p2.Position + " (" + puppet.Ids.Count + " components)");
            return puppet;
        }

        // Removes P2 right away: deactivated now (so none of its components run again this frame), destroyed at end of frame.
        // Test-only mutation switch: proves the "title" test fails without the lifecycle fix.
        static readonly bool s_mutateNoLifecycle = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_LIFECYCLE") == "1";

        internal static void DespawnNow(string reason)
        {
            if (s_mutateNoLifecycle && reason != Instance.m_despawnKey.Value + " pressed") return;
            SeinCharacter p2 = P2Context.P2;
            if (p2 == null && P2Context.P1 == null) return;
            Log.LogInfo("P2 removed: " + reason);
            // Someone downed: P2 comes back before removal; a downed P1 with nobody left to revive it dies as in single
            // player, unless the game itself is tearing gameplay down (title screen etc.) or P1 is already gone.
            bool gameTearingDown = reason == "game removed gameplay objects" || reason == "player 1 is gone";
            Revive.ClearAll(reason, !gameTearingDown);
            ReviveHud.DestroyAll();
            foreach (Puppet puppet in new List<Puppet>(Players.Remotes)) DespawnPuppet(puppet);
            if (Players.Remotes.Count == 0 && P2Context.P1 != null) Instance.Cleanup();
        }

        public static void DespawnPuppet(Puppet puppet)
        {
            SeinCharacter p2 = puppet.Sein;
            if (p2 != null)
            {
                if (Characters.Sein == p2) Characters.Sein = null;
                if (object.ReferenceEquals(Characters.Current, p2)) Characters.Current = null;
                P2Lifecycle.DropCarried(p2);
                p2.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(p2.gameObject);
            }
            P2Hud.Destroy(puppet);
            P2Context.Reset(puppet);
            Players.Remotes.Remove(puppet);
            puppet.Sein = null;
            if (Players.Remotes.Count == 0) Instance.Cleanup();
        }

        // The game's single cleanup point for leaving gameplay (return to title, One Life restart, trial end,
        // debug scene jumps, DestroyGameplayElementsAction). P2 goes before P1 is destroyed.
        [HarmonyPatch(typeof(GameController), "RemoveGameplayObjects")]
        static class RemoveGameplayObjectsPatch
        {
            static void Prefix() { DespawnNow("game removed gameplay objects"); PlayMode.Reset(); }
        }

        void Cleanup()
        {
            P2Input.HoldStill = false;
            P2Hud.Destroy();
            P2Extras.ResetOccupancy();
            m_parked = false;
            P2Context.Reset();
            var cam = UI.Cameras.Current;
            if (cam != null && m_camTarget != null && cam.Target == m_camTarget)
                cam.Target = P2Context.P1 != null ? P2Context.P1.Transform : null;
            P2Context.P2 = null;
            P2Context.P1 = null;
            P2Context.P2Ids.Clear();
        }

        public static void Teleport(SeinCharacter who, Vector3 pos)
        {
            who.Position = pos + new Vector3(0f, 0.5f, 0f);
            VisualInvariants.Grace(1.5f);
            who.Speed = Vector3.zero;
            // The game brings "the" companion along on its own teleports via the global Characters.Ori, which is
            // P1's. When P2 jumps, bring P2's companion along the same way, as P2.
            Puppet puppet = Players.OfSein(who);
            Ori comp = puppet == null ? null : puppet.Companion;
            if (comp != null && comp.gameObject.activeInHierarchy)
            {
                P2Context.Enter(puppet);
                try { comp.MoveOriToPlayer(); }
                catch (Exception e) { Log.LogWarning("companion follow: " + e.Message); }
                finally { P2Context.Exit(); }
            }
            else if (puppet == null && who == Characters.Sein && Characters.Ori != null)
            {
                Characters.Ori.MoveOriToPlayer();
            }
        }

        static readonly FieldInfo[] s_abilityFields = Array.FindAll(typeof(PlayerAbilities).GetFields(BindingFlags.Instance | BindingFlags.Public),
            f => f.FieldType == typeof(CharacterAbility));

        // Shared progress: P1's save is the source of truth; P2 gets the same abilities and caps.
        static void MirrorProgress(SeinCharacter p1, SeinCharacter p2, bool force = false)
        {
            bool changed = false;
            foreach (FieldInfo f in s_abilityFields)
            {
                CharacterAbility a = (CharacterAbility)f.GetValue(p1.PlayerAbilities), b = (CharacterAbility)f.GetValue(p2.PlayerAbilities);
                if (a != null && b != null && b.HasAbility != a.HasAbility) { b.HasAbility = a.HasAbility; changed = true; }
            }
            if (changed || force) P2Lifecycle.EnsureAbilityPrefabs(p2);
            p2.Mortality.Health.MaxHealth = p1.Mortality.Health.MaxHealth;
            p2.Energy.Max = p1.Energy.Max;
        }

        // P2 dying must not trigger the checkpoint reload: put P2 back next to P1 with full health instead.
        [HarmonyPatch(typeof(SeinDamageReciever), "OnKill")]
        static class P2DeathPatch
        {
            static bool Prefix(SeinDamageReciever __instance)
            {
                if (P2Context.P2 == null) return true;
                Puppet puppet = Players.OfInstance(__instance.GetInstanceID());
                bool isP2 = puppet != null;
                // Online: the partner's Ori lives and dies in its owner's game; here it just keeps following them.
                if (isP2 && NetPlay.Enabled) { puppet.Sein.Mortality.Health.RestoreAllHealth(); return false; }
                // Online: the local Ori dying goes downed while the partner can revive it (ReviveNet.cs).
                if (NetPlay.Enabled && !isP2 && Revive.Enabled && P2Context.P1 != null && __instance.transform.IsChildOf(P2Context.P1.transform)) return !Revive.HandleDeathOnline(P2Context.P1);
                if (NetPlay.Enabled && !isP2 && SessionRules.HandleOwnDeathOnline(__instance)) return false;
                // Local co-op: either Ori's death becomes "downed" while the partner is alive (Revive decides).
                if (!NetPlay.Enabled && Revive.Enabled)
                {
                    SeinCharacter who = isP2 ? P2Context.P2
                        : (P2Context.P1 != null && __instance.transform.IsChildOf(P2Context.P1.transform) ? P2Context.P1 : null);
                    if (who != null) return !Revive.HandleDeath(who);
                }
                if (!isP2) return true;
                Log.LogInfo("P2 died; respawning at P1");
                P2Context.P2.Mortality.Health.RestoreAllHealth();
                if (P2Context.P1 != null) Teleport(P2Context.P2, P2Context.P1.Position);
                return false;
            }
        }
    }
}
