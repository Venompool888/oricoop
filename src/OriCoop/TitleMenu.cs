using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CatlikeCoding.TextBox;
using Game;
using HarmonyLib;
using OriCoop.Ui;
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
                        "Title menu: Single Player / Co-op instead of Start Game (Co-op asks local / LAN / Steam / server and host / join before the save slots). false = the game's Start Game, and the question comes after the save slot.");
                return s_titleItems == null || s_titleItems.Value;
            }
        }

        static readonly string[] TitleItemRows = { "title.single", "title.coop" };
        // Menu redesign (issue 「[菜单重构] 主菜单与联机引导」): 多人游玩 asks what the player wants first (本地同屏 / 创建房间 /
        // 加入房间 / 自建服务器), then how to connect (Steam / room code / LAN). The rows, presses, Back and default focus come
        // from Ui/CoopMenuFlow.cs; these arrays only list the keys for the font check (CheckFont) and PagesWithHostMode.
        static readonly string[][] TitlePage = FlowKeys(Ui.CoopMenuFlow.PageCoop);
        static readonly string[][] TitleHostPage = FlowKeys(Ui.CoopMenuFlow.PageHostMethod);
        static readonly string[][] TitleJoinPage = FlowKeys(Ui.CoopMenuFlow.PageJoinMethod);
        static string[][] FlowKeys(int flowPage)
        {
            var keys = new List<string[]>();
            foreach (Ui.CoopMenuFlow.RowKeys r in Ui.CoopMenuFlow.Rows(flowPage)) keys.Add(new[] { r.Label, r.Desc });
            if (flowPage != Ui.CoopMenuFlow.PageCoop) keys.Add(new[] { "title.code.noserver", "title.code.noserver.desc" });
            return keys.ToArray();
        }
        // T-045 (owner's choice B): the 自建服务器 page lists the server mode and 「我的房间」.
        // T-159: the relay is gone; 我的房间 plays through the selected C-server.
        // T-095 (owner's sketch): 自建服务器 = [上次游玩 (only with a room play record)] / 我的房间 / 服务器管理.
        static readonly string[][] ServerModePage = {
            new[] { "rooms.last", "rooms.last.desc" },
            new[] { "lobby.rooms", "lobby.rooms.desc" },
            new[] { "server.manage", "server.manage.desc" },
        };
        // ---- the redesigned 多人游玩 flow (Ui/CoopMenuFlow.cs) -------------------------------------------------------

        // The method pages were opened from 多人游玩 on the title: Back from a join they started returns to 加入房间.
        // false on the old paths (自建服务器 -> 服务器列表 -> 创建房间 / 加入房间, 我的房间), which keep their own Back.
        static bool s_titleFlow;
        static BepInEx.Configuration.ConfigEntry<string> s_lastHostMethod, s_lastJoinMethod;

        static bool IsTitleFlowPage { get { return s_onTitle && (s_page == PageCoop || s_page == PageHostMethod || s_page == PageJoinMethod); } }
        static int FlowPageOf(int page) { return page == PageHostMethod ? Ui.CoopMenuFlow.PageHostMethod : page == PageJoinMethod ? Ui.CoopMenuFlow.PageJoinMethod : Ui.CoopMenuFlow.PageCoop; }

        static BepInEx.Configuration.ConfigEntry<string> MethodEntry(bool host)
        {
            if (Plugin.Instance == null) return null;
            if (host && s_lastHostMethod == null)
                s_lastHostMethod = Plugin.Instance.Config.Bind("Start", "LastHostMethod", "", "Title 多人游玩 -> 创建房间: the connection method chosen last time (steam / cserver / direct); the cursor starts there.");
            if (!host && s_lastJoinMethod == null)
                s_lastJoinMethod = Plugin.Instance.Config.Bind("Start", "LastJoinMethod", "", "Title 多人游玩 -> 加入房间: the connection method chosen last time (steam / cserver / direct); the cursor starts there.");
            return host ? s_lastHostMethod : s_lastJoinMethod;
        }

        static string LastMethod(bool host) { BepInEx.Configuration.ConfigEntry<string> e = MethodEntry(host); return e != null ? e.Value : ""; }

        static void RememberMethod(bool host, string method)
        {
            BepInEx.Configuration.ConfigEntry<string> e = MethodEntry(host);
            if (e == null || e.Value == method) return;
            try { e.Value = method; Plugin.Instance.Config.Save(); }
            catch (Exception ex) { Plugin.Log.LogWarning("title menu: could not remember the " + (host ? "host" : "join") + " method: " + ex.Message); }
        }

        static Ui.CoopMenuFlow.ServerPick CodeServer(out string address)
        {
            return Ui.CoopMenuFlow.ResolveServer(OriCoop.Authority.CServerConfig.Servers, OriCoop.Authority.CServerConfig.Address,
                OriCoop.Authority.CServerConfig.IsValidAddress, out address);
        }

        static string FormatServer(string text, string server)
        {
            try { return string.Format(text, server); } catch (FormatException) { return text; }
        }

        // Rows of 多人游玩 / 创建房间 / 加入房间: zh label, zh description, en label, en description (as PageRows).
        static string[][] TitleFlowRows()
        {
            int fp = FlowPageOf(s_page);
            string server = "";
            bool haveServer = fp != Ui.CoopMenuFlow.PageCoop && CodeServer(out server) != Ui.CoopMenuFlow.ServerPick.None;
            Ui.CoopMenuFlow.RowKeys[] keys = Ui.CoopMenuFlow.Rows(fp);
            var rows = new List<string[]>();
            for (int i = 0; i < keys.Length; i++)
            {
                string desc = Ui.CoopMenuFlow.DescKey(fp, i, haveServer);
                rows.Add(new[] { L10n.T(keys[i].Label, true), FormatServer(L10n.T(desc, true), server), L10n.T(keys[i].Label, false), FormatServer(L10n.T(desc, false), server) });
            }
            return rows.ToArray();
        }

        // A room-code row: select the server ResolveServer picked (the first saved one when none is selected yet).
        static bool UseCodeServer(Ui.CoopMenuFlow.ServerPick pick, string address)
        {
            if (pick == Ui.CoopMenuFlow.ServerPick.First && !OriCoop.Authority.CServerConfig.SetAddress(address)) { ToastKey("cserver.addr.save_failed"); return false; }
            LoadCServer();
            return ValidateCServer();
        }

        static bool TitleFlowPressed(int index)
        {
            string address;
            Ui.CoopMenuFlow.ServerPick pick = CodeServer(out address);
            Ui.CoopMenuFlow.Step step = Ui.CoopMenuFlow.Press(FlowPageOf(s_page), index, pick);
            Plugin.Log.LogInfo("title menu: " + LastPage + " row " + index + " -> " + step);
            switch (step.Act)
            {
                case Ui.CoopMenuFlow.Act.Local:
                    if (!NetPlay.Enabled) Saves.CoopSaveFolder.Leave();
                    TitleChoice = TitleLocal; TitleTransport = "";
                    Plugin.Log.LogInfo("title menu: Co-op -> local, to the save slots");
                    Destroy();
                    GoToSlots();
                    break;
                case Ui.CoopMenuFlow.Act.OpenHostMethods:
                case Ui.CoopMenuFlow.Act.OpenJoinMethods:
                {
                    bool host = step.Act == Ui.CoopMenuFlow.Act.OpenHostMethods;
                    Saves.CoopSaveFolder.Enter();
                    s_titleFlow = true;
                    int page = host ? PageHostMethod : PageJoinMethod;
                    GoPage(page, Ui.CoopMenuFlow.DefaultFocus(FlowPageOf(page), LastMethod(true), LastMethod(false)));
                    break;
                }
                case Ui.CoopMenuFlow.Act.OpenServerMode:
                    s_titleFlow = false;
                    Saves.CoopSaveFolder.Enter();
                    GoPage(PageServerMode, 0);
                    break;
                case Ui.CoopMenuFlow.Act.NeedServer:
                    // No server for a room code: 服务器管理 with the cursor on 添加服务器, and a note why the page changed.
                    s_titleFlow = false;
                    s_transport = "cserver";
                    LoadCServer();
                    GoPage(PageServerManage, 0);
                    ToastKey("title.code.noserver");
                    break;
                case Ui.CoopMenuFlow.Act.Host:
                    s_transport = step.Method;
                    if (step.Method == Ui.CoopMenuFlow.Code && !UseCodeServer(pick, address)) break;
                    RememberMethod(true, step.Method);
                    TitleChoice = TitleOnline; TitleTransport = step.Method;
                    Plugin.Log.LogInfo("title menu: 创建房间 via " + step.Method + ", to the save slots");
                    Destroy();
                    GoToSlots();
                    break;
                case Ui.CoopMenuFlow.Act.JoinSteam:
                case Ui.CoopMenuFlow.Act.JoinEntry:
                    s_transport = step.Method;
                    if (step.Method == Ui.CoopMenuFlow.Code && !UseCodeServer(pick, address)) break;
                    RememberMethod(false, step.Method);
                    Plugin.Log.LogInfo("title menu: 加入房间 via " + step.Method);
                    OpenJoinEntry();
                    break;
            }
            return true;
        }

        // Back on a page a 加入房间 method opened, on the title (LobbyBack calls this first): the Steam join page and the
        // room-code / IP entry page go back to 加入房间 (the cursor on that method). A Steam invite accepted on the title
        // also lands on the Steam join page; its Back goes there too (the title has no Steam host / join page any more).
        // The old 自建服务器 path (s_titleFlow false, C-server) keeps its own Back (the server's room list).
        static bool TitleFlowBack()
        {
            if (!s_onTitle || !(s_titleFlow || s_transport != "cserver")) return false;
            if (s_page == PageJoin && s_transport == "steam") NetPlay.End("join cancelled");
            else if (!(s_page == PageEntry && !CServerEntry)) return false;
            GoPage(PageJoinMethod, Ui.CoopMenuFlow.BackToMethods(s_transport).Focus);
            return true;
        }

        static bool s_lastRoomKnown;
        static bool ServerModeHasLast { get { return s_lastRoomKnown; } }
        static int ServerModeRoomsRow { get { return ServerModeHasLast ? 1 : 0; } }
        static string[][] ServerModeKeys()
        {
            s_lastRoomKnown = OriCoop.Rooms.RoomsPanel.LastPlayedRoom() != null;
            return s_lastRoomKnown ? ServerModePage : new[] { ServerModePage[1], ServerModePage[2] };
        }

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
                    if (NetPlay.Enabled) Saves.CoopSaveFolder.Enter();
                    else Saves.CoopSaveFolder.Leave();
                    TitleChoice = TitleNone; TitleTransport = ""; s_myRoomCreate = false;
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
            if (s_myRoomCreate && TitleChoice == TitleOnline) return MyRoomCreatePick(ui);   // T-173: 我的房间 -> 新建房间
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
            return BeginTitleHost(ui, TitleTransport, "");
        }

        // server: the C-server for a 我的房间 host ("" = the selected one, CServerConfig.Address).
        internal static int BeginTitleHost(SaveSlotsUI ui, string transport, string server)
        {
            if (!Open(ui)) { LastSlotPick = "orb failed"; return 0; }
            s_fromTitle = true;
            s_transport = transport;
            s_roomEntry = RoomEntryKind.TitleHost;
            LastSlotPick = "title-host-" + transport;
            Plugin.Log.LogInfo("title menu: slot picked, hosting via " + transport);
            GoPage(PageHost, 1);
            if (s_transport == "cserver") BeginCServer(true, (string.IsNullOrEmpty(server) ? OriCoop.Authority.CServerConfig.Address : server) + "|");
            else NetPlay.Begin(true, s_transport, "");
            return 2;
        }

        // T-059: the online room page (OnlineRoomScreen) hides and locks the title orb menu while it is up.
        internal static void RoomPanelVisible(bool visible)
        {
            if (s_mgr == null) return;
            // T-120: with [Start] NativeLobby = false the overlay lobby (OnlineLobby) owns the screen; the native page under it
            // stays hidden and locked until the lobby closes (OnlineBack / proceed), whatever the room screen does meanwhile.
            if (!visible && OnlineLobby.IsOpen && !OverlayUnlockMutated) { HideUnderOverlay(); s_mgr.IsActive = true; return; }
            s_mgr.SetVisible(!visible);
            s_mgr.IsActive = !visible;
            s_mgr.IsLocked = visible;
            if (s_tooltip != null) s_tooltip.gameObject.SetActive(!visible);
        }

        // T-173 review: a NativeGui page drawn over the hidden page (the 我的房间 join / list pages) closed. The page comes
        // back, but locked for a moment: the Esc / B that closed it is still down (and not marked used), and the menu would
        // otherwise take the same Cancel and back out of 我的房间 too. The Driver unlocks it at s_unlockAt.
        internal static void RoomPanelShowLocked(float delay)
        {
            RoomPanelVisible(false);
            if (s_mgr == null || !s_mgr.IsVisible) return;
            s_mgr.IsLocked = true;
            s_unlockAt = Math.Max(s_unlockAt, Time.realtimeSinceStartup + delay);
        }

        // Test-only mutation (T-120): the old behaviour, the room screen's reset shows and unlocks the native page under the overlay.
        internal static bool OverlayUnlockMutated { get { return Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1" && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_OVERLAY_UNLOCK") == "1"; } }

        // The native page under the overlay lobby: hidden, locked, no tooltip (the Driver keeps it locked while the lobby is open).
        static void HideUnderOverlay()
        {
            s_mgr.SetVisible(false); s_mgr.IsLocked = true;
            if (s_tooltip != null) s_tooltip.gameObject.SetActive(false);
        }

        // Rooms already selected and wrote the slot; use the same host page and load action.
        internal static void RoomHostSlot(int slot, string server)
        {
            Destroy();
            TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            ui.CurrentSlotIndex = slot;
            ui.CurrentSaveSlot.ChangeSelectionIndex(-1);
            SaveSlotsManager.CurrentSlotIndex = slot;
            s_fromNewGame = false;
            TitleChoice = TitleOnline;
            TitleTransport = "cserver";
            SaveSlotsManager.BackupIndex = -1;
            BeginTitleHost(ui, "cserver", server);
            ui.Active = false;
        }

        internal static void RoomJoin(string target, MyRoomsPages.Page fromPage)
        {
            if (!TitleOrbOpen)
            {
                Destroy();
                TitleScreenManager.SetScreen(TitleScreenManager.Screen.MainMenu);
                OpenTitleOrb();
            }
            s_transport = "cserver";
            s_titleFlow = false;
            s_joinTarget = target;
            s_roomEntry = RoomEntryKind.MyRoomsJoin;
            s_myRoomsJoinPage = fromPage;
            GoPage(PageJoin, 1);
            BeginCServer(false, target);
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
            if (IsTitleFlowPage) return TitleFlowPressed(index);   // 多人游玩 / 创建房间 / 加入房间
            if (s_page == PageServerMode)
            {
                int row = ServerModeHasLast ? index - 1 : index;
                if (row < 0) OpenRooms(OriCoop.Rooms.RoomsPanel.LastPlayedRoom());
                else if (row == 0) OpenRooms(null);
                else { s_transport = "cserver"; LoadCServer(); GoPage(PageServerManage, 0); }
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
            if (s_onTitle && (s_page == PageHostMethod || s_page == PageJoinMethod))
            {
                GoPage(PageCoop, Ui.CoopMenuFlow.Back(FlowPageOf(s_page)).Focus);
                return true;
            }
            if (s_onTitle && s_page == PageServerMode)
            {
                GoPage(PageCoop, Ui.CoopMenuFlow.RowServer);
                return true;
            }
            if (s_onTitle && s_page == PageRole)
            {
                if (s_transport == "cserver") { GoPage(PageServerList, 0); return true; }
                // The title has no LAN / Steam host / join page any more (only a test run opens one): back to 多人游玩.
                GoPage(PageCoop, Ui.CoopMenuFlow.RowHost);
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
            s_titleFlow = false;
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

        internal static void CancelGuestSaveWait()
        {
            if (!GuestJoin.CancelWaiting()) return;
            s_guestStarted = s_guestPickSlot = false;
            GuestStatus = "cancelled";
            if (TitleOrbOpen) Destroy();
            TitleScreenManager.SetScreen(TitleScreenManager.Screen.MainMenu);
            RestoreTitleMain();
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
                if (TitleOrbOpen)
                {
                    // Back where the join was started: 加入房间 (the redesigned title flow), else the old host / join page.
                    if (s_titleFlow || s_transport != "cserver") GoPage(PageJoinMethod, Ui.CoopMenuFlow.BackToMethods(s_transport).Focus);
                    else GoPage(PageRole, 1);
                }
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
            if (!NetPlay.Enabled) Saves.CoopSaveFolder.Leave();
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
            if (!NetPlay.Enabled) Saves.CoopSaveFolder.Leave();
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
                    SlotRecoveryTick();                      // T-173: the slot list after a failed 新建房间 (NativeRooms.cs)
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

        // Test runs (TitleJoinTest, TitlePrologueTest), on the 多人游玩 page: the row towards tr's host or join. LAN / Steam:
        // 创建房间 / 加入房间 (the caller then presses TitleStartRow there); C-server: 自建服务器, as before the redesign (the
        // caller walks on through 服务器管理 -> 服务器列表 to the server's room list).
        static int TitleEntryRow(string tr, bool host)
        {
            return tr == "cserver" ? Ui.CoopMenuFlow.RowServer : host ? Ui.CoopMenuFlow.RowHost : Ui.CoopMenuFlow.RowJoin;
        }

        // The row that starts the host or the join on that page: 创建房间 / 加入房间 on the C-server room list, else the method.
        static int TitleStartRow(string tr, bool host) { return tr == "cserver" ? (host ? 0 : 1) : Ui.CoopMenuFlow.RowOf(tr); }

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

        // T-171: one row of the C-server room list under 创建房间 / 加入房间: loading, no rooms, failed, or a room ("name  n/4").
        static bool CsListRow(string label)
        {
            List<string> one = new List<string> { label };
            return HasKey(one, "csroom.list.loading") || HasKey(one, "csroom.list.none") || HasKey(one, "csroom.list.failed")
                || System.Text.RegularExpressions.Regex.IsMatch(label, @"\s[0-4]/4$");
        }
        static bool HasKey(List<string> labels, string key) { return labels.Contains(L10n.T(key, true)) || labels.Contains(L10n.T(key, false)) || labels.Contains(L10n.T(key, false).ToUpperInvariant()); }
        static string Labels { get { return LastPage + ": " + string.Join(" | ", LastLabels.ToArray()); } }

        // T-095: the server list page only lists the saved servers (or one "no servers" row); adding / removing is under 服务器管理.
        static bool CServerListLabelsMatch()
        {
            List<string> saved = OriCoop.Authority.CServerConfig.Servers;
            if (LastPage != "serverlist") return false;
            if (saved.Count == 0) return LastLabels.Count == 1 && HasKey(LastLabels, "server.none");
            if (LastLabels.Count != saved.Count) return false;
            for (int i = 0; i < saved.Count; i++) if (LastLabels[i] != saved[i]) return false;
            return true;
        }

        static bool ServerModeLabelsMatch()
        {
            return LastPage == "servermode" && LastLabels.Count == (ServerModeHasLast ? 3 : 2)
                && HasKey(LastLabels, "lobby.rooms") && HasKey(LastLabels, "server.manage")
                && !HasKey(LastLabels, "server.cserver")
                && (!ServerModeHasLast || HasKey(LastLabels, "rooms.last"));
        }

        static bool ServerManageLabelsMatch()
        {
            return LastPage == "servermanage" && LastLabels.Count == 4 && HasKey(LastLabels, "server.add")
                && HasKey(LastLabels, "server.remove") && HasKey(LastLabels, "server.list") && HasKey(LastLabels, "diagnostics.title");
        }

        // From the 自建服务器 page: 服务器管理 -> 服务器列表.
        static IEnumerator OpenServerList()
        {
            yield return s_tHost.StartCoroutine(Row(ServerModeRoomsRow + 1, 0.5f));
            yield return s_tHost.StartCoroutine(Row(2, 0.5f));
        }

        static IEnumerator CheckCServerList(Action<bool, string> check)
        {
            yield return s_tHost.StartCoroutine(Row(ServerModeRoomsRow + 1, 0.5f));
            check(ServerManageLabelsMatch(), "title menu: server management page lists Add / Remove / Server list (" + Labels + ")");
            yield return s_tHost.StartCoroutine(Row(2, 0.5f));
            check(CServerListLabelsMatch(), "title menu: C-server list shows only the saved servers (" + Labels + ")");
            int before = OriCoop.Authority.CServerConfig.Servers.Count;
            yield return s_tHost.StartCoroutine(Back(0.5f));
            yield return s_tHost.StartCoroutine(Row(1, 0.5f));
            check(LastPage == "serverremove" && LastLabels.Count == (before == 0 ? 1 : before) &&
                (before != 0 || HasKey(LastLabels, "server.none")), "title menu: Remove lists saved servers, or the empty row");
            check(GlowsAreRed(), "title menu: remove page glow is red (the selected row's pulse means delete)");
            TShot("title_server_remove");
            yield return s_tHost.StartCoroutine(TWait(0.6f));   // the screenshot is taken at the end of the frame
            yield return s_tHost.StartCoroutine(Back(0.5f));
            check(!GlowsAreRed(), "title menu: glow is back to blue outside the remove page");
            yield return s_tHost.StartCoroutine(Row(0, 0.5f));
            check(LastPage == "serveradd" && LastLabels.Count == 4 && HasKey(LastLabels, "common.ok") && HasKey(new List<string> { LastLabels[2] }, "server.test")
                && s_cserverHost.Length == 0 && s_cserverPort.Length == 0,
                "title menu: Add opens empty address / empty port / Test connection / OK (" + Labels + ")");
            s_cserverHost = "127.0.0.1"; s_cserverPort = "1";
            RefreshTexts(true);
            string added = s_cserverHost + ":" + s_cserverPort;
            yield return s_tHost.StartCoroutine(Row(3, 0.5f));
            check(CServerListLabelsMatch() && LastLabels.Count == before + 1 && LastLabels[before] == added,
                "title menu: OK appends the server and shows the list");
            yield return s_tHost.StartCoroutine(Back(0.5f));
            yield return s_tHost.StartCoroutine(Row(1, 0.5f));
            yield return s_tHost.StartCoroutine(Row(before, 0.5f));
            List<string> afterRemove = OriCoop.Authority.CServerConfig.Servers;
            check(afterRemove.Count == before,
                "title menu: picking Remove deletes just that saved row");
            if (LastPage == "serverremove") yield return s_tHost.StartCoroutine(Back(0.5f));
            yield return s_tHost.StartCoroutine(Row(0, 0.5f));
            s_cserverHost = added.Substring(0, added.LastIndexOf(':')); s_cserverPort = added.Substring(added.LastIndexOf(':') + 1);
            RefreshTexts(true);
            yield return s_tHost.StartCoroutine(Row(3, 0.5f));
            yield return s_tHost.StartCoroutine(Row(before, 0.5f));
            check(LastPage == "role" && LastLabels.Count >= 3 && HasKey(LastLabels.GetRange(0, 2), "csroom.list.create") && OriCoop.Authority.CServerConfig.Address == added,
                "title menu: picking a saved server selects it and opens the room list (T-171)");
        }

        // T-093: the how-to-play chooser (CoopMenu) is laid out from the screen size: readable and proportional at 720p..4K.
        static string ModeSelectScaling()
        {
            foreach (int[] s in new[] { new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } })
            {
                OriCoop.Ui.ModeSelectLayout l = OriCoop.Ui.ModeSelectLayout.Compute(s[0], s[1]);
                if (l.FontPx < s[1] / 45f) return s[0] + "x" + s[1] + ": font " + l.FontPx + " px too small";
                if (l.PanelW / s[0] < 0.3f || l.PanelW > s[0] || l.PanelH > s[1]) return s[0] + "x" + s[1] + ": panel " + l.PanelW + "x" + l.PanelH + " not proportional";
            }
            OriCoop.Ui.ModeSelectLayout hd = OriCoop.Ui.ModeSelectLayout.Compute(1920, 1080), uhd = OriCoop.Ui.ModeSelectLayout.Compute(3840, 2160);
            if (Mathf.Abs(uhd.FontPx - 2 * hd.FontPx) > 2 || Mathf.Abs(uhd.PanelW - 2 * hd.PanelW) > 4) return "4K is not twice 1080p (font " + hd.FontPx + " -> " + uhd.FontPx + ", panel " + hd.PanelW + " -> " + uhd.PanelW + ")";
            return "";
        }

        // T-120: [Start] NativeLobby = false shows host / join as the overlay lobby over a hidden, locked native page. Online play
        // starting or ending (NetPlay.Begin / End reset the room screen) must not bring that page back and unlock it: it would
        // then take the same controller presses as the overlay. (Its room-code grid went with the relay, T-159.)
        static IEnumerator OverlayLobbyChecks(Action<bool, string> check)
        {
            yield return s_tHost.StartCoroutine(PressCoop());
            if (!TitleOrbOpen) { check(false, "title menu: overlay lobby checks need the Co-op page (" + Labels + ")"); yield break; }
            HideUnderOverlay();
            OnlineLobby.Open("direct");
            yield return s_tHost.StartCoroutine(TWait(0.5f));
            OnlineRoomScreen.Reset();                       // NetPlay.Begin does this when the overlay starts hosting / joining
            yield return s_tHost.StartCoroutine(TWait(0.6f));   // long enough for the Driver to unlock a page that lost its lock
            check(!s_mgr.IsVisible && s_mgr.IsLocked, "title menu: overlay lobby: starting online play keeps the native page hidden and locked (visible="
                + s_mgr.IsVisible + ", locked=" + s_mgr.IsLocked + ")");
            OnlineRoomScreen.Show(delegate { });            // the room screen over the overlay, then the session ends
            yield return s_tHost.StartCoroutine(TWait(0.3f));
            NetPlay.End("T-120 overlay check");
            yield return s_tHost.StartCoroutine(TWait(0.6f));
            check(!s_mgr.IsVisible && s_mgr.IsLocked && !OnlineRoomScreen.Visible, "title menu: overlay lobby: ending online play keeps the native page hidden and locked (visible="
                + s_mgr.IsVisible + ", locked=" + s_mgr.IsLocked + ", room screen=" + OnlineRoomScreen.Visible + ")");
            OnlineLobby.Close();
            RoomPanelVisible(false);                        // the lobby closed: the native page comes back (OnlineBack does the same)
            yield return s_tHost.StartCoroutine(TWait(0.6f));
            check(s_mgr.IsVisible && !s_mgr.IsLocked, "title menu: overlay lobby closed: the native page is back and usable");
            yield return s_tHost.StartCoroutine(Back(1f));
        }

        // T-153: the co-op menu resized five times and the P2 / revive text recreated five times must not leave a pile of OS
        // fonts behind (at most one new font each: the menu's current size and the shared tag font).
        static IEnumerator FontLeakCheck(Action<bool, string> check)
        {
            yield return null;
            int before = Resources.FindObjectsOfTypeAll(typeof(Font)).Length;
            foreach (int size in new[] { 21, 24, 27, 30, 21 }) CoopMenu.EnsureFont(size);
            string[] tag = { "Arial", "Segoe UI Symbol", "Microsoft YaHei" };
            Font first = OriCoop.Ui.OsFonts.Shared(tag, 64); bool same = true;
            for (int i = 0; i < 4; i++) same &= OriCoop.Ui.OsFonts.Shared(tag, 64) == first;
            yield return null; yield return null;   // Destroy takes effect at the end of the frame
            int after = Resources.FindObjectsOfTypeAll(typeof(Font)).Length;
            CoopMenu.EnsureFont(Mathf.Max(14, Screen.height / 42));   // back to the menu's own size
            check(after - before <= 2 && same, "title menu: menu resizes and recreated HUD text leave no extra fonts (fonts "
                + before + " -> " + after + ", shared tag font reused=" + same + ")");
        }

        // Menu redesign checks (TitleChecks): intent first, then the connection method; Back walks the same way back with
        // the cursor on the row that was pressed; the method pressed last is where the cursor starts next time.
        static IEnumerator FlowChecks(Action<bool, string> check, Action<string> note, int slot)
        {
            note("--- title menu: 多人游玩 -> 创建房间 / 加入房间 -> connection method");
            yield return s_tHost.StartCoroutine(PressCoop());
            if (!TitleOrbOpen) { check(false, "title menu: Co-op page opens (redesign)"); yield break; }
            check(LastPage == "coop" && LastLabels.Count == 4 && HasKey(LastLabels.GetRange(0, 1), "title.local") && HasKey(LastLabels.GetRange(1, 1), "title.coop.host")
                && HasKey(LastLabels.GetRange(2, 1), "title.coop.join") && HasKey(LastLabels.GetRange(3, 1), "server.title"),
                "title menu: 多人游玩 lists 本地同屏 / 创建房间 / 加入房间 / 自建服务器 (" + Labels + ")");
            bool first = true;
            foreach (string tr in new[] { Ui.CoopMenuFlow.Steam, Ui.CoopMenuFlow.Lan })
            {
                if (!TitleOrbOpen) yield return s_tHost.StartCoroutine(PressCoop());
                if (!TitleOrbOpen) { check(false, "title menu: Co-op page opens again (" + tr + ")"); yield break; }
                int rowPress0 = PressSounds, rowMove0 = MoveSounds;
                yield return s_tHost.StartCoroutine(Row(Ui.CoopMenuFlow.RowJoin, 1f));
                if (first) check(PressSounds == rowPress0 + 1 && MoveSounds == rowMove0 + 1,
                    "sounds: a row press plays the select sound once, the page change itself is silent (press " + (PressSounds - rowPress0) + ", move " + (MoveSounds - rowMove0) + ")");
                first = false;
                check(TitleOrbOpen && LastPage == "joinmethod" && LastLabels.Count == 3 && HasKey(LastLabels.GetRange(0, 1), "title.method.steam.join")
                    && HasKey(LastLabels.GetRange(1, 1), "title.method.code") && HasKey(LastLabels.GetRange(2, 1), "title.method.lan.join") && !SlotsReady,
                    "title menu: 加入房间 lists Steam 邀请 / 房间码 / 局域网, no save slot (" + Labels + ")");
                check(s_mgr.Index == Ui.CoopMenuFlow.RowOf(LastMethod(false)), "title menu: 加入房间 starts on the method used last (" + s_mgr.Index + ", last '" + LastMethod(false) + "')");
                TShot("title_join_methods");
                yield return s_tHost.StartCoroutine(TWait(0.5f));
                yield return s_tHost.StartCoroutine(Row(Ui.CoopMenuFlow.RowOf(tr), 1.2f));
                bool joinPage = tr == "steam" ? LastPage == "joining" && OnSteamJoinPage : LastPage == "entry" && IpMode;
                check(TitleOrbOpen && joinPage && !SlotsReady, "title menu: 加入房间 -> " + tr + " opens its join page on the title, no save slot (" + Labels + ")");
                check(LastMethod(false) == tr, "title menu: 加入房间 remembers " + tr + " (" + LastMethod(false) + ")");
                if (tr != "steam")
                {
                    s_mgr.SetCurrentItem(LastLabels.Count - 1);   // Connect row: its description
                    yield return s_tHost.StartCoroutine(TWait(0.4f));
                    check(LastTooltip.Contains(L10n.T("lobby.save.note", true)) || LastTooltip.Contains(L10n.T("lobby.save.note", false)),
                        "title menu: " + tr + " join page says co-op progress is saved automatically (" + LastTooltip.Replace("\n", " / ") + ")");
                }
                TShot("title_" + tr + "_join");
                yield return s_tHost.StartCoroutine(TWait(0.5f));
                yield return s_tHost.StartCoroutine(Back(1f));
                check(TitleOrbOpen && LastPage == "joinmethod" && s_mgr.Index == Ui.CoopMenuFlow.RowOf(tr) && !NetPlay.Enabled,
                    "title menu: Back on the " + tr + " join page returns to 加入房间, cursor on " + tr + " (" + Labels + " @" + s_mgr.Index + ", net=" + NetPlay.CurrentPhase + ")");
                yield return s_tHost.StartCoroutine(Back(1f));
                check(TitleOrbOpen && LastPage == "coop" && s_mgr.Index == Ui.CoopMenuFlow.RowJoin, "title menu: Back on 加入房间 returns to 多人游玩, cursor on 加入房间 (" + Labels + " @" + s_mgr.Index + ")");

                // 创建房间 -> the method -> the save slots -> the slot orb on the host page.
                yield return s_tHost.StartCoroutine(Row(Ui.CoopMenuFlow.RowHost, 1f));
                check(TitleOrbOpen && LastPage == "hostmethod" && LastLabels.Count == 3 && HasKey(LastLabels.GetRange(0, 1), "title.method.steam")
                    && HasKey(LastLabels.GetRange(1, 1), "title.method.code") && HasKey(LastLabels.GetRange(2, 1), "title.method.lan") && !SlotsReady,
                    "title menu: 创建房间 lists Steam 好友 / 房间码 / 局域网 before any save slot (" + Labels + ")");
                TShot("title_host_methods");
                yield return s_tHost.StartCoroutine(TWait(0.3f));
                yield return s_tHost.StartCoroutine(Back(1f));
                check(TitleOrbOpen && LastPage == "coop" && s_mgr.Index == Ui.CoopMenuFlow.RowHost, "title menu: Back on 创建房间 returns to 多人游玩, cursor on 创建房间 (" + Labels + " @" + s_mgr.Index + ")");
                yield return s_tHost.StartCoroutine(Row(Ui.CoopMenuFlow.RowHost, 1f));
                yield return s_tHost.StartCoroutine(Row(Ui.CoopMenuFlow.RowOf(tr), 0.2f));
                yield return s_tHost.StartCoroutine(WaitFor(() => SlotsReady, 6f));
                yield return s_tHost.StartCoroutine(TWait(0.6f));
                check(SlotsReady && TitleChoice == TitleOnline && TitleTransport == tr && SlotPickPlan == "host:" + tr && LastMethod(true) == tr,
                    "title menu: 创建房间 -> " + tr + " -> save slots (" + SlotPickPlan + ", remembered '" + LastMethod(true) + "')");
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
            }

            // 加入房间 -> 房间码: the entry page with a server, else 服务器管理 with a note.
            yield return s_tHost.StartCoroutine(PressCoop());
            if (!TitleOrbOpen) { check(false, "title menu: Co-op page opens again (room code)"); yield break; }
            yield return s_tHost.StartCoroutine(Row(Ui.CoopMenuFlow.RowJoin, 1f));
            string address;
            Ui.CoopMenuFlow.ServerPick pick = CodeServer(out address);
            s_mgr.SetCurrentItem(Ui.CoopMenuFlow.RowOf(Ui.CoopMenuFlow.Code));
            yield return s_tHost.StartCoroutine(TWait(0.4f));
            check(pick == Ui.CoopMenuFlow.ServerPick.None ? LastTooltip.Contains(L10n.T("title.code.noserver.desc")) : LastTooltip.Contains(address),
                "title menu: the room-code row says which server it uses, or that one must be added (" + LastTooltip.Replace("\n", " / ") + ")");
            yield return s_tHost.StartCoroutine(Row(Ui.CoopMenuFlow.RowOf(Ui.CoopMenuFlow.Code), 1f));
            if (pick == Ui.CoopMenuFlow.ServerPick.None)
            {
                check(TitleOrbOpen && LastPage == "servermanage" && s_mgr.Index == 0 && LastTooltip.Contains(L10n.T("title.code.noserver")),
                    "title menu: 房间码 without a server opens 服务器管理 on 添加服务器 with a note (" + Labels + ", " + LastTooltip + ")");
                yield return s_tHost.StartCoroutine(Back(1f));
                yield return s_tHost.StartCoroutine(Back(1f));
            }
            else
            {
                check(TitleOrbOpen && LastPage == "entry" && s_entryKind == 0 && s_transport == "cserver" && OriCoop.Authority.CServerConfig.Address == address,
                    "title menu: 加入房间 -> 房间码 opens the code dial on " + address + " (" + Labels + ")");
                yield return s_tHost.StartCoroutine(Back(1f));
                check(TitleOrbOpen && LastPage == "joinmethod" && s_mgr.Index == Ui.CoopMenuFlow.RowOf(Ui.CoopMenuFlow.Code),
                    "title menu: Back on the room-code page returns to 加入房间 (" + Labels + " @" + s_mgr.Index + ")");
                yield return s_tHost.StartCoroutine(Back(1f));
            }
            check(TitleOrbOpen && LastPage == "coop", "title menu: back on 多人游玩 after the room-code checks (" + Labels + ")");
            yield return s_tHost.StartCoroutine(Back(1f));
            check(!IsOpen && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, "title menu: Back on 多人游玩 returns to the title items (redesign)");
            yield return s_tHost.StartCoroutine(TWait(0.5f));
        }

        public static IEnumerator TitleChecks(MonoBehaviour host, Action<bool, string> check, Action<string> note, int slot)
        {
            s_tHost = host;
            string scaling = ModeSelectScaling();
            check(scaling.Length == 0, "title menu: how-to-play chooser scales with the resolution (720p to 4K) " + scaling);
            yield return s_tHost.StartCoroutine(FontLeakCheck(check));
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

            // Menu redesign: 多人游玩 -> 创建房间 / 加入房间 -> the connection method (Ui/CoopMenuFlow.cs).
            yield return s_tHost.StartCoroutine(FlowChecks(check, note, slot));

            // The old C-server path: 自建服务器 -> 服务器管理 -> 服务器列表 -> the server's 创建房间 / 加入房间 / room list.
            for (int row = 4; row <= 4; row++)
            {
                string tr = "cserver";
                yield return s_tHost.StartCoroutine(PressCoop());
                if (!TitleOrbOpen) { check(false, "title menu: Co-op page opens again (" + tr + ")"); yield break; }
                int rowPress0 = PressSounds, rowMove0 = MoveSounds;
                yield return s_tHost.StartCoroutine(Row(row == 4 ? 3 : row, 1f));
                if (row == 1) check(PressSounds == rowPress0 + 1 && MoveSounds == rowMove0 + 1,
                    "sounds: a row press plays the select sound once, the page change itself is silent (press " + (PressSounds - rowPress0) + ", move " + (MoveSounds - rowMove0) + ")");
                if (tr == "cserver")
                {
                    check(TitleOrbOpen && ServerModeLabelsMatch() && s_mgr.Index == 0,
                        "title menu: own-server page lists My rooms and Server management (and Last played only with a record), no host mode, default focus on the first row (" + Labels + ")");
                    yield return s_tHost.StartCoroutine(CheckCServerList(check));
                }
                // T-171: the C-server role page is 创建房间 / 加入房间 (rows 0 and 1, the same rows as host / join elsewhere),
                // then the server's room list (loading / no rooms / failed / one row per listed room).
                bool csList = tr == "cserver";
                check(TitleOrbOpen && LastPage == "role" && s_transport == tr && !SlotsReady && (csList
                    ? LastLabels.Count >= 2 && HasKey(LastLabels.GetRange(0, 1), "csroom.list.create") && HasKey(LastLabels.GetRange(1, 1), "csroom.list.join")
                    : HasKey(LastLabels, "lobby.host") && HasKey(LastLabels, "lobby.join")),
                    "title menu: " + tr + " asks host / join before any save slot (" + Labels + ")");
                if (csList) check(LastLabels.Count >= 3 && LastLabels.GetRange(2, LastLabels.Count - 2).TrueForAll(CsListRow),
                    "title menu: selected C-server has Create / Join, then only room list rows (T-171) (" + Labels + ")");
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
                if (tr == "cserver")
                {
                    if (tr == "cserver")
                    {
                        check(CServerListLabelsMatch(), "title menu: Back from C-server Host / Join returns to the list");
                        yield return s_tHost.StartCoroutine(Back(1f));
                        check(ServerManageLabelsMatch(), "title menu: Back from the server list returns to Server management");
                        yield return s_tHost.StartCoroutine(Back(1f));
                    }
                    check(TitleOrbOpen && LastPage == "servermode" && s_mgr.Index == (tr == "cserver" ? ServerModeRoomsRow + 1 : ServerModeRoomsRow),
                        "title menu: " + tr + " Back returns to the mode choice (" + Labels + ")");
                    yield return s_tHost.StartCoroutine(Back(1f));
                }
                check(TitleOrbOpen && LastPage == "coop", "title menu: Back on host / join returns to the Co-op page (" + Labels + ")");
                yield return s_tHost.StartCoroutine(Back(1f));
            }

            yield return s_tHost.StartCoroutine(OverlayLobbyChecks(check));

            // Local co-op: leave the choice set; the harness then picks the slot and the save loads (checked there).
            yield return s_tHost.StartCoroutine(PressCoop());
            if (TitleOrbOpen) { yield return s_tHost.StartCoroutine(Row(0, 0.2f)); yield return s_tHost.StartCoroutine(WaitFor(() => SlotsReady, 6f)); }
            check(SlotsReady && TitleChoice == TitleLocal && SlotPickPlan == "local", "title menu: Local Co-op -> save slots (" + SlotPickPlan + ")");
        }
    }
}
