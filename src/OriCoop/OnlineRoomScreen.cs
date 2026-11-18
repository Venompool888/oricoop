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
        static float ignoreUntil;
        static Vector2 memberScroll, saveScroll;
        static string[] addresses = new string[0];
        public static bool Visible { get; private set; }
        public static bool Confirmed { get; private set; }
        internal static int DrawnSeatMask;
        internal static bool DrawnAddEnabled;
        public static void Reset()
        {
            Visible = Confirmed = false; proceed = null; buttons.Clear(); drawn.Clear();
            NativeModeMenu.RoomPanelVisible(false);
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
            drawn.Clear(); buttons.Clear();
            addresses = NetPlay.IsHost && NetPlay.TransportName == "direct" ? NetPlay.LocalAddresses() : new string[0];
            NativeModeMenu.RoomPanelVisible(true);
        }
        public static void Continue()
        {
            if (!Visible) return;
            Confirmed = true; Visible = false;
            NativeModeMenu.RoomPanelVisible(false);
            Action next = proceed; proceed = null;
            if (next != null) next();
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
        internal static bool Click(string id)
        {
            Action action;
            if (!Visible || !drawn.TryGetValue(id, out action)) return false;
            action(); return true;
        }
        public static void Tick()
        {
            if (!Visible || Time.unscaledTime < ignoreUntil) return;
            if (!NetPlay.Enabled) { Reset(); return; }
            if (buttons.Count == 0) return;
            if (Core.Input.MenuDown.OnPressed || Input.GetKeyDown(KeyCode.DownArrow)) focus = (focus + 1) % buttons.Count;
            if (Core.Input.MenuUp.OnPressed || Input.GetKeyDown(KeyCode.UpArrow)) focus = (focus + buttons.Count - 1) % buttons.Count;
            if (Core.Input.ActionButtonA.OnPressed || Input.GetKeyDown(KeyCode.Return)) buttons[Mathf.Clamp(focus, 0, buttons.Count - 1)]();
        }
        public static void Draw()
        {
            if (!Visible) return;
            if (!NetPlay.Enabled) { Reset(); return; }
            NativeModeMenu.RoomPanelVisible(true);
            NativeGui.BeginPage("online.room");
            try
            {
                GUI.depth = -250;
                buttons.Clear();
                if (Event.current.type == EventType.Repaint) drawn.Clear();
                Rect area = new Rect(Screen.width * 0.08f, Screen.height * 0.08f, Screen.width * 0.84f, Screen.height * 0.84f);
                NativeGui.Panel("room.background", area, new Color(0.12f, 0.12f, 0.12f, 0.96f));
                GUILayout.BeginArea(area);
                Label("room.title", L10n.T("room.title"));
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(area.width * 0.48f));
                Label("room.saves", L10n.T("room.saves"));
                saveScroll = GUILayout.BeginScrollView(saveScroll, GUILayout.Height(Mathf.Max(60, area.height - 145)));
                if (NetPlay.RoomCode.Length > 0) Label("room.code", L10n.T("lobby.room.code") + ": " + NetPlay.RoomCode);
                foreach (string address in addresses) Label("room.address." + address, L10n.T("lobby.your_address") + ": " + address + ":" + NetPlay.ConfigPort);
                if (NetPlay.IsHost && NetPlay.UsesSteam) Button("room.invite", L10n.T("room.invite"), NetPlay.OpenSteamInvite);
                if (NetPlay.IsHost)
                {
                    for (int slot = 0; slot < SaveSlotsManager.SaveSlotCount; slot++)
                    {
                        if (slot != SaveSlotsManager.CurrentSlotIndex && !System.IO.File.Exists(Rooms.RoomGame.PathFor(slot))) continue;
                        int selected = slot;
                        bool saveEnabled = GUI.enabled;
                        GUI.enabled = saveEnabled && NativeModeMenu.CanSelectRoomSave;
                        Button("room.save." + slot, (slot == SaveSlotsManager.CurrentSlotIndex ? "* " : "") + L10n.F("rooms.slot_summary", slot + 1, Rooms.RoomGame.Summary(slot)),
                            delegate { NativeModeMenu.SelectRoomSave(selected); });
                        GUI.enabled = saveEnabled;
                    }
                }
                else Label("room.hostsave", L10n.T("room.host_save"));
                GUILayout.EndScrollView();
                Button("room.continue", L10n.T(NetPlay.IsHost ? "lobby.start" : "rooms.continue"), Continue);
                GUILayout.EndVertical();
                GUILayout.BeginVertical();
                Label("room.members", L10n.T("room.members"));
                memberScroll = GUILayout.BeginScrollView(memberScroll, GUILayout.Height(Mathf.Max(60, area.height - 145)));
                DrawnSeatMask = 0;
                for (int seat = 1; seat <= 4; seat++)
                {
                    int owner = NetPlay.SeatOwner(seat);
                    if (owner == 0 && NetPlay.MultiSeatSupported) continue;
                    if (owner == 0 && seat != Players.LocalSeat && Array.IndexOf(NetPlay.RemoteSeats, seat) < 0) continue;
                    if (owner == 0) owner = seat;
                    DrawnSeatMask |= 1 << seat;
                    bool local = Players.IsLocal(seat);
                    Color old = GUI.contentColor; GUI.contentColor = Roles.ColorOf(seat);
                    Label("room.member." + seat, L10n.F("room.member", seat, owner, L10n.T(local ? "room.local" : "room.remote")));
                    GUI.contentColor = old;
                    if (!local) continue;
                    GUILayout.BeginHorizontal();
                    int localSeat = seat;
                    Button("room.input." + seat, InputText(seat), delegate { NextInput(localSeat); });
                    if (seat != Players.LocalSeat) Button("room.remove." + seat, L10n.F("coop.remove_local", seat), delegate { seats.RemoveLocalPlayer(localSeat); });
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                bool enabled = GUI.enabled;
                DrawnAddEnabled = NetPlay.CanAddLocal;
                GUI.enabled = enabled && DrawnAddEnabled;
                Button("room.add", L10n.T("room.add_local"), delegate { Add(); });
                GUI.enabled = enabled;
                if (!NetPlay.MultiSeatSupported) Label("room.server_note", L10n.T("room.server_pending"));
                GUILayout.EndVertical(); GUILayout.EndHorizontal();
                Button("room.leave", L10n.T("common.cancel"), delegate { Reset(); NetPlay.End("left room"); });
                GUILayout.EndArea();
            }
            finally { NativeGui.EndPage(); }
        }
    }
}
