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
        static readonly HashSet<string> s_imguiPages = new HashSet<string>();
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
            static void Prefix(string t)
            {
                Record(t, "imgui " + s_where);
                if (!string.IsNullOrEmpty(t) && t.Trim().Length > 0) s_imguiPages.Add(s_where);
            }
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
            bool roomsShown = false;
            try { roomsShown = NativeModeMenu.L10nRoomsOpen(); } catch (Exception e) { note("l10n probe: native rooms: " + e.Message); }
            yield return host.StartCoroutine(Wait(1.2f));
            ScanBoxes();
            try { if (roomsShown) NativeModeMenu.L10nRoomsClose(); } catch (Exception) { }
            Where("");
            ScanBoxes();

            // Ask for the patched descriptors, not the raw (unmodified) Chinese fields.
            string[] chineseNames = { "奥里", "奥日", "赛安", "纳鲁", "古莫", "古门", "黑子", "埃基", "阿诺", "尼尔", "里姆", "菲尔", "阿祖" };
            int nameItems = 0, nameOffenders = 0;
            var nameSamples = new List<string>();
            foreach (TranslatedMessageProvider provider in Resources.FindObjectsOfTypeAll<TranslatedMessageProvider>())
                foreach (TranslatedMessageProvider.MessageItem item in provider.Messages)
                {
                    int index = nameItems++;
                    string text = item.GetDescriptor(Language.Chinese).Message;
                    if (string.IsNullOrEmpty(text)) continue;
                    foreach (string name in chineseNames)
                        if (text.IndexOf(name, StringComparison.Ordinal) >= 0)
                        {
                            nameOffenders++;
                            if (nameSamples.Count < 10) nameSamples.Add(provider.name + " item " + index + ": " + text.Replace("\n", "\\n"));
                            break;
                        }
                }
            check(nameOffenders == 0, "l10n: Chinese game descriptors keep character names in English (" + nameItems + " items, " + nameOffenders + " offenders)");
            foreach (string sample in nameSamples) note("l10n name offender: " + sample);

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
            yield return host.StartCoroutine(FontFrames(host, check, note));

            if (zh)
            {
                // Empty Chinese forces the same fallback + terms path used by the game's hints.
                TranslatedMessageProvider provider = null;
                MessageBox box = null;
                try
                {
                    provider = ScriptableObject.CreateInstance<TranslatedMessageProvider>();
                    provider.Messages.Add(new TranslatedMessageProvider.MessageItem
                    {
                        English = "I am #Sein#, the light and eyes of the #Spirit Tree#.", Chinese = ""
                    });
                    box = Game.UI.Hints.Show(provider, HintLayer.Gameplay, 6f);
                }
                catch (Exception e) { note("l10n name hint: " + e.Message); }
                yield return host.StartCoroutine(Wait(1.5f));
                string shown = null;
                try { if (box != null) shown = Traverse.Create(box).Field("m_currentMessage").Field("Message").GetValue<string>(); }
                catch (Exception e) { note("l10n name hint text: " + e.Message); }
                check(box != null && !string.IsNullOrEmpty(shown) && HasHan(shown) && shown.IndexOf("Sein", StringComparison.Ordinal) >= 0,
                    "l10n: native Chinese game hint contains Sein (" + shown + ")");
                Shot("l10n_name_hint");
                yield return host.StartCoroutine(Wait(0.5f));
                try { if (box != null && box.Visibility != null) box.Visibility.HideMessageScreenImmediately(); } catch (Exception) { }
                if (provider != null) UnityEngine.Object.Destroy(provider);
            }
        }



        static IEnumerator FontFrames(MonoBehaviour host, Action<bool, string> check, Action<string> note)
        {
            note("--- native game font probe");
            var pages = new List<string> { "session.paused", "session.save", "steam.invite", "display.notice", "p2.joinhint", "saves.dialog" };
            // Further pages announce themselves: any class with static string[] L10nTestPages() and
            // static void L10nTestShow(string page, bool on) (so ported files need no edit here).
            var shows = new Dictionary<string, System.Reflection.MethodInfo>();
            foreach (Type t in typeof(L10nProbe).Assembly.GetTypes())
            {
                System.Reflection.MethodInfo list = t.GetMethod("L10nTestPages", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                System.Reflection.MethodInfo show = t.GetMethod("L10nTestShow", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (list == null || show == null) continue;
                foreach (string pg in (string[])list.Invoke(null, null)) { pages.Add(pg); shows[pg] = show; }
            }
            try
            {
                foreach (string page in pages)
                {
                    s_imguiPages.Remove(page);
                    Where(page);
                    bool shot = false;
                    SessionScreens.TestPage = page.StartsWith("session.") ? page : "";
                    SteamInviteHook.TestShow = page == "steam.invite";
                    DisplayFix.TestShow = page == "display.notice";
                    Plugin.TestJoinHint = page == "p2.joinhint";
                    CoopSaves.TestShow = page == "saves.dialog";
                    foreach (KeyValuePair<string, System.Reflection.MethodInfo> kv in shows) if (kv.Key != page) kv.Value.Invoke(null, new object[] { kv.Key, false });
                    if (shows.ContainsKey(page)) shows[page].Invoke(null, new object[] { page, true });
                    float end = Time.realtimeSinceStartup + 1f;
                    bool seen = false, native = false;
                    while (Time.realtimeSinceStartup < end)
                    {
                        yield return null;
                        if (NativeGui.PagesLastFrame.Contains(page))
                        {
                            seen = true;
                            if (NativeGui.LabelsLastFrame > 0 && NativeGui.PageUsesGameFont(page)) native = true;
                        }
                        ScanBoxes();
                        if (!shot && Time.realtimeSinceStartup > end - 0.3f) { shot = true; Shot("native_" + page.Replace(".", "_")); }
                    }
                    check(seen, "native font: page drawn " + page);
                    check(native, "native font: native label uses current BitmapFont " + page);
                    check(!s_imguiPages.Contains(page), "native font: no IMGUI strings " + page);
                }
            }
            finally
            {
                foreach (Camera cam in Camera.allCameras)
                    note("camera " + cam.name + " depth " + cam.depth + " mask 0x" + cam.cullingMask.ToString("X") + " ortho " + cam.orthographic + " clear " + cam.clearFlags + " near " + cam.nearClipPlane + (cam == P2Hud.GuiCamera() ? " (GUI camera)" : ""));
                if (SaveSlotsUI.Instance != null)
                {
                    foreach (Renderer rr in SaveSlotsUI.Instance.GetComponentsInChildren<Renderer>())
                    {
                        note("save slots renderer layer " + rr.gameObject.layer + " queue " + (rr.sharedMaterial != null ? rr.sharedMaterial.renderQueue : -1) + " z " + rr.transform.position.z + " " + rr.name);
                        break;
                    }
                }
                foreach (KeyValuePair<string, string> kv in NativeGui.Metrics) note("native label " + kv.Key + ": " + kv.Value);
                SessionScreens.TestPage = ""; SteamInviteHook.TestShow = false; DisplayFix.TestShow = false;
                Plugin.TestJoinHint = false; CoopSaves.TestShow = false;
                foreach (KeyValuePair<string, System.Reflection.MethodInfo> kv in shows) kv.Value.Invoke(null, new object[] { kv.Key, false });
                Where("");
            }
            yield return host.StartCoroutine(host_LanguageSwitch(check, note));
        }


        static void Shot(string name)
        {
            try
            {
                string outFile = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT");
                string dir = string.IsNullOrEmpty(outFile) ? "." : System.IO.Path.GetDirectoryName(outFile);
                string path = System.IO.Path.Combine(dir, name + "_" + (L10n.Zh ? "zh" : "en") + ".png");
                Application.CaptureScreenshot(path);
                Plugin.Log.LogInfo("l10n probe: screenshot " + path);
            }
            catch (Exception) { }
        }

        // Live switch through the game's own setter (as Options > Language does): our native boxes set through
        // L10n.Set, the title items and the IMGUI text must follow. Skipped under ORICOOP_FORCE_ZH (the getter is
        // pinned to Chinese there).
        static IEnumerator host_LanguageSwitch(Action<bool, string> check, Action<string> note)
        {
            if (Environment.GetEnvironmentVariable("ORICOOP_FORCE_ZH") == "1" || GameSettings.Instance == null) { note("l10n: live switch skipped (forced Chinese)"); yield break; }
            note("--- live language switch");
            Language start = GameSettings.Instance.Language;
            GameText probe = new GameText("OriCoop_L10nSwitchProbe");
            MessageBox box = probe.Ok ? probe.Go.GetComponentInChildren<MessageBox>() : null;
            if (box != null) { probe.Go.SetActive(true); L10n.Set(box, "server.title.desc"); }
            int relabels0 = NativeModeMenu.LanguageRelabels;
            Language[] seq = { start == Language.Chinese ? Language.English : Language.Chinese, start };
            foreach (Language lang in seq)
            {
                GameSettings.Instance.Language = lang;
                float end = Time.realtimeSinceStartup + 0.6f;
                while (Time.realtimeSinceStartup < end) yield return null;
                bool zh = lang == Language.Chinese;
                string want = L10n.T("server.title.desc", zh);
                string got = null;
                try { got = box != null ? Traverse.Create(box).Field("m_currentMessage").Field("Message").GetValue<string>() : null; } catch (Exception) { }
                check(L10n.Zh == zh, "l10n live switch: L10n.Zh follows the game language (" + lang + ")");
                check(box != null && got == want, "l10n live switch: native box re-reads its text (" + lang + ": '" + got + "')");
                SessionScreens.TestPage = "session.paused";
                Where("switch " + lang);
                float end2 = Time.realtimeSinceStartup + 0.6f;
                while (Time.realtimeSinceStartup < end2) yield return null;
                Shot("switch_session_paused");
                SessionScreens.TestPage = "";
            }
            check(NativeModeMenu.LanguageRelabels >= relabels0 + 2, "l10n live switch: title items relabelled on each switch (" + (NativeModeMenu.LanguageRelabels - relabels0) + ")");
            check(GameSettings.Instance.Language == start, "l10n live switch: language restored (" + GameSettings.Instance.Language + ")");
            probe.Destroy();
        }
        static IEnumerator Wait(float s) { float end = Time.realtimeSinceStartup + s; while (Time.realtimeSinceStartup < end) yield return null; }
    }
}
