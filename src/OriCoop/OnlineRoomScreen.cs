using System;
using System.Collections.Generic;
using Game;
using UnityEngine;

namespace OriCoop
{
    // A transport-independent room page using the existing NativeGui/IMGUI rendering path.
    internal static class OnlineRoomScreen
    {
        static readonly GameSeatView seats = new GameSeatView();
        static Action proceed;
        static readonly List<Action> buttons = new List<Action>();
        static readonly Dictionary<string, Action> drawn = new Dictionary<string, Action>();
        static int focus;
        static bool inGame, fromPause, showFriends;
        static string inviteResult = "";
        internal static bool SteamInGame { get { return inGame && Visible; } }
        internal static bool SteamInGameAvailable { get { return NetPlay.Enabled && NetPlay.UsesSteam && NetPlay.LocalInGameplay(); } }

        static float ignoreUntil;
        static Vector2 memberScroll, saveScroll;
        static string[] addresses = new string[0];
        // T-174: the rows are read once per frame on Layout and drawn unchanged on Repaint / input events.
        static readonly Ui.LayoutSnapshot<Ui.RoomScreenModel> frame = new Ui.LayoutSnapshot<Ui.RoomScreenModel>();
        static readonly Func<Ui.RoomScreenModel> s_buildModel = BuildModel;
        static readonly Func<int, int> s_seatOwner = NetPlay.SeatOwner;
        static readonly Func<int, bool> s_isLocal = Players.IsLocal;
        public static bool Visible { get; private set; }
        public static bool Confirmed { get; private set; }
        internal static int DrawnSeatMask;
        internal static bool DrawnAddEnabled;
        // NetPlay.Begin and End call this every time (End even when online play is already off). Only give the native page back
        // if this screen had taken it (T-120: an unconditional reset showed and unlocked the page under the overlay lobby).
        public static void Reset()
        {
            bool wasVisible = Visible, roomPage = CsRoomPage.IsOpen && !CsRoomPage.InGame;   // T-189: the in-game page has no title page to go back to
            CloseInGame();
            SteamRoomMenu.Reset();
            showFriends = false; inviteResult = "";
            Visible = Confirmed = false; proceed = null; buttons.Clear(); drawn.Clear(); frame.Clear();
            CsRoomPage.Close();   // T-171
            if (wasVisible || NativeModeMenu.OverlayUnlockMutated) NativeModeMenu.RoomPanelVisible(false);
            if (roomPage) NativeModeMenu.RoomPageEndedBySession();   // T-176: no dead host / join page after a kick either
        }
        public static bool BeforeProceed(Action next)
        {
            if (Confirmed) return false;
            Show(next); return true;
        }
        public static void Show(Action next)
        {
            if (Visible) return;
            proceed = next; Visible = true; focus = 0; ignoreUntil = Time.unscaledTime + 0.3f;
            drawn.Clear(); buttons.Clear(); frame.Clear();
            addresses = NetPlay.IsHost && NetPlay.TransportName == "direct" ? NetPlay.LocalAddresses() : new string[0];
            NativeModeMenu.RoomPanelVisible(true);
            if (CsRoomPage.Active) CsRoomPage.Open();   // T-171: a C-server room gets the 房间管理页
        }
        public static void Continue()
        {
            if (!Visible) return;
            Confirmed = true; Visible = false;
            CsRoomPage.Close();
            CsLobby.Changed();   // tell the room this PC is in the game now
            NativeModeMenu.RoomPanelVisible(false);
            Action next = proceed; proceed = null;
            if (next != null) next();
        }
        internal static void OpenSteamInGame()
        {
            if (!SteamInGameAvailable || Visible) return;
            inGame = true; fromPause = PauseMenuEntry.Suspend();
            SessionRules.ScreenOpened();
            Show(null);
        }

        internal static void CloseInGame()
        {
            if (!inGame) return;
            inGame = false; Visible = false; showFriends = false;
            buttons.Clear(); drawn.Clear(); frame.Clear();
            if (fromPause) PauseMenuEntry.Return();
            fromPause = false;
        }

        internal static void OpenSteamFriends()
        {
            if (!Visible) OpenSteamInGame();
            if (!Visible) return;
            showFriends = true; inviteResult = ""; focus = 0;
        }

        static void Back()
        {
            if (showFriends) { showFriends = false; focus = 0; return; }
            if (inGame) { CloseInGame(); return; }
            if (OnlineLobby.IsOpen) NativeModeMenu.LeaveRoomPage();
            else NativeModeMenu.LeaveSteamRoom();
        }

        internal static bool Add()
        {
            if (!NetPlay.CanAddLocal) return false;
            for (int candidate = -1; candidate <= 3; candidate++)
            {
                InputDevice d = Device(candidate); bool used = false;
                foreach (int seat in seats.LocalSeats)
                {
                    InputDevice current = seats.InputOf(seat);
                    if (current.Kind == d.Kind && (candidate == -1 || current.Pad == candidate)) used = true;
                }
                if (!used) return seats.AddLocalPlayer(d);
            }
            return false;
        }
        static InputDevice Device(int pad) { return new InputDevice(pad < 0 ? InputDevice.DeviceKind.KeyboardMouse : InputDevice.DeviceKind.Pad, pad); }
        static string InputText(int seat)
        {
            InputDevice d = seats.InputOf(seat);
            return d.Kind == InputDevice.DeviceKind.KeyboardMouse ? L10n.T("coop.keyboard_only") : L10n.F("room.pad", d.Pad + 1);
        }
        static void NextInput(int seat)
        {
            InputDevice d = seats.InputOf(seat);
            int next = d.Kind == InputDevice.DeviceKind.KeyboardMouse ? 0 : d.Pad == 3 ? -1 : d.Pad + 1;
            seats.SetInput(seat, Device(next));
        }
        static void Label(string id, string text) { NativeGui.LayoutLabel(id, text, GUI.skin.label); }
        static void Button(string id, string text, Action action)
        {
            int index = buttons.Count;
            if (GUI.enabled) { buttons.Add(action); if (Event.current.type == EventType.Repaint) drawn[id] = action; }
            if (NativeGui.LayoutButton(id, (GUI.enabled && focus == index ? "> " : "") + text, GUI.skin.button, GUILayout.Height(32)) && GUI.enabled) action();
        }
        internal static bool HasButton(string id) { return drawn.ContainsKey(id); }
        internal static bool Click(string id)
        {
            Action action;
            if (Visible && CsRoomPage.IsOpen) return id == "room.continue" && CsRoomPage.TestContinue();
            if (!Visible || !drawn.TryGetValue(id, out action)) return false;
            action(); return true;
        }
        public static void Tick()
        {
            SteamRoomMenu.Tick();
            CsLobby.Tick();   // T-171: C-server room page state (ready, ping, start) between PCs, also while in the game
            MyRoomsPages.Tick();   // T-173: 我的房间 join / list pages
            if (CsRoomPage.InGame) { CsRoomPage.Tick(); return; }   // T-189: pause menu -> 房间管理
            if (!Visible || Time.unscaledTime < ignoreUntil) return;
            if (!NetPlay.Enabled) { Reset(); return; }
            if (CsRoomPage.IsOpen) { CsRoomPage.Tick(); return; }
            if (NetPlay.UsesSteam)
            {
                if (!inGame && !NetPlay.IsHost)
                {
                    if (!NetPlay.PartnerHere && NetPlay.Problem.Length > 0) { Reset(); return; }
                    if (NetPlay.PartnerHere && SteamRoomMenu.HostStarted) { Continue(); return; }
                }
                if (Core.Input.Cancel.OnPressed || Input.GetKeyDown(KeyCode.Escape)) { Core.Input.Cancel.Used = true; Back(); return; }
            }
            if (buttons.Count == 0) return;
            if (Core.Input.MenuDown.OnPressed || Input.GetKeyDown(KeyCode.DownArrow)) focus = (focus + 1) % buttons.Count;
            if (Core.Input.MenuUp.OnPressed || Input.GetKeyDown(KeyCode.UpArrow)) focus = (focus + buttons.Count - 1) % buttons.Count;
            if (Core.Input.ActionButtonA.OnPressed || Input.GetKeyDown(KeyCode.Return)) buttons[Mathf.Clamp(focus, 0, buttons.Count - 1)]();
        }
        // Everything that decides how many controls the page has. Only text (labels, input names) is read live.
        static Ui.RoomScreenModel BuildModel()
        {
            Ui.RoomScreenModel m = new Ui.RoomScreenModel();
            m.IsHost = NetPlay.IsHost;
            m.RoomCode = NetPlay.RoomCode ?? "";
            m.Addresses = addresses;
            m.ShowInvite = NetPlay.IsHost && NetPlay.UsesSteam;
            m.Steam = NetPlay.Enabled && NetPlay.UsesSteam;
            m.InGame = inGame;
            if (m.Steam)
            {
                m.SteamReady = NetPlay.SteamReady;
                m.RetryHost = m.IsHost && !m.SteamReady && NetPlay.Problem.Length > 0;
                m.ShowFriends = showFriends && m.IsHost;
                m.HasSpace = SteamRoomMenu.HasSpace;
                m.SteamTitle = m.IsHost ? L10n.T("steam.host.title") : L10n.F("steam.join.title", SteamRoomMenu.HostName);
                m.SteamStatus = m.InGame ? SteamRoomMenu.Status(L10n.Zh) :
                    m.IsHost ? (!m.SteamReady && NetPlay.Problem.Length > 0 ? L10n.T(Ui.JoinErrorText.Key(NetPlay.Problem, false, true)) :
                        L10n.T(m.SteamReady ? "lobby.steam_ready" : "lobby.steam.preparing")) + " · " + SteamRoomMenu.Status(L10n.Zh) :
                        L10n.T("steam.wait.host");
                m.SteamMembers.AddRange(SteamRoomMenu.Members);
                if (m.ShowFriends)
                    foreach (Net.SteamFriendRow friend in SteamRoomMenu.List(false))
                        m.Friends.Add(new KeyValuePair<ulong,string>(friend.User, friend.Name));
            }
            if (m.IsHost && !m.InGame)
            {
                int picked = CsRoomPage.PickedSlot;
                for (int slot = 0; slot < SaveSlotsManager.SaveSlotCount; slot++)
                {
                    if (slot != picked && !System.IO.File.Exists(Rooms.RoomGame.PathFor(slot))) continue;
                    m.Saves.Add(new Ui.RoomSaveRow { Slot = slot, Current = slot == picked, Summary = Rooms.RoomGame.Summary(slot) });
                }
            }
            Ui.RoomScreenRows.Members(m.Members, s_seatOwner, NetPlay.MultiSeatSupported, Players.LocalSeat, NetPlay.RemoteSeats, s_isLocal);
            m.AddEnabled = NetPlay.CanAddLocal;
            m.ServerNote = !NetPlay.MultiSeatSupported;
            return m;
        }
        public static void Draw()
        {
            MyRoomsPages.Draw();   // T-173
            if (!MyRoomsPages.IsOpen && NativeModeMenu.MyRoomsMenuOpen) MyRoomsPages.DrawMenuKeys();
            if (CsRoomPage.InGame) { CsRoomPage.Draw(); return; }   // T-189
            if (!Visible) return;
            if (!NetPlay.Enabled) { Reset(); return; }
            NativeModeMenu.RoomPanelVisible(true);
            if (CsRoomPage.IsOpen) { CsRoomPage.Draw(); return; }
            Ui.RoomScreenModel model = frame.Get(Event.current.type == EventType.Layout, s_buildModel);
            if (model == null) return;      // opened after this frame's Layout: draw from the next frame on
            NativeGui.BeginPage("online.room");
            try
            {
                GUI.depth = -250;
                buttons.Clear();
                if (Event.current.type == EventType.Repaint) drawn.Clear();
                Rect area = new Rect(Screen.width * 0.08f, Screen.height * 0.08f, Screen.width * 0.84f, Screen.height * 0.84f);
                NativeGui.Panel("room.background", area, new Color(0.12f, 0.12f, 0.12f, 0.96f));
                GUILayout.BeginArea(area);
                Label("room.title", model.Steam ? model.SteamTitle : L10n.T("room.title"));
                if (model.Steam) Label("room.status", model.SteamStatus);
                if (model.ShowFriends)
                {
                    if (model.Friends.Count == 0) Label("room.empty", L10n.T("steam.no.friends"));
                    memberScroll = GUILayout.BeginScrollView(memberScroll, GUILayout.Height(Mathf.Max(60, area.height - 170)));
                    foreach (var friend in model.Friends)
                    {
                        ulong user = friend.Key;
                        Button("room.invite." + user, friend.Value, delegate {
                            inviteResult = L10n.T(SteamRoomMenu.Invite(user) ? "steam.invited" : "steam.invite.failed");
                        });
                    }
                    GUILayout.EndScrollView();
                    Label("room.invite.result", inviteResult);
                    Button("room.back", L10n.T("common.back"), Back);
                    GUILayout.EndArea();
                    return;
                }
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(area.width * 0.48f));
                if (!model.InGame) Label("room.saves", L10n.T("room.saves"));
                saveScroll = GUILayout.BeginScrollView(saveScroll, GUILayout.Height(Mathf.Max(60, area.height - (model.Steam ? 200 : 145))));
                if (model.RoomCode.Length > 0) Label("room.code", L10n.T("lobby.room.code") + ": " + model.RoomCode);
                foreach (string address in model.Addresses) Label("room.address." + address, L10n.T("lobby.your_address") + ": " + address + ":" + NetPlay.ConfigPort);
                if (model.ShowInvite && model.SteamReady) Button("room.invite", L10n.T("room.invite"), NetPlay.OpenSteamInvite);
                if (model.IsHost)
                {
                    foreach (Ui.RoomSaveRow row in model.Saves)
                    {
                        int selected = row.Slot;
                        bool saveEnabled = GUI.enabled;
                        GUI.enabled = saveEnabled && NativeModeMenu.CanSelectRoomSave;
                        Button("room.save." + row.Slot, (row.Current ? "* " : "") + L10n.F("rooms.slot_summary", row.Slot + 1, row.Summary),
                            delegate { NativeModeMenu.SelectRoomSave(selected); });
                        GUI.enabled = saveEnabled;
                    }
                }
                else if (!model.InGame) Label("room.hostsave", L10n.SessionText("room.host_save"));
                GUILayout.EndScrollView();
                if (model.RetryHost) Button("room.retry", L10n.T("common.retry"), delegate { NetPlay.Begin(true, "steam", ""); });
                if (model.InGame) Button("room.back", L10n.T("common.back"), CloseInGame);
                else if (!model.Steam || model.IsHost && model.SteamReady)
                    Button("room.continue", L10n.T(model.IsHost ? "lobby.start" : "rooms.continue"), Continue);
                GUILayout.EndVertical();
                GUILayout.BeginVertical();
                Label("room.members", L10n.T("room.members"));
                memberScroll = GUILayout.BeginScrollView(memberScroll, GUILayout.Height(Mathf.Max(60, area.height - (model.Steam ? 200 : 145))));
                DrawnSeatMask = model.SeatMask;
                if (model.Steam)
                {
                    for (int i = 0; i < model.SteamMembers.Count; i++) Label("room.steam.member." + i, model.SteamMembers[i]);
                    if (model.HasSpace) Label("room.steam.empty", L10n.T("steam.wait.friend"));
                }
                foreach (Ui.RoomMemberRow row in model.Members)
                {
                    if (model.Steam && !row.Local) continue;
                    int seat = row.Seat;
                    Color old = GUI.contentColor; GUI.contentColor = Roles.ColorOf(seat);
                    Label("room.member." + seat, L10n.F("room.member", seat, row.Owner, L10n.T(row.Local ? "room.local" : "room.remote")));
                    GUI.contentColor = old;
                    if (!row.Local) continue;
                    GUILayout.BeginHorizontal();
                    Button("room.input." + seat, InputText(seat), delegate { NextInput(seat); });
                    if (row.Removable) Button("room.remove." + seat, L10n.F("coop.remove_local", seat), delegate { seats.RemoveLocalPlayer(seat); });
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                bool enabled = GUI.enabled;
                DrawnAddEnabled = model.AddEnabled;
                GUI.enabled = enabled && DrawnAddEnabled;
                Button("room.add", L10n.T("room.add_local"), delegate { Add(); });
                GUI.enabled = enabled;
                if (model.ServerNote) Label("room.server_note", L10n.T("room.server_pending"));
                GUILayout.EndVertical(); GUILayout.EndHorizontal();
                Button("room.leave", L10n.T(model.Steam ? "csroom.act.leave" : "common.cancel"), delegate {
                    if (model.InGame) { CloseInGame(); NetPlay.End("left Steam room"); }
                    else if (model.Steam) Back();
                    else LeaveRoom();
                });
                GUILayout.EndArea();
            }
            finally { NativeGui.EndPage(); }
        }

        static readonly bool MutateRoomCancel = System.Environment.GetEnvironmentVariable("ORICOOP_MUTATE_ROOM_CANCEL_NAV") == "1";
        static void LeaveRoom()
        {
            if (MutateRoomCancel) { Reset(); NetPlay.End("left room"); return; }
            NativeModeMenu.LeaveRoomPage();
        }
    }
}
