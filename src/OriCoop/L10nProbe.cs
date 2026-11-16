using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CatlikeCoding.TextBox;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // ORICOOP_L10N_PROBE=1 (tests): records every text OriCoop draws, through IMGUI (all GUI.Label/Button/Box/
    // GUILayout string calls go through GUIContent.Temp) and through the native MessageBoxes of our own objects
    // (roots named OriCoop*), and checks them in the current game language:
    //   - no label mixes Chinese with Latin words outside the whitelist (proper nouns, keys, units);
    //   - in English no label contains Chinese; in Chinese no label is an English table text (did not switch);
    //   - every key resolves, and every Chinese text of the table has its glyphs in the live Chinese font.
    // Run the title scenario twice: run-test.ps1 -Scenarios title with ORICOOP_L10N_PROBE=1, once with
    // ORICOOP_FORCE_ZH=1 (Chinese) and once without (English). Checks() is called from TestHarness's title scenario.
    static class L10nProbe
    {
        public static bool On { get { return Environment.GetEnvironmentVariable("ORICOOP_L10N_PROBE") == "1"; } }

        static readonly Dictionary<string, string> s_seen = new Dictionary<string, string>();   // text -> where
        static string s_where = "";
        static float s_nextScan;

        public static readonly HashSet<string> Whitelist = new HashSet<string>(StringComparer.Ordinal)
        {
            "Steam", "IP", "OriCoop", "Ori", "UDP", "TCP", "ms", "P1", "P2", "P3", "P4", "F5", "F8", "F9", "F10",
            "ID", "Ctrl", "V", "A", "B", "X", "Y", "Esc", "Enter", "PC", "Tailscale", "LAN", "HUD", "RTT",
        };
        static readonly Regex s_markup = new Regex(@"<[^>]*>|\[[^\]]*\]|\{\d+[^}]*\}|#");
        static readonly Regex s_placeholder = new Regex(@"\{\d+[^}]*\}");
        static readonly Regex s_word = new Regex(@"[A-Za-z][A-Za-z0-9']*");

        public static bool HasCjk(string s)
        {
            foreach (char c in s) if ((c >= '㐀' && c <= '鿿') || (c >= '　' && c <= '〿') || (c >= '＀' && c <= '￯')) return true;
            return false;
        }

        static bool HasHan(string s)
        {
            foreach (char c in s) if (c >= '㐀' && c <= '鿿') return true;
            return false;
        }

        // Latin words outside the whitelist in a text that also has Chinese, or "" when the text is fine.
        public static string MixedWords(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasHan(text)) return "";
            var bad = new List<string>();
            foreach (Match m in s_word.Matches(s_markup.Replace(s_placeholder.Replace(text, "1"), " ")))
                if (!Whitelist.Contains(m.Value)) bad.Add(m.Value);
            return string.Join(",", bad.ToArray());
        }

        public static void Where(string where) { s_where = where ?? ""; }

        static void Record(string text, string where)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0 || s_seen.Count > 5000) return;
            if (!s_seen.ContainsKey(text)) s_seen[text] = where;
        }

        [HarmonyPatch]
        static class GuiContentTempPatch
        {
            static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(GUIContent), "Temp", new[] { typeof(string) });
            }
            static bool Prepare() { return On; }
            static void Prefix(string t) { Record(t, "imgui " + s_where); }
        }

        // Native boxes of our own objects, sampled a few times a second (called from Checks' coroutine and Tick).
        static void ScanBoxes()
        {
            if (Time.realtimeSinceStartup < s_nextScan) return;
            s_nextScan = Time.realtimeSinceStartup + 0.2f;
            try
            {
                foreach (MessageBox box in UnityEngine.Object.FindObjectsOfType<MessageBox>())
                {
                    if (box == null) continue;
                    RecordBox(box);
                }
            }
            catch (Exception) { }
        }

        // Ours = any ancestor named OriCoop* (our clones are reparented under the game's menus).
        static string OurRoot(Transform t)
        {
            for (; t != null; t = t.parent) if (t.name.StartsWith("OriCoop")) return t.name;
            return null;
        }

        static void RecordBox(MessageBox box)
        {
            if (box == null || !box.gameObject.activeInHierarchy) return;
            string root = OurRoot(box.transform);
            if (root == null) return;
            string text = null;
            try { text = Traverse.Create(box).Field("m_currentMessage").Field("Message").GetValue<string>(); } catch (Exception) { }
            if (string.IsNullOrEmpty(text)) text = box.OverrideText;
            Record(text, "native " + root + " " + s_where + " page=" + NativeModeMenu.LastPage);
        }

        [HarmonyPatch(typeof(MessageBox), "RefreshText")]
        static class BoxRefreshPatch
        {
            static bool Prepare() { return On; }
            static void Postfix(MessageBox __instance) { RecordBox(__instance); }
        }

        [HarmonyPatch(typeof(MessageBox), "Update")]
        static class BoxScanPatch
        {
            static bool Prepare() { return On; }
            static void Postfix() { ScanBoxes(); }
        }

        static HashSet<int> ChineseGlyphs() { return Glyphs(true); }

        static HashSet<int> Glyphs(bool chinese)
        {
            foreach (MessageBox box in Resources.FindObjectsOfTypeAll<MessageBox>())
            {
                try
                {
                    if (box == null || box.LanguageStyles == null) continue;
                    TextStyleCollection col = chinese ? box.LanguageStyles.Chinese : box.LanguageStyles.English;
                    if (col == null) continue;
                    if (col.styles == null || col.styles.Length == 0 || col.styles[0].font == null) continue;
                    BitmapFont font = col.styles[0].font;
                    var have = new HashSet<int>();
                    if (font.asciiChars != null) foreach (BitmapFontChar c in font.asciiChars) if (c != null) have.Add(c.id);
                    if (font.otherChars != null) foreach (BitmapFontChar c in font.otherChars) if (c != null) have.Add(c.id);
                    if (have.Count > 50) return have;
                }
                catch (Exception) { }
            }
            return null;
        }

        // Shows the IMGUI surfaces we can open without a session, then evaluates everything recorded so far.
        public static IEnumerator Checks(MonoBehaviour host, Action<bool, string> check, Action<string> note)
        {
            if (!On) yield break;
            bool zh = L10n.Zh;
            string lang = zh ? "zh" : "en";
            note("--- l10n probe (" + lang + ")");

            Where("F8 settings"); CoopMenu.Open(); yield return host.StartCoroutine(Wait(0.8f)); CoopMenu.Close();
            Where("toast"); NetHud.Toast(L10n.T("server.title.desc", true), L10n.T("server.title.desc", false)); yield return host.StartCoroutine(Wait(0.8f));
            Where("my rooms");
            try { OriCoop.Rooms.RoomsPanel.Open(); } catch (Exception e) { note("l10n probe: rooms panel: " + e.Message); }
            yield return host.StartCoroutine(Wait(1.2f));
            try { if (OriCoop.Rooms.RoomsPanel.Instance != null) Traverse.Create(OriCoop.Rooms.RoomsPanel.Instance).Field("visible").SetValue(false); } catch (Exception) { }
            Where("");
            ScanBoxes();

            // Table: every key has both texts, Chinese texts only use baked glyphs, no mixing inside an entry.
            HashSet<int> glyphs = ChineseGlyphs(), latin = Glyphs(false);
            var noLatin = new List<string>();
            int keys = 0; var noGlyph = new List<string>(); var mixed = new List<string>();
            var enTexts = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in L10n.Keys)
            {
                keys++;
                string z = L10n.T(key, true), e = L10n.T(key, false);
                enTexts.Add(e);
                string mw = MixedWords(z);
                if (mw.Length > 0 || HasCjk(e)) mixed.Add(key);
                if (glyphs != null)
                    foreach (char c in z)
                        if (c > '~' && !char.IsWhiteSpace(c) && !glyphs.Contains(c)) { noGlyph.Add(key + "'" + c + "'"); break; }
                if (latin != null)
                    foreach (char c in s_markup.Replace(e, ""))
                        if (c > ' ' && !latin.Contains(c)) { noLatin.Add(key + "'" + c + "'"); break; }
            }
            check(L10n.MissingCount == 0, "l10n: every key used at run time resolves (" + L10n.MissingList + ")");
            check(mixed.Count == 0, "l10n: no table entry mixes languages (" + string.Join(", ", mixed.ToArray()) + ")");
            check(glyphs != null, "l10n: found the live Chinese font");
            check(latin != null && noLatin.Count == 0, "l10n: every English table text has its glyphs in the game's English font (" + string.Join(", ", noLatin.ToArray()) + ")");
            check(noGlyph.Count == 0, "l10n: every Chinese table text has its glyphs in the live font (" + string.Join(", ", noGlyph.ToArray()) + ")");

            // Everything drawn during the run.
            var bad = new List<string>();
            int n = 0;
            foreach (KeyValuePair<string, string> kv in s_seen)
            {
                n++;
                string t = kv.Key, why = null;
                string mw = MixedWords(t);
                if (mw.Length > 0) why = "mixes Chinese with " + mw;
                else if (!zh && HasCjk(t)) why = "Chinese in English mode";
                else if (zh && enTexts.Contains(t) && s_word.IsMatch(t) && !HasCjk(t) && s_word.Matches(t).Count > 1) why = "English table text in Chinese mode";
                if (why != null && bad.Count < 40) bad.Add("[" + kv.Value + "] " + why + ": " + t.Replace("\n", "\\n"));
            }
            foreach (string b in bad) note("l10n probe: " + b);
            note("l10n probe: " + n + " distinct texts recorded, " + keys + " keys");
            check(n > 10, "l10n: probe recorded the pages' texts (" + n + ")");
            check(bad.Count == 0, "l10n: every drawn label is in one language (" + lang + ", " + bad.Count + " bad)");
        }

        static IEnumerator Wait(float s) { float end = Time.realtimeSinceStartup + s; while (Time.realtimeSinceStartup < end) yield return null; }
    }
}
