using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using OriCoop.Rooms;

namespace OriCoop
{
    public static partial class NativeModeMenu
    {
        static readonly List<Action> s_roomActions = new List<Action>();
        static string s_roomsCode = "", s_roomsPassword = "";
        static bool IsRoomsPage { get { return (s_page >= PageRooms && s_page <= PageRoomSlots) || s_page == PageRoomsMenu; } }
        static bool s_roomsList;   // T-095: the room list page (房间列表) is open; false = the 我的房间 submenu

        internal static void OpenRooms() { OpenRooms(null); }

        // lastRoom != null: the 上次游玩 row, open that room once the room service answers.
        internal static void OpenRooms(string lastRoom)
        {
            s_transport = "relay";
            s_roomsList = false;
            RoomsPanel.Open(lastRoom);
            GoPage(PageRoomsMenu, 0);
        }

        static bool s_roomProbeWasOpen;
        static int s_roomProbePage, s_roomProbeFocus;
        internal static bool L10nRoomsOpen()
        {
            s_roomProbeWasOpen = IsOpen;
            s_roomProbePage = s_page;
            s_roomProbeFocus = s_mgr == null ? 0 : s_mgr.Index;
            if (!IsOpen && !OpenTitleOrbForInvite()) return false;
            OpenRooms();
            return true;
        }

        internal static void L10nRoomsClose()
        {
            RoomsPanel.Instance.Back();
            if (s_roomProbeWasOpen) GoPage(s_roomProbePage, s_roomProbeFocus);
            else { Destroy(); RestoreTitleMain(); }
        }

        internal static void ShowRoomError()
        {
            if (!IsOpen) OpenTitleOrbForInvite();
            if (IsOpen) GoPage(RoomPage(RoomsPanel.Instance.Screen), 0);
        }

        static int RoomPage(string screen)
        {
            switch (screen)
            {
                case "room": return PageRoomView;
                case "create": return PageRoomCreate;
                case "join": return PageRoomJoin;
                case "password": return PageRoomPassword;
                case "slots": return PageRoomSlots;
                default: return s_roomsList ? PageRooms : PageRoomsMenu;
            }
        }

        static void SyncRoomPage()
        {
            RoomsPanel p = RoomsPanel.Instance;
            int next = RoomPage(p.Screen);
            if (next == s_page) return;
            if (next == PageRoomPassword || next == PageRoomCreate || (next == PageRoomJoin && (s_page == PageRooms || s_page == PageRoomsMenu)))
                s_roomsPassword = "";
            if (next == PageRoomJoin && (s_page == PageRooms || s_page == PageRoomsMenu)) s_roomsCode = "";
            GoPage(next, 0);
        }

        static string RF(string key, bool zh, params object[] args) { return string.Format(L10n.T(key, zh), args); }

        static void RoomRow(List<string[]> rows, string label, string description, Action press)
        {
            rows.Add(new[] { label, description });
            s_roomActions.Add(press);
        }

        static string RoomDialLabel(bool code, bool zh)
        {
            string value = code ? s_roomsCode : s_roomsPassword;
            bool editing = s_editing && (code ? s_entryKind == 0 : s_entryKind == 5);
            if (editing) value = s_dial != null ? s_dial.Render(true, Mark) : EntryDisplay(zh);
            return L10n.T(code ? "rooms.code" : "rooms.password", zh) + ": " + (value.Length == 0 ? "_" : value);
        }

        static void RoomDialRow(List<string[]> rows, bool code, bool zh)
        {
            RoomRow(rows, RoomDialLabel(code, zh), L10n.T(code ? "lobby.dial.help" : "rooms.password_dial", zh)
                + (code ? "\n" + L10n.T("lobby.room.legacy", zh) : ""), delegate { StartRoomEdit(code); });
        }

        static string[][] RoomRows(bool zh)
        {
            var rows = new List<string[]>();
            s_roomActions.Clear();
            RoomsPanel p = RoomsPanel.Instance;
            bool mutated = Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1"
                && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_ROOMS_NATIVE_OFF") == "1";
            if (!mutated)
            {
                if (p.Busy) RoomRow(rows, L10n.T("rooms.connecting", zh), p.Message, null);
                else
                {
                    if (p.Message.Length > 0)
                        RoomRow(rows, RF("rooms.title", zh, p.ServiceName), p.Message, null);
                    if (s_page == PageRoomsMenu)
                    {
                        RoomRow(rows, L10n.T("rooms.create", zh), L10n.T(p.Online ? "rooms.choose_save" : "rooms.offline", zh),
                            delegate { if (p.Online) p.BeginCreate(); });
                        RoomRow(rows, L10n.T("rooms.list", zh), L10n.T("rooms.list.desc", zh),
                            delegate { s_roomsList = true; });
                        RoomRow(rows, L10n.T("rooms.join_room", zh), L10n.T(p.Online ? "lobby.room.legacy" : "rooms.offline", zh),
                            delegate { if (p.Online) p.BeginJoin(); });
                    }
                    else if (s_page == PageRooms)
                    {
                        if (!p.Online && p.Message.Length == 0)
                            RoomRow(rows, RF("rooms.title", zh, p.ServiceName), L10n.T("rooms.offline", zh), null);
                        if (p.Online && p.Available && (p.Rooms == null || p.Rooms.Count == 0) && p.Message.Length == 0)
                            RoomRow(rows, L10n.T("rooms.empty", zh), L10n.T("rooms.empty.desc", zh), null);
                        if (p.Available && p.Rooms != null)
                            foreach (RoomBackupInfo room in p.Rooms)
                            {
                                string id = room.Id;
                                string desc = RoomDescription(p, room, zh);
                                RoomRow(rows, RF("rooms.save_count", zh, room.Name, room.Saves.Count), desc,
                                    room.Server == p.Server && p.Online ? (Action)delegate { p.OpenRoom(id); } : null);
                                if (room.Server != p.Server || !p.Online)
                                    foreach (RoomBackupSave save in room.Saves)
                                    {
                                        string sid = save.Id;
                                        RoomRow(rows, RF("rooms.copy_named", zh, save.Name), RoomsPanel.FormatSummary(save.Summary, zh),
                                            delegate { p.CopyLocal(id, sid); });
                                    }
                            }
                    }
                    else if (s_page == PageRoomView && p.Info != null)
                    {
                        RoomWire.Fields info = p.Info;
                        string members = "";
                        for (int i = 0; i < info.GetLong("m.count", 0); i++)
                            members += RF("rooms.member", zh, info.GetLong("m." + i + ".seat", 0), info.GetString("m." + i + ".nick")) + "\n";
                        RoomRow(rows, info.GetString("name") + " - " + info.GetString("code"),
                            RF("rooms.share", zh, info.GetString("code")) + "\n" + members
                            + RF("rooms.last_played", zh, RoomsPanel.TimeText(info.GetLong("played", 0))), null);
                        for (int i = 0; i < info.GetLong("save.count", 0); i++)
                        {
                            string prefix = "save." + i + ".";
                            string save = info.GetString(prefix + "id");
                            string name = info.GetString(prefix + "name");
                            string desc = RoomsPanel.FormatSummary(info.GetString(prefix + "summary"), zh);
                            for (int seat = 1; seat <= 2; seat++)
                                desc += "\n" + RF("rooms.seat_seconds", zh, seat, info.GetLong(prefix + "secs.seat." + seat, 0));
                            desc += "\n" + RF("rooms.saved", zh, info.GetLong(prefix + "secs.both", 0),
                                p.MemberName(info.GetString(prefix + "by")), RoomsPanel.TimeText(info.GetLong(prefix + "at", 0)));
                            string holder = info.GetString(prefix + "holder");
                            if (holder.Length > 0) desc += "\n" + RF("rooms.playing", zh, p.MemberName(holder));
                            RoomRow(rows, L10n.T("rooms.continue", zh) + " " + RF("rooms.save_version", zh, name, info.GetString(prefix + "version")),
                                desc, delegate { p.Enter(save); });
                            RoomRow(rows, RF("rooms.copy_named", zh, name), desc, delegate { p.CopySave(save); });
                        }
                    }
                    else if (s_page == PageRoomCreate)
                    {
                        RoomRow(rows, L10n.T("rooms.name", zh) + ": " + p.AutoName, L10n.T("rooms.auto_name.desc", zh), null);
                        RoomDialRow(rows, false, zh);
                        for (int i = 0; i < SaveSlotsManager.SaveSlotCount; i++)
                        {
                            int slot = i;
                            if (!File.Exists(RoomGame.PathFor(slot))) continue;
                            RoomRow(rows, RF("rooms.create_slot", zh, slot + 1, ""), RoomsPanel.FormatSummary(RoomGame.Summary(slot), zh),
                                delegate { p.CreateWithSlot(slot, s_roomsPassword); });
                        }
                    }
                    else if (s_page == PageRoomJoin)
                    {
                        RoomDialRow(rows, true, zh);
                        RoomDialRow(rows, false, zh);
                        RoomRow(rows, L10n.T("rooms.join", zh), L10n.T("lobby.connect.desc", zh),
                            delegate { p.JoinWithCode(s_roomsCode, s_roomsPassword); });
                    }
                    else if (s_page == PageRoomPassword)
                    {
                        RoomDialRow(rows, false, zh);
                        RoomRow(rows, L10n.T("rooms.confirm", zh), L10n.T("rooms.password_again", zh),
                            delegate { p.ConfirmPassword(s_roomsPassword); });
                    }
                    else if (s_page == PageRoomSlots)
                    {
                        for (int i = 0; i < SaveSlotsManager.SaveSlotCount; i++)
                        {
                            int slot = i;
                            RoomRow(rows, p.Overwrite == slot ? RF("rooms.overwrite", zh, slot + 1) : RF("rooms.slot_name", zh, slot + 1),
                                RoomsPanel.FormatSummary(RoomGame.Summary(slot), zh), delegate { p.PickSlot(slot); });
                        }
                    }
                }
            }
            RoomRow(rows, L10n.T("common.back", zh), L10n.T("common.back.desc", zh), RoomBack);
            return rows.ToArray();
        }

        static string RoomDescription(RoomsPanel p, RoomBackupInfo room, bool zh)
        {
            string desc = "";
            foreach (RoomBackupMember member in room.Members) desc += RF("rooms.member", zh, member.Seat, member.Nick) + "\n";
            RoomWire.Fields detail;
            if (p.Refreshed.TryGetValue(room.Id, out detail))
            {
                desc += RF("rooms.last_played", zh, RoomsPanel.TimeText(detail.GetLong("played", 0)));
                for (int i = 0; i < detail.GetLong("save.count", 0); i++)
                {
                    string holder = detail.GetString("save." + i + ".holder");
                    foreach (RoomBackupMember member in room.Members)
                        if (holder.Length > 0 && member.Steam == holder) desc += "\n" + RF("rooms.playing", zh, member.Nick);
                }
            }
            return desc;
        }

        static void RoomPressed(int index)
        {
            RoomsPanel p = RoomsPanel.Instance;
            if (RoomPage(p.Screen) != s_page || index < 0 || index >= s_roomActions.Count) return;
            if (p.Busy && index != s_roomActions.Count - 1) return;
            Action press = s_roomActions[index];
            if (press == null) return;
            press();
            if (IsRoomsPage) { SyncRoomPage(); RefreshTexts(true); }
        }

        static void RoomBack()
        {
            RoomsPanel p = RoomsPanel.Instance;
            if (s_page == PageRooms) { s_roomsList = false; GoPage(PageRoomsMenu, 1); return; }
            p.Back();
            if (!p.Visible) GoPage(PageServerMode, ServerModeRoomsRow);
            else SyncRoomPage();
        }

        static void StartRoomEdit(bool code)
        {
            s_entryKind = code ? 0 : 5;
            s_entry = code ? s_roomsCode : s_roomsPassword;
            s_legacyCode = code && HasLegacyLetter(s_entry);
            s_cursor = 0;
            StartEdit();
        }

        static void StopRoomEdit()
        {
            if (s_entryKind == 0) s_roomsCode = s_entry;
            else s_roomsPassword = s_entry;
            s_dial = null;
            s_editing = false;
            s_mgr.IsLocked = true;
            s_unlockAt = Time.realtimeSinceStartup + 0.25f;
            RefreshTexts(true);
            OnHighlightChanged();
        }

        static bool RoomCodeFocused { get { return s_page == PageRoomJoin && s_mgr.Index == (RoomsPanel.Instance.Message.Length > 0 ? 1 : 0); } }
        static bool RoomPasswordFocused
        {
            get
            {
                int offset = RoomsPanel.Instance.Message.Length > 0 ? 1 : 0;
                return (s_page == PageRoomJoin || s_page == PageRoomCreate) ? s_mgr.Index == offset + 1
                    : s_page == PageRoomPassword && s_mgr.Index == offset;
            }
        }

        static void RoomTick()
        {
            SyncRoomPage();
            if (s_editing) EditTick();
            else if (!s_mgr.IsLocked && !RoomsPanel.Instance.Busy && (RoomCodeFocused || RoomPasswordFocused))
            {
                bool code = RoomCodeFocused;
                if (code && HasLegacyLetter(Input.inputString))
                {
                    StartRoomEdit(true);
                    s_dial = null; s_legacyCode = true; s_entry = ""; Insert(TypedChars()); RefreshTexts(true);
                }
                else
                {
                    int key = DigitKeyDown();
                    if (key >= 0) { StartRoomEdit(code); if (s_dial != null) { s_dial.Type(key); DialWriteBack(); } else Insert(key.ToString()); RefreshTexts(true); }
                }
            }
            if (Time.realtimeSinceStartup >= s_nextRefresh) RefreshTexts(false);
        }

        internal static int TestServerModeRoomsRow { get { return ServerModeRoomsRow; } }
        static bool RoomTestOn { get { return Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1"; } }
        internal static string[] TestRoomRows() { return RoomTestOn && IsOpen && IsRoomsPage ? LastLabels.ToArray() : new string[0]; }
        internal static string TestRoomState()
        {
            if (!RoomTestOn) return "";
            RoomsPanel p = RoomsPanel.Instance;
            return "page=" + LastPage + (p == null ? "" : ", screen=" + p.Screen + ", busy=" + p.Busy + ", message=" + p.Message);
        }
        internal static bool TestRoomPress(int index)
        {
            if (!RoomTestOn || !IsOpen || !IsRoomsPage || s_mgr.IsLocked || s_editing
                || index < 0 || index >= LastLabels.Count || (RoomsPanel.Instance.Busy && index != LastLabels.Count - 1)) return false;
            s_mgr.SetCurrentItem(index);
            s_mgr.PressCurrentItem();
            return true;
        }
        internal static bool TestRoomPress(string label)
        {
            string[] rows = TestRoomRows();
            for (int i = 0; i < rows.Length; i++) if (rows[i].IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0) return TestRoomPress(i);
            return false;
        }
        internal static bool TestSetDial(string digits)
        {
            if (!RoomTestOn || !IsRoomsPage || !s_editing) return false;
            if (s_entryKind == 0 && HasLegacyLetter(digits)) { s_legacyCode = true; s_entry = digits.ToUpperInvariant(); }
            else
            {
                s_legacyCode = false;
                if (s_dial == null) s_dial = s_entryKind == 0 ? OriCoop.Ui.DigitDial.Room() : OriCoop.Ui.DigitDial.Password();
                s_dial.Load(digits); DialWriteBack();
            }
            StopRoomEdit();
            return true;
        }
        internal static bool TestRoomBack()
        {
            if (!RoomTestOn || !IsOpen || !IsRoomsPage || s_mgr.IsLocked) return false;
            s_mgr.OnBackPressed();
            return true;
        }
    }
}
