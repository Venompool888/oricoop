using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace OriCoop
{
    // T-180 check for run-test's "title" scenario: Unity believes a key is held (a lost key-up), then the player presses
    // a key. Oracle (owner's report / vanilla after alt-tab): a new press on "按任意键继续" moves on.
    // Keys are faked in the test-only TestQuiet plugin (InputBlock.FakeAnyKey / FakeAnyKeyDown), which every
    // UnityEngine.Input.anyKey / anyKeyDown call in the game and the mod is redirected to in test games.
    public static class TitleAnyKeyTest
    {
        static FieldInfo s_held, s_down;

        public static bool Available
        {
            get
            {
                if (s_held != null) return true;
                Type t = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if ((t = a.GetType("OriCoop.TestQuiet.InputBlock")) != null) break;
                if (t == null) return false;
                s_held = t.GetField("FakeAnyKey"); s_down = t.GetField("FakeAnyKeyDown");
                if (s_held == null || s_down == null) { s_held = s_down = null; return false; }
                return true;
            }
        }

        static void Set(FieldInfo f, bool v) { if (f != null) f.SetValue(null, v); }
        public static void Release() { Set(s_held, false); Set(s_down, false); }
        // Call while still in the game: the key goes down there and its key-up is "lost", as in the owner's session.
        public static void HoldKeyFromGameplay() { if (Available) Set(s_held, true); }

        // Returns via onDone: (moved on, diagnostic).
        public static IEnumerator StuckThenPress(Action<bool, string> onDone)
        {
            int stuck0 = TitleAnyKey.StuckEvents;
            try
            {
                float end = Time.unscaledTime + 1.5f;
                while (Time.unscaledTime < end) yield return null;
                bool stillThere = TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart;
                Set(s_down, true); yield return null; yield return null; // a new key press: down for one rendered frame...
                Set(s_down, false);                                      // ...then held like the stuck one (still held)
                end = Time.unscaledTime + 3f;
                while (Time.unscaledTime < end && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart) yield return null;
                bool moved = TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.PressStart;
                onDone(moved && stillThere, "heldAloneMovedOn=" + !stillThere + " stuckEvents=" + (TitleAnyKey.StuckEvents - stuck0)
                    + " freshPresses=" + TitleAnyKey.FreshPresses + " screen=" + TitleScreenManager.CurrentScreen);
            }
            finally { Release(); }
        }
    }
}
