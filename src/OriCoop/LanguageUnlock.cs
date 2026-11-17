using System;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Unlocks the game's own Simplified Chinese.
    //
    // The translation (TranslatedMessageProvider.Chinese, options texts) and the Chinese font ship with this build,
    // but GameSettings.Language's setter turns Chinese into English and the Options > Language list leaves it
    // out. With [Language] UnlockChinese = true (default) we let the setter keep Chinese, add "简体中文" to the
    // language list, and remember the choice: the game resets the language to Steam's client language at every
    // start (Steamworks.Awake), so if the player picked Chinese in the game we keep it.
    // Missing glyphs in our own Chinese strings are handled by FontGlyphs.
    static class LanguageUnlock
    {
        static BepInEx.Configuration.ConfigEntry<bool> s_unlock, s_prefer;
        static bool s_inSteamAwake;

        static void Bind()
        {
            if (s_unlock != null || Plugin.Instance == null) return;
            s_unlock = Plugin.Instance.Config.Bind("Language", "UnlockChinese", true,
                "Allow the game's built-in Simplified Chinese (locked in this build): adds 简体中文 to Options > Language.");
            s_prefer = Plugin.Instance.Config.Bind("Language", "PreferChinese", false,
                "Set automatically when you pick 简体中文 in Options > Language: keeps Chinese even though the game switches to Steam's language at start.");
        }

        public static bool Unlocked { get { Bind(); return s_unlock == null || s_unlock.Value; } }

        [HarmonyPatch(typeof(GameSettings), "set_Language")]
        static class SetterPatch
        {
            static bool Prefix(GameSettings __instance, ref Language value)
            {
                if (!Unlocked) return true;
                // Steam's start-up language reset: keep the player's in-game choice of Chinese.
                if (s_inSteamAwake && s_prefer != null && s_prefer.Value) value = Language.Chinese;
                if (value != Language.Chinese) return true;
                Traverse.Create(__instance).Field("m_language").SetValue(Language.Chinese);
                Game.Events.Scheduler.OnGameLanguageChange.Call();
                return false;
            }
        }

        [HarmonyPatch(typeof(Steamworks), "Awake")]
        static class SteamAwakePatch
        {
            static void Prefix() { s_inSteamAwake = true; }
            static void Postfix() { s_inSteamAwake = false; }
        }

        [HarmonyPatch(typeof(LanguageOptions), "OnEnable")]
        static class OptionsListPatch
        {
            static void Postfix(LanguageOptions __instance)
            {
                if (!Unlocked) return;
                try
                {
                    string label = __instance.Chinese != null ? __instance.Chinese.ToString() : null;
                    if (string.IsNullOrEmpty(label)) label = "简体中文";
                    LanguageOptions list = __instance;
                    list.AddItem(Language.Chinese, label, delegate { list.SetLanguage(Language.Chinese); });
                }
                catch (Exception e) { Plugin.Log.LogWarning("language: could not add Chinese to the list: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(LanguageOptions), "SetLanguage")]
        static class RememberPatch
        {
            static void Prefix(Language language)
            {
                Bind();
                if (s_prefer != null && Unlocked) s_prefer.Value = language == Language.Chinese;
            }
        }

#if !ZHPATCH
        // ORICOOP_LANG_PROBE=1 (tests): switch to Chinese through the game's own setter when a save slot is picked
        // (after the settings file has loaded; runs before NativeModeMenu's prefix so its page is in Chinese).
        static bool s_probed;
        [HarmonyPatch(typeof(SaveSlotsUI), "UsedSaveSlotSelected")]
        [HarmonyPriority(Priority.First)]
        static class ProbePatch
        {
            static void Prefix()
            {
                if (s_probed || GameSettings.Instance == null) return;
                s_probed = true;
                if (Environment.GetEnvironmentVariable("ORICOOP_LANG_PROBE") != "1") return;
                GameSettings.Instance.Language = Language.Chinese;
                Plugin.Log.LogInfo("language probe: set Chinese through the setter -> " + GameSettings.Instance.Language + " (unlocked=" + Unlocked + ")");
            }
        }
#endif
    }
}
