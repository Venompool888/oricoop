using System;
using Game;
using HarmonyLib;
using SmartInput;
using UnityEngine;

namespace OriCoop
{
    // T-180: after quitting to the title, "按任意键继续" ignored keyboard, mouse and pad until an alt-tab
    // (owner's trace 2026-11-21 19:44:22-19:44:27). The screen reacts only to the press EDGE of Core.Input.AnyStart
    // (TitleScreenPressStartLogic.FixedUpdate), and PlayerInput.IsAnyStartPressed ORs the pad buttons with Unity's
    // Input.anyKey (every keyboard key and mouse button). When Unity misses a key-up (Windows drops it around an IME
    // switch or a focus change; Unity's legacy input never recovers by itself), anyKey stays true, AnyStart never
    // goes back to "released", and no press of any device makes an edge. Losing focus makes Unity clear its key
    // state, which is why alt-tab helped.
    // Fix, only on the press-any-key screen: when the keyboard/mouse part has been held for 0.5 s, treat it as stuck:
    // reset Unity's input once and from then on ignore the held level; a pad button or a NEW key-down (anyKeyDown,
    // latched per rendered frame because fixed steps can skip a frame) still counts. A real release ends it.
    public static class TitleAnyKey
    {
        const float StuckAfter = 0.5f;
        public static int StuckEvents, FreshPresses;
        public static bool Stuck { get { return s_stuck; } }
        // Test-only mutation switch: proves the title's stuck-key check fails without this fix.
        static readonly bool s_mutate = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_TITLE_STUCK") == "1";
        static float s_heldSince = -1f;
        static bool s_stuck;
        static int s_fresh;   // fixed steps left that report a fresh key press

        static bool OnPressStart
        {
            get { return Characters.Sein == null && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart; }
        }

        // Runs first among IsAnyStartPressed's postfixes, so the test harness's fake press (TestHarness.FakeAnyKeySteps)
        // still forces "pressed" after it.
        [HarmonyPatch(typeof(PlayerInput), "IsAnyStartPressed")]
        static class Level
        {
            [HarmonyPriority(Priority.First)]
            static void Postfix(ref bool __result) { __result = Filter(__result); }
        }

        // anyKeyDown is true for one rendered frame only; latch it for the next fixed steps.
        [HarmonyPatch(typeof(GameController), "Update")]
        static class FreshKey
        {
            static void Postfix() { if (s_stuck && Input.anyKeyDown) { s_fresh = 2; FreshPresses++; } }
        }

        static bool Filter(bool level)
        {
            if (s_mutate) return level;
            if (!OnPressStart) { Clear(); return level; }
            bool pad = PadPressed();
            bool keyboard = level && !pad;
            if (!keyboard) { if (s_stuck) Note("released"); Clear(); return level; }
            float now = Time.realtimeSinceStartup;
            if (s_heldSince < 0f) s_heldSince = now;
            if (!s_stuck && now - s_heldSince >= StuckAfter)
            {
                s_stuck = true; StuckEvents++;
                try { Input.ResetInputAxes(); } catch (Exception) { }
                Note("a key or mouse button reads as held for " + StuckAfter + " s; ignoring held keys, a new press or a pad button continues");
            }
            if (!s_stuck) return level;
            if (s_fresh > 0) { s_fresh--; return true; }
            return false;
        }

        static void Clear() { s_heldSince = -1f; s_stuck = false; s_fresh = 0; }

        static bool PadPressed()
        {
            return XboxControllerInput.GetButton(XboxControllerInput.Button.Start) || XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonA)
                || XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonB) || XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonX)
                || XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonY);
        }

        static void Note(string what)
        {
            if (Plugin.Log != null) Plugin.Log.LogInfo("title any key: " + what);
            CoopTrace.Event("ui", "title.anykey", "state", what);
        }
    }
}
