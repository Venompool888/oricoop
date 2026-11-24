// Test-only plugin: installed ONLY into the test copy (C:\OriCoop\game\BepInEx\plugins\OriCoop.TestQuiet.dll) by
// src\TestQuiet\build-testquiet.ps1. Never shipped in a kit and never copied to the owner's D: game.
// It does nothing unless the test launcher (src\test-launch.ps1) sets the environment variables:
//   ORICOOP_QUIET=1     the game makes no sound (AudioListener.volume = 0 every frame), never reads a real gamepad,
//                       keyboard or mouse and never vibrates a controller (InputBlock.cs, T-085).
//   ORICOOP_NO_STEAM=1  Ori's Steamworks.Awake is skipped, exactly like the vanilla "Steam not available" path
//                       (SteamInterface stays null, the component is destroyed). Without this, the test copy calls
//                       SteamAPI_Init with steam_appid.txt (387290) and Steam tracks the test process as
//                       "Ori and the Blind Forest running" (Steam logs\gameprocess_log.txt), which can block or
//                       confuse the owner's own Play button and shows "playing Ori" to friends.
using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace OriCoop.TestQuiet
{
    [BepInPlugin("local.oricoop.testquiet", "OriCoop TestQuiet", "1.0.0")]
    public class TestQuietPlugin : BaseUnityPlugin
    {
        static bool Flag(string name) { return Environment.GetEnvironmentVariable(name) == "1"; }

        bool m_quiet;
        int m_w, m_h;
        float m_nextSize;

        void Awake()
        {
            m_quiet = Flag("ORICOOP_QUIET");
            // ORICOOP_SCREEN=WxH: keep the window at the size the test asked for. Unity takes the size saved in the
            // registry (shared with the owner's copy) over -screen-width/-height, so a bad saved size (e.g. a
            // minimized 375x22 window) would otherwise shrink every later test game.
            string s = Environment.GetEnvironmentVariable("ORICOOP_SCREEN") ?? "";
            string[] wh = s.Split('x');
            if (wh.Length != 2 || !int.TryParse(wh[0], out m_w) || !int.TryParse(wh[1], out m_h) || m_w < 64 || m_h < 64) { m_w = 0; m_h = 0; }
            bool noSteam = Flag("ORICOOP_NO_STEAM");
            if (noSteam)
            {
                new Harmony("local.oricoop.testquiet").PatchAll(typeof(SteamSkip));
            }
            // T-085: a launcher-started test game (ORICOOP_QUIET=1) never reads or vibrates a real device. Patched here
            // for what is loaded, again in Start for plugin assemblies loaded after this one (the mod), then verified.
            // Test-only mutation switch ORICOOP_MUTATE_NO_INPUT_BLOCK=1: no blocking and no line, so the launcher must refuse.
            if (m_quiet && !Flag("ORICOOP_MUTATE_NO_INPUT_BLOCK"))
            {
                // T-227 test-only mutation: the mod's own device imports are left alone; the self-test must say so.
                InputBlock.MutateNoNativeBlock = Flag("ORICOOP_MUTATE_NO_NATIVE_PAD_BLOCK");
                m_harmony = new Harmony("local.oricoop.testquiet.input");
                try { InputBlock.Apply(m_harmony, Logger); } catch (Exception e) { m_blockError = e.GetType().Name + ": " + e.Message; }
                Harmony h = m_harmony; BepInEx.Logging.ManualLogSource log = Logger;
                AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => { try { InputBlock.Apply(h, log); } catch (Exception e) { log.LogError("TestQuiet: input block on a late assembly: " + e.Message); } };
            }
            float sc;
            if (float.TryParse(Environment.GetEnvironmentVariable("ORICOOP_TIMESCALE") ?? "", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out sc) && sc >= 1f && sc <= 8f) m_scale = sc;
            if (m_quiet || noSteam) Logger.LogInfo("TestQuiet: quiet=" + m_quiet + " noSteam=" + noSteam + " timeScale x" + m_scale);
            m_fps = Flag("ORICOOP_FPS_LOG");
            // ORICOOP_TARGET_FPS=N (10..59): hold the game at N fps, like a slow PC (T-071 reproduces game-timed checks at a
            // low game rate this way; Ori itself sets 60). Vsync is turned off so the cap applies.
            int fps; if (int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_TARGET_FPS") ?? "", out fps) && fps >= 10 && fps <= 59) m_targetFps = fps;
            if (m_targetFps > 0) Logger.LogInfo("TestQuiet: target fps " + m_targetFps);
            if (!m_quiet && m_scale == 1f && !m_fps && m_targetFps == 0) enabled = false;
        }

        // ORICOOP_TIMESCALE=N (test speed-up, opt-in per suite): multiply the game's own Time.timeScale by N. The game
        // sets timeScale itself (loading 0.5, pause 0, normal 1); any value that differs from the one we wrote last is
        // taken as the game's new base. Only for suites whose checks do not depend on real time (no latency/timeouts).
        float m_scale = 1f, m_base = 1f, m_applied = -1f;
        int m_targetFps;
        void ApplyFps() { if (m_targetFps > 0 && (Application.targetFrameRate != m_targetFps || QualitySettings.vSyncCount != 0)) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = m_targetFps; } }
        void Update() { ApplyFps(); ApplyScale(); SampleFrame(); }

        Harmony m_harmony;
        string m_blockError;
        // The launcher refuses to run a test whose games do not log "physical input blocked, vibration blocked".
        void Start()
        {
            if (m_harmony == null) return;
            if (m_blockError == null)
            {
                try { InputBlock.Apply(m_harmony, Logger); m_blockError = InputBlock.Verify(); }
                catch (Exception e) { m_blockError = e.GetType().Name + ": " + e.Message; }
            }
            if (m_blockError == null) Logger.LogInfo("TestQuiet: physical input blocked, vibration blocked (" + InputBlock.Sites + " call sites, "
                + InputBlock.DeviceImports + " device imports, " + InputBlock.SkippedMethods + " device methods off)");
            else Logger.LogError("TestQuiet: INPUT BLOCK FAILED: " + m_blockError);
        }

        // ORICOOP_FPS_LOG=1: every 10 s log the frame time (avg / p95 / max ms, unscaled) as "TestQuiet: frames ...".
        // Used to measure what parallel test games cost a game the owner plays (a stand-in copy).
        bool m_fps;
        readonly System.Collections.Generic.List<float> m_frames = new System.Collections.Generic.List<float>();
        float m_nextFps, m_gameSum, m_realSum;
        void SampleFrame()
        {
            if (!m_fps) return;
            m_frames.Add(Time.unscaledDeltaTime * 1000f);
            // Game time per real second: Ori caps a frame's game time at Time.maximumDeltaTime (1/60, or 1/30 after
            // it detects slow frames), so below that frame rate the game's own timers (respawn, revive) run slow.
            if (Time.timeScale > 0f) { m_gameSum += Time.deltaTime / Time.timeScale; m_realSum += Time.unscaledDeltaTime; }
            if (Time.realtimeSinceStartup < m_nextFps) return;
            m_nextFps = Time.realtimeSinceStartup + 10f;
            if (m_frames.Count < 10) { m_frames.Clear(); m_gameSum = 0f; m_realSum = 0f; return; }
            m_frames.Sort();
            float sum = 0f; foreach (float f in m_frames) sum += f;
            Logger.LogInfo("TestQuiet: frames n=" + m_frames.Count + " avg=" + (sum / m_frames.Count).ToString("F2") + "ms p95="
                + m_frames[(int)(m_frames.Count * 0.95f)].ToString("F2") + "ms max=" + m_frames[m_frames.Count - 1].ToString("F2") + "ms"
                + " gameRate=" + (m_realSum > 0f ? m_gameSum / m_realSum : 0f).ToString("F2") + " maxDelta=" + Time.maximumDeltaTime.ToString("F4"));
            m_frames.Clear(); m_gameSum = 0f; m_realSum = 0f;
        }
        void ApplyScale()
        {
            if (m_scale == 1f) return;
            float cur = Time.timeScale;
            if (Mathf.Abs(cur - m_applied) > 0.0001f) m_base = cur;
            m_applied = Mathf.Min(100f, m_base * m_scale);
            if (Time.timeScale != m_applied) Time.timeScale = m_applied;
        }

        void LateUpdate()
        {
            ApplyScale();
            if (AudioListener.volume != 0f) AudioListener.volume = 0f;
            if (m_w > 0 && Time.realtimeSinceStartup >= m_nextSize && (Screen.width != m_w || Screen.height != m_h || Screen.fullScreen))
            {
                m_nextSize = Time.realtimeSinceStartup + 2f;
                Logger.LogInfo("TestQuiet: window " + Screen.width + "x" + Screen.height + " -> " + m_w + "x" + m_h);
                Screen.SetResolution(m_w, m_h, false);
            }
        }

        [HarmonyPatch(typeof(global::Steamworks), "Awake")]
        static class SteamSkip
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(global::Steamworks __instance)
            {
                UnityEngine.Object.Destroy(__instance);
                return false;
            }
        }
    }
}
