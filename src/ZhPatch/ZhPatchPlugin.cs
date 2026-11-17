using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using CatlikeCoding.TextBox;
using HarmonyLib;
using UnityEngine;

// The three shared localisation files need only these two services, not the co-op plugin.
namespace OriCoop
{
    internal static class Plugin
    {
        internal static BaseUnityPlugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;
    }
}

namespace OriZhPatch
{
    [BepInPlugin("oricoop.zhpatch", "Ori DE 简体中文补丁 / Simplified Chinese patch", "1.0.0")]
    [BepInIncompatibility("local.oricoop")]
    public sealed class ZhPatchPlugin : BaseUnityPlugin
    {
        void Awake()
        {
            // BepInEx normally rejects this plugin (and logs the incompatibility) before Awake.
            if (Chainloader.PluginInfos.ContainsKey("local.oricoop"))
            {
                Logger.LogWarning("Chinese patch skipped: OriCoop is installed and already includes this patch.");
                return;
            }
            OriCoop.Plugin.Instance = this;
            OriCoop.Plugin.Log = Logger;
            // This assembly contains only the three shared localisation files and this entry/probe.
            new Harmony("oricoop.zhpatch").PatchAll(typeof(ZhPatchPlugin).Assembly);
            GameObject runner = new GameObject("OriZhPatchRunner");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.hideFlags = HideFlags.HideAndDontSave;
            runner.AddComponent<GlyphRunner>();
            if (Environment.GetEnvironmentVariable("ORICOOP_ZHPATCH_PROBE") == "1")
                runner.AddComponent<ZhPatchProbe>();
            // Bind now: the game later destroys BepInEx's manager, but the config entries must survive it.
            Logger.LogInfo("Ori DE Simplified Chinese patch 1.0.0 loaded (no co-op features); unlocked=" + OriCoop.LanguageUnlock.Unlocked);
        }

        public sealed class GlyphRunner : MonoBehaviour
        {
            void Update() { OriCoop.FontGlyphs.RetargetLateMaterials(); }
        }
    }

    // Opt-in only. Does not select a save slot or enter gameplay.
    public sealed class ZhPatchProbe : MonoBehaviour
    {
        readonly List<string> lines = new List<string>();
        int passed, total;
        string output;

        void Check(bool ok, string message)
        {
            total++;
            if (ok) passed++;
            string line = (ok ? "PASS " : "FAIL ") + message.Replace("\r", "\\r").Replace("\n", "\\n");
            lines.Add(line);
            OriCoop.Plugin.Log.LogInfo(line);
        }

        void Start() { StartCoroutine(RunSafely()); }

        IEnumerator RunSafely()
        {
            output = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT");
            IEnumerator test = Run();
            while (true)
            {
                bool next = false;
                try { next = test.MoveNext(); }
                catch (Exception e) { Check(false, "probe exception: " + e); }
                if (!next) break;
                yield return test.Current;
            }
            string result = "RESULT " + (passed == total && total > 0 ? "PASS " : "FAIL ") + passed + "/" + total;
            lines.Add(result);
            OriCoop.Plugin.Log.LogInfo(result);
            try
            {
                if (string.IsNullOrEmpty(output)) throw new IOException("ORICOOP_TEST_OUT is not set");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                File.WriteAllLines(output, lines.ToArray());
            }
            catch (Exception e) { OriCoop.Plugin.Log.LogError("zhpatch probe result: " + e); }
            Application.Quit();
        }

        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        IEnumerator Run()
        {
            float start = Time.realtimeSinceStartup;
            while (!TitleReady() && Time.realtimeSinceStartup - start < 90f) yield return null;
            Check(TitleReady(), "title/save screen is ready");
            if (!TitleReady()) yield break;
            // Never use ORICOOP_FORCE_ZH: verify the actual game's setter/getter pair.
            GameSettings.Instance.Language = Language.Chinese;
            yield return StartCoroutine(Wait(2f));
            if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart)
                TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
            yield return StartCoroutine(Wait(3f));

            bool coop = Chainloader.PluginInfos.ContainsKey("local.oricoop");
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType("OriCoop.NetPlay", false) != null) coop = true;
            Check(!coop, "OriCoop GUID and OriCoop.NetPlay are absent");
            Check(GameSettings.Instance.Language == Language.Chinese, "game language reads Chinese after its own setter");
            Check(OriCoop.FontGlyphs.Status.StartsWith("replaced ", StringComparison.Ordinal) && OriCoop.FontGlyphs.Added > 0,
                "embedded glyph atlas installed: " + OriCoop.FontGlyphs.Status + "; Added=" + OriCoop.FontGlyphs.Added);

            var samples = new HashSet<string>();
            foreach (MessageBox box in Resources.FindObjectsOfTypeAll<MessageBox>())
            {
                if (box == null || !box.gameObject.activeInHierarchy || box.LanguageStyles == null || box.LanguageStyles.Chinese == null) continue;
                TranslatedMessageProvider provider = box.MessageProvider as TranslatedMessageProvider;
                if (provider == null || provider.Messages == null) continue;
                // Only inspect the descriptor this live box is displaying, not other pages in its provider.
                string text = provider.Messages[box.MessageIndex].GetDescriptor(Language.Chinese).Message;
                if (!HasHan(text) || samples.Contains(text)) continue;
                samples.Add(text);
                CheckMenuGlyphs(box, text);
                if (samples.Count >= 3) break;
            }
            Check(samples.Count >= 3, "at least three distinct Chinese strings from the game's live title/save message providers (" + samples.Count + ")");

            // The game's hint source text, passed through exactly the blank-entry fallback/terms hook used in-game.
            var hint = new TranslatedMessageProvider.MessageItem
            {
                English = "I am #Sein#, the light and eyes of the #Spirit Tree#.", Chinese = ""
            };
            int used = OriCoop.ZhFallback.Used;
            string shown = hint.GetDescriptor(Language.Chinese).Message;
            Check(OriCoop.ZhFallback.Used > used && shown == "我叫 #Sein#，是#精灵之树#的光芒与眼睛。",
                "native hint fallback preserves English character name: " + shown);
            string dir = string.IsNullOrEmpty(output) ? Application.dataPath : Path.GetDirectoryName(Path.GetFullPath(output));
            string shot = Path.Combine(dir, "zhpatch_title_zh.png");
            if (File.Exists(shot)) File.Delete(shot);
            Application.CaptureScreenshot(shot);
            yield return StartCoroutine(Wait(2f));
            Check(File.Exists(shot) && new FileInfo(shot).Length > 0, "screenshot zhpatch_title_zh.png written");
        }

        void CheckMenuGlyphs(MessageBox box, string text)
        {
            // Refresh uses the game's parser: [Stick]/[SaveSlotBackup] become styled icons, not literal text.
            // TextBox then resolves each character's actual font (default Chinese, icon, named style, etc.).
            box.RefreshText();
            TextBox tb = box.TextBox;
            string parsed = box.FormatText ? MessageParserUtility.ProcessString(text) : text;
            int length = Traverse.Create(tb).Field("textLength").GetValue<int>();
            bool covered = tb.styleCollection == box.LanguageStyles.Chinese && length == parsed.Length;
            int expectedHan = 0, checkedHan = 0;
            foreach (char ch in text) if (ch >= '\u3400' && ch <= '\u9fff') expectedHan++;
            string missing = "";
            for (int i = 0; i < Math.Min(length, parsed.Length); i++)
            {
                CharMetaData meta = tb.GetCharacterMetaData(i);
                if (meta.type == CharType.Style || meta.type == CharType.Whitespace) continue;
                char ch = parsed[i];
                if (ch >= '\u3400' && ch <= '\u9fff') checkedHan++;
                // Compare against the pre-render character too: the renderer substitutes a square for missing ink.
                if (meta.id != ch || meta.font == null || !HasGlyph(meta.font, ch) || meta.font[ch].height <= 0f)
                {
                    covered = false;
                    missing += ch + "@" + (meta.font == null ? "null" : meta.font.name) + ";";
                }
            }
            Check(covered && expectedHan > 0 && checkedHan == expectedHan,
                "live menu font covers '" + text + "'; Han=" + checkedHan + "/" + expectedHan + "; missing='" + missing + "'");
        }

        static bool TitleReady()
        {
            return Time.realtimeSinceStartup > 8f && GameSettings.Instance != null &&
                (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart ||
                 TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu ||
                 (SaveSlotsUI.Instance != null && SaveSlotsUI.Instance.IsVisible));
        }

        static bool HasHan(string text)
        {
            if (text == null) return false;
            foreach (char ch in text) if (ch >= '\u3400' && ch <= '\u9fff') return true;
            return false;
        }

        static bool HasGlyph(BitmapFont font, char ch)
        {
            foreach (BitmapFontChar[] chars in new[] { font.asciiChars, font.otherChars })
                if (chars != null) foreach (BitmapFontChar glyph in chars)
                    if (glyph != null && glyph.id == ch) return true;
            return false;
        }
    }
}
