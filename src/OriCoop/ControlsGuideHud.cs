using System;
using BepInEx.Configuration;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // T-053: draws the one-time controls guide (ControlsGuide) with the game's font at the bottom of the screen.
    // Online only, once per install ([UI] ControlsGuideSeen). It is a plain label: no input, no pause, and it
    // fades with the game HUD (menus and cutscenes hide it). Test runs ignore the saved flag and show it each run.
    internal static class ControlsGuideHud
    {
        static ConfigEntry<bool> s_seen;
        static ControlsGuide s_guide;
        static GUIStyle s_style;
        static string s_text = "";
        static bool s_textZh;
        internal static int FramesDrawn;          // tests: frames the guide was on screen
        internal static string LastText = "";     // tests: what it said
        internal static float LastTimeScale = 1f; // tests: Time.timeScale while it was on screen

        static ControlsGuide Guide
        {
            get
            {
                if (s_guide != null) return s_guide;
                bool seen = false;
                if (!TestHarness.Enabled)
                {
                    s_seen = Plugin.Instance.Config.Bind("UI", "ControlsGuideSeen", false,
                        "The move/jump guide shown the first time you play online. Set to false to see it again.");
                    seen = s_seen.Value;
                }
                s_guide = new ControlsGuide(seen);
                return s_guide;
            }
        }

        internal static void Draw()
        {
            ControlsGuide guide = Guide;
            if (guide.Done) return;
            bool playing = NetPlay.Enabled && NetPlay.LocalInGameplay();
            float hud = playing ? P2Hud.GameHudOpacity() : 0f;
            float a = guide.Update(playing && hud > 0.01f, Time.unscaledTime) * hud;
            if (guide.Done)
            {
                if (s_seen != null) { s_seen.Value = true; Plugin.Instance.Config.Save(); }
                Plugin.Log.LogInfo("controls guide: shown");
                return;
            }
            if (a <= 0.01f) return;
            if (s_style == null) s_style = new GUIStyle(GUI.skin.label);
            s_style.fontSize = Mathf.Max(14, Screen.height / 38);
            s_style.alignment = TextAnchor.MiddleCenter;
            Color c = new Color(1f, 1f, 1f, a);
            s_style.normal.textColor = c;
            if (s_text.Length == 0 || s_textZh != L10n.Zh) { s_textZh = L10n.Zh; s_text = BuildText(s_textZh); }
            Rect r = new Rect(0f, Screen.height * 0.78f, Screen.width, s_style.fontSize * 2f);
            NativeGui.BeginPage("guide.controls");
            try
            {
                if (NativeGui.Available) NativeGui.Label("guide.controls.text", r, s_text, s_style.fontSize, TextAnchor.MiddleCenter, c);
                else GUI.Label(r, s_text, s_style);
            }
            finally { NativeGui.EndPage(); }
            if (Event.current == null || Event.current.type == EventType.Repaint)
            {
                FramesDrawn++; LastText = s_text; LastTimeScale = Time.timeScale;
            }
        }

        static string BuildText(bool zh)
        {
            string left = "", right = "", jump = "";
            try
            {
                PlayerInputRebinding.KeyBindingSettings k = PlayerInputRebinding.KeyRebindings;
                if (k != null)
                {
                    left = First(k.HorizontalDigiPadLeft); right = First(k.HorizontalDigiPadRight); jump = First(k.Jump);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("controls guide: key bindings unavailable: " + e.Message); }
            if (jump.Length == 0) jump = "Space";
            return ControlsGuide.Text(zh, left, right, jump);
        }

        static string First(KeyCode[] keys)
        {
            return keys != null && keys.Length > 0 ? keys[0].ToString() : "";
        }
    }
}
