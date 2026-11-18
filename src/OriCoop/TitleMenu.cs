using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CatlikeCoding.TextBox;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Title menu: the game's "Start Game" item becomes two items in the game's own style (design 2026-11-15):
    //   单人游玩 / Single Player -> save slots -> single player (no "how do you want to play?" page)
    //   多人游玩 / Co-op         -> the native orb (NativeModeMenu, opened on the title instead of a save slot):
    //     本地同屏                -> save slots -> local co-op loads at once (controller setup next)
    //     局域网直连 / Steam / 中继 -> 开房 / 加入 (same orb)
    //        开房 -> save slots (existing save, or empty -> difficulty -> new game) -> the slot orb opens on the
    //               host page of that transport (room code / address / Steam invite) -> 开始游戏
    //        加入 -> NO save slot: join page on the title orb (IP + port / room code / Steam invite) -> connect ->
    //               the host's checkpoint loads straight from the title (GuestJoin.BeginNoSlot); CoopSaves keeps
    //               the guest's copy (linked slot, else an empty one; the player picks a slot only if all are full).
    // "Single Player" is the original item (its own Pressed action, so the game's own transition to the slots);
    // "Co-op" is a copy of it whose press opens the orb, and after a slot-bound choice runs the original's action.
    // The choice lives in TitleChoice until the title main menu shows again (back from the slots, return to title).
    // No title choice (the test harness forcing the slot screen, config launches, [Start] TitleModeItems = false):
    // the old "how do you want to play?" page after the slot pick (NativeModeMenu.Enabled) is the fallback.
    public static partial class NativeModeMenu
    {
        public const int TitleNone = 0, TitleSingle = 1, TitleLocal = 2, TitleOnline = 3;
        public static int TitleChoice;                   // TitleOnline = host (a guest never reaches the slots)
        public static string TitleTransport = "";
        public static string LastSlotPick = "";          // what the last slot pick did (harness checks)
        public static readonly List<string> TitleLabels = new List<string>();
        public static bool TitleItemsReady { get { return s_tCoop != null && s_tSingle != null && s_tMain != null; } }
        public static bool TitleOrbOpen { get { return IsOpen && s_onTitle; } }
        public static bool OnTitleOrb { get { return s_onTitle; } }
        public static bool GuestFromTitle { get { return s_guestStarted; } }   // a no-slot guest join is running
        public static bool GuestPickingSlot { get { return s_guestPickSlot; } }
        public static string GuestStatus = "";

        static bool s_fromTitle;          // the slot orb was opened on the host page by a title choice
        static bool s_guestStarted;       // GuestJoin.BeginNoSlot ran for this join
        static bool s_guestPickSlot;      // all slots full: the save slots are up to pick one for the co-op copy
        static int s_guestAborts;
        static float s_pickToastAt;
        static BepInEx.Configuration.ConfigEntry<bool> s_titleItems;
        static CleverMenuItemSelectionManager s_tMain;
        static CleverMenuItem s_tSingle, s_tCoop;
        static ActionMethod s_tStartAction;
        static PlaySoundAction s_tCoopSound;
        static string s_tEnglishCase = "";             // "upper" when the game's English item text is in capitals
        static bool s_tLastZh;
        static TitleDriver s_tDriver;
        static bool s_tWarned;

        static bool TitleItemsOn
        {
            get
            {
                if (s_titleItems == null && Plugin.Instance != null)
                    s_titleItems = Plugin.Instance.Config.Bind("Start", "TitleModeItems", true,
                        "Title menu: Single Player / Co-op instead of Start Game (Co-op asks local / LAN / Steam / relay and host / join before the save slots). false = the game's Start Game, and the question comes after the save slot.");
                return s_titleItems == null || s_titleItems.Value;
            }
        }

        static readonly string[] TitleItemRows = { "title.single", "title.coop" };
        static readonly string[][] TitlePage = {
            new[] { "title.local", "title.local.desc" },
            new[] { "mode.direct", "title.direct.desc" },
            new[] { "mode.steam", "title.steam.desc" },
            new[] { "server.title", "server.title.desc" },
        };
        static readonly string[] TitleTransports = { "", "direct", "steam", "relay" };
        // T-045 (owner's choice B): the 自建服务器 page lists the server mode and 「我的房间」; the host-mode row is gone from the
        // menu (the relay path stays in code behind My rooms and for the tests).
        static readonly string[][] ServerModePage = {
            new[] { "server.cserver", "server.cserver.desc" },
            new[] { "lobby.rooms", "lobby.rooms.desc" },
        };

        // ---- hooks ------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(TitleScreenManager), "SetScreen")]
        static class TitleSetScreenPatch { static void Postfix(TitleScreenManager.Screen screen) { OnTitleScreen(screen); } }

        [HarmonyPatch(typeof(TitleScreenManager), "SetScreenImmediate")]
        static class TitleSetScreenImmediatePatch { static void Postfix(TitleScreenManager.Screen screen) { OnTitleScreen(screen); } }

        static void OnTitleScreen(TitleScreenManager.Screen screen)
        {
            try
            {
                MaybeStartTitleJoinTest();
                if (screen == TitleScreenManager.Screen.MainMenu || screen == TitleScreenManager.Screen.PressStart)
                {
                    if (TitleChoice != TitleNone) Plugin.Log.LogInfo("title menu: choice cleared (back on the " + screen + " screen)");
                    TitleChoice = TitleNone; TitleTransport = "";
                    if (s_guestPickSlot) { s_guestPickSlot = false; s_guestStarted = false; }
                }
                if (screen != TitleScreenManager.Screen.MainMenu && TitleOrbOpen) Destroy();
                if (screen == TitleScreenManager.Screen.MainMenu || screen == TitleScreenManager.Screen.PressStart) InstallTitleItems();
            }
            catch (Exception e) { Plugin.Log.LogError("title menu: " + e); }
        }

        // Slot picked (UsedSlotPatch / NewGamePatch). 0 = no title choice (old flow), 1 = load as is, 2 = orb opened.
        static int TitleSlotPick(SaveSlotsUI ui)
        {
            if (s_guestPickSlot)
            {
                // All slots were full: this slot keeps the co-op copy (CoopSaves backs it up first).
                // The UI's index: this prefix runs before the game copies it into SaveSlotsManager.
                s_guestPickSlot = false;
                int picked = ui.CurrentSlotIndex;
                LastSlotPick = "guest-slot-" + picked;
                Plugin.Log.LogInfo("title menu: guest keeps the co-op copy in slot " + picked);
                ui.Active = false;
                GuestJoin.UseSlot(picked);
                return 3;
            }
            if (TitleChoice == TitleNone || NetPlay.Enabled) { LastSlotPick = TitleChoice == TitleNone ? "no title choice" : "online already"; return 0; }
            if (TitleChoice == TitleSingle || TitleChoice == TitleLocal)
            {
                int mode = TitleChoice == TitleSingle ? PlayMode.Single : PlayMode.Coop;
                Pending = mode;
                PlayMode.LastChoice = mode;
                LastSlotPick = TitleChoice == TitleSingle ? "title-single" : "title-local";
                Plugin.Log.LogInfo("title menu: slot picked, " + PlayMode.Name(mode) + " (chosen on the title)");
                return 1;
            }
            // Online host: the slot orb opens straight on the host page.
            return BeginTitleHost(ui, TitleTransport, (NetPlay.ConfigRelayServer ?? "").Trim());
        }

        internal static int BeginTitleHost(SaveSlotsUI ui, string transport, string relay)
        {
            if (!Open(ui)) { LastSlotPick = "orb failed"; return 0; }
            s_fromTitle = true;
            s_transport = transport;
            LastSlotPick = "title-host-" + transport;
            Plugin.Log.LogInfo("title menu: slot picked, hosting via " + transport);
            GoPage(PageHost, 1);
            if (s_transport == "cserver") BeginCServer(true, OriCoop.Authority.CServerConfig.Address + "|");
            else NetPlay.Begin(true, s_transport, s_transport == "relay" ? relay + "|" : "");
            return 2;
        }

        // T-059: the online room page (OnlineRoomScreen) hides and locks the title orb menu while it is up.
        internal static void RoomPanelVisible(bool visible)
        {
            if (s_mgr == null) return;
            s_mgr.SetVisible(!visible);
            s_mgr.IsActive = !visible;
            s_mgr.IsLocked = visible;
            if (s_tooltip != null) s_tooltip.gameObject.SetActive(!visible);
        }

        // Rooms already selected and wrote the slot; use the same host page and load action.
        internal static void RoomHostSlot(int slot, string relay)
        {
            Destroy();
            TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            ui.CurrentSlotIndex = slot;
            ui.CurrentSaveSlot.ChangeSelectionIndex(-1);
            SaveSlotsManager.CurrentSlotIndex = slot;
            s_fromNewGame = false;
            TitleChoice = TitleOnline;
            TitleTransport = "relay";
            SaveSlotsManager.BackupIndex = -1;
            BeginTitleHost(ui, "relay", relay);
            ui.Active = false;
        }

        internal static void RoomJoin(string target)
        {
            if (!TitleOrbOpen)
            {
                Destroy();
                TitleScreenManager.SetScreen(TitleScreenManager.Screen.MainMenu);
                OpenTitleOrb();
            }
            s_transport = "relay";
            s_joinTarget = target;
            GoPage(PageJoin, 1);
            NetPlay.Begin(false, "relay", target);
        }

        // What a slot pick would do now (harness checks).
        public static string SlotPickPlan
        {
            get
            {
                if (s_guestPickSlot) return "guest-slot";
                if (NetPlay.Enabled) return "online already";
                if (TitleChoice == TitleSingle) return "single";
                if (TitleChoice == TitleLocal) return "local";
                if (TitleChoice == TitleOnline) return "host:" + TitleTransport;
                return Enabled ? "ask" : "load";
            }
        }

        // ---- the orb on the title: Co-op page, host / join, join pages -------------------------------------------

        static bool TitlePressed(int index)
        {
            if (s_onTitle && s_page == PageCoop)
            {
                if (index == 0)
                {
                    TitleChoice = TitleLocal; TitleTransport = "";
                    Plugin.Log.LogInfo("title menu: Co-op -> local, to the save slots");
                    Destroy();
                    GoToSlots();
                }
                else if (index == 3) GoPage(PageServerMode, 0);
                else if (index < TitleTransports.Length) LobbyOpen(TitleTransports[index]);
                return true;
            }
            if (s_page == PageServerMode)
            {
                if (index == 0) LobbyOpen("cserver");
                else OpenRooms();
                return true;
            }
            if (s_onTitle && s_page == PageRole && index == 0)
            {
                if (s_transport == "cserver" && !ValidateCServer()) return true;
                string srv = LobbyServer;
                if (UsesRoomCode && srv.Length == 0) { ToastKey("lobby.server.required"); return true; }
                TitleChoice = TitleOnline; TitleTransport = s_transport;
                Plugin.Log.LogInfo("title menu: host via " + s_transport + ", to the save slots");
                Destroy();
                GoToSlots();
                return true;
            }
            if (s_fromTitle && s_page == PageHost && index == 2) { HostBackToSlots(); return true; }
            return false;
        }

        static bool TitleBack()
        {
            if (s_onTitle && s_page == PageCoop)
            {
                Plugin.Log.LogInfo("title menu: Co-op page back");
                Destroy();
                RestoreTitleMain();
                return true;
            }
            if (s_onTitle && s_page == PageServerMode)
            {
                GoPage(PageCoop, 3);
                return true;
            }
            if (s_onTitle && s_page == PageRole)
            {
                if (s_transport == "cserver") { GoPage(PageServerList, 0); return true; }
                if (UsesRoomCode) { GoPage(PageServerMode, 1); return true; }
                int row = Array.IndexOf(TitleTransports, s_transport);
                GoPage(PageCoop, row < 1 ? 1 : row);
                return true;
            }
            if (s_fromTitle && s_page == PageHost) { HostBackToSlots(); return true; }
            return false;
        }

        static void HostBackToSlots()
        {
            NetPlay.End("host cancelled");
            BackToSlots();
        }

        static bool OpenTitleOrb()
        {
            SaveSlotUI slot;
            GameObject prefab = FindDifficultyScreen(out slot);
            if (prefab == null || s_tMain == null) { Plugin.Log.LogWarning("title menu: no difficulty screen to copy"); return false; }
            Transform mainT = s_tMain.transform;
            bool ok = OpenOrb(prefab, null, delegate
            {
                // Where the title items are, at the size the orb has on the save slots.
                s_root.transform.parent = mainT.parent;
                Vector3 c = Vector3.zero; int n = 0;
                foreach (CleverMenuItem it in s_tMain.MenuItems) if (it != null && it.gameObject.activeInHierarchy) { c += it.transform.position; n++; }
                s_root.transform.position = n > 0 ? c / n : mainT.position;
                Vector3 want = slot != null && slot.HighlightAnimator != null ? slot.HighlightAnimator.transform.lossyScale * 1.5384f : Vector3.one;
                Vector3 ps = mainT.parent != null ? mainT.parent.lossyScale : Vector3.one;
                s_root.transform.localScale = new Vector3(SafeDiv(want.x, ps.x), SafeDiv(want.y, ps.y), SafeDiv(want.z, ps.z));
                Vector3 lp = s_root.transform.localPosition; lp.z = mainT.localPosition.z; s_root.transform.localPosition = lp;
            });
            if (!ok) return false;
            if (s_items.Count < TitlePage.Length) Plugin.Log.LogWarning("title menu: the copied screen has only " + s_items.Count + " rows");
            // Title items away while the orb is up (kept: Back shows them again).
            s_tMain.SetVisible(false); s_tMain.IsActive = false; s_tMain.IsLocked = true;
            Plugin.Log.LogInfo("title menu: orb on the title (" + string.Join(" | ", LastLabels.ToArray()) + ") at " + s_root.transform.position + " scale " + s_root.transform.lossyScale);
            return true;
        }

        static float SafeDiv(float a, float b) { return Mathf.Abs(b) < 1e-6f ? a : a / b; }

        // A guest connected on the title (join page, or a Steam invite): load the host's save with no slot pick.
        static void TitleGuestProceed()
        {
            if (s_guestStarted) return;
            StartGuestNoSlot("join page");
        }

        public static void StartGuestNoSlot(string why)
        {
            if (s_guestStarted || !NetPlay.Enabled || NetPlay.IsHost) return;
            if (OnlineRoomScreen.BeforeProceed(delegate { StartGuestNoSlot(why); })) return;
            s_guestAborts = GuestJoin.Aborts;
            if (!GuestJoin.BeginNoSlot())
            {
                Plugin.Log.LogWarning("title menu: guest join without a slot could not start (" + why + ")");
                return;
            }
            s_guestStarted = true;
            GuestStatus = "waiting";
            // The game's load-from-title action expects the save-slot screen (it fades that screen out and loads):
            // put it up underneath the transfer screen, with its input held (TitleTick) until the load runs.
            if (TitleOrbOpen) Destroy();
            if (TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.SaveSlots) TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
            Plugin.Log.LogInfo("title menu: guest connected (" + why + "); loading the host's save without a save slot");
        }

        // Per frame (NativeModeMenu.Driver).
        static void TitleTick()
        {
            if (!s_guestStarted) return;
            if (GuestJoin.Aborts != s_guestAborts)
            {
                s_guestStarted = false; s_guestPickSlot = false;
                GuestStatus = "aborted: " + GuestJoin.LastAbort;
                Plugin.Log.LogWarning("title menu: guest join stopped (" + GuestJoin.LastAbort + ")");
                if (NetPlay.Enabled) NetPlay.End("guest join stopped");
                if (TitleOrbOpen) GoPage(PageRole, 1);
                else if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.SaveSlots) TitleScreenManager.SetScreen(TitleScreenManager.Screen.MainMenu);
                return;
            }
            if (GuestJoin.NeedSlotChoice && !s_guestPickSlot)
            {
                // Every slot holds an unrelated save: the player picks which one keeps the co-op copy.
                s_guestPickSlot = true;
                GuestStatus = "pick a slot";
                if (TitleOrbOpen) Destroy();
                s_pickToastAt = 0f;
                if (TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.SaveSlots) TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
                if (SaveSlotsUI.Instance != null) SaveSlotsUI.Instance.Active = true;
                return;
            }
            // Picking: keep the prompt up (other toasts, e.g. "joined the host's game", would replace it).
            if (s_guestPickSlot && Time.realtimeSinceStartup >= s_pickToastAt)
            {
                s_pickToastAt = Time.realtimeSinceStartup + 3.6f;   // NetHud toasts queue, 3.5 s each
                string text = L10n.T("title.slots.full");
                NetHud.Toast(L10n.Zh ? text : "", L10n.Zh ? "" : text);
            }
            // Waiting for the host's save: the slot list is only a backdrop, no input.
            if (!s_guestPickSlot && !GuestJoin.Loading && SaveSlotsUI.Instance != null && SaveSlotsUI.Instance.Active
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.SaveSlots) SaveSlotsUI.Instance.Active = false;
            if (GuestJoin.Loading)
            {
                GuestStatus = "loading";
                if (TitleOrbOpen) Destroy();
            }
            if (NetPlay.LocalInGameplay()) { s_guestStarted = false; GuestStatus = "in game"; }
        }

        // ---- title items -----------------------------------------------------------------------------------

        static TitleScreenManager TitleManager { get { return Traverse.Create(typeof(TitleScreenManager)).Field("s_instance").GetValue<TitleScreenManager>(); } }

        // Options > Language switches while our title items (or the orb) are alive: relabel them in the new
        // language (they are set with SetMessage, which the game does not re-read on a language change).
        static bool s_langHooked;
        public static int LanguageRelabels;   // for tests
        static void HookLanguage()
        {
            if (s_langHooked) return;
            s_langHooked = true;
            L10n.Changed += delegate
            {
                try
                {
                    if (s_tSingle != null && !L10n.Zh) s_tEnglishCase = EnglishCaseOf(s_tSingle);
                    RelabelTitleItems();
                    if (IsOpen && s_mgr != null) Relabel(s_mgr.Index);
                    LanguageRelabels++;
                    Plugin.Log.LogInfo("title menu: language changed, relabelled (" + string.Join(" | ", TitleLabels.ToArray()) + ")");
                }
                catch (Exception e) { Plugin.Log.LogWarning("title menu: relabel after language change: " + e.Message); }
            };
        }

        // "upper" when the game's own item is written in capitals ("START GAME"), else "".
        static string EnglishCaseOf(CleverMenuItem item)
        {
            foreach (MessageBox b in item.GetComponentsInChildren<MessageBox>(true))
            {
                string txt = "";
                try { txt = b.MessageProvider != null ? b.MessageProvider.ToString() : ""; } catch (Exception) { }
                if (txt.Length > 2 && txt == txt.ToUpperInvariant() && txt != txt.ToLowerInvariant()) return "upper";
                if (txt.Length > 0) break;
            }
            return "";
        }

        static void InstallTitleItems()
        {
            HookLanguage();
            if (!TitleItemsOn) return;
            TitleScreenManager tm = TitleManager;
            if (tm == null || tm.MainMenuScreen == null) return;
            CleverMenuItemSelectionManager mm = tm.MainMenuScreen;
            EnsureTitleDriver();
            if (s_tMain == mm && s_tCoop != null && s_tSingle != null) { RelabelTitleItems(); return; }
            s_tMain = null; s_tSingle = null; s_tCoop = null; s_tStartAction = null;
            int startIdx = -1;
            for (int i = 0; i < mm.MenuItems.Count && startIdx < 0; i++)
                if (mm.MenuItems[i] != null && LeadsToSaveSlots(mm.MenuItems[i].Pressed, 0, new HashSet<object>())) startIdx = i;
            if (startIdx < 0)
            {
                if (!s_tWarned) { s_tWarned = true; Plugin.Log.LogWarning("title menu: no item leads to the save slots; keeping Start Game\n" + Dump(mm.transform)); }
                return;
            }
            CleverMenuItem start = mm.MenuItems[startIdx];
            Plugin.Log.LogInfo("title menu: start item '" + start.name + "' (" + startIdx + " of " + mm.MenuItems.Count + ", " + mm.ItemDirection + ")\n" + Dump(mm.transform));
            try
            {
                GameObject go = (GameObject)UnityEngine.Object.Instantiate(start.gameObject);
                go.name = "OriCoop_TitleCoop";
                go.transform.parent = start.transform.parent;
                go.transform.localScale = start.transform.localScale;
                go.transform.localRotation = start.transform.localRotation;
                go.transform.localPosition = start.transform.localPosition;
                go.SetActive(start.gameObject.activeSelf);
                CleverMenuItem coop = go.GetComponent<CleverMenuItem>();
                FixClonedGlows(start, coop);
                // Its copied "pressed" sequence holds the game's press sound (mainMenuPressStartGameSoundProvider):
                // keep that for our press, drop the rest (it would go to the save slots).
                s_tCoopSound = go.GetComponentInChildren<PlaySoundAction>(true);
                coop.Pressed = null;
                coop.PressedCallback += OnTitleCoopPressed;
                start.PressedCallback += OnTitleSinglePressed;
                s_tStartAction = start.Pressed;
                // Insert after Start, in the manager and in the layout that stacks the items.
                mm.MenuItems.Insert(startIdx + 1, coop);
                if (mm.Index > startIdx) mm.Index++;
                CleverMenuItemLayout layout = null;
                foreach (CleverMenuItemLayout l in tm.GetComponentsInChildren<CleverMenuItemLayout>(true))
                    if (l.MenuItems != null && l.MenuItems.Contains(start)) { layout = l; break; }
                if (layout != null)
                {
                    layout.MenuItems.Insert(layout.MenuItems.IndexOf(start) + 1, coop);
                    layout.Sort();
                }
                else
                {
                    // No layout: push the items below down by one item step.
                    Vector3 step = Vector3.zero;
                    if (startIdx + 2 < mm.MenuItems.Count) step = mm.MenuItems[startIdx + 2].transform.position - start.transform.position;
                    else if (startIdx > 0) step = start.transform.position - mm.MenuItems[startIdx - 1].transform.position;
                    for (int i = startIdx + 2; i < mm.MenuItems.Count; i++) mm.MenuItems[i].transform.position += step;
                    go.transform.position = start.transform.position + step;
                }
                if (mm.ItemDirection == CleverMenuItemSelectionManager.Direction.NavigationCage)
                {
                    var add = new List<CleverMenuItemSelectionManager.NavigationData>();
                    foreach (var n in mm.Navigation)
                    {
                        if (n.From == start) add.Add(new CleverMenuItemSelectionManager.NavigationData { From = coop, To = n.To });
                        if (n.To == start) add.Add(new CleverMenuItemSelectionManager.NavigationData { From = n.From, To = coop });
                    }
                    add.Add(new CleverMenuItemSelectionManager.NavigationData { From = start, To = coop });
                    add.Add(new CleverMenuItemSelectionManager.NavigationData { From = coop, To = start });
                    mm.Navigation.AddRange(add);
                }
                // English casing of the game's own item ("START GAME" or "Start Game").
                s_tEnglishCase = EnglishCaseOf(start);
                s_tMain = mm; s_tSingle = start; s_tCoop = coop;
                RelabelTitleItems();
                Plugin.Log.LogInfo("title menu: Single Player / Co-op items in (" + (layout != null ? "layout " + layout.name : "no layout") + ", " + string.Join(" | ", TitleLabels.ToArray()) + ")");
            }
            catch (Exception e) { Plugin.Log.LogError("title menu: install failed, keeping Start Game: " + e); s_tCoop = null; }
        }

        // T-020: the blue wisp behind a highlighted title item is its "glows" child (TransparencyAnimator, the item's
        // HighlightAnimator). A TransparencyAnimator caches each renderer's alpha from the SHARED material when it
        // initializes (decomp TransparencyAnimator.RendererData), and an unhighlighted item has written alpha 0 into those
        // shared materials. Our copy initializes during Instantiate, while Start Game's glows are faded out, so it cached 0
        // as the full alpha and its wisp could never show (only the text lifted). It also missed the main menu's own fade
        // (the parent TransparencyAnimator registers its children once, before the copy existed).
        // Fix: give the copy's glow renderers their own materials at the original's cached alpha, re-cache, start
        // unhighlighted, and register the copy with the parent fade like a native item.
        static readonly FieldInfo s_taData = typeof(TransparencyAnimator).GetField("m_rendererData", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly string[] s_taProps = { "_Color", "_MaskDissolveColor", "_AdditiveLayerColor" };

        static string RelPath(Transform root, Transform t)
        {
            string p = "";
            for (; t != null && t != root; t = t.parent) p = t.name + "#" + t.GetSiblingIndex() + "/" + p;
            return p;
        }

        // Visible wisp of an item: sum of the alpha its glow renderers draw with (enabled renderers only).
        static float GlowOf(CleverMenuItem item)
        {
            float sum = 0f;
            if (item == null) return 0f;
            foreach (TransparencyAnimator ta in item.GetComponentsInChildren<TransparencyAnimator>(true))
            {
                string prop = s_taProps[Mathf.Clamp((int)ta.Mode, 0, s_taProps.Length - 1)];
                foreach (Renderer r in ta.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled && r.gameObject.activeInHierarchy && r.sharedMaterial != null && r.sharedMaterial.HasProperty(prop))
                        sum += r.sharedMaterial.GetColor(prop).a;
            }
            return sum;
        }

        static void FixClonedGlows(CleverMenuItem start, CleverMenuItem coop)
        {
            if (s_taData == null) { Plugin.Log.LogWarning("title menu: TransparencyAnimator.m_rendererData not found; Co-op wisp left as copied"); return; }
            // Original alpha of every glow renderer of Start Game, by its path under the item.
            Dictionary<string, float> alpha = new Dictionary<string, float>();
            foreach (TransparencyAnimator ta in start.GetComponentsInChildren<TransparencyAnimator>(true))
            {
                ta.Initialize();   // menu not shown yet: cache now, while the shared materials still hold the real alpha
                System.Collections.IList data = s_taData.GetValue(ta) as System.Collections.IList;
                if (data == null) continue;
                foreach (object d in data)
                {
                    Type t = d.GetType();
                    Renderer r = t.GetField("Renderer").GetValue(d) as Renderer;
                    if (r != null) alpha[RelPath(start.transform, r.transform)] = (float)t.GetField("OriginalAlpha").GetValue(d);
                }
            }
            int fixedRenderers = 0, animators = 0;
            foreach (TransparencyAnimator ta in coop.GetComponentsInChildren<TransparencyAnimator>(true))
            {
                string prop = s_taProps[Mathf.Clamp((int)ta.Mode, 0, s_taProps.Length - 1)];
                foreach (Renderer r in ta.GetComponentsInChildren<Renderer>(true))
                {
                    float a;
                    if (r.sharedMaterial == null || !r.sharedMaterial.HasProperty(prop) || !alpha.TryGetValue(RelPath(coop.transform, r.transform), out a)) continue;
                    Material m = new Material(r.sharedMaterial);
                    Color c = m.GetColor(prop); c.a = a; m.SetColor(prop, c);
                    r.sharedMaterial = m;
                    fixedRenderers++;
                }
                ta.CacheOriginals();
                animators++;
            }
            // Start like a native unhighlighted item; the selection manager highlights it when it is chosen.
            BaseAnimator hl = coop.HighlightAnimator;
            if (hl != null) { hl.AnimatorDriver.GoToStart(); hl.SampleValue(0f, true); }
            // Join the main menu's fade (it registered its children before the copy existed).
            TransparencyAnimator.Register(coop.transform);
            // Each animator whose nearest animator above it is outside the copy (the glows) joins that parent's fade.
            foreach (TransparencyAnimator ta in coop.GetComponentsInChildren<TransparencyAnimator>(true))
            {
                TransparencyAnimator above = ta.transform.parent != null ? ta.transform.parent.GetComponentInParent<TransparencyAnimator>() : null;
                if (above != null && !above.transform.IsChildOf(coop.transform)) TransparencyAnimator.Register(ta.transform);
            }
            Plugin.Log.LogInfo("title menu: Co-op wisp: " + fixedRenderers + " glow renderer(s) in " + animators + " animator(s) at Start Game's alpha, joined the menu fade");
        }

        // Does this action (or an action it runs) set the title screen to the save slots?
        static bool LeadsToSaveSlots(object a, int depth, HashSet<object> seen)
        {
            if (a == null || depth > 6) return false;
            UnityEngine.Object uo = a as UnityEngine.Object;
            if (uo != null && uo == null) return false;           // destroyed
            if (!seen.Add(a)) return false;
            SetTitleScreenAction st = a as SetTitleScreenAction;
            if (st != null) return st.Screen == TitleScreenManager.Screen.SaveSlots;
            for (Type t = a.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object v;
                    try { v = f.GetValue(a); } catch (Exception) { continue; }
                    if (v is ActionMethod) { if (LeadsToSaveSlots(v, depth + 1, seen)) return true; }
                    else if (v is IEnumerable && !(v is string) && (f.FieldType.IsArray || f.FieldType.IsGenericType))
                    {
                        foreach (object e in (IEnumerable)v) if (e is ActionMethod && LeadsToSaveSlots(e, depth + 1, seen)) return true;
                    }
                }
            return false;
        }

        static void RelabelTitleItems()
        {
            TitleLabels.Clear();
            if (s_tSingle == null || s_tCoop == null) return;
            CleverMenuItem[] items = { s_tSingle, s_tCoop };
            for (int i = 0; i < 2; i++)
            {
                MessageBox[] boxes = items[i].GetComponentsInChildren<MessageBox>(true);
                string en = L10n.T(TitleItemRows[i], false);
                if (s_tEnglishCase == "upper") en = en.ToUpperInvariant();
                string label = boxes.Length > 0 ? PickFor(boxes[0], TitleItemRows[i]) : en;
                TitleLabels.Add(label);
                foreach (MessageBox b in boxes) Set(b, label);
            }
        }

        // PickResolved() for boxes outside the orb: Chinese only if this box's Chinese font has every character.
        static readonly Dictionary<int, HashSet<int>> s_fontGlyphs = new Dictionary<int, HashSet<int>>();
        static string PickFor(MessageBox box, string textKey)
        {
            string zh = L10n.T(textKey, true), en = L10n.T(textKey, false);
            if (s_tEnglishCase == "upper") en = en.ToUpperInvariant();
            if (!Zh) return en;
            try
            {
                if (box == null || box.LanguageStyles == null || box.LanguageStyles.Chinese == null) return zh;
                TextStyleCollection col = box.LanguageStyles.Chinese;
                if (col.styles == null || col.styles.Length == 0 || col.styles[0].font == null) return zh;
                BitmapFont font = col.styles[0].font;
                int n = font.otherChars != null ? font.otherChars.Length : 0;
                int key = font.GetInstanceID() * 31 + n;
                HashSet<int> have;
                if (!s_fontGlyphs.TryGetValue(key, out have))
                {
                    have = new HashSet<int>();
                    if (font.otherChars != null) foreach (BitmapFontChar c in font.otherChars) have.Add(c.id);
                    s_fontGlyphs[key] = have;
                }
                foreach (char ch in zh)
                    if (ch > ' ' && !('!' <= ch && ch <= '~') && !have.Contains(ch))
                    {
                        Plugin.Log.LogWarning("title menu: Chinese font lacks '" + ch + "' in '" + zh + "'; showing English");
                        return en;
                    }
            }
            catch (Exception) { }
            return zh;
        }

        static void OnTitleSinglePressed()
        {
            if (TitleOrbOpen) return;
            TitleChoice = TitleSingle; TitleTransport = "";
            Plugin.Log.LogInfo("title menu: Single Player");
        }

        static void OnTitleCoopPressed()
        {
            if (IsOpen) return;
            Plugin.Log.LogInfo("title menu: Co-op (press sound " + SoundName(s_tCoopSound) + ")");
            if (s_tCoopSound != null) { try { s_tCoopSound.Perform(null); PressSounds++; } catch (Exception e) { Plugin.Log.LogWarning("title menu: press sound failed: " + e.Message); } }
            if (!OpenTitleOrb())
            {
                // No orb: the slots, and the old question after the slot pick.
                TitleChoice = TitleNone;
                TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
            }
        }

        // From the orb: the row press already played the menu's select sound, so not the Start Game action
        // (sound + SetTitleScreenAction SaveSlots), only its screen change.
        static void GoToSlots()
        {
            TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
        }

        // A Steam invite accepted at the title: open the orb there so the join page can show it.
        static bool OpenTitleOrbForInvite()
        {
            if (IsOpen || !TitleItemsReady || TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.MainMenu) return false;
            return OpenTitleOrb();
        }

        static GameObject FindDifficultyScreen(out SaveSlotUI sceneSlot)
        {
            sceneSlot = null;
            TitleScreenManager tm = TitleManager;
            try
            {
                if (tm != null && tm.SaveSlotsScreen != null && tm.SaveSlotsScreen.ItemsUI != null && tm.SaveSlotsScreen.Items != null)
                    foreach (SaveSlotUI s in tm.SaveSlotsScreen.Items) if (s != null && s.DifficultyScreen != null) { sceneSlot = s; return s.DifficultyScreen; }
            }
            catch (Exception) { }
            foreach (SaveSlotUI s in Resources.FindObjectsOfTypeAll<SaveSlotUI>())
                if (s != null && s.DifficultyScreen != null) return s.DifficultyScreen;
            return null;
        }

        static void RestoreTitleMain()
        {
            if (s_tMain == null) return;
            s_tMain.SetVisible(true);
            s_tMain.IsActive = true;
            s_tMain.IsLocked = true;                      // the Back press is still down
            s_tMainUnlockAt = Time.realtimeSinceStartup + 0.3f;
            if (s_tCoop != null) s_tMain.SetCurrentMenuItem(s_tCoop);
        }
        static float s_tMainUnlockAt = -1f;

        static void EnsureTitleDriver()
        {
            if (s_tDriver != null) return;
            GameObject go = new GameObject("OriCoop_TitleMenuDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_tDriver = go.AddComponent<TitleDriver>();
            s_tLastZh = Zh;
        }

        class TitleDriver : MonoBehaviour
        {
            void Update()
            {
                try
                {
                    if (s_tMainUnlockAt > 0f && Time.realtimeSinceStartup >= s_tMainUnlockAt)
                    {
                        s_tMainUnlockAt = -1f;
                        if (s_tMain != null && !TitleOrbOpen) s_tMain.IsLocked = false;
                    }
                    bool zh = Zh;
                    if (zh != s_tLastZh) { s_tLastZh = zh; RelabelTitleItems(); if (IsOpen) RefreshTexts(true); }
                    if (!IsOpen) TitleTick();                // with the orb open, NativeModeMenu.Driver ticks it
                }
                catch (Exception e) { Plugin.Log.LogError("title menu: tick failed: " + e); }
            }
        }

        // ---- harness checks (TestHarness "title" scenario, on the title main menu) ------------------------------

        static MonoBehaviour s_tHost;
        static IEnumerator TWait(float s) { float end = Time.realtimeSinceStartup + s; while (Time.realtimeSinceStartup < end) yield return null; }

        static void TShot(string name)
        {
            string outFile = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT");
            string dir = string.IsNullOrEmpty(outFile) ? Application.dataPath : Path.GetDirectoryName(Path.GetFullPath(outFile));
            string p = Path.Combine(dir, name + ".png");
            Application.CaptureScreenshot(p);
            Plugin.Log.LogInfo("title menu: screenshot " + p);
        }

        static bool SlotsReady { get { SaveSlotsUI ui = SaveSlotsUI.Instance; return ui != null && ui.IsVisible && ui.Active && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.SaveSlots; } }

        static IEnumerator WaitFor(Func<bool> cond, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end && !cond()) yield return null;
        }

        static IEnumerator BackToTitleMenu()
        {
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            if (ui != null && ui.OnBackPressedAction != null) ui.OnBackPressedAction.Perform(null);
            else TitleScreenManager.SetScreen(TitleScreenManager.Screen.MainMenu);
            yield return s_tHost.StartCoroutine(WaitFor(() => TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, 5f));
            yield return s_tHost.StartCoroutine(TWait(1f));
        }

        static IEnumerator PressCoop()
        {
            s_tMain.SetCurrentMenuItem(s_tCoop);
            yield return s_tHost.StartCoroutine(TWait(0.3f));
            s_tMain.PressCurrentItem();
            yield return s_tHost.StartCoroutine(TWait(1.2f));
        }

        // Press a row of the open orb (the native way: focus, then the manager's press).
        static IEnumerator Row(int row, float wait)
        {
            if (s_mgr == null) yield break;
            s_mgr.SetCurrentItem(row);
            yield return s_tHost.StartCoroutine(TWait(0.3f));
            if (s_mgr != null) s_mgr.PressCurrentItem();
            yield return s_tHost.StartCoroutine(TWait(wait));
        }

        static IEnumerator Back(float wait)
        {
            if (s_mgr != null) s_mgr.OnBackPressed();
            yield return s_tHost.StartCoroutine(TWait(wait));
        }

        static bool HasKey(List<string> labels, string key) { return labels.Contains(L10n.T(key, true)) || labels.Contains(L10n.T(key, false)) || labels.Contains(L10n.T(key, false).ToUpperInvariant()); }
        static string Labels { get { return LastPage + ": " + string.Join(" | ", LastLabels.ToArray()); } }

        static bool CServerListLabelsMatch()
        {
            List<string> saved = OriCoop.Authority.CServerConfig.Servers;
            if (LastPage != "serverlist" || LastLabels.Count != saved.Count + 2 ||
                !HasKey(new List<string> { LastLabels[0] }, "server.add") ||
                !HasKey(new List<string> { LastLabels[1] }, "server.remove")) return false;
            for (int i = 0; i < saved.Count; i++) if (LastLabels[i + 2] != saved[i]) return false;
            return true;
        }

        static IEnumerator CheckCServerList(Action<bool, string> check)
        {
            check(CServerListLabelsMatch(), "title menu: C-server list has Add / Remove and saved rows (" + Labels + ")");
            int before = OriCoop.Authority.CServerConfig.Servers.Count;
            yield return s_tHost.StartCoroutine(Row(1, 0.5f));
            check(LastPage == "serverremove" && LastLabels.Count == (before == 0 ? 1 : before) &&
                (before != 0 || HasKey(LastLabels, "common.back")), "title menu: Remove lists saved servers, or Back when empty");
            yield return s_tHost.StartCoroutine(Back(0.5f));
            yield return s_tHost.StartCoroutine(Row(0, 0.5f));
            check(LastPage == "serveradd" && LastLabels.Count == 3 && HasKey(LastLabels, "common.save"),
                "title menu: Add opens address / port / Save");
            if (!OriCoop.Authority.CServerConfig.IsValidAddress(s_cserverHost + ":" + s_cserverPort))
            { s_cserverHost = "127.0.0.1"; s_cserverPort = "1"; }
            string added = s_cserverHost + ":" + s_cserverPort;
            yield return s_tHost.StartCoroutine(Row(2, 0.5f));
            check(CServerListLabelsMatch() && LastLabels.Count == before + 3 && LastLabels[before + 2] == added,
                "title menu: Save appends the server and returns to the list");
            yield return s_tHost.StartCoroutine(Row(1, 0.5f));
            yield return s_tHost.StartCoroutine(Row(before, 0.5f));
            check(CServerListLabelsMatch() && LastLabels.Count == before + 2,
                "title menu: picking Remove deletes just that saved row and returns to the list");
            yield return s_tHost.StartCoroutine(Row(0, 0.5f));
            int colon = added.LastIndexOf(':');
            s_cserverHost = added.Substring(0, colon); s_cserverPort = added.Substring(colon + 1);
            yield return s_tHost.StartCoroutine(Row(2, 0.5f));
            yield return s_tHost.StartCoroutine(Row(before + 2, 0.5f));
            check(LastPage == "role" && LastLabels.Count == 2 && OriCoop.Authority.CServerConfig.Address == added,
                "title menu: picking a saved server selects it and opens Host / Join");
        }

        public static IEnumerator TitleChecks(MonoBehaviour host, Action<bool, string> check, Action<string> note, int slot)
        {
            s_tHost = host;
            note("--- title menu: Single Player / Co-op");
            yield return s_tHost.StartCoroutine(WaitFor(() => TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu && TitleItemsReady, 15f));
            check(TitleItemsReady && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu,
                "title menu: Single Player and Co-op on the title (screen=" + TitleScreenManager.CurrentScreen + ")");
            if (!TitleItemsReady) yield break;
            yield return s_tHost.StartCoroutine(TWait(1f));
            check(TitleLabels.Count == 2 && HasKey(TitleLabels, "title.single") && HasKey(TitleLabels, "title.coop"),
                "title menu: labels " + string.Join(" | ", TitleLabels.ToArray()));
            int si = s_tMain.MenuItems.IndexOf(s_tSingle), ci = s_tMain.MenuItems.IndexOf(s_tCoop);
            check(si >= 0 && ci == si + 1 && s_tCoop.gameObject.activeInHierarchy, "title menu: Co-op right under Single Player (" + si + ", " + ci + ")");
            check(Vector3.Distance(s_tSingle.transform.position, s_tCoop.transform.position) > 0.01f, "title menu: items do not overlap");
            // T-020: the highlighted item shows its blue wisp ("glows"), the Co-op copy exactly like Single Player.
            s_tMain.SetCurrentMenuItem(s_tSingle);
            yield return s_tHost.StartCoroutine(TWait(1.2f));
            float singleOn = GlowOf(s_tSingle), coopOff = GlowOf(s_tCoop);
            s_tMain.SetCurrentMenuItem(s_tCoop);
            yield return s_tHost.StartCoroutine(TWait(1.2f));
            float coopOn = GlowOf(s_tCoop), singleOff = GlowOf(s_tSingle);
            note("title menu: wisp alpha single on=" + singleOn.ToString("F3") + " off=" + singleOff.ToString("F3") + ", co-op on=" + coopOn.ToString("F3") + " off=" + coopOff.ToString("F3"));
            check(singleOn > 0.05f && singleOff < singleOn * 0.2f, "title menu: Single Player's wisp shows only while highlighted (on " + singleOn.ToString("F3") + ", off " + singleOff.ToString("F3") + ")");
            check(coopOn > singleOn * 0.8f && coopOn < singleOn * 1.25f && coopOff < singleOn * 0.2f,
                "title menu: Co-op shows the same wisp as Single Player when highlighted (co-op on " + coopOn.ToString("F3") + ", off " + coopOff.ToString("F3") + "; single on " + singleOn.ToString("F3") + ")");
            TShot("title_items");
            yield return s_tHost.StartCoroutine(TWait(0.5f));

            check(PagesWithHostMode().Length == 0, "menus: no page definition lists the host-mode row (" + PagesWithHostMode() + ")");

            // Co-op page and Back.
            int press0 = PressSounds;
            yield return s_tHost.StartCoroutine(PressCoop());
            check(PressSounds > press0 && s_tCoopSound != null, "sounds: the Co-op title item plays the game's press sound (" + SoundName(s_tCoopSound) + ")");
            check(TitleOrbOpen && LastPage == "coop" && LastLabels.Count == 4 && HasKey(LastLabels, "title.local") && HasKey(LastLabels, "server.title"),
                "title menu: Co-op page with 4 choices (" + Labels + ")");
            check(!s_tMain.IsVisible, "title menu: title items hidden under the Co-op page");
            yield return s_tHost.StartCoroutine(TWait(0.8f));
            check(GlowOf(s_tCoop) < 0.02f, "title menu: Co-op's wisp fades out with the title items (" + GlowOf(s_tCoop).ToString("F3") + ")");
            if (!TitleOrbOpen) yield break;
            yield return s_tHost.StartCoroutine(TWait(0.5f));
            TShot("title_coop_page");
            yield return s_tHost.StartCoroutine(TWait(0.5f));
            int move0 = MoveSounds;
            s_mgr.SetCurrentItem(1);
            yield return s_tHost.StartCoroutine(TWait(0.3f));
            s_mgr.SetCurrentItem(2);
            yield return s_tHost.StartCoroutine(TWait(0.3f));
            check(MoveSounds == move0 + 2 && s_changeSound != null, "sounds: moving the highlight plays the menu's change sound once per move (" + (MoveSounds - move0) + " for 2 moves; " + SoundInfo + ")");
            int back0 = BackSounds;
            yield return s_tHost.StartCoroutine(Back(1f));
            check(BackSounds == back0 + 1, "sounds: Back plays the menu's cancel sound (" + SoundInfo + ")");
            check(!IsOpen && s_tMain.IsVisible && s_tMain.IsActive && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu,
                "title menu: Back on the Co-op page returns to the title items");
            yield return s_tHost.StartCoroutine(TWait(0.5f));

            // Single Player: straight to the slots, the slot pick loads without the "how to play" page.
            s_tMain.SetCurrentMenuItem(s_tSingle);
            yield return s_tHost.StartCoroutine(TWait(0.3f));
            s_tMain.PressCurrentItem();
            yield return s_tHost.StartCoroutine(WaitFor(() => SlotsReady, 6f));
            check(SlotsReady && TitleChoice == TitleSingle && SlotPickPlan == "single", "title menu: Single Player -> save slots, slot loads single player (" + SlotPickPlan + ")");
            yield return s_tHost.StartCoroutine(BackToTitleMenu());
            check(TitleChoice == TitleNone && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, "title menu: back from the slots forgets the choice");

            // Online: transport -> 开房 / 加入 on the title.
            for (int row = 1; row <= 4; row++)
            {
                if (row == 3) continue;   // the relay row is gone (T-045); the 自建服务器 page is checked below
                string tr = row == 4 ? "cserver" : TitleTransports[row];
                yield return s_tHost.StartCoroutine(PressCoop());
                if (!TitleOrbOpen) { check(false, "title menu: Co-op page opens again (" + tr + ")"); yield break; }
                int rowPress0 = PressSounds, rowMove0 = MoveSounds;
                yield return s_tHost.StartCoroutine(Row(row == 4 ? 3 : row, 1f));
                if (row == 1) check(PressSounds == rowPress0 + 1 && MoveSounds == rowMove0 + 1,
                    "sounds: a row press plays the select sound once, the page change itself is silent (press " + (PressSounds - rowPress0) + ", move " + (MoveSounds - rowMove0) + ")");
                if (tr == "cserver")
                {
                    check(TitleOrbOpen && LastPage == "servermode" && LastLabels.Count == 2 && s_mgr.Index == 0
                        && HasKey(LastLabels, "server.cserver") && HasKey(LastLabels, "lobby.rooms") && !HasKey(LastLabels, "server.relay"),
                        "title menu: own-server page lists server mode and My rooms, no host mode, default focus on server mode (" + Labels + ")");
                    yield return s_tHost.StartCoroutine(Row(0, 1f));
                    yield return s_tHost.StartCoroutine(CheckCServerList(check));
                }
                check(TitleOrbOpen && LastPage == "role" && s_transport == tr && HasKey(LastLabels, "lobby.host") && HasKey(LastLabels, "lobby.join")
                    && !SlotsReady,
                    "title menu: " + tr + " asks host / join before any save slot (" + Labels + ")");
                if (tr == "cserver") check(LastLabels.Count == 2, "title menu: selected C-server has only Host / Join (" + Labels + ")");
                TShot("title_" + tr + "_role");
                yield return s_tHost.StartCoroutine(TWait(0.5f));

                // 加入: the join page on the title, no save slot.
                yield return s_tHost.StartCoroutine(Row(1, 1.2f));
                bool joinPage = tr == "steam" ? LastPage == "joining" : LastPage == "entry";
                check(TitleOrbOpen && joinPage && !SlotsReady,
                    "title menu: " + tr + " join page on the title, no save slot (" + Labels + ")");
                if (tr != "steam")
                {
                    s_mgr.SetCurrentItem(LastLabels.Count - (tr == "direct" ? 1 : 2));   // Connect row: its description
                    yield return s_tHost.StartCoroutine(TWait(0.4f));
                    check(LastTooltip.Contains(L10n.T("lobby.save.note", true)) || LastTooltip.Contains(L10n.T("lobby.save.note", false)),
                        "title menu: " + tr + " join page says co-op progress is saved automatically (" + LastTooltip.Replace("\n", " / ") + ")");
                }
                TShot("title_" + tr + "_join");
                yield return s_tHost.StartCoroutine(TWait(0.5f));
                if (UsesRoomCode && LobbyServer.Length == 0)
                {
                    s_entry = "ABC234"; s_cursor = 5;
                    yield return s_tHost.StartCoroutine(Row(1, 0.6f));
                    check(TitleOrbOpen && LastPage == "entry" && !NetPlay.Enabled,
                        "title menu: " + tr + " join without a server stays on the entry page (" + LastTooltip + ")");
                }
                yield return s_tHost.StartCoroutine(Back(1f));                     // join -> role (ends a Steam join)
                check(TitleOrbOpen && LastPage == "role" && !NetPlay.Enabled, "title menu: Back on the " + tr + " join page returns to host / join (" + Labels + ", net=" + NetPlay.CurrentPhase + ")");

                // 开房: the save slots, then the slot orb on the host page.
                if (UsesRoomCode && LobbyServer.Length == 0)
                {
                    yield return s_tHost.StartCoroutine(Row(0, 0.6f));
                    check(TitleOrbOpen && LastPage == "role", "title menu: " + tr + " host without a server stays on host / join (" + LastTooltip + ")");
                }
                else
                {
                    yield return s_tHost.StartCoroutine(Row(0, 0.2f));
                    yield return s_tHost.StartCoroutine(WaitFor(() => SlotsReady, 6f));
                    yield return s_tHost.StartCoroutine(TWait(0.6f));
                    check(SlotsReady && TitleChoice == TitleOnline && TitleTransport == tr && SlotPickPlan == "host:" + tr,
                        "title menu: " + tr + " host -> save slots (" + SlotPickPlan + ")");
                    SaveSlotsUI ui = SaveSlotsUI.Instance;
                    if (ui == null || !SlotsReady) yield break;
                    ui.SetCurrentItem(slot);
                    ui.UsedSaveSlotSelected();
                    yield return s_tHost.StartCoroutine(TWait(2f));
                    check(IsOpen && !s_onTitle && LastPage == "hosting" && s_transport == tr && (tr == "steam" || NetPlay.Enabled),
                        "title menu: " + tr + " host slot pick opens the host page (" + Labels + ", net=" + NetPlay.CurrentPhase + ")");
                    TShot("title_" + tr + "_host");
                    yield return s_tHost.StartCoroutine(TWait(0.5f));
                    yield return s_tHost.StartCoroutine(Back(1.2f));
                    check(!IsOpen && SlotsReady && !NetPlay.Enabled, "title menu: Back on the " + tr + " host page closes the room and returns to the save slots (net=" + NetPlay.CurrentPhase + ")");
                    yield return s_tHost.StartCoroutine(BackToTitleMenu());
                    continue;
                }
                yield return s_tHost.StartCoroutine(Back(1f));
                if (tr == "relay" || tr == "cserver")
                {
                    if (tr == "cserver")
                    {
                        check(CServerListLabelsMatch(), "title menu: Back from C-server Host / Join returns to the list");
                        yield return s_tHost.StartCoroutine(Back(1f));
                    }
                    check(TitleOrbOpen && LastPage == "servermode" && s_mgr.Index == (tr == "cserver" ? 0 : 1),
                        "title menu: " + tr + " Back returns to the mode choice (" + Labels + ")");
                    yield return s_tHost.StartCoroutine(Back(1f));
                }
                check(TitleOrbOpen && LastPage == "coop", "title menu: Back on host / join returns to the Co-op page (" + Labels + ")");
                yield return s_tHost.StartCoroutine(Back(1f));
            }

            // Local co-op: leave the choice set; the harness then picks the slot and the save loads (checked there).
            yield return s_tHost.StartCoroutine(PressCoop());
            if (TitleOrbOpen) { yield return s_tHost.StartCoroutine(Row(0, 0.2f)); yield return s_tHost.StartCoroutine(WaitFor(() => SlotsReady, 6f)); }
            check(SlotsReady && TitleChoice == TitleLocal && SlotPickPlan == "local", "title menu: Local Co-op -> save slots (" + SlotPickPlan + ")");
        }
    }
}
