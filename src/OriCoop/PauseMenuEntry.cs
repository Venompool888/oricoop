using System;
using System.Collections.Generic;
using CatlikeCoding.TextBox;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    internal static class PauseMenuEntry
    {
        const string ItemName = "OriCoop_PauseSettings";
        const string RoomItemName = "OriCoop_PauseRoom";   // T-189: 房间管理 (the in-game room page), C-server sessions only
        static PauseScreen s_held;
        static MenuScreenManager s_menu;
        static CleverMenuItem s_selection;
        static bool s_active, s_locked, s_menuSuspended, s_lockClosing;
        static float s_unlockAt;
        static CleverMenuItem s_labelSource;
        // Our items and their texts: relabelled from the cloned native item on a language change.
        static readonly List<KeyValuePair<CleverMenuItem, Func<string>>> s_labels = new List<KeyValuePair<CleverMenuItem, Func<string>>>();
        static string s_englishCase = "";
        static bool s_languageHooked;
        // T-189 test-only mutation switch: 房间管理 for any online session (LAN too); the titlejoin LAN run must catch it.
        static readonly bool s_mutateRoomAnyOnline = NativeGui.MutationOn("ORICOOP_MUTATE_PAUSE_ROOM_ANY");   // test runs incl. titlejoin

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

        static string Cased(string text) { return !L10n.Zh && s_englishCase == "upper" ? text.ToUpperInvariant() : text; }
        static string EntryText() { return Cased(L10n.T("pause.multiplayer_settings")); }
        static string RoomText() { return Cased(L10n.T("pause.room")); }

        static void RelabelEntry()
        {
            if (s_labelSource == null) return;
            if (!L10n.Zh) s_englishCase = EnglishCaseOf(s_labelSource);
            s_labels.RemoveAll(delegate(KeyValuePair<CleverMenuItem, Func<string>> l) { return l.Key == null; });
            foreach (KeyValuePair<CleverMenuItem, Func<string>> l in s_labels)
                foreach (MessageBox box in l.Key.GetComponentsInChildren<MessageBox>(true)) L10n.Set(box, l.Value);
        }

        // Awake runs while the master UI is being constructed, even before the title.
        // Install only when pause is actually shown; OnShow covers Show and ShowImmediate.
        [HarmonyPatch(typeof(PauseScreen), "OnShow")]
        static class ShowPatch
        {
            static void Prefix(PauseScreen __instance) { Install(__instance); }
        }

        static CleverMenuItem Find(CleverMenuItemSelectionManager nav, string name)
        {
            foreach (CleverMenuItem item in nav.MenuItems) if (item != null && item.name == name) return item;
            return null;
        }

        // T-189: the pause menu shows 房间管理 now (Ui/InGameRoomRules via CsRoomPage.InGameAvailable).
        internal static bool RoomEntryWanted { get { return CsRoomPage.InGameAvailable || (s_mutateRoomAnyOnline && NetPlay.Enabled && NetPlay.LocalInGameplay()); } }

        static void Install(PauseScreen screen)
        {
            CleverMenuItemSelectionManager nav = screen.NavigationManager;
            if (nav == null || nav.MenuItems == null || nav.MenuItems.Count == 0) return;
            // T-189: a 房间管理 left from an earlier C-server game goes as soon as the pause menu opens without one, also
            // outside gameplay (the prologue, the title probe), before anything returns early.
            CleverMenuItem stale = Find(nav, RoomItemName);
            if (stale != null && (!RoomEntryWanted || s_probe))
            {
                RemoveItem(screen, stale);
                Plugin.Log.LogInfo("pause menu: room page entry removed (no C-server game)");
            }
            if (!s_probe && !NetPlay.LocalInGameplay()) return;
            if (Find(nav, ItemName) == null &&
                AddItem(screen, ItemName, Math.Min(2, nav.MenuItems.Count), EntryText, CoopMenu.Open) != null)
                Plugin.Log.LogInfo("pause menu: multiplayer settings entry installed");
            // T-189: 房间管理 between 难度 and 退出 (right above the last native item, the game's quit), only while a C-server
            // session plays; taken out again when the pause menu opens without one (LAN, single player, the session ended).
            bool want = RoomEntryWanted && !s_probe;
            CleverMenuItem room = Find(nav, RoomItemName);
            if (want && room == null)
            {
                int quit = QuitIndex(nav);
                if (quit > 0 && AddItem(screen, RoomItemName, quit, RoomText, CsRoomPage.OpenInGame) != null)
                    Plugin.Log.LogInfo("pause menu: room page entry installed");
            }
        }

        // The game's quit item: the last native item of the pause menu.
        static int QuitIndex(CleverMenuItemSelectionManager nav)
        {
            for (int i = nav.MenuItems.Count - 1; i >= 0; i--)
            {
                CleverMenuItem item = nav.MenuItems[i];
                if (item != null && !item.name.StartsWith("OriCoop_", StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        // A copy of the first native item (font, highlight animation, select sound, navigation) at the given index, with our
        // text and action instead of the copied one. null when it could not be added (nothing changed then).
        static CleverMenuItem AddItem(PauseScreen screen, string name, int index, Func<string> text, Action pressed)
        {
            GameObject clone = null;
            try
            {
                CleverMenuItemSelectionManager nav = screen.NavigationManager;
                CleverMenuItem source = nav.MenuItems[0];
                if (source == null || index < 1 || index > nav.MenuItems.Count) return null;
                CleverMenuItem before = nav.MenuItems[index - 1];
                CleverMenuItem after = index < nav.MenuItems.Count ? nav.MenuItems[index] : null;
                clone = (GameObject)UnityEngine.Object.Instantiate(source.gameObject);
                clone.name = name;
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
                s_labelSource = source;
                s_englishCase = "";
                if (!s_languageHooked) { L10n.Changed += RelabelEntry; s_languageHooked = true; }
                s_labels.Add(new KeyValuePair<CleverMenuItem, Func<string>>(entry, text));
                RelabelEntry();
                entry.PressedCallback += delegate
                {
                    if (pressSound != null) pressSound.Perform(null);
                    pressed();
                };
                clone.SetActive(true);
                nav.MenuItems.Insert(index, entry);
                if (nav.Index >= index) nav.Index++;
                CleverMenuItemLayout layout = LayoutOf(screen, before);
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
                return entry;
            }
            catch (Exception e)
            {
                if (clone != null) RemoveItem(screen, clone.GetComponent<CleverMenuItem>());
                Plugin.Log.LogWarning("pause menu: entry " + name + " unavailable; use F8: " + e.Message);
                return null;
            }
        }

        static CleverMenuItemLayout LayoutOf(PauseScreen screen, CleverMenuItem item)
        {
            foreach (CleverMenuItemLayout candidate in screen.GetComponentsInChildren<CleverMenuItemLayout>(true))
                if (candidate.MenuItems != null && candidate.MenuItems.Contains(item)) return candidate;
            return null;
        }

        // Undo AddItem: out of the navigation (its neighbours linked again), out of the layout (or the items below moved back
        // up), destroyed. The selection moves to the item above when it was on this one.
        static void RemoveItem(PauseScreen screen, CleverMenuItem entry)
        {
            if (entry == null) return;
            try
            {
                CleverMenuItemSelectionManager nav = screen.NavigationManager;
                int i = nav != null ? nav.MenuItems.IndexOf(entry) : -1;
                if (i >= 0)
                {
                    CleverMenuItem before = i > 0 ? nav.MenuItems[i - 1] : null;
                    CleverMenuItem after = i + 1 < nav.MenuItems.Count ? nav.MenuItems[i + 1] : null;
                    bool wasCurrent = nav.CurrentMenuItem == entry;
                    CleverMenuItemLayout layout = LayoutOf(screen, entry);
                    if (layout == null && before != null)
                    {
                        Vector3 step = entry.transform.position - before.transform.position;
                        for (int k = i + 1; k < nav.MenuItems.Count; k++) nav.MenuItems[k].transform.position -= step;
                    }
                    nav.MenuItems.RemoveAt(i);
                    if (nav.Index > i || (nav.Index == i && nav.Index >= nav.MenuItems.Count)) nav.Index = Math.Max(0, nav.Index - 1);
                    nav.Navigation.RemoveAll(delegate(CleverMenuItemSelectionManager.NavigationData edge) { return edge.From == entry || edge.To == entry; });
                    if (nav.ItemDirection == CleverMenuItemSelectionManager.Direction.NavigationCage && before != null && after != null)
                    {
                        nav.Navigation.Add(new CleverMenuItemSelectionManager.NavigationData { From = before, To = after });
                        nav.Navigation.Add(new CleverMenuItemSelectionManager.NavigationData { From = after, To = before });
                    }
                    if (layout != null) { layout.MenuItems.Remove(entry); layout.Sort(); }
                    if (wasCurrent && before != null) nav.SetCurrentMenuItem(before);
                }
                else foreach (CleverMenuItemLayout l in screen.GetComponentsInChildren<CleverMenuItemLayout>(true)) l.MenuItems.Remove(entry);
            }
            catch (Exception e) { Plugin.Log.LogWarning("pause menu: removing " + entry.name + ": " + e.Message); }
            s_labels.RemoveAll(delegate(KeyValuePair<CleverMenuItem, Func<string>> l) { return l.Key == entry || l.Key == null; });
            UnityEngine.Object.Destroy(entry.gameObject);
        }

        // ---- T-189 harness hooks -------------------------------------------------------------------------

        // Where 房间管理 sits in the open pause menu: its index and the game's quit item's index (-1 = not there).
        internal static bool RoomEntryShown(out int index, out int quitIndex, out string selected)
        {
            index = -1; quitIndex = -1; selected = "";
            PauseScreen screen = PauseScreen.Instance;
            if (screen == null || screen.NavigationManager == null) return false;
            CleverMenuItemSelectionManager nav = screen.NavigationManager;
            CleverMenuItem room = Find(nav, RoomItemName);
            index = room != null ? nav.MenuItems.IndexOf(room) : -1;
            quitIndex = QuitIndex(nav);
            selected = nav.CurrentMenuItem != null ? nav.CurrentMenuItem.name : "";
            return room != null && room.gameObject.activeInHierarchy;
        }

        internal static bool RoomEntrySelected
        {
            get
            {
                PauseScreen screen = PauseScreen.Instance;
                return screen != null && screen.NavigationManager != null && screen.NavigationManager.CurrentMenuItem != null
                    && screen.NavigationManager.CurrentMenuItem.name == RoomItemName && screen.NavigationManager.IsVisible;
            }
        }

        // Select 房间管理 and press it as a player does (the native item's press: sound, then our action).
        internal static bool PressRoomEntry()
        {
            PauseScreen screen = PauseScreen.Instance;
            CleverMenuItem room = screen != null && screen.NavigationManager != null ? Find(screen.NavigationManager, RoomItemName) : null;
            if (room == null) return false;
            screen.NavigationManager.SetCurrentMenuItem(room);
            room.OnPressed();
            return true;
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
