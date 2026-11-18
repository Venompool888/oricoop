using System;
using System.Collections.Generic;
using HarmonyLib;

namespace OriCoop
{
    // The game's Chinese translation was never finished (this build locks Chinese; LanguageUnlock turns it on), so
    // some entries are blank. For a blank entry TranslatedMessageProvider.MessageItem.GetDescriptor returns the
    // placeholder "_Empty_пусто_空_空の_", whose Cyrillic and kana letters aren't in the Chinese font and draw as
    // boxes (seen on empty save/backup slots). Here a blank Chinese entry uses a known Chinese word if we have one,
    // otherwise the English text.
    static class ZhFallback
    {
        public static int Used;   // fallbacks taken (for tests / logs)
        static readonly HashSet<string> s_logged = new HashSet<string>();
        static Dictionary<string, string> s_zhMap;
        static List<KeyValuePair<string, string>> s_terms;
        static readonly Dictionary<string, string> s_termCache = new Dictionary<string, string>(StringComparer.Ordinal);
        // This low-confidence glossary name must survive the shorter approved Wall Jump replacement.
        // File escapes: \n = a real line break, \\ = one backslash (some game strings hold a literal backslash-n,
        // which the game's own text parser turns into a line break later).
        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // Chinese for the entries the game left blank: zh_extra.txt (embedded), one "key<TAB>Chinese" per line, where
        // key = Ui.ZhTextKey.Of(English) so the file carries no game text (escapes above). Lines starting with # are
        // comments.
        static Dictionary<string, string> Map()
        {
            if (s_zhMap != null) return s_zhMap;
            s_zhMap = new Dictionary<string, string>(StringComparer.Ordinal);
            s_zhMap[Ui.ZhTextKey.Of("Empty")] = "空"; s_zhMap[Ui.ZhTextKey.Of("Empty Slot")] = "空的栏位";
            try
            {
                using (var st = typeof(ZhFallback).Assembly.GetManifestResourceStream("OriCoop.zh_extra.txt"))
                {
                    if (st != null)
                        using (var rd = new System.IO.StreamReader(st, System.Text.Encoding.UTF8))
                        {
                            string line;
                            while ((line = rd.ReadLine()) != null)
                            {
                                if (line.Length == 0 || (line[0] == '#' && line.IndexOf('\t') < 0)) continue;
                                int tab = line.IndexOf('\t');
                                if (tab <= 0 || tab == line.Length - 1) continue;
                                string key = line.Substring(0, tab);
                                if (!Ui.ZhTextKey.IsKey(key)) continue;   // only hashed keys; never English text
                                s_zhMap[key] = Unescape(line.Substring(tab + 1));
                            }
                        }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("zh fallback: can't read zh_extra.txt: " + e.Message); }
            Plugin.Log.LogInfo("zh fallback: " + s_zhMap.Count + " Chinese texts for blank entries");
            return s_zhMap;
        }

        // The Chinese for a blank entry's English text (tried as stored, then trimmed), or null.
        internal static string Lookup(string english)
        {
            if (string.IsNullOrEmpty(english)) return null;
            string zh;
            if (Map().TryGetValue(Ui.ZhTextKey.Of(english), out zh)) return zh;
            string trimmed = english.Trim();
            if (trimmed != english && Map().TryGetValue(Ui.ZhTextKey.Of(trimmed), out zh)) return zh;
            return null;
        }

        static List<KeyValuePair<string, string>> Terms()
        {
            if (s_terms != null) return s_terms;
            s_terms = new List<KeyValuePair<string, string>>();
            try
            {
                using (var st = typeof(ZhFallback).Assembly.GetManifestResourceStream("OriCoop.zh_terms.txt"))
                {
                    if (st != null)
                        using (var rd = new System.IO.StreamReader(st, System.Text.Encoding.UTF8))
                        {
                            string line;
                            while ((line = rd.ReadLine()) != null)
                            {
                                if (line.Length == 0 || line[0] == '#') continue;
                                int tab = line.IndexOf('\t');
                                if (tab <= 0 || tab == line.Length - 1) continue;
                                string source = Unescape(line.Substring(0, tab));
                                string target = Unescape(line.Substring(tab + 1));
                                // Rules cannot consume markup delimiters or placeholder boundaries.
                                if (source.IndexOfAny(new[] { '[', ']', '#', '^' }) >= 0) continue;
                                s_terms.Add(new KeyValuePair<string, string>(source, target));
                            }
                        }
                }
                s_terms.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b)
                {
                    int length = b.Key.Length.CompareTo(a.Key.Length);
                    return length != 0 ? length : string.CompareOrdinal(a.Key, b.Key);
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning("zh terms: can't read zh_terms.txt: " + e.Message); }
            return s_terms;
        }

        static readonly string[] s_characterNames = { "Ori", "Sein", "Naru", "Gumo", "Gumon", "Kuro", "Eki", "Ano", "Nir", "Reem", "Fil", "Atsu" };

        static bool IsHan(char c)
        {
            return (c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff');
        }

        static bool IsNameLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
        }

        // Only character names get Han/Latin spacing; leave markup and all other text untouched.
        // For #Name# put any space outside the highlight, never between # and the name.
        static string SpaceNames(string text)
        {
            var spaces = new bool[text.Length + 1];
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                if (depth != 0)
                {
                    if (text[i] == ']') depth--;
                    continue;
                }
                foreach (string name in s_characterNames)
                {
                    int end = i + name.Length;
                    if (end > text.Length || string.CompareOrdinal(text, i, name, 0, name.Length) != 0) continue;
                    if ((i > 0 && IsNameLetter(text[i - 1])) || (end < text.Length && IsNameLetter(text[end]))) continue;
                    int left = i, right = end;
                    if (left > 0 && text[left - 1] == '#') left--;
                    if (right < text.Length && text[right] == '#') right++;
                    // Only tight Han adjacency needs a space, so existing spaces cannot be doubled.
                    if (left > 0 && IsHan(text[left - 1])) spaces[left] = true;
                    if (right < text.Length && IsHan(text[right])) spaces[right] = true;
                    i = end - 1;
                    break;
                }
            }
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (spaces[i]) sb.Append(' ');
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        static string ApplyTerms(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cached;
            if (s_termCache.TryGetValue(text, out cached)) return cached;
            List<KeyValuePair<string, string>> terms = Terms();
            var sb = new System.Text.StringBuilder(text.Length);
            int depth = 0;
            for (int i = 0; i < text.Length; )
            {
                char ch = text[i];
                if (ch == '[') depth++;
                if (depth != 0 || ch == '#' || ch == '^' || ch == ']')
                {
                    sb.Append(ch);
                    if (ch == ']' && depth != 0) depth--;
                    i++;
                    continue;
                }
                bool matched = false;
                foreach (KeyValuePair<string, string> term in terms)
                {
                    if (i + term.Key.Length <= text.Length && string.CompareOrdinal(text, i, term.Key, 0, term.Key.Length) == 0)
                    {
                        sb.Append(term.Value);
                        i += term.Key.Length;
                        matched = true;
                        break;
                    }
                }
                if (!matched) { sb.Append(ch); i++; }
            }
            string result = SpaceNames(sb.ToString());
            s_termCache[text] = result;
            return result;
        }

#if !ZHPATCH
        // ORICOOP_ZH_SCAN=1: when the save screen wakes, ask every loaded translated text for its Chinese version and
        // log how many were blank and whether any placeholder still comes out.
        [HarmonyPatch(typeof(SaveSlotsUI), "Awake")]
        static class ScanPatch
        {
            static void Postfix()
            {
                if (Environment.GetEnvironmentVariable("ORICOOP_ZH_SCAN") != "1") return;
                try
                {
                    int providers = 0, items = 0, blank = 0, placeholders = 0;
                    foreach (TranslatedMessageProvider p in UnityEngine.Resources.FindObjectsOfTypeAll<TranslatedMessageProvider>())
                    {
                        providers++;
                        foreach (TranslatedMessageProvider.MessageItem it in p.Messages)
                        {
                            items++;
                            if (string.IsNullOrEmpty(it.Chinese))
                            {
                                blank++;
                                if (!string.IsNullOrEmpty(it.English) && Lookup(it.English) == null)
                                    Plugin.Log.LogInfo("ZHSCAN still English: " + it.English.Replace("\n", "\\n"));
                            }
                            MessageDescriptor d = it.GetDescriptor(Language.Chinese);
                            if (d.Message != null && d.Message.StartsWith("_Empty_")) placeholders++;
                        }
                    }
                    if (Environment.GetEnvironmentVariable("ORICOOP_ZH_SHOTS") == "1" && !s_shotsStarted)
                    {
                        s_shotsStarted = true;
                        Plugin.Instance.StartCoroutine(Shots());
                    }
                    Plugin.Log.LogInfo("ZHSCAN providers " + providers + ", items " + items + ", blank Chinese " + blank + ", placeholders left " + placeholders + ", fallbacks " + Used);
                }
                catch (Exception e) { Plugin.Log.LogWarning("ZHSCAN failed: " + e); }
            }
        }

        // ORICOOP_ZH_SHOTS=1 (with ORICOOP_ZH_SCAN=1): once in gameplay, show some of the formerly blank texts
        // through the game's own hint and ability boxes and take a screenshot of each (next to ORICOOP_TEST_OUT).
        static bool s_shotsStarted;
        static System.Collections.IEnumerator Shots()
        {
            float t0 = UnityEngine.Time.realtimeSinceStartup;
            while (Game.Characters.Sein == null || !Game.Characters.Sein.gameObject.activeInHierarchy)
            {
                if (UnityEngine.Time.realtimeSinceStartup - t0 > 120f) yield break;
                yield return null;
            }
            yield return new UnityEngine.WaitForSeconds(8f);
            string outFile = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT");
            string dir = string.IsNullOrEmpty(outFile) ? "." : System.IO.Path.GetDirectoryName(outFile);
            string[][] samples =
            {
                new[] { "hint", "Press [Dash] to use the #Dash Ability#!" },
                new[] { "hint", "Use #Bash# [Bash] on #Light Bursts# [LightSpheres] to reach greater heights!" },
                new[] { "hint", "No other #Spirit Wells# have been activated yet!" },
                new[] { "ability", "You've learned #Dash#!\\nTo use it, press [Dash] in order to #dash# into the direction you're facing!" },
                new[] { "hint", "Ori, look! Even though our light had grown weak in this haze, it #lifted the darkness# that held claim of this place." },
            };
            for (int i = 0; i < samples.Length; i++)
            {
                MessageBox box = null;
                try
                {
                    TranslatedMessageProvider p = UnityEngine.ScriptableObject.CreateInstance<TranslatedMessageProvider>();
                    p.Messages.Add(new TranslatedMessageProvider.MessageItem { English = samples[i][1], Chinese = "" });
                    box = samples[i][0] == "ability" ? Game.UI.MessageController.ShowAbilityMessage(p, null) : Game.UI.Hints.Show(p, HintLayer.Gameplay, 6f);
                }
                catch (Exception e) { Plugin.Log.LogWarning("ZHSHOT " + i + ": " + e.Message); }
                yield return new UnityEngine.WaitForSeconds(1.5f);
                string path = System.IO.Path.Combine(dir, "zh_" + i + ".png");
                UnityEngine.Application.CaptureScreenshot(path);
                Plugin.Log.LogInfo("ZHSHOT " + path + " (" + (box != null ? "shown" : "not shown") + ")");
                yield return new UnityEngine.WaitForSeconds(1.5f);
                try { if (box != null && box.Visibility != null) box.Visibility.HideMessageScreenImmediately(); } catch (Exception) { }
                yield return new UnityEngine.WaitForSeconds(0.5f);
            }
            Plugin.Log.LogInfo("ZHSHOT done");
        }

#endif

        [HarmonyPatch(typeof(TranslatedMessageProvider.MessageItem), "GetDescriptor")]
        static class GetDescriptorPatch
        {
            // Harmony also runs this after our fallback Prefix skips the original method.
            static void Postfix(Language language, ref MessageDescriptor __result)
            {
                if (language == Language.Chinese) __result.Message = ApplyTerms(__result.Message);
            }

            static bool Prefix(TranslatedMessageProvider.MessageItem __instance, Language language, ref MessageDescriptor __result)
            {
                try
                {
                    if (language != Language.Chinese || !string.IsNullOrEmpty(__instance.Chinese)) return true;
                    string en = __instance.English;
                    if (string.IsNullOrEmpty(en)) return true;
                    string text = Lookup(en) ?? en;
                    Used++;
                    if (s_logged.Count < 50 && s_logged.Add(en)) Plugin.Log.LogInfo("zh fallback: blank Chinese entry for '" + en + "' -> '" + text + "'");
                    __result = new MessageDescriptor(text, __instance.Emotion, __instance.Sound);
                    return false;
                }
                catch (Exception) { return true; }
            }
        }
    }
}
