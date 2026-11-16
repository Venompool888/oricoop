using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CatlikeCoding.TextBox;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Playtest 2026-11-16, P5: the Sunken Glades guide stone (sunkenGladesWaterhole/guideStoneSetup: 精灵洞穴 /
    // 空洞树林 / Black Root Burrows). Its text fades in on SeinEnterExitTrigger enter and out on exit.
    //  * Text stayed on screen after both players left: an Ori whose collider vanished inside the zone (death,
    //    revive, puppet snap) never sent OnTriggerExit, so the zone thought it was still occupied (P2Extras zones).
    //  * Chinese came out garbled: the stone's own text material still sampled the replaced font atlas (FontGlyphs).
    // Harness scenario "signpost" (local co-op, run with ORICOOP_FORCE_ZH=1 for the glyph checks) and helpers for the
    // online two-client check in TitleInputTest.cs.
    static class SignpostTest
    {
        public static readonly Vector3 StonePos = new Vector3(-69f, -208f, 0f);
        public static readonly Vector3 AwayPos = new Vector3(-120f, -207f, 0f);
        const string Root = "sunkenGladesWaterhole/guideStoneSetup";

        public static RuntimeSceneMetaData Scene()
        {
            foreach (RuntimeSceneMetaData s in Core.Scenes.Manager.AllScenes)
                if (s != null && s.Scene == "sunkenGladesWaterhole") return s;
            return null;
        }

        static BaseAnimator s_timeline;
        static SeinEnterExitTrigger s_zone;
        static AnimatorDriver Driver()
        {
            if (s_timeline == null)
            {
                GameObject t = GameObject.Find(Root + "/directions/timeline");
                s_timeline = t != null ? t.GetComponent<BaseAnimator>() : null;
                GameObject z = GameObject.Find(Root + "/directions/triggerCollider");
                s_zone = z != null ? z.GetComponent<SeinEnterExitTrigger>() : null;
            }
            return s_timeline != null ? s_timeline.AnimatorDriver : null;
        }

        public static bool Found { get { return Driver() != null; } }
        // Shown: fading in or fully in. Hidden: fading out or gone.
        public static bool Shown { get { AnimatorDriver d = Driver(); return d != null && !d.IsReversed && d.CurrentTime > 0.01f; } }
        public static bool Hidden { get { AnimatorDriver d = Driver(); return d != null && (d.IsReversed || d.CurrentTime <= 0.01f); } }
        public static string Diag()
        {
            AnimatorDriver d = Driver();
            SeinEnterExitTrigger z = s_zone;
            return d == null ? "no stone" : "t=" + d.CurrentTime.ToString("F2") + "/" + d.Duration.ToString("F2") + (d.IsReversed ? " reversed" : " forward")
                + " active=" + s_timeline.gameObject.activeInHierarchy + " occupants=" + (z == null ? "?" : P2Extras.OccupantsOf(z)) + " staleExits=" + P2Extras.StaleZoneExits;
        }

        // Chinese glyph check: every text renderer of the stone samples the atlas its font's glyph table points into
        // (the WenKai atlas once FontGlyphs replaced it), and the font has every character of the three texts.
        public static bool GlyphsOk(out string diag)
        {
            diag = "lang=" + GameSettings.Instance.Language + " fontglyphs=" + FontGlyphs.Status + " lateMats=" + FontGlyphs.LateMaterialsFixed;
            GameObject stone = GameObject.Find(Root);
            if (stone == null) { diag += " no stone"; return false; }
            bool ok = true; int boxes = 0;
            foreach (MessageBox box in stone.GetComponentsInChildren<MessageBox>(true))
            {
                boxes++;
                TextStyleCollection col = box.LanguageStyles != null ? box.LanguageStyles.Current : null;
                BitmapFont font = col != null && col.styles != null && col.styles.Length > 0 && col.styles[0] != null ? col.styles[0].font : null;
                string text = "";
                try { foreach (MessageDescriptor m in box.MessageProvider.GetMessages()) text += m.Message; } catch (Exception) { }
                HashSet<int> have = new HashSet<int>();
                if (font != null)
                {
                    if (font.asciiChars != null) foreach (BitmapFontChar c in font.asciiChars) have.Add(c.id);
                    if (font.otherChars != null) foreach (BitmapFontChar c in font.otherChars) have.Add(c.id);
                }
                string miss = "";
                foreach (char ch in text) if (ch > ' ' && ch != '#' && !have.Contains(ch)) miss += ch;
                string tex = "";
                foreach (Renderer r in box.GetComponentsInChildren<Renderer>(true))
                {
                    Material m = r.sharedMaterial;
                    Texture t = m != null && m.HasProperty("_TxtTexture") ? m.GetTexture("_TxtTexture") : null;
                    tex += (t == null ? "null" : t.name) + ";";
                    if (GameSettings.Instance.Language == Language.Chinese && (t == null || !t.name.Contains("+OriCoop"))) ok = false;
                }
                if (miss.Length > 0) ok = false;
                diag += " | '" + text + "' font=" + (font == null ? "null" : font.name) + " missing='" + miss + "' atlas=" + tex;
            }
            return ok && boxes == 3;
        }

        static string ShotPath(TestHarness h, string name) { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(h.ResultPath)), name + ".png"); }
        static IEnumerator Wait(float s) { float end = Time.unscaledTime + s; while (Time.unscaledTime < end) yield return null; }
        // Pins both Oris (position and speed) every frame, like the harness's HoldBoth.
        static IEnumerator Hold(SeinCharacter a, Vector3 pa, SeinCharacter b, Vector3 pb, float s)
        {
            float end = Time.unscaledTime + s;
            VisualInvariants.Grace(s + 3f);   // pinned teleports: the companions need time to catch up
            while (Time.unscaledTime < end)
            {
                if (a != null) { a.Position = pa; a.Speed = Vector3.zero; }
                if (b != null) { b.Position = pb; b.Speed = Vector3.zero; }
                yield return new WaitForFixedUpdate();
            }
        }

        public static IEnumerator Run(TestHarness h)
        {
            h.Note("--- scenario: signpost (guide stone text)");
            SeinCharacter p1 = P2Context.P1, p2 = P2Context.P2;
            RuntimeSceneMetaData scene = Scene();
            if (scene == null || p1 == null || p2 == null) { h.Check(false, "signpost: scene and both players present"); yield break; }
            VisualInvariants.Grace(20f);
            Plugin.Teleport(p1, AwayPos); Plugin.Teleport(p2, AwayPos + Vector3.right);
            float end = Time.unscaledTime + 15f;
            while (Time.unscaledTime < end && !Found) yield return null;
            yield return h.StartCoroutine(Wait(2f));
            h.Check(Found && Hidden, "signpost: guide stone loaded, text hidden while nobody is near (" + Diag() + ")");

            // Both near: shown. Both leave: hidden.
            Plugin.Teleport(p1, StonePos); Plugin.Teleport(p2, StonePos);
            yield return h.StartCoroutine(Hold(p1, StonePos, p2, StonePos, 2.5f));
            h.Check(Shown, "signpost: text shows with both players at the stone (" + Diag() + ")");
            string why; bool glyphs = GlyphsOk(out why);
            Application.CaptureScreenshot(ShotPath(h, "signpost_near"));
            h.Check(glyphs, "signpost: text renders from the font's own atlas, no missing characters (" + why + ")");
            yield return h.StartCoroutine(Hold(p1, AwayPos, p2, AwayPos + Vector3.right, 2.5f));
            h.Check(Hidden, "signpost: text hides when both walk away (" + Diag() + ")");

            // P2's collider vanishes inside the zone (as on death / revive / a puppet snap), then P1 walks away.
            // P2 stays pinned at the stone (no collider, so no exit event), which also keeps the scene loaded.
            yield return h.StartCoroutine(Hold(p1, StonePos, p2, StonePos, 2.5f));
            string before = Diag();
            List<Collider> off = new List<Collider>();
            foreach (Collider c in p2.GetComponentsInChildren<Collider>()) if (c.enabled && c.CompareTag("Player")) { c.enabled = false; off.Add(c); }
            yield return h.StartCoroutine(Hold(p1, StonePos, p2, StonePos, 0.5f));
            yield return h.StartCoroutine(Hold(p1, AwayPos, p2, StonePos, 2.5f));
            h.Check(off.Count > 0 && Hidden, "signpost: text hides after P1 leaves while P2's collider vanished inside (" + off.Count + " collider(s); before: " + before + "; now: " + Diag() + ")");
            Application.CaptureScreenshot(ShotPath(h, "signpost_away"));
            yield return h.StartCoroutine(Hold(p1, AwayPos, p2, AwayPos + Vector3.right, 0.5f));
            foreach (Collider c in off) if (c != null) c.enabled = true;
            yield return h.StartCoroutine(Hold(p1, AwayPos, p2, AwayPos + Vector3.right, 1f));
            h.Check(Hidden, "signpost: still hidden once P2 is back to normal away from it (" + Diag() + ")");
        }
    }
}
