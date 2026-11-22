using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Game;
using OriCoop.Authority;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // T-171: 房间管理页 for C-server rooms (design: docs/design/room-page/codex/room-concept-1.png).
    // OnlineRoomScreen keeps its state machine (Show / Continue / Reset, the callers and tests stay as they are) and hands
    // the drawing and input to this page while the session is a C-server one. Drawn with NativeGui (the game's font and
    // GUI camera) over the title scene; the save orb on the left is a copy of the game's own save slot orb.
    //   title 房间管理 · left (T-173 layout room-v2-a): room name banner ([E] 改名 for the manager), orb, plate with
    //   "1: 沉没林地 - 6%", play time, difficulty, then IP:Port and the invite code · right: 玩家列表 P1-P4 (name, ping, status)
    //   bottom: keycap bar 开始游戏 / 准备 / 换存档 / 踢出 / 离开房间 (every item can also be focused and pressed / clicked;
    //   the keys are in the config, [RoomPage]).
    // T-189: the same page in the game (pause menu -> 房间管理, OpenInGame): no orb, save, ready or start; the room name,
    //   ip:port and the invite code at the left, 玩家列表 at the right, and the bar [开放邀请码] / [复制邀请码] / [踢出] / [返回] /
    //   离开房间 (asked first, then the main menu). Back returns to the pause menu with 房间管理 still selected.
    internal static class CsRoomPage
    {
        static readonly CsRoomModel s_model = new CsRoomModel(delegate (string key) { return L10n.T(key); });
        static bool s_open;
        static float s_ignoreUntil;
        // One press = one action, whether it comes as a key (every frame) or a game button (every fixed step): Ui/PressGate.cs.
        static readonly PressGate s_gate = new PressGate();
        static readonly CsRoomKeyMap s_keys = new CsRoomKeyMap();
        const int GateCancel = -1, GateUp = -2, GateDown = -3, GateLeft = -4, GateRight = -5, GateConfirm = -6;
        static readonly List<GameObject> s_hidden = new List<GameObject>();
        static GameObject s_orb;
        static SaveSlotUI s_orbUi;
        static string s_orbKey = "";
        static ConfigEntry<KeyCode> s_keyStart, s_keyReady, s_keySwitch, s_keyKick, s_keyLeave;
        static ConfigEntry<KeyCode> s_padStart, s_padReady, s_padSwitch, s_padKick;
        static ConfigEntry<KeyCode> s_keyCode, s_padCode;   // T-173: 开放邀请码
        static ConfigEntry<KeyCode> s_keyCopy, s_padCopy;   // T-189: 复制邀请码 (in the game)
        static bool s_inGame, s_fromPause;   // T-189: opened from the pause menu (PauseMenuEntry held it)
        static float s_copiedUntil;          // T-189: "已复制" next to the code until then
        static string s_copied = "";         // T-189: what the last 复制邀请码 put on the clipboard (for the harness)
        static readonly bool s_testRun = Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN"));
        // T-173 (room-v2-a): [E] 改名. While the name is being typed every other key / button / click of the page is ignored.
        static ConfigEntry<KeyCode> s_keyRename;
        static readonly RoomNameEdit s_edit = new RoomNameEdit();
        const int GateRename = -7, GateEditCancel = -8, GateEditConfirm = -9;
        static Rect s_renameRect;   // the "[E] 改名" hint as drawn (empty when it is not)
        static bool s_mineRoom;     // a 我的房间 room was seen since the page opened: its invite code is private, never the live code
        // The rename key that started the edit is still down: its key repeat (Input.inputString) is not typed.
        static bool s_renameKeyHeld;
        static readonly List<Rect> s_barRects = new List<Rect>();
        static string s_barIcons = "";   // for tests: the icon markup of the bar as last drawn
        static readonly Rect[] s_rowRects = new Rect[5];
        internal static string LastAction = "";   // for the harness

        public static bool Active { get { return CsLobby.Active; } }
        public static bool IsOpen { get { return s_open; } }
        // T-189: the page is open in the game (from the pause menu), not on the title.
        public static bool InGame { get { return s_open && s_inGame; } }
        // T-189: the pause menu offers 房间管理 now (Ui/InGameRoomRules: a linked C-server session in the game).
        public static bool InGameAvailable
        {
            get { return InGameRoomRules.ShowPauseEntry(NetPlay.Enabled, NetPlay.TransportName, CServerLink.Client != null, NetPlay.LocalInGameplay()); }
        }
        internal static CsRoomModel Model { get { return s_model; } }

        static void BindKeys()
        {
            if (s_keyStart != null || Plugin.Instance == null) return;
            ConfigFile c = Plugin.Instance.Config;
            s_keyStart = c.Bind("RoomPage", "StartKey", KeyCode.Space, "Server room page: start the game (the room's creator, once everyone is ready).");
            s_keyReady = c.Bind("RoomPage", "ReadyKey", KeyCode.R, "Server room page: ready / not ready.");
            s_keySwitch = c.Bind("RoomPage", "ChangeSaveKey", KeyCode.C, "Server room page: change the room's save (the room's creator).");
            s_keyKick = c.Bind("RoomPage", "KickKey", KeyCode.Delete, "Server room page: remove the selected player from the room (the room's creator).");
            s_keyLeave = c.Bind("RoomPage", "LeaveKey", KeyCode.Escape, "Server room page: leave the room.");
            s_padStart = c.Bind("RoomPage", "StartButton", KeyCode.JoystickButton7, "Server room page, controller: start the game (Menu / Start).");
            s_padReady = c.Bind("RoomPage", "ReadyButton", KeyCode.JoystickButton2, "Server room page, controller: ready (X).");
            s_padSwitch = c.Bind("RoomPage", "ChangeSaveButton", KeyCode.JoystickButton3, "Server room page, controller: change the save (Y).");
            s_padKick = c.Bind("RoomPage", "KickButton", KeyCode.JoystickButton6, "Server room page, controller: kick the selected player (View / Back).");
            s_keyCode = c.Bind("RoomPage", "OpenCodeKey", KeyCode.O, "My room page: open the invite code for 5 minutes.");
            s_padCode = c.Bind("RoomPage", "OpenCodeButton", KeyCode.JoystickButton3, "My room page, controller: open the invite code (Y).");
            s_keyCopy = c.Bind("RoomPage", "CopyCodeKey", KeyCode.C, "In-game room page: copy the invite code to the clipboard.");
            s_padCopy = c.Bind("RoomPage", "CopyCodeButton", KeyCode.JoystickButton2, "In-game room page, controller: copy the invite code (X).");
            s_keyRename = c.Bind("RoomPage", "RenameKey", KeyCode.E, "Server room page: rename the room (the room's manager; English letters only). Enter confirms, Esc cancels.");
        }

        // ---- open / close --------------------------------------------------------------------------------

        public static void Open()
        {
            if (s_open) return;
            BindKeys();
            s_open = true;
            s_ignoreUntil = Time.unscaledTime + 0.3f;
            s_gate.Block(Time.unscaledTime, 0.3f);   // the press that opened the page is still down
            s_model.ResetFocus();
            s_edit.Cancel();
            s_renameRect = new Rect();
            s_mineRoom = Rooms.MyRooms.InRoom;
            if (Rooms.MyRooms.InRoom && Rooms.MyRooms.RoomName.Length > 0) CsLobby.SetRoomName(Rooms.MyRooms.RoomName);   // T-173
            // The room's save is this PC's slot only when this PC created the room (CServerClient.Created). NetPlay.IsHost is
            // the arbiter (the lowest online seat), which a late joiner on a freed lower seat also is.
            CsLobby.PageOpened(SlotSummary(PickedSlot));
            HideSlotScreen();
            Plugin.Log.LogInfo("cs room: page open (" + (CsLobby.CreatedRoom ? "creator, slot " + (PickedSlot + 1) : "joined") + ")");
        }

        // T-189: pause menu -> 房间管理. The pause menu stays logically open and hidden (PauseMenuEntry.Suspend, as 多人游戏设置
        // does); Close gives it back with 房间管理 still selected.
        public static void OpenInGame()
        {
            if (s_open || !InGameAvailable) return;
            BindKeys();
            s_open = true; s_inGame = true;
            s_fromPause = PauseMenuEntry.Suspend();
            s_ignoreUntil = Time.unscaledTime + 0.3f;
            s_gate.Block(Time.unscaledTime, 0.3f);   // the press that picked 房间管理 is still down
            s_model.ResetFocus();
            s_edit.Cancel();
            s_renameRect = new Rect();
            s_copiedUntil = 0f;
            s_mineRoom = Rooms.MyRooms.InRoom;
            if (Rooms.MyRooms.InRoom && Rooms.MyRooms.RoomName.Length > 0) CsLobby.SetRoomName(Rooms.MyRooms.RoomName);
            Plugin.Log.LogInfo("cs room: in-game page open (" + (Rooms.MyRooms.InRoom ? "my room" : "server room " + (NetPlay.RoomCode ?? "")) + ", pause held " + s_fromPause + ")");
        }

        public static void Close()
        {
            if (!s_open) return;
            s_open = false;
            s_edit.Cancel();
            if (s_inGame)
            {
                s_inGame = false;
                s_model.InGame = false;
                NativeGui.BeginPage("cs.room"); NativeGui.EndPage();
                if (s_fromPause) PauseMenuEntry.Return();
                s_fromPause = false;
                Plugin.Log.LogInfo("cs room: in-game page closed");
                return;
            }
            DestroyOrb();
            foreach (GameObject g in s_hidden) if (g != null) g.SetActive(true);
            s_hidden.Clear();
            NativeGui.BeginPage("cs.room"); NativeGui.EndPage();   // nothing drawn this frame: NativeGui hides the page's elements
            Plugin.Log.LogInfo("cs room: page closed");
        }

        // The save slot screen's own orbs and key legend sit where the page draws; hide them while it is open.
        static void HideSlotScreen()
        {
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            if (ui != null && ui.ItemsUI != null) Hide(ui.ItemsUI.gameObject);
            if (ui != null && ui.CopyLegend != null) Hide(ui.CopyLegend.gameObject);
            // titleScreenSwallowsNest/ui/group: "6. saveSlots/legend" (Select / Backups / Delete / Back) and the Ori logo
            // would show through the page's title and key bar.
            SaveSlotUI any = AnySlot();
            Transform group = any != null ? any.transform.root.Find("ui/group") : null;
            if (group == null) return;
            foreach (string path in new[] { "6. saveSlots/legend", "oriLogo", "definitiveEdition", "chinaTitle" })
            {
                Transform t = group.Find(path);
                if (t != null) Hide(t.gameObject);
            }
        }

        static void Hide(GameObject g)
        {
            if (g == null || !g.activeSelf) return;
            g.SetActive(false); s_hidden.Add(g);
        }

        // ---- the room's save ------------------------------------------------------------------------------

        static string SlotSummary(int slot)
        {
            CsRoomSummary s = new CsRoomSummary { Slot = slot, Name = CsLobby.RoomName };
            SaveSlotInfo info = slot >= 0 && slot < SaveSlotsManager.SaveSlotCount ? SaveSlotsManager.SlotByIndex(slot) : null;
            if (info != null)
            {
                s.Area = info.AreaName ?? ""; s.Completion = info.Completion; s.Seconds = info.TotalSeconds; s.Difficulty = (int)info.Difficulty;
            }
            return s.Encode();
        }

        // The save slot this room plays. On the title the slot screen's pick (SaveSlotsUI.CurrentSlotIndex): the title host
        // flow skips the game's UsedSaveSlotSelected until the game really starts, so SaveSlotsManager.CurrentSlotIndex still
        // holds an older slot there (the page showed the wrong save, and 我的房间, which sets both, copied the orb with the host
        // page menu attached). In gameplay SaveSlotsManager is the authority.
        internal static int PickedSlot
        {
            get
            {
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                if (ui != null && !NetPlay.LocalInGameplay() && ui.CurrentSlotIndex >= 0 && ui.CurrentSlotIndex < SaveSlotsManager.SaveSlotCount)
                    return ui.CurrentSlotIndex;
                return SaveSlotsManager.CurrentSlotIndex;
            }
        }

        static void SwitchSave()
        {
            if (!NativeModeMenu.CanSelectRoomSave) return;
            int count = SaveSlotsManager.SaveSlotCount, current = PickedSlot;
            for (int step = 1; step <= count; step++)
            {
                int slot = (current + step) % count;
                if (SaveSlotsManager.SlotByIndex(slot) == null || !System.IO.File.Exists(Rooms.RoomGame.PathFor(slot))) continue;
                NativeModeMenu.SelectRoomSave(slot);
                if (SaveSlotsManager.CurrentSlotIndex != slot) return;
                CsLobby.LocalSummary = SlotSummary(slot);
                CsLobby.Changed();
                Plugin.Log.LogInfo("cs room: save slot " + (slot + 1));
                return;
            }
        }

        // ---- input ---------------------------------------------------------------------------------------

        static int Code(ConfigEntry<KeyCode> key) { return key != null ? (int)key.Value : 0; }

        // The shortcut keys from the config, in priority order. A key may carry several actions (换存档 and 开放邀请码 share
        // Y): the press goes to the first one the bar has enabled (CsRoomModel.ShortcutFirst).
        static void MapKeys()
        {
            s_keys.Clear();
            if (s_inGame)
            {
                // T-189: [SPACE] / Y 开放邀请码, [C] / X 复制邀请码, [DEL] / View 踢出; Esc / B is 返回 (the cancel edge in Tick).
                s_keys.Bind(CsRoomAction.OpenCode, Code(s_keyStart)); s_keys.Bind(CsRoomAction.OpenCode, Code(s_keyCode));
                s_keys.Bind(CsRoomAction.OpenCode, Code(s_padCode));
                s_keys.Bind(CsRoomAction.CopyCode, Code(s_keyCopy)); s_keys.Bind(CsRoomAction.CopyCode, Code(s_padCopy));
                s_keys.Bind(CsRoomAction.Kick, Code(s_keyKick)); s_keys.Bind(CsRoomAction.Kick, Code(s_padKick));
                return;
            }
            s_keys.Bind(CsRoomAction.Start, Code(s_keyStart)); s_keys.Bind(CsRoomAction.Start, Code(s_padStart));
            s_keys.Bind(CsRoomAction.Ready, Code(s_keyReady)); s_keys.Bind(CsRoomAction.Ready, Code(s_padReady));
            s_keys.Bind(CsRoomAction.SwitchSave, Code(s_keySwitch)); s_keys.Bind(CsRoomAction.SwitchSave, Code(s_padSwitch));
            s_keys.Bind(CsRoomAction.Kick, Code(s_keyKick)); s_keys.Bind(CsRoomAction.Kick, Code(s_padKick));
            s_keys.Bind(CsRoomAction.OpenCode, Code(s_keyCode)); s_keys.Bind(CsRoomAction.OpenCode, Code(s_padCode));
            s_keys.Bind(CsRoomAction.Leave, Code(s_keyLeave));
        }

        public static void Tick()
        {
            if (!s_open) return;
            // T-189: the session ended, the game left gameplay or the pause menu went away under the page: close it.
            if (s_inGame && (!InGameAvailable || UI.Menu == null || !UI.Menu.MainMenuVisible)) { Close(); return; }
            Fill();
            MapKeys();
            float now = Time.unscaledTime;
            // Every input goes through the gate every frame (also while it is blocked), so a press is seen from its start
            // and fires once (Ui/PressGate.cs). At most one action per frame: shortcuts first, then back / arrows / confirm.
            CsRoomAction action = CsRoomAction.None;
            bool picked = false;
            foreach (int key in s_keys.Keys())
            {
                if (!s_gate.Edge(key, RoomUiDraw.KeyHeld((KeyCode)key), now) || picked) continue;   // pad buttons via the game's input
                action = s_model.ShortcutFirst(s_keys.ActionsFor(key));
                picked = true;
            }
            bool leaveKey = s_keyLeave != null && s_keyLeave.Value != KeyCode.None && Input.GetKey(s_keyLeave.Value);
            bool cancel = s_gate.Edge(GateCancel, Input.GetKey(KeyCode.Escape) || leaveKey, Core.Input.Cancel.Pressed, now);
            bool up = s_gate.Edge(GateUp, Input.GetKey(KeyCode.UpArrow), Core.Input.MenuUp.Pressed, now);
            bool down = s_gate.Edge(GateDown, Input.GetKey(KeyCode.DownArrow), Core.Input.MenuDown.Pressed, now);
            bool left = s_gate.Edge(GateLeft, Input.GetKey(KeyCode.LeftArrow), Core.Input.MenuLeft.Pressed, now);
            bool right = s_gate.Edge(GateRight, Input.GetKey(KeyCode.RightArrow), Core.Input.MenuRight.Pressed, now);
            bool confirm = s_gate.Edge(GateConfirm, Input.GetKey(KeyCode.Return), Core.Input.ActionButtonA.Pressed, now);
            // T-173 (room-v2-a): the rename key, and Esc / Enter for the name being typed (keyboard keys only).
            bool rename = s_gate.Edge(GateRename, s_keyRename != null && s_keyRename.Value != KeyCode.None && Input.GetKey(s_keyRename.Value), now);
            bool editCancel = s_gate.Edge(GateEditCancel, Input.GetKey(KeyCode.Escape), now);
            bool editConfirm = s_gate.Edge(GateEditConfirm, Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter), now);
            if (s_edit.Editing) { TickEdit(editCancel, editConfirm, now); return; }   // nothing else acts while typing
            if (now < s_ignoreUntil) return;
            if (!picked && rename && MayRename) { BeginRename(); return; }   // this frame's typed "e" is not taken
            if (!picked)
            {
                if (cancel) action = s_model.Shortcut(s_inGame ? CsRoomAction.Back : CsRoomAction.Leave);   // T-189: Esc / B = 返回 in the game
                else if (up) s_model.Up();
                else if (down) s_model.Down();
                else if (left) s_model.Left();
                else if (right) s_model.Right();
                else if (confirm) action = s_model.Press();
            }
            Run(action);
        }

        internal static void Run(CsRoomAction action)
        {
            if (action == CsRoomAction.None) return;
            LastAction = action.ToString();
            Plugin.Log.LogInfo("cs room: " + action + (action == CsRoomAction.Kick ? " P" + s_model.KickSeat : ""));
            switch (action)
            {
                case CsRoomAction.Ready: CsLobby.SetReady(!CsLobby.LocalReady); break;
                case CsRoomAction.SwitchSave: SwitchSave(); break;
                case CsRoomAction.Kick:
                    int seat = s_model.KickSeat;   // the focused player row, or with the focus on the bar the picked one
                    if (seat < 1) break;
                    if (CServerLink.Client != null) CServerLink.Client.Kick(seat);
                    CsLobby.Forget(seat);
                    break;
                case CsRoomAction.Start:
                    // A pending 我的房间 room is only really created by this start (T-173): confirm it (the code closes).
                    if (s_model.Persistent && !s_model.Confirmed && CsLobby.CreatedRoom) Rooms.MyRooms.Confirm();   // only the creator confirms
                    if (!s_model.GameRunning && !s_model.Confirmed) CsLobby.SendStart();   // confirmed rooms: each player goes in alone
                    OnlineRoomScreen.Continue();
                    break;
                case CsRoomAction.OpenCode: Rooms.MyRooms.OpenCode(); break;
                case CsRoomAction.CopyCode: CopyCode(); break;
                case CsRoomAction.Back:
                    if (s_model.LeaveAsked) s_model.CancelLeave();   // 取消 on the leave question
                    else Close();                                    // back to the pause menu
                    break;
                case CsRoomAction.ConfirmLeave: LeaveGame(); break;
                case CsRoomAction.Leave:
                    if (s_inGame) { s_model.AskLeave(); break; }   // T-189: asked first in the game
                    if (s_model.Persistent && !s_model.Confirmed && s_model.IsManager) Rooms.MyRooms.AbandonPending();
                    NativeModeMenu.LeaveRoomPage();
                    break;
            }
        }

        // T-189: the shown code (a 我的房间 room's open invite code, or the server-list room's live code) onto the clipboard.
        // Only the code, as the join page's box takes it.
        static void CopyCode()
        {
            string code;
            CodeLine(out code);
            if (code.Length == 0) return;
            // Test runs never write the real clipboard: it belongs to the window station, shared with the owner's desktop
            // (docs/testing-rules.md: tests must not disturb the owner). They record the copy only.
            try { if (!s_testRun) GUIUtility.systemCopyBuffer = code; s_copied = code; s_copiedUntil = Time.unscaledTime + 2f; }
            catch (Exception e) { Plugin.Log.LogWarning("cs room: copy failed: " + e.Message); }
        }

        // T-189: 离开房间 confirmed on the in-game page: the pause menu's own quit (GameController.RestartGame, what the game's
        // ReturnToTitleScreenAction does). Returning to the title ends the online session (TitleReturn); a 我的房间 room stays
        // in 我的房间 on the room service.
        static void LeaveGame()
        {
            Close();
            Plugin.Log.LogInfo("cs room: left the room from the game: back to the main menu");
            LeftFromGame++;
            if (GameController.Instance != null) GameController.Instance.RestartGame();
        }
        internal static int LeftFromGame;   // for the harness

        // Harness stand-in for the old "continue" button: ready this PC; the room's manager also starts once everyone is
        // ready (or joins a game already running). true once this PC has gone into the game.
        internal static bool TestContinue()
        {
            if (!s_open) return false;
            Fill();
            if (!CsLobby.LocalReady) Run(CsRoomAction.Ready);
            Fill();
            CsRoomAction start = s_model.Shortcut(CsRoomAction.Start);
            if (start == CsRoomAction.None) return false;
            Run(start);
            return true;
        }

        // ---- rename (T-173, room-v2-a) ------------------------------------------------------------------

        // The rename key or a click on the "[E] 改名" hint: start typing, with the current name in the box.
        static bool BeginRename()
        {
            if (!s_open || !MayRename) return false;
            s_edit.Begin(RoomName());
            s_renameKeyHeld = s_keyRename != null && s_keyRename.Value != KeyCode.None && Input.GetKey(s_keyRename.Value);
            LastAction = "Rename";
            Plugin.Log.LogInfo("cs room: rename started");
            return true;
        }

        // While the name is typed: printable ASCII goes in (Ui/RoomNameEdit keeps what a room name may hold), Backspace
        // deletes, Enter confirms, Esc cancels. Losing the right to rename (another PC manages the room now, a game started)
        // cancels.
        static void TickEdit(bool cancel, bool confirm, float now)
        {
            if (!MayRename || cancel) { EndEdit(now, false); return; }
            // The rename key held past the OS key-repeat delay repeats its character: not typed until it is let go.
            if (s_renameKeyHeld && (s_keyRename == null || !Input.GetKey(s_keyRename.Value))) s_renameKeyHeld = false;
            int held = s_renameKeyHeld ? (int)s_keyRename.Value : -1;   // a printable key's KeyCode is its lowercase ASCII
            foreach (char c in Input.inputString)
            {
                if (held >= 32 && held <= 126 && char.ToLowerInvariant(c) == (char)held) continue;
                if (c == '\b') s_edit.Backspace();
                else if (c == '\n' || c == '\r') { EndEdit(now, true); return; }
                else s_edit.Type(c.ToString());
            }
            if (confirm) EndEdit(now, true);
        }

        // The edit ended: the press that ended it (and anything else down now) must not also act on the page.
        static void EndEdit(float now, bool confirm)
        {
            string name = null;
            if (confirm) name = s_edit.Confirm();
            else s_edit.Cancel();
            s_ignoreUntil = Math.Max(s_ignoreUntil, now + MyRoomsPageFlow.EditEndIgnore);
            s_gate.Block(now, MyRoomsPageFlow.EditEndIgnore);
            Plugin.Log.LogInfo("cs room: rename " + (name != null ? "confirmed" : "kept the old name"));
            if (name != null) ApplyRename(name);
        }

        // A confirmed new name (cleaned, not the old one): the C-server room's save line carries it to every PC (LobbyState)
        // and the server's room info; a 我的房间 room is also renamed on the room service.
        // The model is as this frame's Tick (or TestRename) filled it: no Fill here, it could queue a room-service refresh
        // just before the rename. The room service refusing a 我的房间 rename puts its name back on the C-server side too.
        static void ApplyRename(string name)
        {
            if (!MayRename || string.IsNullOrEmpty(name)) return;
            string old = RoomName();
            if (!CsLobby.Rename(name)) return;
            if (Rooms.MyRooms.InRoom)
                Rooms.MyRooms.Rename(name, delegate (bool ok)
                {
                    if (ok || old.Length == 0 || !CsLobby.Active || !CsLobby.IsManager) return;
                    Plugin.Log.LogWarning("cs room: the room service kept the name '" + old + "': the room's name goes back to it");
                    CsLobby.Rename(old);
                });
            Plugin.Log.LogInfo("cs room: room renamed to '" + name + "'");
        }

        // [E] 改名 is offered: this PC manages the room, no game is running (CsRoomModel.CanRename) and it has the room's
        // save line that carries the name to the other PCs (CsLobby.CanRename).
        static bool MayRename { get { return s_model.CanRename && CsLobby.CanRename; } }

        // The room's English name: a 我的房间 room's name on the room service, else the name in the room's save line (the
        // creator's own name for it on the creator's PC). "" = not known yet.
        static string RoomName()
        {
            if (Rooms.MyRooms.InRoom)
            {
                string mine = RoomNames.Clean(Rooms.MyRooms.RoomName);
                if (mine.Length > 0) return mine;
            }
            string name = CsRoomSummary.Parse(CsLobby.RoomSummary).Name;
            if (name.Length == 0 && CsLobby.Active && CsLobby.CreatedRoom) name = RoomNames.Clean(CsLobby.RoomName);
            return name;
        }

        // The C-server this PC plays on, host:port.
        static string IpPort() { return CServerConfig.Address ?? ""; }

        // The invite line under the orb and the code in it ("" = none shown). A 我的房间 room: its room-service invite code
        // (private: never the C-server's live code), with the time left once reopened, or 未开放. A C-server room from the
        // server list: the live room code other players type under 加入房间.
        static string CodeLine(out string code) { return CodeLine(out code, false); }

        // T-189: the text itself is Ui/InGameRoomRules.CodeLine (unit-tested); full = the in-game page's 房间已满.
        static string CodeLine(out string code, bool full)
        {
            bool mine = s_mineRoom || Rooms.MyRooms.InRoom;
            if (mine) s_mineRoom = true;
            code = mine ? (Rooms.MyRooms.InRoom ? Rooms.MyRooms.Code : "") : NetPlay.RoomCode ?? "";
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            return InGameRoomRules.CodeLine(delegate (string key) { return L10n.T(key); }, mine, code, mine ? Rooms.MyRooms.CodeExpires : 0, now, full);
        }

        // ---- harness hooks (read-only, plus TestRename through the real confirm path) -------------------------

        internal static string NameShown { get { return s_open ? RoomName() : ""; } }
        internal static string IpPortShown { get { return s_open ? IpPort() : ""; } }
        internal static string CodeShown { get { string code; if (!s_open) return ""; CodeLine(out code); return code; } }
        internal static string CodeLineShown { get { string code; return s_open ? CodeLine(out code) : ""; } }
        internal static bool CanRenameNow { get { if (!s_open) return false; Fill(); return MayRename; } }
        internal static int BarItems { get { return s_open ? s_barRects.Count : 0; } }
        internal static string BarIconMarkup { get { return s_open ? s_barIcons : ""; } }   // the drawn bar's icons, "|"-joined
        internal static bool RenameHintShown { get { return s_open && s_renameRect.width > 0f; } }
        internal static bool Renaming { get { return s_edit.Editing; } }
        internal static string Copied { get { return s_copied; } }   // T-189: the last code put on the clipboard
        internal static bool CopiedShown { get { return s_open && Time.unscaledTime < s_copiedUntil; } }
        // T-189: the bar's actions as drawn now, comma-joined (disabled ones marked "(off)").
        internal static string BarActions
        {
            get
            {
                if (!s_open) return "";
                Fill();
                List<string> items = new List<string>();
                foreach (CsRoomBarItem item in s_model.Bar()) items.Add(item.Action + (item.Enabled ? "" : "(off)"));
                return string.Join(",", items.ToArray());
            }
        }

        // Rename as a player does: start the edit, clear the box, type the name, confirm (EndEdit -> ApplyRename).
        // true once this page shows the new name.
        internal static bool TestRename(string name)
        {
            if (!s_open) return false;
            Fill();
            if (!BeginRename()) return false;
            while (s_edit.Text.Length > 0) s_edit.Backspace();
            s_edit.Type(name);
            EndEdit(Time.unscaledTime, true);
            return RoomName() == RoomNames.Clean(name);
        }

        // The model's seats from the C-server roster and the players' lobby states.
        static void Fill()
        {
            // T-173: a 我的房间 room: pending (T-171 rules) or confirmed (进入 / 开放邀请码), from the room service.
            bool mine = Rooms.MyRooms.InRoom;
            if (mine) Rooms.MyRooms.RefreshRoom();
            s_model.Persistent = mine;
            s_model.Confirmed = mine && !Rooms.MyRooms.Pending;
            s_model.CodeOpen = mine && Rooms.MyRooms.Code.Length > 0;
            s_model.CanOpenCode = mine && Rooms.MyRooms.CanOpenCode;   // room-service members < seats, not the C-server seats
            s_model.InGame = s_inGame;   // T-189
            string shownCode;
            CodeLine(out shownCode);
            s_model.HasCode = shownCode.Length > 0;
            CServerClient c = CServerLink.Client;
            int rtt = NetPlay.RttMs > 0f ? (int)(NetPlay.RttMs + 0.5f) : -1;
            for (int seat = 1; seat <= CsRoomModel.SeatCount; seat++)
            {
                CsRoomSeat s = s_model.Seats[seat];
                bool local = c != null && c.Seats.Contains(seat);
                s.Taken = c != null && (c.SeatTaken(seat) || local);
                s.Online = c != null && (c.SeatOnlineNow(seat) || local);
                s.Local = local;
                s.Nick = c != null ? c.SeatNick(seat) : "";
                if (s.Nick.Length == 0) s.Nick = "P" + seat;
                s.Ready = local ? CsLobby.LocalReady : CsLobby.RemoteReady(seat);
                s.RttMs = local ? rtt : CsLobby.RemoteRtt(seat);
                s.Playing = !local && CsLobby.RemotePlaying(seat);
            }
            s_model.SeatsUpdated();   // a picked player who left is no longer picked
        }

        // ---- drawing -------------------------------------------------------------------------------------

        static readonly Color Text = RoomUiDraw.Text;
        static readonly Color Dim = RoomUiDraw.Dim;
        static readonly Color Gold = RoomUiDraw.Gold;

        public static void Draw()
        {
            if (!s_open) return;
            Fill();
            NativeGui.BeginPage("cs.room");
            try
            {
                GUI.depth = -250;
                float w = Screen.width, h = Screen.height;
                if (s_inGame)
                {
                    // T-189: over the paused game: a dark veil so the page reads, then the page without the save column.
                    NativeGui.Panel("csroom.veil", new Rect(0f, 0f, w, h), new Color(0.01f, 0.02f, 0.04f, 0.72f));
                    RoomUiDraw.Title("csroom.title", L10n.T("csroom.title"));
                    DrawName(w, h);
                    DrawInfoInGame(w, h);
                    DrawPlayers(w, h);
                    DrawBar(w, h);
                    Mouse();
                    return;
                }
                // Title with a dark mist band behind it (the game's menus put their titles on one).
                RoomUiDraw.Title("csroom.title", L10n.T("csroom.title"));

                // T-173 layout room-v2-a (docs/design/room-page/codex/room-v2-a.png): left column = room name banner, orb,
                // save plate, IP:Port + invite code; right = 玩家列表; bottom = the key bar.
                DrawName(w, h);
                DrawSave(w, h);
                DrawInfo(w, h);
                DrawPlayers(w, h);
                DrawBar(w, h);
                Mouse();
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawSave(float w, float h)
        {
            CsRoomSummary save = CsRoomSummary.Parse(CsLobby.RoomSummary);
            // room-v2-a: the orb smaller and lower than before (centre 0.276 W / 0.42 H, 0.32 H across) under the name banner.
            Rect orb = new Rect(w * OrbX - h * 0.16f, h * 0.26f, h * 0.32f, h * 0.32f);
            if (save.HasSave) PlaceOrb(save, orb);
            else
            {
                DestroyOrb();
                NativeGui.Label("csroom.nosave", orb, L10n.T("csroom.no_save"), h * 0.03f, TextAnchor.MiddleCenter, Dim);
            }
            // The save plate under the orb (0.15-0.40 W, 0.60-0.72 H): "1: 沉没林地 - 6%", play time, difficulty.
            float cw = w * 0.26f, x = w * OrbX - cw / 2f;
            RoomUiDraw.Description("csroom.saveband", new Rect(w * 0.135f, h * 0.578f, w * 0.282f, h * 0.16f), "");
            string line = NativeModeMenu.CsRoomSaveLine(save, L10n.Zh);
            if (save.Slot >= 0 && line.StartsWith((save.Slot + 1) + ": "))   // the slot number in the game's highlight colour
                line = "*" + (save.Slot + 1) + ":*" + line.Substring((save.Slot + 1).ToString().Length + 1);
            int clock = line.LastIndexOf("  ", StringComparison.Ordinal);
            if (clock > 0) line = line.Substring(0, clock);
            NativeGui.Label("csroom.save.area", new Rect(x, h * 0.59f, cw, h * 0.05f), line, h * 0.032f, TextAnchor.MiddleCenter, Text);
            NativeGui.Label("csroom.save.time", new Rect(x, h * 0.638f, cw, h * 0.045f), CsRoomSummary.Clock(save.Seconds), h * 0.029f, TextAnchor.MiddleCenter, Text);
            NativeGui.Label("csroom.save.diff", new Rect(x, h * 0.678f, cw, h * 0.045f), DifficultyText(save.Difficulty), h * 0.029f, TextAnchor.MiddleCenter, Gold);
        }

        const float OrbX = 0.276f;   // the left column's centre (fraction of the width)

        // room-v2-a: the room name on a small smoky banner above the orb (0.11-0.42 W, 0.17-0.24 H), with "[E] 改名" at its
        // right when this PC may rename and plays on the keyboard (no text input on a pad). While typing: the typed text
        // with a blinking caret.
        static void DrawName(float w, float h)
        {
            RoomUiDraw.Banner("csroom.name.banner", new Rect(w * 0.1f, h * 0.152f, w * 0.33f, h * 0.105f));
            bool editing = s_edit.Editing;
            bool keyboard = PlayerInput.Instance == null || PlayerInput.Instance.WasKeyboardUsedLast;
            bool hint = !editing && MayRename && keyboard && s_keyRename != null && s_keyRename.Value != KeyCode.None;
            string name = editing ? s_edit.Text + ((int)(Time.unscaledTime * 2f) % 2 == 0 ? "|" : " ") : RoomName();
            if (name.Length == 0) name = "-";
            float cy = h * 0.205f;
            bool left = hint || editing;
            Rect r = left ? new Rect(w * 0.135f, cy - h * 0.035f, w * 0.215f, h * 0.07f) : new Rect(w * 0.12f, cy - h * 0.035f, w * 0.29f, h * 0.07f);
            float font = Mathf.Min(h * 0.032f, r.width / (4.2f + 0.55f * name.Length));   // a long name gets a smaller font
            NativeGui.Label("csroom.name", r, L10n.F("csroom.room_name", name), font, left ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, Text);
            s_renameRect = hint ? RoomUiDraw.KeyHint("csroom.rename", w * 0.357f, cy, h * 0.032f, RoomUiDraw.KeyIcon(s_keyRename.Value, KeyCode.None),
                RoomUiDraw.KeyName(s_keyRename.Value), L10n.T("csroom.rename"), Text) : new Rect();
        }

        // room-v2-a: under the save plate, centred under the orb: "IP:Port host:port" and the invite line.
        static void DrawInfo(float w, float h)
        {
            float cw = w * 0.3f, x = w * OrbX - cw / 2f;
            // The server address alone (owner: it already reads as ip:port, no "IP:Port" label).
            NativeGui.Label("csroom.ipport", new Rect(x, h * 0.737f, cw, h * 0.04f), IpPort(), h * 0.029f, TextAnchor.MiddleCenter, Text);
            string code;
            NativeGui.Label("csroom.code", new Rect(x, h * 0.775f, cw, h * 0.04f), CodeLine(out code), h * 0.029f, TextAnchor.MiddleCenter, Text);
        }

        // T-189: the in-game left column under the name banner: ip:port, the invite line ("房间已满" once every seat is taken),
        // then a note: "已复制" after 复制邀请码, the leave question, or how long an opened code stays open.
        static void DrawInfoInGame(float w, float h)
        {
            float cw = w * 0.33f, x = w * 0.1f;
            RoomUiDraw.Description("csroom.infoband", new Rect(w * 0.1f, h * 0.29f, w * 0.33f, h * 0.2f), "");
            NativeGui.Label("csroom.ipport", new Rect(x, h * 0.31f, cw, h * 0.05f), IpPort(), h * 0.032f, TextAnchor.MiddleCenter, Text);
            string code, line = CodeLine(out code, s_model.SeatsFull);
            NativeGui.Label("csroom.code", new Rect(x, h * 0.37f, cw, h * 0.05f), line, h * 0.032f, TextAnchor.MiddleCenter, Text);
            string note = s_model.LeaveAsked ? L10n.T("csroom.leave_ask")
                : Time.unscaledTime < s_copiedUntil ? L10n.T("csroom.copied")
                : s_model.Persistent && code.Length == 0 && !s_model.SeatsFull ? L10n.T("csroom.code_minutes") : "";
            NativeGui.Label("csroom.note", new Rect(x, h * 0.43f, cw, h * 0.045f), note, h * 0.028f, TextAnchor.MiddleCenter,
                s_model.LeaveAsked ? Gold : Dim);
        }

        static string DifficultyText(int difficulty)
        {
            if (difficulty < 0 || !Enum.IsDefined(typeof(DifficultyMode), difficulty)) return "";
            try
            {
                SaveSlotUI any = s_orbUi != null ? s_orbUi : AnySlot();
                if (any != null)
                {
                    // The slot screen's own line, already "难度：简单" / "Difficulty: Easy" in the game's language.
                    MethodInfo m = typeof(SaveSlotUI).GetMethod("DifficultyModeToMessage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    object message = m != null ? m.Invoke(any, new object[] { (DifficultyMode)difficulty }) : null;
                    if (message is MessageDescriptor) return ((MessageDescriptor)message).Message;
                }
            }
            catch (Exception e) { NativeGui.WarnOnce("csroom.difficulty", "difficulty", e); }
            return L10n.F("csroom.difficulty", ((DifficultyMode)difficulty).ToString());
        }

        static void DrawPlayers(float w, float h)
        {
            float left = w * 0.47f, width = w * 0.44f;
            RoomUiDraw.Banner("csroom.players.banner", new Rect(w * 0.575f, h * 0.25f, w * 0.23f, h * 0.11f));   // room-v2-a
            NativeGui.Label("csroom.players", new Rect(left, h * 0.27f, width, h * 0.07f), L10n.T("csroom.players"), h * 0.045f, TextAnchor.MiddleCenter, Text);
            float rowH = h * 0.068f, gap = h * 0.08f, top = h * 0.37f;
            for (int seat = 1; seat <= CsRoomModel.SeatCount; seat++)
            {
                CsRoomRow row = s_model.Row(seat);
                Rect r = new Rect(left, top + (seat - 1) * gap, width, rowH);
                s_rowRects[seat] = r;
                RoomUiDraw.Band("csroom.row." + seat, r, row.Selected || row.Picked);
                float font = h * 0.032f;
                Color name = row.Empty ? Dim : Text;
                NativeGui.Label("csroom.row." + seat + ".p", new Rect(r.x + width * 0.04f, r.y, width * 0.1f, rowH), row.Label, font, TextAnchor.MiddleLeft, Text);
                NativeGui.Label("csroom.row." + seat + ".name", new Rect(r.x + width * 0.14f, r.y, width * 0.46f, rowH), row.Name, font, TextAnchor.MiddleLeft, name);
                NativeGui.Label("csroom.row." + seat + ".ping", new Rect(r.x + width * 0.6f, r.y, width * 0.16f, rowH), row.Ping, font, TextAnchor.MiddleCenter, Text);
                // Gold = ready on the title page; in the game (T-189) there is no ready: this PC's own row in gold.
                bool gold = s_inGame ? !row.Empty && s_model.Seats[seat].Local : !row.Empty && row.Ready;
                NativeGui.Label("csroom.row." + seat + ".status", new Rect(r.x + width * 0.78f, r.y, width * 0.2f, rowH), row.Status, font, TextAnchor.MiddleCenter,
                    gold ? Gold : Text);
            }
        }

        static void DrawBar(float w, float h)
        {
            List<RoomUiDraw.Key> keys = new List<RoomUiDraw.Key>();
            string icons = "";
            foreach (CsRoomBarItem item in s_model.Bar())
            {
                KeyCode key, pad;
                Keys(item.Action, out key, out pad);
                bool noKey = key == KeyCode.None && pad == KeyCode.None;   // T-189: in-game 离开房间: the label alone
                string icon = noKey ? null : RoomUiDraw.KeyIcon(key, pad);
                icons += (icons.Length > 0 ? "|" : "") + icon;
                keys.Add(new RoomUiDraw.Key { Name = noKey ? "" : KeyName(item.Action), Icon = icon, Label = item.Label, Enabled = item.Enabled, Selected = item.Selected });
            }
            s_barIcons = icons;
            s_barRects.Clear();
            s_barRects.AddRange(RoomUiDraw.KeyBar("csroom.bar", keys));
        }

        // Mouse: a click on a bar item presses it, a click on a player row selects it.
        static void Mouse()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 0 || Time.unscaledTime < s_ignoreUntil) return;
            if (s_edit.Editing) { e.Use(); return; }   // typing the room name: no click acts on the page
            if (s_renameRect.width > 0f && s_renameRect.Contains(e.mousePosition)) { e.Use(); BeginRename(); return; }
            for (int i = 0; i < s_barRects.Count; i++)
                if (s_barRects[i].Contains(e.mousePosition)) { e.Use(); Run(s_model.Click(i)); return; }
            for (int seat = 1; seat <= CsRoomModel.SeatCount; seat++)
                if (s_rowRects[seat].Contains(e.mousePosition)) { e.Use(); s_model.ClickRow(seat); return; }
        }

        // ---- keycaps -------------------------------------------------------------------------------------

        // The configured keyboard key and controller button of a bar action (Leave on a controller: B, the game's Cancel).
        static void Keys(CsRoomAction action, out KeyCode key, out KeyCode pad)
        {
            ConfigEntry<KeyCode> k = null, p = null;
            switch (action)
            {
                case CsRoomAction.Start: k = s_keyStart; p = s_padStart; break;
                case CsRoomAction.Ready: k = s_keyReady; p = s_padReady; break;
                case CsRoomAction.SwitchSave: k = s_keySwitch; p = s_padSwitch; break;
                case CsRoomAction.Kick: k = s_keyKick; p = s_padKick; break;
                case CsRoomAction.Leave: k = s_keyLeave; break;
                case CsRoomAction.OpenCode: k = s_inGame ? s_keyStart : s_keyCode; p = s_padCode; break;   // T-189: [SPACE] in the game
                case CsRoomAction.CopyCode: k = s_keyCopy; p = s_padCopy; break;
                case CsRoomAction.Back: k = s_keyLeave; break;
            }
            key = k != null ? k.Value : KeyCode.None;
            pad = action == CsRoomAction.Leave || action == CsRoomAction.Back ? KeyCode.JoystickButton1 : p != null ? p.Value : KeyCode.None;
            // T-189: in the game Esc / B is 返回; 离开房间 has no key of its own (focus it and confirm, or click it). The leave
            // question's 确定离开 takes Enter / A (the focus starts on it).
            if (s_inGame && action == CsRoomAction.Leave) { key = KeyCode.None; pad = KeyCode.None; }
            if (action == CsRoomAction.ConfirmLeave) { key = KeyCode.Return; pad = KeyCode.JoystickButton0; }
        }

        // The key's name for a drawn keycap: only used when the game has no button icon for the key (RoomUiDraw.KeyIcon).
        static string KeyName(CsRoomAction action)
        {
            bool keyboard = PlayerInput.Instance == null || PlayerInput.Instance.WasKeyboardUsedLast;
            KeyCode key, padKey;
            Keys(action, out key, out padKey);
            if (!keyboard)
            {
                switch (padKey)
                {
                    case KeyCode.JoystickButton0: return "A";
                    case KeyCode.JoystickButton1: return "B";
                    case KeyCode.JoystickButton2: return "X";
                    case KeyCode.JoystickButton3: return "Y";
                    case KeyCode.JoystickButton4: return "LB";
                    case KeyCode.JoystickButton5: return "RB";
                    case KeyCode.JoystickButton6: return "VIEW";
                    case KeyCode.JoystickButton7: return "MENU";
                }
            }
            return RoomUiDraw.KeyName(key);
        }

        // ---- the orb -------------------------------------------------------------------------------------

        // A copy of a save slot of the game's slot screen, showing the room's save, placed in the given screen rect.
        static void PlaceOrb(CsRoomSummary save, Rect screen)
        {
            try
            {
                Camera camera = P2Hud.GuiCamera();
                SaveSlotUI source = AnySlot(save.Slot);
                if (camera == null || source == null) return;
                string key = save.Encode() + "|" + screen + "|" + L10n.Zh;
                if (s_orb == null)
                {
                    bool was = source.gameObject.activeSelf;
                    source.gameObject.SetActive(false);                 // the copy starts inactive: no Awake / Start of its own
                    s_orb = (GameObject)UnityEngine.Object.Instantiate(source.gameObject);
                    source.gameObject.SetActive(was);
                    s_orb.name = "OriCoop_CsRoomOrb";
                    s_orbUi = s_orb.GetComponent<SaveSlotUI>();
                    if (s_orbUi != null) { s_orbUi.IsSuspended = true; s_orbUi.enabled = false; }
                    StripMenus(s_orb);   // owner playtest: the copy showed the game's difficulty picker (EASY / NORMAL / HARD)
                    foreach (Behaviour b in s_orb.GetComponentsInChildren<CleverMenuItem>(true)) b.enabled = false;
                    s_orb.transform.position = source.transform.position;
                    s_orb.transform.rotation = source.transform.rotation;
                    s_orb.transform.localScale = source.transform.lossyScale;
                    s_orb.SetActive(true);
                    s_orbKey = "";
                    if (!s_dumped) { s_dumped = true; Plugin.Log.LogInfo("cs room: orb copy tree\n" + Dump(s_orb.transform, 0, 6)); DumpScreen(); }
                }
                if (key == s_orbKey) return;
                s_orbKey = key;
                ApplyOrb(save);
                // Size and place: measure the screenshot's screen size, scale to the rect, centre it.
                Renderer pic = s_orbUi != null && s_orbUi.Screenshot != null ? s_orbUi.Screenshot : s_orb.GetComponentInChildren<Renderer>();
                if (pic == null) return;
                Bounds b0 = pic.bounds;
                Vector3 a = camera.WorldToScreenPoint(b0.min), c = camera.WorldToScreenPoint(b0.max);
                float size = Mathf.Max(1f, Mathf.Abs(c.y - a.y));
                s_orb.transform.localScale *= screen.height / size;
                Bounds b1 = pic.bounds;
                Vector3 centre = camera.WorldToScreenPoint(b1.center);
                Vector3 target = camera.ScreenToWorldPoint(new Vector3(screen.center.x, Screen.height - screen.center.y, centre.z));
                s_orb.transform.position += target - b1.center;
            }
            catch (Exception e) { NativeGui.WarnOnce("csroom.orb", "orb", e); }
        }

        // SaveSlotUI.Apply for our save line (it reads the slot by index, which a joined player does not have).
        static void ApplyOrb(CsRoomSummary save)
        {
            SaveSlotUI o = s_orbUi;
            if (o == null) return;
            if (o.SaveGroup != null) o.SaveGroup.SetActive(true);
            if (o.EmptyGroup != null) o.EmptyGroup.SetActive(false);
            if (o.FullyCompletedGroup != null) o.FullyCompletedGroup.SetActive(false);
            if (o.DeathGroup != null) o.DeathGroup.SetActive(false);
            // The page writes the save lines under the orb itself (the slot's own ones are tiny at this size): hide the
            // slot's info block (texts, health / energy icons, its backdrop) and the backups list.
            Transform info = FindDeep(o.transform, "slotInfo");
            if (info != null) info.gameObject.SetActive(false);
            if (o.BackupsAnimator != null) o.BackupsAnimator.gameObject.SetActive(false);
            Texture texture = SaveSlotsScreenshotManager.Instance != null ? SaveSlotsScreenshotManager.Instance.FindScreenshot(save.Area) : null;
            if (o.Screenshot != null) o.Screenshot.material.SetTexture(ShaderProperties.MainTexture, texture != null ? texture : o.EmptyTexture);
            if (o.ScreenshotB != null) o.ScreenshotB.material.SetTexture(ShaderProperties.MainTexture, texture != null ? texture : o.EmptyTexture);
            // Copied from a faded-out slot screen (a player who joined from the title), the orb's parts are off at alpha 0. Give
            // them the alphas a shown slot has (measured on the slot screen: picture / ring 0.5, veins 0.48, glow 0.18,
            // reflection 0.11).
            Renderer shot = o.Screenshot;
            if (shot != null && shot.sharedMaterial != null && shot.sharedMaterial.HasProperty("_Color") && shot.sharedMaterial.color.a < 0.01f)
                foreach (Renderer r in o.GetComponentsInChildren<Renderer>(true))
                {
                    float a = ShownAlpha(r.name);
                    if (a < 0f || r.sharedMaterial == null || !r.sharedMaterial.HasProperty("_Color")) continue;
                    Color c = r.material.color; c.a = a; r.material.color = c;
                    r.enabled = true;   // the fade also switches renderers off at alpha 0
                }
        }

        static bool s_dumped;

        // A save slot orb to copy: the slot screen's (the room's slot when it has one), else any loaded slot (a player who
        // joined from the title has no slot screen up; its objects exist in the title scene, inactive).
        static SaveSlotUI AnySlot() { return AnySlot(0); }
        static SaveSlotUI AnySlot(int slot)
        {
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            if (ui != null && ui.Items != null && ui.Items.Count > 0) return ui.Items[Mathf.Clamp(slot, 0, ui.Items.Count - 1)];
            foreach (SaveSlotUI candidate in Resources.FindObjectsOfTypeAll<SaveSlotUI>())
                if (candidate != null && candidate.gameObject.scene.IsValid() && candidate.name != "OriCoop_CsRoomOrb") return candidate;
            return null;
        }

        static string Dump(Transform t, int depth, int max)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Renderer r = t.GetComponent<Renderer>();
            sb.Append(new string(' ', depth * 2)).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)")
                .Append(r != null ? " [" + r.GetType().Name + (r.sharedMaterial != null && r.sharedMaterial.mainTexture != null ? " " + r.sharedMaterial.mainTexture.name : "") + "]" : "").Append('\n');
            if (depth < max) foreach (Transform c in t) sb.Append(Dump(c, depth + 1, max));
            return sb.ToString();
        }

        // What else is on screen while the page is open (the slot screen's legend, the logo), for placing the page.
        static void DumpScreen()
        {
            try
            {
                Transform from = SaveSlotsUI.Instance != null ? SaveSlotsUI.Instance.transform : null;
                Transform root = from != null ? from.root : null;
                if (root != null) Plugin.Log.LogInfo("cs room: slot screen root tree\n" + Dump(root, 0, 4));
                Camera camera = P2Hud.GuiCamera();
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    Camera cam = camera != null && (camera.cullingMask & (1 << r.gameObject.layer)) != 0 ? camera : Camera.main;
                    if (cam == null) continue;
                    Vector3 c = cam.WorldToScreenPoint(r.bounds.center);
                    if (c.y < Screen.height * 0.6f && c.y > Screen.height * 0.12f) continue;   // only the top and bottom bands
                    sb.Append(Path(r.transform)).Append(cam == camera ? " gui" : " main").Append(" @").Append((int)c.x).Append(',').Append((int)(Screen.height - c.y)).Append('\n');
                }
                Plugin.Log.LogInfo("cs room: renderers in the title / bar bands\n" + sb);
            }
            catch (Exception e) { NativeGui.WarnOnce("csroom.dump", "dump", e); }
        }

        static string Path(Transform t) { return t.parent == null ? t.name : Path(t.parent) + "/" + t.name; }

        static float ShownAlpha(string part)
        {
            switch (part)
            {
                case "screenshot": case "saveSlotRing": return 0.5f;
                case "gravityElementVeinsSoftA": return 0.48f;
                case "particleGlowSprites": return 0.18f;
                case "screenshotReflection": return 0.11f;
                default: return -1f;
            }
        }

        // Menus that sit on the save slot (the title host page NativeModeMenu opens on it, OriCoop_ModeMenu, a copy of the
        // game's difficulty screen; or the game's own difficulty screen kept for a new game) are copied along with the
        // slot. In the copy they show their raw texts (EASY / NORMAL / HARD + description, in English) and cannot be
        // used. The room page's orb shows only the save: remove every menu from the copy before it becomes active.
        static void StripMenus(GameObject copy)
        {
            // Test-only mutation: keep the menus (the titlejoin cserver check "orb shows only the save" must then fail).
            // (run-titlejoin clears ORICOOP_TEST for its clients, so a titlejoin run counts as a test run here too.)
            bool testRun = Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN"));
            if (testRun && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_ORB_MENUS") == "1") return;
            List<GameObject> menus = new List<GameObject>();
            foreach (CleverMenuItemSelectionManager m in copy.GetComponentsInChildren<CleverMenuItemSelectionManager>(true))
                if (m != null && m.gameObject != copy && !menus.Contains(m.gameObject)) menus.Add(m.gameObject);
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
                if (t != null && t.gameObject != copy && t.name.StartsWith("OriCoop_ModeMenu") && !menus.Contains(t.gameObject)) menus.Add(t.gameObject);
            foreach (GameObject g in menus) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            if (menus.Count > 0) Plugin.Log.LogInfo("cs room: removed " + menus.Count + " menu(s) from the orb copy");
        }

        // Menus left inside the orb copy (harness: must be 0; -1 = no copy).
        internal static int OrbMenus
        {
            get
            {
                if (s_orb == null) return -1;
                int n = 0;
                foreach (CleverMenuItemSelectionManager m in s_orb.GetComponentsInChildren<CleverMenuItemSelectionManager>(true))
                    if (m != null && m.gameObject != s_orb) n++;
                foreach (Transform t in s_orb.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("OriCoop_ModeMenu")) n++;
                return n;
            }
        }

        static Transform FindDeep(Transform t, string name)
        {
            foreach (Transform c in t)
            {
                if (c.name == name) return c;
                Transform deep = FindDeep(c, name);
                if (deep != null) return deep;
            }
            return null;
        }

        static void DestroyOrb()
        {
            if (s_orb != null) UnityEngine.Object.Destroy(s_orb);
            s_orb = null; s_orbUi = null; s_orbKey = "";
        }
    }
}
