using System;
using System.Collections;
using System.IO;
using OriCoop.Pads;
using UnityEngine;

namespace OriCoop.Glyphs
{
    // T-178 screenshot probe (ORICOOP_GLYPH_PROBE=1, launched by src\run-glyphprobe.ps1). Keeps the control scheme on
    // Controller and walks title -> save slots -> Options > Controls, switching the icon set Xbox -> PlayStation ->
    // Xbox on each page; checks that every live icon texture shows the wanted set and saves the game's own frame.
    // Lines: "GLYPHPROBE PASS/FAIL ...", result file = ORICOOP_TEST_OUT.
    public class GlyphProbe : MonoBehaviour
    {
        public static bool Enabled { get { return Environment.GetEnvironmentVariable("ORICOOP_GLYPH_PROBE") == "1"; } }
        static string Out { get { return Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT"); } }
        static string Dir { get { return string.IsNullOrEmpty(Out) ? Application.dataPath : Path.GetDirectoryName(Path.GetFullPath(Out)); } }
        int m_fails, m_checks;

        void Note(string s) { Plugin.Log.LogInfo("GLYPHPROBE " + s); try { if (!string.IsNullOrEmpty(Out)) File.AppendAllText(Out, s + "\n"); } catch (Exception) { /* log line already written */ } }
        void Shot(string name) { string p = Path.Combine(Dir, "glyph_" + name + ".png"); Application.CaptureScreenshot(p); Note("screenshot " + p); }
        static IEnumerator Wait(float s) { float end = Time.realtimeSinceStartup + s; while (Time.realtimeSinceStartup < end) yield return null; }

        void Start() { PadGlyphs.Trace = true; StartCoroutine(Run()); }

        void Update()
        {
            Application.runInBackground = true;
            var gs = GameSettings.Instance;
            if (gs != null && gs.CurrentControlScheme != ControlScheme.Controller) gs.CurrentControlScheme = ControlScheme.Controller;
        }

        // switch, wait for the swap + a frame, check the live textures, screenshot
        IEnumerator Page(string page, PadFamily family)
        {
            PadGlyphs.TestForce = family;
            PadGlyphs.RescanSoon();
            yield return StartCoroutine(Wait(1.5f));
            string d = PadGlyphs.Describe();
            string[] parts = d.Split(' ');
            string[] n = parts.Length > 1 ? parts[1].Split('/') : new string[0];
            bool ok = parts[0] == family.ToString() && n.Length == 2 && n[0] == n[1] && n[1] != "0";
            m_checks++; if (!ok) m_fails++;
            Note((ok ? "ok " : "FAIL ") + page + " " + family + ": " + d);
            Shot(page + "_" + family);
            yield return StartCoroutine(Wait(0.5f));
        }

        IEnumerator Run()
        {
            Note("up; PlayStation pad connected=" + PadDetector.PlayStationConnected + ", last=" + PadDetector.Last);
            for (int i = 0; i < 4; i++) { string v = VirtualPads.Describe(i); if (v != null) Note("virtual slot " + i + ": " + v); }
            float t0 = Time.realtimeSinceStartup;
            while (TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.PressStart && TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.MainMenu)
            {
                if (Time.realtimeSinceStartup - t0 > 90f) { Note("FAIL title never shown"); Finish(); yield break; }
                yield return StartCoroutine(Wait(0.5f));
            }
            yield return StartCoroutine(Wait(6f));
            TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
            yield return StartCoroutine(Wait(3f));
            yield return StartCoroutine(Page("saveslots", PadFamily.Xbox));
            yield return StartCoroutine(Page("saveslots", PadFamily.PlayStation));
            TitleScreenManager.SetScreen(TitleScreenManager.Screen.MainMenu);
            yield return StartCoroutine(Wait(1f));
            try { Game.UI.Menu.ShowOptions(); } catch (Exception e) { Note("FAIL options: " + e.Message); }
            yield return StartCoroutine(Wait(2f));
            try
            {
                var nav = OptionsScreen.Instance.Navigation;
                foreach (var it in nav.MenuItems) if (it.name.ToLower().Contains("control")) nav.SetCurrentMenuItem(it);
            }
            catch (Exception e) { Note("FAIL controls page: " + e.Message); }
            yield return StartCoroutine(Wait(2f));
            yield return StartCoroutine(Page("controls", PadFamily.PlayStation));
            yield return StartCoroutine(Page("controls", PadFamily.Xbox));
            yield return StartCoroutine(Page("controls", PadFamily.PlayStation));
            yield return StartCoroutine(InputCheck());
            Finish();
        }

        void Check(bool ok, string what) { m_checks++; if (!ok) m_fails++; Note((ok ? "ok " : "FAIL ") + what); }

        // A simulated PlayStation pad on a free XInput slot must reach the game (J2i polling, which P1 uses) and P2's
        // sampler with the XInput meaning of the PlayStation buttons (Cross = A = jump, Circle = B = cancel).
        IEnumerator InputCheck()
        {
            int slot = -1;
            for (int i = 3; i >= 0 && slot < 0; i--) { var st = new J2i.Net.XInputWrapper.XInputState(); if (J2i.Net.XInputWrapper.XInput.XInputGetState(i, ref st) != 0) slot = i; }
            if (slot < 0) { Note("skip input check: no free XInput slot"); yield break; }
            Note("input check on simulated slot " + slot);
            VirtualPads.TestSnapshot = new PadSnapshot { Buttons = 0x1000, LX = -32768 };   // Cross, stick full left
            VirtualPads.TestPacket++; VirtualPads.TestSlot = slot;
            yield return StartCoroutine(Wait(0.5f));
            var c = J2i.Net.XInputWrapper.XboxController.RetrieveController(slot);
            Check(c.IsConnected && c.IsAPressed && c.LeftThumbStickX < -30000, "game sees Cross as A on slot " + slot + " (connected=" + c.IsConnected + " A=" + c.IsAPressed + " lx=" + c.LeftThumbStickX + ")");
            var p2 = new InputState(); P2Input.SampleExtra(slot, p2);
            Check(P2Input.IsConnected(slot) && p2.Jump.IsPressed && p2.HorizontalAnalogLeft < -0.9f, "P2 sampler: Cross = jump, stick left (jump=" + p2.Jump.IsPressed + " lx=" + p2.HorizontalAnalogLeft + ")");
            VirtualPads.TestSnapshot = new PadSnapshot { Buttons = 0x2000 };                // Circle
            VirtualPads.TestPacket++;
            yield return StartCoroutine(Wait(0.5f));
            P2Input.SampleExtra(slot, p2);
            Check(!c.IsAPressed && c.IsBPressed && p2.Cancel.IsPressed && !p2.Jump.IsPressed, "Circle = B / cancel, Cross released (A=" + c.IsAPressed + " B=" + c.IsBPressed + ")");
            VirtualPads.TestSlot = -1;
            yield return StartCoroutine(Wait(0.5f));
            Check(!c.IsConnected || !c.IsBPressed, "simulated pad removed");
        }

        void Finish()
        {
            Note((m_fails == 0 && m_checks > 0 ? "PASS " : "FAIL ") + (m_checks - m_fails) + "/" + m_checks + " icon checks");
            PadGlyphs.TestForce = PadFamily.None;
            StartCoroutine(QuitSoon());
        }

        static IEnumerator QuitSoon() { float end = Time.realtimeSinceStartup + 2f; while (Time.realtimeSinceStartup < end) yield return null; Application.Quit(); }
    }
}
