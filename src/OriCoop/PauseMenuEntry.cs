using System;
using CatlikeCoding.TextBox;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    internal static class PauseMenuEntry
    {
        const string ItemName = "OriCoop_PauseSettings";
        static PauseScreen s_held;
        static MenuScreenManager s_menu;
        static CleverMenuItem s_selection;
        static bool s_active, s_locked, s_menuSuspended, s_lockClosing;
        static float s_unlockAt;
        static CleverMenuItem s_labelSource, s_labelEntry;
        static string s_englishCase = "";
        static bool s_languageHooked;

        // Match the cloned native item's English casing, as the title menu does.
        static string EnglishCaseOf(CleverMenuItem item)
        {
            foreach (MessageBox box in item.GetComponentsInChildren<MessageBox>(true))
            {
                string text = "";
                try { text = box.MessageProvider != null ? box.MessageProvider.ToString() : ""; } catch (Exception) { }
                if (text.Length > 2 && text == text.ToUpperInvariant() && text != text.ToLowerInvariant()) return "upper";
                if (text.Length > 0) break;
            }
            return "";
        }

        static string EntryText()
        {
            string text = L10n.T("pause.multiplayer_settings");
            return !L10n.Zh && s_englishCase == "upper" ? text.ToUpperInvariant() : text;
        }

        static void RelabelEntry()
        {
            if (s_labelSource == null || s_labelEntry == null) return;
            if (!L10n.Zh) s_englishCase = EnglishCaseOf(s_labelSource);
            foreach (MessageBox box in s_labelEntry.GetComponentsInChildren<MessageBox>(true)) L10n.Set(box, EntryText);
        }

        // Awake runs while the master UI is being constructed, even before the title.
        // Install only when pause is actually shown; OnShow covers Show and ShowImmediate.
        [HarmonyPatch(typeof(PauseScreen), "OnShow")]
        static class ShowPatch
        {
            static void Prefix(PauseScreen __instance) { Install(__instance); }
        }

        static void Install(PauseScreen screen)
        {
            if (!s_probe && !NetPlay.LocalInGameplay()) return;
            GameObject clone = null;
            try
            {
                CleverMenuItemSelectionManager nav = screen.NavigationManager;
                if (nav == null || nav.MenuItems == null || nav.MenuItems.Count == 0) return;
                foreach (CleverMenuItem item in nav.MenuItems) if (item != null && item.name == ItemName) return;
                CleverMenuItem source = nav.MenuItems[0];
                if (source == null) return;
                int index = Math.Min(2, nav.MenuItems.Count);
                CleverMenuItem before = nav.MenuItems[index - 1];
                CleverMenuItem after = index < nav.MenuItems.Count ? nav.MenuItems[index] : null;
                clone = (GameObject)UnityEngine.Object.Instantiate(source.gameObject);
                clone.name = ItemName;
                clone.transform.parent = source.transform.parent;
                clone.transform.localPosition = source.transform.localPosition;
                clone.transform.localRotation = source.transform.localRotation;
                clone.transform.localScale = source.transform.localScale;
                CleverMenuItem entry = clone.GetComponent<CleverMenuItem>();
                // Keep native highlight/unhighlight animations and sounds, never the copied resume action.
                PlaySoundAction pressSound = clone.GetComponentInChildren<PlaySoundAction>(true);
                entry.Pressed = null;
                entry.Activated = null;
                entry.Visible = null;
                AccessTools.Field(typeof(CleverMenuItem), "PressedCallback").SetValue(entry, (Action)delegate { });
                s_labelSource = source; s_labelEntry = entry;
                s_englishCase = "";
                if (!s_languageHooked) { L10n.Changed += RelabelEntry; s_languageHooked = true; }
                RelabelEntry();
                entry.PressedCallback += delegate
                {
                    if (pressSound != null) pressSound.Perform(null);
                    CoopMenu.Open();
                };
                clone.SetActive(true);
                nav.MenuItems.Insert(index, entry);
                if (nav.Index >= index) nav.Index++;
                CleverMenuItemLayout layout = null;
                foreach (CleverMenuItemLayout candidate in screen.GetComponentsInChildren<CleverMenuItemLayout>(true))
                    if (candidate.MenuItems != null && candidate.MenuItems.Contains(before)) { layout = candidate; break; }
                if (layout != null)
                {
                    layout.MenuItems.Insert(layout.MenuItems.IndexOf(before) + 1, entry);
                    layout.Sort();
                }
                else
                {
                    Vector3 step = after != null ? after.transform.position - before.transform.position :
                        index > 1 ? before.transform.position - nav.MenuItems[index - 2].transform.position : Vector3.down * source.Space;
                    entry.transform.position = before.transform.position + step;
                    for (int i = index + 1; i < nav.MenuItems.Count; i++) nav.MenuItems[i].transform.position += step;
                }
                if (nav.ItemDirection == CleverMenuItemSelectionManager.Direction.NavigationCage)
                {
                    nav.Navigation.RemoveAll(delegate(CleverMenuItemSelectionManager.NavigationData edge)
                    { return (edge.From == before && edge.To == after) || (edge.From == after && edge.To == before); });
                    nav.Navigation.Add(new CleverMenuItemSelectionManager.NavigationData { From = before, To = entry });
                    nav.Navigation.Add(new CleverMenuItemSelectionManager.NavigationData { From = entry, To = before });
                    if (after != null)
                    {
                        nav.Navigation.Add(new CleverMenuItemSelectionManager.NavigationData { From = entry, To = after });
                        nav.Navigation.Add(new CleverMenuItemSelectionManager.NavigationData { From = after, To = entry });
                    }
                }
                Plugin.Log.LogInfo("pause menu: multiplayer settings entry installed");
            }
            catch (Exception e)
            {
                if (clone != null)
                {
                    CleverMenuItem entry = clone.GetComponent<CleverMenuItem>();
                    if (screen.NavigationManager != null)
                    {
                        screen.NavigationManager.MenuItems.Remove(entry);
                        screen.NavigationManager.Navigation.RemoveAll(delegate(CleverMenuItemSelectionManager.NavigationData edge) { return edge.From == entry || edge.To == entry; });
                    }
                    foreach (CleverMenuItemLayout layout in screen.GetComponentsInChildren<CleverMenuItemLayout>(true)) layout.MenuItems.Remove(entry);
                    UnityEngine.Object.Destroy(clone);
                }
                Plugin.Log.LogWarning("pause menu: entry unavailable; use F8: " + e.Message);
            }
        }

        // Leave UI.Menu logically open: its existing suspension/session accounting is untouched.
        internal static bool Suspend()
        {
            if (!s_probe && !NetPlay.LocalInGameplay()) return false;
            // A quick reopen can happen during the return-input debounce.
            if (s_menu != null) Release();
            PauseScreen screen = PauseScreen.Instance;
            MenuScreenManager menu = UI.Menu;
            if (screen == null || menu == null || !menu.MainMenuVisible || menu.CurrentScreen != MenuScreenManager.Screens.Pause) return false;
            CleverMenuItemSelectionManager nav = screen.NavigationManager;
            s_held = screen; s_menu = menu; s_selection = nav.CurrentMenuItem;
            s_active = nav.IsActive; s_locked = nav.IsLocked;
            s_menuSuspended = menu.IsSuspended; s_lockClosing = menu.LockClosingMenu;
            s_unlockAt = 0f;
            nav.IsActive = false; nav.IsLocked = true;
            nav.SetVisibleImmediate(false);
            menu.IsSuspended = true; menu.LockClosingMenu = true;
            return true;
        }

        internal static void Return()
        {
            if (s_held == null) { Release(); return; }
            if (s_menu != null && s_menu == UI.Menu && s_held == PauseScreen.Instance &&
                s_menu.MainMenuVisible && s_menu.CurrentScreen == MenuScreenManager.Screens.Pause)
            {
                CleverMenuItemSelectionManager nav = s_held.NavigationManager;
                nav.SetVisibleImmediate(true);
                if (s_selection != null) nav.SetCurrentMenuItem(s_selection);
                s_unlockAt = Time.realtimeSinceStartup + 0.3f;
            }
            else Release();
        }

        internal static void Tick()
        {
            if (s_menu == null) return;
            if (s_held == null || s_held != PauseScreen.Instance || s_menu != UI.Menu) { Release(); return; }
            if (s_unlockAt <= 0f || Time.realtimeSinceStartup < s_unlockAt) return;
            // Do not let the B / Esc / Start that closed settings close the pause menu too.
            if (Core.Input.Cancel.Pressed || Core.Input.Start.Pressed || Core.Input.ActionButtonA.Pressed || Input.GetKey(KeyCode.Escape)) return;
            Release();
        }

        static void Release()
        {
            if (s_held != null)
            {
                s_held.NavigationManager.IsActive = s_active;
                s_held.NavigationManager.IsLocked = s_locked;
            }
            if (s_menu != null) { s_menu.IsSuspended = s_menuSuspended; s_menu.LockClosingMenu = s_lockClosing; }
            s_held = null; s_menu = null; s_selection = null; s_unlockAt = 0f;
        }

        // Real native pause UI, including on the title probe when UI.Menu has been constructed.
        static bool s_probe, s_probeWasVisible;
        static MenuScreenManager.Screens s_probeScreen;
        internal static string[] L10nTestPages() { return new string[] { "pause.entry" }; }
        internal static void L10nTestShow(string page, bool on)
        {
            if (page != "pause.entry") return;
            if (!on)
            {
                if (!s_probe) return;
                s_probe = false;
                if (CoopMenu.IsOpen) CoopMenu.Close();
                Release();
                if (UI.Menu != null)
                {
                    if (s_probeWasVisible) UI.Menu.ChangeScreen(s_probeScreen);
                    else UI.Menu.HideMenuScreen(true);
                }
                return;
            }
            if (s_probe || UI.Menu == null || PauseScreen.Instance == null) return;
            s_probeWasVisible = UI.Menu.MainMenuVisible; s_probeScreen = UI.Menu.CurrentScreen;
            s_probe = true;
            UI.Menu.ShowMenuScreen();
        }

        static MessageBox ProbeBox()
        {
            if (!s_probe || PauseScreen.Instance == null || !PauseScreen.Instance.NavigationManager.IsVisible) return null;
            foreach (CleverMenuItem item in PauseScreen.Instance.NavigationManager.MenuItems)
                if (item != null && item.name == ItemName && item.gameObject.activeInHierarchy) return item.GetComponentInChildren<MessageBox>();
            return null;
        }

        // The font probe normally counts only NativeGui labels. Account for the actual game-owned box,
        // not a duplicate overlay label, without changing NativeGui or L10nProbe.
        [HarmonyPatch(typeof(NativeGui), "LateUpdate")]
        static class ProbeFramePatch
        {
            static bool Prepare() { return L10nProbe.On; }
            static void Postfix()
            {
                if (ProbeBox() == null) return;
                NativeGui.PagesLastFrame.Add("pause.entry");
                NativeGui.LabelsLastFrame++;
            }
        }

        [HarmonyPatch(typeof(NativeGui), "PageUsesGameFont")]
        static class ProbeFontPatch
        {
            static bool Prepare() { return L10nProbe.On; }
            static bool Prefix(string page, ref bool __result)
            {
                if (page != "pause.entry") return true;
                MessageBox box = ProbeBox();
                TextStyleCollection actual = box != null && box.TextBox != null ? box.TextBox.styleCollection : null;
                TextStyleCollection current = box != null && box.LanguageStyles != null ? box.LanguageStyles.Current : null;
                __result = actual != null && current != null && actual.styles != null && current.styles != null &&
                    actual.styles.Length > 0 && current.styles.Length > 0 && actual.styles[0].font != null && actual.styles[0].font == current.styles[0].font;
                return false;
            }
        }
    }
}
