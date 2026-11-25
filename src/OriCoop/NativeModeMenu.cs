using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CatlikeCoding.TextBox;
using Game;
using HarmonyLib;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // "How do you want to play?" in the game's own menu style, shown right after a save slot is chosen (before the
    // game loads). It is a copy of the game's own difficulty-select screen (SaveSlotUI.DifficultyScreen: a list of
    // CleverMenuItems with the game's font, glow, sounds and controller/keyboard/mouse navigation), relabelled:
    //   Main page:   Single player / Local co-op / Online co-op
    //   Online page: Server room / Direct P2P / Steam friends   -> host / join pages (NativeLobby.cs)
    // The choice is handed to PlayMode (Pending) and applied once gameplay runs; Local co-op then opens the
    // controller setup page. B / Esc goes back (Online -> Main -> back to the save slots).
    //
    // Since the title has Single Player / Co-op (TitleMenu.cs) this page is the fallback for loads without a title
    // choice (Continue, config-driven launches, the test harness); with an online title choice it opens straight on
    // the host / join page (Back there = back to the save slots).
    // Used save: Harmony prefix on SaveSlotsUI.UsedSaveSlotSelected (skipped, re-called after the choice).
    // New game:  prefix on SaveSlotsUI.SetDifficulty (difficulty screen hidden, kept, re-called after the choice).
    //
    // Off when: [Start] NativeModeMenu = false, AskModeOnLoad = false, online (NetPlay), or the test harness runs
    // (unless ORICOOP_MODEMENU_PROBE=1: then the menu drives itself, dumps its object tree and takes screenshots).
    public static partial class NativeModeMenu
    {
        public static int Pending = PlayMode.Undecided;   // read by PlayMode.Tick
        public static bool IsOpen { get { return s_root != null; } }
        public static string LastPage = "";
        public static readonly List<string> LastLabels = new List<string>();
        internal static Func<bool> SuppressAutomaticMenu;

        static BepInEx.Configuration.ConfigEntry<bool> s_enabled;
        static bool s_bypass;
        static GameObject s_root;
        static CleverMenuItemSelectionManager s_mgr;
        static readonly List<CleverMenuItem> s_items = new List<CleverMenuItem>();
        static readonly List<MessageBox[]> s_itemBoxes = new List<MessageBox[]>();
        static int s_page;                 // 0 main, 1 online
        static bool s_fromNewGame;
        static RoomEntryKind s_roomEntry;
        static MyRoomsPages.Page s_myRoomsJoinPage;
        static DifficultyMode s_difficulty;
        static float s_unlockAt;
        static Driver s_driver;

        public static bool Probe { get { return Environment.GetEnvironmentVariable("ORICOOP_MODEMENU_PROBE") == "1"; } }
        static bool LobbyProbeOn { get { return Environment.GetEnvironmentVariable("ORICOOP_LOBBY_PROBE") == "1"; } }

        static bool Enabled
        {
            get
            {
                if (s_enabled == null && Plugin.Instance != null)
                    s_enabled = Plugin.Instance.Config.Bind("Start", "NativeModeMenu", true,
                        "Ask single / local co-op / online in the game's own menu style right after choosing a save slot (before it loads). false = the old overlay screen after the save has loaded.");
                if (Probe || LobbyProbeOn) return true;
                if (s_enabled != null && !s_enabled.Value) return false;
                return PlayMode.AskOnLoad && !(SuppressAutomaticMenu != null && SuppressAutomaticMenu()) && !NetPlay.Enabled;
            }
        }

        // Would picking a save slot show this menu outside a test run? (title regression check)
        public static bool AsksOnSlotPick { get { return (s_enabled == null || s_enabled.Value) && PlayMode.AskOnLoad && !NetPlay.Enabled; } }

        static bool Zh { get { return L10n.Zh; } }

        // Glyphs of the game's Chinese menu font (filled by CheckGlyphs). It has only ~900 characters (no 机, 本, 电脑 ...),
        // so a Chinese string with any missing character is shown in English instead of with gaps.
        static HashSet<int> s_zhGlyphs;
        static string PickResolved(string zh, string en)
        {
            if (!Zh) return en;
            if (s_zhGlyphs == null) return zh;
            foreach (char ch in zh) if (ch > ' ' && !('!' <= ch && ch <= '~') && !s_zhGlyphs.Contains(ch)) return en;
            return zh;
        }

        // Label key, description key.
        static readonly string[][] MainPage = {
            new[] { "mode.single", "mode.single.desc" },
            new[] { "mode.local", "mode.local.desc" },
            new[] { "mode.online", "mode.online.desc" },
        };
        static readonly string[][] OnlinePage = {
            new[] { "server.title", "server.title.desc" },
            new[] { "mode.direct", "mode.direct.desc" },
            new[] { "mode.steam", "mode.steam.desc" },
        };
        static string[][] PageRows
        {
            get
            {
                if (IsLobbyPage)
                {
                    string[][] zh = LobbyRows(true), en = LobbyRows(false);
                    var lobby = new List<string[]>();
                    for (int i = 0; i < zh.Length; i++) lobby.Add(new[] { zh[i][0], zh[i][1], en[i][0], en[i][1] });
                    return lobby.ToArray();
                }
                if (IsTitleFlowPage) return TitleFlowRows();   // 多人游玩 / 创建房间 / 加入房间 on the title (TitleMenu.cs)
                string[][] keys = s_page == 0 ? MainPage : s_page == 1 ? OnlinePage : ServerModeKeys();
                var rows = new List<string[]>();
                foreach (string[] row in keys) rows.Add(Row(row[0], row[1]));
                return rows.ToArray();
            }
        }

        // ---- hooks --------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(SaveSlotsUI), "UsedSaveSlotSelected")]
        static class UsedSlotPatch
        {
            static bool Prefix(SaveSlotsUI __instance)
            {
                if (s_bypass || IsOpen) return true;
                s_fromNewGame = false;
                // A choice made on the title (TitleMenu.cs): load as chosen, the host page, or (a guest with every
                // slot full) the slot that keeps the co-op copy.
                if (TitleChoice != TitleNone || GuestPickingSlot)
                {
                    if (__instance.SelectingDifficulty) { try { __instance.CancelDifficultyScreen(); } catch (Exception) { } }
                    int t = TitleSlotPick(__instance);
                    if (t == 1) return true;
                    if (t >= 2) { __instance.Active = false; return false; }
                }
                if (!Enabled) return true;
                // A leftover difficulty screen (only reachable by scripted input, e.g. the test harness) would draw
                // on top of ours: close it.
                if (__instance.SelectingDifficulty) { try { __instance.CancelDifficultyScreen(); } catch (Exception) { } }
                if (!Open(__instance)) return true;
                // The game's own A-press path sets Active = false right after this call, but a direct call (the test
                // harness) doesn't: the save list would keep reading input under our orb, and an A press on an empty
                // slot opens the vanilla difficulty screen on top of ours. OnBack sets it back to true.
                __instance.Active = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(SaveSlotsUI), "SetDifficulty")]
        static class NewGamePatch
        {
            static bool Prefix(SaveSlotsUI __instance, DifficultyMode difficulty)
            {
                // An online guest never starts a new game: it loads the host's save from the title (GuestJoin.cs).
                if (!IsOpen && GuestJoin.Intercept(__instance)) return false;
                if (s_bypass || IsOpen) return true;
                GameObject diff = Traverse.Create(__instance).Field("m_difficultyScreen").GetValue<GameObject>();
                if (diff == null) return true;
                s_fromNewGame = true;
                s_difficulty = difficulty;
                int t = TitleChoice != TitleNone ? TitleSlotPick(__instance) : 0;
                if (t == 1) return true;
                if (t == 0 && (!Enabled || !Open(__instance))) return true;
                diff.GetComponent<CleverMenuItemSelectionManager>().SetVisible(false);   // hidden, kept (SaveSlotsUI waits on it)
                return false;
            }
        }

        // ---- open / close ---------------------------------------------------------------------------------

        static bool Open(SaveSlotsUI ui)
        {
            SaveSlotUI slot = ui.CurrentSaveSlot;
            if (slot == null || slot.DifficultyScreen == null) { Plugin.Log.LogWarning("mode menu: no difficulty screen to copy; using the old screen"); return false; }
            return OpenOrb(slot.DifficultyScreen, slot, null);
        }

        // Four visible native rows; saved-server pages grow logical rows and scroll the same cloned items.
        const int MaxItems = 4;
        static readonly List<Vector3> s_itemPositions = new List<Vector3>();
        static readonly List<Vector2> s_itemSizes = new List<Vector2>();
        static CleverMenuItemSelectionManager.Direction s_itemDirection;
        static int s_listTop;
        static bool s_onTitle;             // the orb sits on the title (TitleMenu.cs), not on a save slot
        static bool s_settle;              // first unlock after opening: settle the copied screen's highlights

        // slot != null: on that save slot. slot == null: on the title; place() puts it where the title items are.
        static bool OpenOrb(GameObject prefab, SaveSlotUI slot, Action place)
        {
            try
            {
                s_root = (GameObject)InstantiateUtility.Instantiate(prefab);
                s_root.name = "OriCoop_ModeMenu";
                s_mgr = s_root.GetComponent<CleverMenuItemSelectionManager>();
                if (s_mgr == null || s_mgr.MenuItems == null || s_mgr.MenuItems.Count < 3) { Plugin.Log.LogWarning("mode menu: unexpected difficulty screen layout; using the old screen"); Destroy(); return false; }
                s_onTitle = slot == null;
                if (slot != null)
                {
                    Transform parent = slot.HighlightAnimator != null ? slot.HighlightAnimator.transform : slot.transform;
                    s_root.transform.parent = parent;
                    s_root.transform.localScale = Vector3.one * 1.5384f;    // same as SaveSlotsUI.EmptySaveSlotSelected
                    s_root.transform.localPosition = Vector3.zero;
                    HideSlotTexts(slot);
                }
                else if (place != null) place();

                if (Probe || !s_dumped) { s_dumped = true; Plugin.Log.LogInfo("mode menu: difficulty screen tree\n" + Dump(s_root.transform)); }

                // Keep the first MaxItems items, hide the rest; strip the difficulty actions.
                s_items.Clear(); s_itemBoxes.Clear(); s_itemPositions.Clear(); s_itemSizes.Clear();
                s_itemDirection = s_mgr.ItemDirection;
                int keep = Math.Min(MaxItems, s_mgr.MenuItems.Count);
                for (int i = 0; i < s_mgr.MenuItems.Count; i++)
                {
                    CleverMenuItem it = s_mgr.MenuItems[i];
                    if (i >= keep) { if (it != null) it.gameObject.SetActive(false); continue; }
                    it.Pressed = null;
                    it.Activated = null;
                    it.Visible = null;
                    if (it.Highlight != null && LooksLikeText(it.Highlight)) it.Highlight = null;
                    if (it.Unhighlight != null && LooksLikeText(it.Unhighlight)) it.Unhighlight = null;
                    int index = i;
                    it.PressedCallback += () => OnPressed(index);
                    it.gameObject.SetActive(true);
                    s_items.Add(it);
                    s_itemBoxes.Add(it.GetComponentsInChildren<MessageBox>(true));
                    s_itemPositions.Add(s_root.transform.InverseTransformPoint(it.transform.position));
                    s_itemSizes.Add(it.Size);
                }
                s_mgr.MenuItems.RemoveRange(keep, s_mgr.MenuItems.Count - keep);
                s_mgr.Navigation.RemoveAll(n => n.From == null || n.To == null || !s_items.Contains(n.From) || !s_items.Contains(n.To));
                s_mgr.BackAction = null;
                s_mgr.BackItem = null;
                // The move sound: the copied screen's own changeSound (its OptionChangeAction). We play it from
                // OnHighlightChanged for real moves only, not for our page changes (bug #13).
                s_changeSound = s_mgr.OptionChangeAction;
                if (s_changeSound == null) s_changeSound = s_root.GetComponentInChildren<PlaySoundAction>(true);
                s_mgr.OptionChangeAction = null;
                s_soundIndex = -1;
                s_mgr.OnBackPressedCallback = OnBack;
                // The description under the orb is one shared tooltip box (CleverMenuItemTooltipController fills it
                // from each item on every focus change). Our callback runs after it and writes our text instead.
                CleverMenuItemTooltipController tip = s_root.GetComponentInChildren<CleverMenuItemTooltipController>(true);
                s_tooltip = tip != null ? tip.Tooltip : null;
                s_mgr.OptionChangeCallback += OnHighlightChanged;

                CheckGlyphs();                            // before Relabel: PickResolved() needs the Chinese glyph set
                if (s_itemBoxes.Count > 0 && s_itemBoxes[0].Length > 0) FindCursorStyle(s_itemBoxes[0][0]);
                s_page = s_onTitle ? PageCoop : 0;
                Relabel(s_onTitle ? 0 : PlayMode.LastChoice == PlayMode.Single ? 0 : 1);
                s_mgr.SetVisible(true);
                s_mgr.IsLocked = true;                    // the A press that chose the save is still "pressed" this frame
                s_unlockAt = Time.realtimeSinceStartup + 0.35f;
                s_settle = true;
                EnsureDriver();
                Plugin.Log.LogInfo("mode menu: opened (" + (s_onTitle ? "on the title" : s_fromNewGame ? "new game, " + s_difficulty : "save slot " + SaveSlotsManager.CurrentSlotIndex) + ")");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("mode menu: open failed, using the old screen: " + e);
                Destroy();
                return false;
            }
        }

        static bool s_dumped;

        static bool LooksLikeText(ActionMethod a)
        {
            string n = a.GetType().Name;
            return n.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Difficulty", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void Destroy()
        {
            RestoreGlows();
            if (s_root != null)
            {
                if (s_mgr != null) { s_mgr.IsLocked = true; s_mgr.SetVisible(false); }
                InstantiateUtility.Destroy(s_root, 2f);
            }
            s_root = null; s_mgr = null; s_items.Clear(); s_itemBoxes.Clear(); s_baseCurves.Clear();
            s_itemPositions.Clear(); s_itemSizes.Clear(); s_listTop = 0;
            s_fromTitle = false; s_onTitle = false; s_settle = false; s_soundIndex = -1;
            s_roomEntry = RoomEntryKind.None; s_myRoomsJoinPage = MyRoomsPages.Page.None;
            RestoreSlotTexts();
        }

        // The save slot's own lines ("2: Empty Slot", area name, health...) sit under the orb and show through the
        // tooltip's dark backdrop. Hide them while the menu is open; put them back when it closes.
        // The slot also keeps reading Up/Down while highlighted (SaveSlotUI.FixedUpdate): each press opens its
        // backups list ("00:10:54 - Sunken Glades - 2%" lines inside the orb) and moves the backup selection, so
        // moving through our menu could pick an older backup to load. Suspend the slot (FixedUpdate then returns
        // early, the backup the player chose before pressing A is kept) and hide the backups list until we close.
        static readonly List<GameObject> s_hiddenSlotTexts = new List<GameObject>();
        static SaveSlotUI s_suspendedSlot;
        static bool s_slotWasSuspended;

        static void HideSlotTexts(SaveSlotUI slot)
        {
            RestoreSlotTexts();
            foreach (MessageBox b in new[] { slot.EmptySlot, slot.AreaName, slot.Health, slot.Energy, slot.Time, slot.Difficulty })
            {
                if (b == null || !b.gameObject.activeSelf) continue;
                b.gameObject.SetActive(false);
                s_hiddenSlotTexts.Add(b.gameObject);
            }
            if (slot.BackupsAnimator != null && slot.BackupsAnimator.gameObject.activeSelf)
            {
                slot.BackupsAnimator.gameObject.SetActive(false);
                s_hiddenSlotTexts.Add(slot.BackupsAnimator.gameObject);
            }
            s_suspendedSlot = slot;
            s_slotWasSuspended = slot.IsSuspended;
            slot.IsSuspended = true;
            Plugin.Log.LogInfo("mode menu: slot " + slot.SaveSlotIndex + " suspended (backup index " + slot.BackupIndex + "), " + s_hiddenSlotTexts.Count + " slot object(s) hidden");
        }

        static void KeepSlotSuspended()
        {
            if (s_suspendedSlot != null && !s_suspendedSlot.IsSuspended) s_suspendedSlot.IsSuspended = true;
        }

        static void RestoreSlotTexts()
        {
            foreach (GameObject g in s_hiddenSlotTexts) if (g != null) g.SetActive(true);
            s_hiddenSlotTexts.Clear();
            if (s_suspendedSlot != null) s_suspendedSlot.IsSuspended = s_slotWasSuspended;
            s_suspendedSlot = null;
        }

        // ---- pages ----------------------------------------------------------------------------------------

        static void Relabel(int focus)
        {
            LastPage = PageNames[s_page];
            for (int i = 0; i < s_labelCache.Length; i++) { s_labelCache[i] = null; s_descCache[i] = null; }
            string[][] rows = PageRows;
            bool quiet = s_quiet; s_quiet = true;
            try
            {
                ApplyRows(rows);
                s_mgr.SetCurrentItem(Mathf.Clamp(focus, 0, Math.Max(0, Math.Min(s_items.Count, rows.Length) - 1)));
            }
            finally { s_quiet = quiet; }
            UpdateGlowTint();   // T-095: red wisp on the remove page
        }

        static void Set(MessageBox box, string text)
        {
            if (box == null) return;
            try { box.SetMessage(new MessageDescriptor(text)); }
            catch (Exception e) { Plugin.Log.LogWarning("mode menu: set text failed on " + box.name + ": " + e.Message); }
        }

        static MessageBox s_tooltip;
        public static string LastTooltip = "";

        static void OnHighlightChanged()
        {
            if (s_mgr == null) return;
            if (!s_quiet && s_mgr.Index != s_soundIndex && s_soundIndex >= 0) PlayMove();
            s_soundIndex = s_mgr.Index;
            LayoutServerRows(PageRows.Length);
            if (s_tooltip == null) return;
            int i = s_mgr.Index;
            string[][] rows = PageRows;
            if (i < 0 || i >= rows.Length) return;
            string text = TooltipFor(rows, i);
            LastTooltip = text;
            s_tooltip.gameObject.SetActive(true);
            Set(s_tooltip, text);
        }

        static void OnPressed(int index)
        {
            if (s_mgr == null || s_mgr.IsLocked) return;
            PlayPress();
            if (TitlePressed(index)) return;             // title orb pages (TitleMenu.cs)
            if (s_page == 0)
            {
                if (index == 0) Choose(PlayMode.Single);
                else if (index == 1) Choose(PlayMode.Coop);
                else { s_page = 1; Relabel(0); Plugin.Log.LogInfo("mode menu: online page"); }
            }
            else if (IsLobbyPage) LobbyPressed(index);
            else
            {
                if (index == 0) { Saves.CoopSaveFolder.Enter(); GoPage(PageServerMode, 0); return; }
                string transport = index == 1 ? "direct" : "steam";
                Plugin.Log.LogInfo("mode menu: online option " + transport);
                if (UseNativeLobby) { LobbyOpen(transport); return; }
                // [Start] NativeLobby = false: host / join pages are the overlay (OnlineLobby.cs); this native page
                // stays hidden underneath until the lobby proceeds or comes back.
                HideUnderOverlay();
                s_lobbyIndex = index;
                OnlineLobby.Open(transport);
            }
        }

        static void OnBack()
        {
            if (s_mgr == null || s_mgr.IsLocked) return;
            PlayBack();
            if (TitleBack()) return;                     // title orb, or the host page after a title choice
            if (s_page == PageServerMode) { GoPage(1, 0); return; }
            if (IsLobbyPage) { LobbyBack(); return; }
            if (s_page == 1) { s_page = 0; Relabel(2); OnHighlightChanged(); return; }
            BackToSlots();
        }

        // T-176: Reset (also called by End) restores the old orb, so navigate and lock it after End.
        internal static void LeaveRoomPage()
        {
            RoomLeaveDestination destination = TakeRoomLeaveDestination();
            OnlineRoomScreen.Reset();
            NetPlay.End("left room");
            if (OnlineLobby.IsOpen) OnlineLobby.ReturnFromRoom();
            else GoAfterRoom(destination);
        }

        // T-176: the session ended by itself while the room page was up (kicked: CServerLink.EndForGood -> NetPlay.End ->
        // OnlineRoomScreen.Reset): the same way back as leaving, a frame later (End is still running). After LeaveRoomPage
        // the entry is already taken, so this does nothing.
        internal static void RoomPageEndedBySession()
        {
            RoomLeaveDestination destination = TakeRoomLeaveDestination();
            if (destination != RoomLeaveDestination.None && Plugin.Instance != null) Plugin.Instance.StartCoroutine(GoAfterRoomNextFrame(destination));
        }

        static RoomLeaveDestination TakeRoomLeaveDestination()
        {
            RoomLeaveDestination destination = RoomLeaveDestination.None;
            if (IsOpen && (s_page == PageHost || s_page == PageJoin))
                destination = UsesRoomCode ? RoomLeaveFlow.Destination(s_roomEntry) :
                    RoomLeaveFlow.DirectOrSteam(s_page == PageHost, s_transport == "steam",
                        s_page == PageHost ? s_roomEntry == RoomEntryKind.TitleHost : s_onTitle);
            s_roomEntry = RoomEntryKind.None;
            return destination;
        }

        static System.Collections.IEnumerator GoAfterRoomNextFrame(RoomLeaveDestination destination)
        {
            yield return null;
            if (!NetPlay.Enabled && IsOpen) GoAfterRoom(destination);   // not if a new session started or the orb is gone
        }

        static void GoAfterRoom(RoomLeaveDestination destination)
        {
            switch (destination)
            {
                case RoomLeaveDestination.SaveSlots:
                    Core.Input.Cancel.Used = true;           // Esc / B on the page
                    Core.Input.ActionButtonA.Used = true;    // 「离开房间」 pressed with A / Enter: must not pick the slot again
                    SaveSlotsUI ui = SaveSlotsUI.Instance;
                    if (ui != null && ui.SelectingDifficulty) ui.CancelDifficultyScreen();
                    s_fromNewGame = false;
                    BackToSlots();
                    return;
                case RoomLeaveDestination.HostRole:
                case RoomLeaveDestination.RoomList: GoPage(PageRole, 0); break;
                case RoomLeaveDestination.JoinRole: GoPage(PageRole, 1); break;
                case RoomLeaveDestination.DirectAddress: GoPage(PageEntry, 1); break;
                case RoomLeaveDestination.JoinMethods: GoPage(PageJoinMethod, Ui.CoopMenuFlow.BackToMethods("steam").Focus); break;
                case RoomLeaveDestination.CodeEntry: GoPage(PageEntry, 1); break;
                case RoomLeaveDestination.MyRooms:
                    if (Rooms.RoomsPanel.Instance.Screen != "list") Rooms.RoomsPanel.Instance.Back();
                    BackToMyRoomsMenu();
                    if (s_myRoomsJoinPage != MyRoomsPages.Page.None)
                    {
                        MyRoomsPages.Show(s_myRoomsJoinPage, BackToMyRoomsMenu);
                        return;
                    }
                    break;
                default: return;
            }
            RoomPanelShowLocked(MyRoomsPageFlow.NativeUnlockDelay);
        }

        static void BackToSlots()
        {
            Plugin.Log.LogInfo("mode menu: back to the save slots");
            if (!NetPlay.Enabled && TitleChoice != TitleOnline) Saves.CoopSaveFolder.Leave();
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            Destroy();
            if (ui == null) return;
            if (s_fromNewGame)
            {
                GameObject diff = Traverse.Create(ui).Field("m_difficultyScreen").GetValue<GameObject>();
                if (diff != null) diff.GetComponent<CleverMenuItemSelectionManager>().SetVisible(true);
            }
            else ui.Active = true;
        }

        static int s_lobbyIndex;
        // Called by OnlineLobby: online play is running (host ready / guest connected): load the chosen save.
        // A guest's save is replaced by the host's once the session starts (SaveSync), so any slot works.
        internal static bool CanSelectRoomSave
        {
            get
            {
                Rooms.RoomsPanel panel = Rooms.RoomsPanel.Instance;
                bool linked = panel != null && panel.Session != null && panel.Session.Active;
                return Saves.RoomSaveSelection.CanSelect(NetPlay.IsHost, NetPlay.LocalInGameplay(), NetPlay.Connected, linked);
            }
        }
        internal static void SelectRoomSave(int slot)
        {
            if (!CanSelectRoomSave || slot < 0 || slot >= SaveSlotsManager.SaveSlotCount || SaveSlotsUI.Instance == null
                || !System.IO.File.Exists(Rooms.RoomGame.PathFor(slot))) return;
            SaveSlotsUI.Instance.CurrentSlotIndex = slot;
            SaveSlotsManager.CurrentSlotIndex = slot;
            SaveSlotsManager.BackupIndex = -1;
            s_fromNewGame = false;
        }

        public static void OnlineProceed()
        {
            if (s_root == null) return;
            if (OnlineRoomScreen.BeforeProceed(OnlineProceed)) return;
            if (s_onTitle) { TitleGuestProceed(); return; }   // a guest joined from the title: no save slot
            if (NetPlay.Enabled && !s_fromTitle)
            {
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                if (ui != null && s_fromNewGame == SaveSlotsManager.SlotExists(ui.CurrentSlotIndex))
                {
                    // The slot picked before the lobby now belongs to the co-op folder.
                    if (s_fromNewGame) ui.CancelDifficultyScreen();
                    s_fromNewGame = false;
                    if (NetPlay.IsHost) { BackToSlots(); return; }
                    TitleGuestProceed();
                    return;
                }
            }
            Choose(PlayMode.Single);   // locally one Ori; the partner's Ori comes from the network
        }
        // Called by OnlineLobby: Back from its first page: show the native online page again.
        public static void OnlineBack()
        {
            if (s_mgr == null) return;
            if (s_fromTitle) { BackToSlots(); return; }
            s_mgr.SetVisible(true);
            s_page = 1; Relabel(s_lobbyIndex);
            s_mgr.IsLocked = true; s_unlockAt = Time.realtimeSinceStartup + 0.35f;   // the Back press is still down
            OnHighlightChanged();
        }

        static void Choose(int mode)
        {
            Plugin.Log.LogInfo("mode menu: chose " + PlayMode.Name(mode));
            if (!NetPlay.Enabled) Saves.CoopSaveFolder.Leave();
            Pending = mode;
            PlayMode.LastChoice = mode;
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            Destroy();
            if (ui == null) return;
            s_bypass = true;
            try
            {
                if (s_fromNewGame) ui.SetDifficulty(s_difficulty);
                else { ui.UsedSaveSlotSelected(); ui.Active = false; }
            }
            finally { s_bypass = false; }
        }

        // ---- sounds: the same as the game's menus (bug #13) --------------------------------------------------
        // Move: the copied difficulty screen's changeSound. Press / Back: the save-slot screen's select and
        // cancel sounds (what the game plays around its own difficulty screen).
        static ActionMethod s_changeSound;
        static bool s_quiet;               // our own focus changes (page switches, settling) make no sound
        static int s_soundIndex = -1;
        public static int MoveSounds, PressSounds, BackSounds;
        public static string SoundInfo = "";

        static SaveSlotsUI SlotsUIAny
        {
            get
            {
                if (SaveSlotsUI.Instance != null) return SaveSlotsUI.Instance;
                TitleScreenManager tm = TitleManager;
                return tm != null ? tm.SaveSlotsScreen : null;
            }
        }

        static string SoundName(ActionMethod a)
        {
            PlaySoundAction p = a as PlaySoundAction;
            if (p == null) return a == null ? "none" : a.GetType().Name;
            if (p.SoundProvider != null) return p.SoundProvider.name;
            return p.Audio != null && p.Audio.Clip != null ? p.Audio.Clip.name : "?";
        }

        static void LogSounds()
        {
            SaveSlotsUI ui = SlotsUIAny;
            string info = "move=" + SoundName(s_changeSound)
                + " press=" + (ui != null && ui.SelectSound != null ? ui.SelectSound.name : "none")
                + " back=" + (ui != null && ui.CancelDifficultyMenuSound != null ? ui.CancelDifficultyMenuSound.name : "none");
            if (info == SoundInfo) return;
            SoundInfo = info;
            Plugin.Log.LogInfo("mode menu: sounds " + info);
        }

        static void PlayMove()
        {
            if (s_changeSound == null) return;
            try { s_changeSound.Perform(null); MoveSounds++; LogSounds(); }
            catch (Exception e) { Plugin.Log.LogWarning("mode menu: move sound failed: " + e.Message); }
        }

        static void PlayProvider(SoundProvider sp)
        {
            if (sp == null) return;
            Vector3 at = s_root != null ? s_root.transform.position : Vector3.zero;
            Core.Sound.Play(sp.GetSound(null), at, null);
        }

        static void PlayPress()
        {
            try { SaveSlotsUI ui = SlotsUIAny; if (ui == null || ui.SelectSound == null) return; PlayProvider(ui.SelectSound); PressSounds++; LogSounds(); }
            catch (Exception e) { Plugin.Log.LogWarning("mode menu: press sound failed: " + e.Message); }
        }

        static void PlayBack()
        {
            try { SaveSlotsUI ui = SlotsUIAny; if (ui == null || ui.CancelDifficultyMenuSound == null) return; PlayProvider(ui.CancelDifficultyMenuSound); BackSounds++; LogSounds(); }
            catch (Exception e) { Plugin.Log.LogWarning("mode menu: back sound failed: " + e.Message); }
        }

        // ---- glyphs: does the game's menu font have every character we draw? --------------------------------

        public static string MissingGlyphs = "";

        static void CheckGlyphs()
        {
            try
            {
                MessageBox box = null;
                foreach (MessageBox[] bs in s_itemBoxes) if (bs.Length > 0) { box = bs[0]; break; }
                if (box == null || box.LanguageStyles == null) return;
                // Always check the Chinese font (our zh labels) and the current language's font (what is drawn now).
                CheckFont("current (" + GameSettings.Instance.Language + ")", box.LanguageStyles.Current, !Zh, true);
                CheckFont("Chinese", box.LanguageStyles.Chinese, false, false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("mode menu: glyph check failed: " + e.Message); }
        }

        public static string MissingGlyphsZh = "";

        static void CheckFont(string what, TextStyleCollection col, bool english, bool isCurrent)
        {
            if (col == null || col.styles == null || col.styles.Length == 0 || col.styles[0].font == null) { Plugin.Log.LogInfo("mode menu: no " + what + " font"); return; }
            BitmapFont font = col.styles[0].font;
            var have = new HashSet<int>();
            if (font.otherChars != null) foreach (BitmapFontChar c in font.otherChars) have.Add(c.id);
            var missing = new StringBuilder();
            var seen = new HashSet<char>();
            foreach (string[][] page in new[] { MainPage, OnlinePage, TitlePage, TitleHostPage, TitleJoinPage, ServerModePage })
                foreach (string[] row in page)
                    foreach (string key in row)
                        foreach (char ch in L10n.T(key, !english))
                        {
                            if (ch <= ' ' || ('!' <= ch && ch <= '~') || !seen.Add(ch)) continue;
                            if (!have.Contains(ch)) missing.Append(ch);
                        }
            string m = missing.ToString();
            if (isCurrent) MissingGlyphs = m; else { MissingGlyphsZh = m; s_zhGlyphs = have; }
            Plugin.Log.LogInfo("mode menu: " + what + " font '" + font.name + "' (" + (font.otherChars != null ? font.otherChars.Length : 0) + " non-ASCII glyphs)"
                + (m.Length == 0 ? ": all characters present" : ": MISSING " + m));
        }

        // ---- diagnostics ------------------------------------------------------------------------------------

        // T-091: every page definition the title orb, the mode menu (single / local / online) and the server page draw
        // from. None may list the old relay host-mode row (T-045, removed with the relay in T-159); returns the offending page names ("" = none).
        internal static string PagesWithHostMode()
        {
            string bad = "";
            string[] names = { "MainPage", "OnlinePage", "TitlePage", "TitleHostPage", "TitleJoinPage", "ServerModePage" };
            string[][][] pages = { MainPage, OnlinePage, TitlePage, TitleHostPage, TitleJoinPage, ServerModePage };
            for (int i = 0; i < pages.Length; i++)
                foreach (string[] row in pages[i]) foreach (string key in row) if (key == "server.relay" || key == "server.relay.desc") bad += names[i] + " ";
            return bad.Trim();
        }

        static string Dump(Transform t)
        {
            var sb = new StringBuilder();
            DumpRec(t, 0, sb);
            return sb.ToString();
        }

        static void DumpRec(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)").Append(" :");
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                sb.Append(' ').Append(c.GetType().Name);
                MessageBox mb = c as MessageBox;
                if (mb != null)
                {
                    string txt = "";
                    try { txt = mb.MessageProvider != null ? mb.MessageProvider.GetType().Name + ":" + mb.MessageProvider.ToString() : (mb.TextBox != null ? mb.TextBox.DefaultText : ""); } catch (Exception) { }
                    sb.Append("[\"").Append(Shorten(txt)).Append("\"]");
                }
                CleverMenuItem mi = c as CleverMenuItem;
                if (mi != null) sb.Append("[pressed=").Append(mi.Pressed != null ? mi.Pressed.GetType().Name : "-")
                    .Append(" hl=").Append(mi.Highlight != null ? mi.Highlight.GetType().Name : "-").Append("]");
            }
            sb.Append('\n');
            if (depth < 8) foreach (Transform ch in t) DumpRec(ch, depth + 1, sb);
        }

        static string Shorten(string s) { if (s == null) return ""; s = s.Replace("\n", "\\n"); return s.Length > 40 ? s.Substring(0, 40) + "…" : s; }

        // ---- per-frame driver (unlock after open; probe mode) ------------------------------------------------

        static void EnsureDriver()
        {
            if (s_driver != null) return;
            GameObject go = new GameObject("OriCoop_ModeMenuDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_driver = go.AddComponent<Driver>();
        }

        class Driver : MonoBehaviour
        {
            float m_probeT = -1f; int m_probeStep, m_probeRoomsStep, m_probeSub, m_probeRoomsWait;

            void Update()
            {
                if (s_mgr != null && s_mgr.IsLocked && !OnlineLobby.IsOpen && !s_editing && Time.realtimeSinceStartup >= s_unlockAt)
                {
                    s_mgr.IsLocked = false;
                    if (s_settle)
                    {
                        // The copied difficulty screen starts on its 2nd item ("Normal"); that item's own description
                        // can stay lit after the focus moved on its first frame. Settle the highlights once.
                        s_settle = false;
                        for (int i = 0; i < s_items.Count; i++) if (i != s_mgr.Index && s_items[i] != null) s_items[i].OnUnhighlight();
                        s_quiet = true;
                        try { s_mgr.SetCurrentItem(s_mgr.Index); OnHighlightChanged(); } finally { s_quiet = false; }
                    }
                }
                TitleTick();
                KeepSlotSuspended();
                try { LobbyTick(); } catch (Exception e) { Plugin.Log.LogError("mode menu: lobby tick failed: " + e); }
                if (Probe) ProbeTick();
            }

            // Self-driving check: walk every page (online lobby included, without a server: direct host and a
            // join to a dead address), screenshot each, then pick Local co-op so the test run carries on.
            float m_nextAt;
            void ProbeTick()
            {
                if (!IsOpen) { m_probeT = -1f; return; }
                if (m_probeT < 0f) { m_probeT = 0f; m_probeStep = 0; m_nextAt = 1.5f; }
                m_probeT += Time.unscaledDeltaTime;
                if (m_probeT < m_nextAt || (s_mgr.IsLocked && !s_editing)) return;
                float wait = 0.7f;
                switch (m_probeStep)
                {
                    case 0:
                        if (PagesWithHostMode().Length > 0) Plugin.Log.LogError("mode menu probe: host mode is still listed on " + PagesWithHostMode());
                        Shot("modemenu_main"); break;
                    case 1: s_mgr.SetCurrentItem(2); OnHighlightChanged(); break;
                    case 2: Shot("modemenu_main_online_focus"); break;
                    case 3: s_mgr.PressCurrentItem(); break;                      // -> online page
                    case 4:
                        if (LastLabels.Count != 3 || !HasKey(LastLabels, "server.title"))
                            Plugin.Log.LogError("mode menu probe: expected 3 online choices including Server (" + Labels + ")");
                        Shot("modemenu_online"); break;
                    case 5: s_mgr.SetCurrentItem(0); s_mgr.PressCurrentItem(); break;   // Server -> mode
                    case 6:
                        if (m_probeRoomsStep == 0)
                        {
                            if (!ServerModeLabelsMatch() || s_mgr.Index != 0)
                                Plugin.Log.LogError("mode menu probe: expected My rooms / Server management and no host mode, default focus on My rooms (" + Labels + ")");
                            s_mgr.SetCurrentItem(ServerModeRoomsRow); s_mgr.PressCurrentItem();
                        }
                        else if (m_probeRoomsStep == 1)
                        {
                            Rooms.RoomsPanel rp = Rooms.RoomsPanel.Instance;
                            if (rp != null && rp.Busy && m_probeRoomsWait++ < 20) m_probeRoomsStep--;   // the room service is still answering: look again
                            else
                            {
                                m_probeRoomsWait = 0;
                                bool noPopup = typeof(Rooms.RoomsPanel).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) == null;
                                // T-173: 新建房间 / 加入房间 / 房间列表 (only when the player has rooms) / 返回; a service message row may come first.
                                bool hasRooms = rp != null && Rooms.MyRooms.RoomCount > 0;
                                if (LastPage != "roomsmenu" || LastLabels.Count < (hasRooms ? 4 : 3) || !HasKey(LastLabels, "myrooms.create")
                                    || !HasKey(LastLabels, "myrooms.join") || HasKey(LastLabels, "myrooms.list") != hasRooms
                                    || !HasKey(LastLabels, "common.back") || !s_mgr.IsVisible || !s_mgr.IsActive || !noPopup)
                                    Plugin.Log.LogError("mode menu probe: expected native rooms rows (new / join" + (hasRooms ? " / list" : ", no list without rooms")
                                        + " / back) and no popup (" + Labels + ")");
                                else Plugin.Log.LogInfo("mode menu probe: native rooms rows verified (no popup)");
                                Shot("modemenu_rooms"); s_mgr.OnBackPressed();
                            }
                        }
                        else
                        {
                            if (LastPage != "servermode") Plugin.Log.LogError("mode menu probe: expected rooms Back to server mode (" + Labels + ")");
                            m_probeStep = 16;   // T-159: the relay host / join steps (7-16) are gone; on to 服务器管理 (17)
                        }
                        if (++m_probeRoomsStep < 3) m_probeStep--;
                        break;
                    case 17:   // 自建服务器 -> 服务器管理 -> 服务器列表 -> back -> 添加服务器
                        if (m_probeSub == 0) { s_mgr.SetCurrentItem(ServerModeRoomsRow + 1); s_mgr.PressCurrentItem(); }
                        else if (m_probeSub == 1)
                        {
                            if (!ServerManageLabelsMatch()) Plugin.Log.LogError("mode menu probe: expected Add / Remove / Server list (" + Labels + ")");
                            s_mgr.SetCurrentItem(2); s_mgr.PressCurrentItem();
                        }
                        else if (m_probeSub == 2)
                        {
                            if (!CServerListLabelsMatch()) Plugin.Log.LogError("mode menu probe: expected the saved C-servers only (" + Labels + ")");
                            s_mgr.OnBackPressed();
                        }
                        else if (m_probeSub == 3) { s_mgr.SetCurrentItem(1); s_mgr.PressCurrentItem(); wait = 1.5f; }   // Remove: red wisp
                        else if (m_probeSub == 4)
                        {
                            if (LastPage != "serverremove" || !GlowsAreRed()) Plugin.Log.LogError("mode menu probe: expected the remove page with a red wisp (" + Labels + ")");
                            Shot("modemenu_remove");
                        }
                        else if (m_probeSub == 5) s_mgr.OnBackPressed();   // the screenshot is taken at the end of the frame: leave the page one step later
                        else { s_mgr.SetCurrentItem(0); s_mgr.PressCurrentItem(); }
                        if (++m_probeSub < 7) m_probeStep--; else m_probeSub = 0;
                        break;
                    case 18:
                        if (LastPage != "serveradd" || LastLabels.Count != 4 || !HasKey(LastLabels, "common.ok") || s_cserverHost.Length != 0 || s_cserverPort.Length != 0)
                            Plugin.Log.LogError("mode menu probe: expected empty address / empty port / Test connection / OK (" + Labels + ")");
                        s_cserverHost = "127.0.0.1"; s_cserverPort = "1"; s_testState = 0; RefreshTexts(true);
                        s_mgr.SetCurrentItem(2); s_mgr.PressCurrentItem(); wait = 3f; break;   // Test connection (nothing listens there)
                    case 19:
                        if (s_testState != 3) Plugin.Log.LogError("mode menu probe: connection test to a dead address must report failure (state " + s_testState + ")");
                        s_mgr.SetCurrentItem(3); s_mgr.PressCurrentItem(); break;   // OK -> saved, list
                    case 20:
                        if (!CServerListLabelsMatch() || LastLabels[LastLabels.Count - 1] != s_cserverHost + ":" + s_cserverPort)
                            Plugin.Log.LogError("mode menu probe: OK did not append to the C-server list (" + Labels + ")");
                        s_mgr.SetCurrentItem(LastLabels.Count - 1); s_mgr.PressCurrentItem(); break;
                    case 21:
                        if (LastPage != "role" || s_transport != "cserver" || LastLabels.Count != 2 ||
                            !HasKey(LastLabels, "lobby.host") || !HasKey(LastLabels, "lobby.join") ||
                            OriCoop.Authority.CServerConfig.Address != s_cserverHost + ":" + s_cserverPort)
                            Plugin.Log.LogError("mode menu probe: picking a saved C-server must select it and open Host / Join (" + Labels + ")");
                        s_mgr.SetCurrentItem(1); s_mgr.PressCurrentItem(); break;
                    case 22:
                        if (LastPage != "entry" || s_entryKind != 0)
                            Plugin.Log.LogError("mode menu probe: expected cserver room code entry (" + Labels + ")");
                        Shot("lobby_entry_cserver"); s_mgr.OnBackPressed(); break;
                    case 23: s_mgr.OnBackPressed(); break;                        // -> list
                    case 24:                                                      // -> server management, then the 自建服务器 page
                        s_mgr.OnBackPressed();
                        if (m_probeSub++ == 0) m_probeStep--; else m_probeSub = 0;
                        break;
                    case 25: s_mgr.OnBackPressed(); break;                        // -> online
                    case 26: s_mgr.SetCurrentItem(1); s_mgr.PressCurrentItem(); break;   // direct -> role
                    case 27: s_mgr.SetCurrentItem(0); s_mgr.PressCurrentItem(); wait = 2f; break;   // host
                    case 28: Shot("lobby_host_direct"); break;
                    case 29: s_mgr.SetCurrentItem(0); s_mgr.PressCurrentItem(); break;   // copy address
                    case 30: Shot("lobby_host_copied"); Plugin.Log.LogInfo("mode menu probe: clipboard '" + GUIUtility.systemCopyBuffer + "' phase " + NetPlay.CurrentPhase); break;
                    case 31: s_mgr.SetCurrentItem(2); s_mgr.PressCurrentItem(); break;   // cancel -> role
                    case 32: s_mgr.SetCurrentItem(1); s_mgr.PressCurrentItem(); break;   // join -> IP entry
                    case 33: LoadAddress("127.0.0.1:1", 0); RefreshTexts(true); Shot("lobby_entry_ip"); break;
                    case 34: s_mgr.SetCurrentItem(2); s_mgr.PressCurrentItem(); wait = 1.5f; break;   // connect -> joining
                    case 35: Shot("lobby_joining"); break;
                    case 36: s_mgr.OnBackPressed(); break;                        // cancel -> entry
                    case 37: s_mgr.OnBackPressed(); break;                        // -> role
                    case 38: s_mgr.OnBackPressed(); break;                        // -> online
                    case 39: s_mgr.OnBackPressed(); break;                        // -> main
                    case 40: Plugin.Log.LogInfo("mode menu probe: back on page " + LastPage + ", NetPlay " + NetPlay.CurrentPhase); s_mgr.SetCurrentItem(1); break;
                    case 41: s_mgr.PressCurrentItem(); break;                     // local co-op
                    default: return;
                }
                m_probeStep++;
                m_nextAt = m_probeT + wait;
            }

            static void Shot(string name)
            {
                string outFile = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT");
                string dir = string.IsNullOrEmpty(outFile) ? Application.dataPath : Path.GetDirectoryName(Path.GetFullPath(outFile));
                string p = Path.Combine(dir, name + ".png");
                Application.CaptureScreenshot(p);
                Plugin.Log.LogInfo("mode menu probe: screenshot " + p + " page=" + LastPage + " labels=" + string.Join(" | ", LastLabels.ToArray()));
            }
        }
    }
}
