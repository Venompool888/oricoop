// Test-only plugin: installed ONLY into the test copy (C:\OriCoop\game\BepInEx\plugins\OriCoop.TestQuiet.dll) by
// src\TestQuiet\build-testquiet.ps1. Never shipped in a kit and never copied to the owner's D: game.
// It does nothing unless the test launcher (src\test-launch.ps1) sets the environment variables:
//   ORICOOP_QUIET=1     the game makes no sound (AudioListener.volume = 0 every frame).
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
            float sc;
            if (float.TryParse(Environment.GetEnvironmentVariable("ORICOOP_TIMESCALE") ?? "", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out sc) && sc >= 1f && sc <= 8f) m_scale = sc;
            if (m_quiet || noSteam) Logger.LogInfo("TestQuiet: quiet=" + m_quiet + " noSteam=" + noSteam + " timeScale x" + m_scale);
            if (!m_quiet && m_scale == 1f) enabled = false;
        }

        // ORICOOP_TIMESCALE=N (test speed-up, opt-in per suite): multiply the game's own Time.timeScale by N. The game
        // sets timeScale itself (loading 0.5, pause 0, normal 1); any value that differs from the one we wrote last is
        // taken as the game's new base. Only for suites whose checks do not depend on real time (no latency/timeouts).
        float m_scale = 1f, m_base = 1f, m_applied = -1f;
        void Update() { ApplyScale(); }
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
