using System;
using System.Collections.Generic;
using OriCoop.Ui;

namespace OriCoop.Rooms
{
    internal static class MyRooms
    {
        static string prepareError;
        static float nextRefresh;

        public static bool Busy { get { return RoomsPanel.Instance != null && RoomsPanel.Instance.Busy; } }
        public static string Message { get { return prepareError ?? (RoomsPanel.Instance == null ? "" : RoomsPanel.Instance.Message); } }

        public static void Prepare(string server)
        {
            prepareError = null;
            server = (server ?? "").Trim();
            if (server.Length > 0 && server != Authority.CServerConfig.Address
                && !Authority.CServerConfig.SetAddress(server))
            {
                prepareError = RoomsPanel.ErrorText("bad_request");
                return;
            }
            RoomsPanel p = RoomsPanel.Instance;
            if (p == null || p.GameServer != Authority.CServerConfig.Address || !p.Available || (!p.Online && !p.Busy))
                RoomsPanel.Open();
            RoomsPanel.Instance.UseOpenRooms();
        }

        // The local rooms of the selected room service only (review F21): another service's rooms can be neither
        // entered nor deleted through this one.
        static List<RoomBackupInfo> LocalRooms()
        {
            RoomsPanel p = RoomsPanel.Instance;
            return p == null ? new List<RoomBackupInfo>() : MyRoomsRules.OnServer(p.Rooms, p.Server);
        }

        public static List<MyRoomsListEntry> ListRooms()
        {
            Prepare("");
            List<MyRoomsListEntry> result = new List<MyRoomsListEntry>();
            RoomsPanel p = RoomsPanel.Instance;
            if (p == null || p.Rooms == null) return result;
            List<RoomBackupInfo> rooms = LocalRooms();
            rooms.Sort(delegate(RoomBackupInfo a, RoomBackupInfo b) { return b.Played.CompareTo(a.Played); });
            foreach (RoomBackupInfo room in rooms)
            {
                RoomWire.Fields info;
                p.Refreshed.TryGetValue(room.Id, out info);
                bool creator = false;
                foreach (RoomBackupMember member in room.Members)
                    if (member.Seat == 1 && member.Steam == p.Steam) creator = true;
                result.Add(new MyRoomsListEntry
                {
                    Id = room.Id,
                    Name = info == null ? room.Name : info.GetString("name"),
                    Number = info == null ? room.Number : info.GetLong("no", -1),
                    Members = info == null ? room.Members.Count : (int)info.GetLong("m.count", 0),
                    Seats = MyRoomsRules.Seats(info, room),
                    Creator = creator
                });
            }
            return result;
        }

        public static int RoomCount { get { return LocalRooms().Count; } }

        static void WhenReady(Action<string> done)
        {
            if (prepareError != null) done(prepareError);
            else RoomsPanel.Instance.WhenReady(done);
        }

        // Create / join / enter: done(null) once the room session hosts or joins (RoomGame.Host / Join), done(error)
        // when anything before that fails (enter and download included). Cancel() drops the running one silently.
        public static void CreateFromSlot(int slot, string name, Action<string> done)
        {
            Prepare("");
            if (prepareError != null) { done(prepareError); return; }
            RoomsPanel.Instance.CreateOpen(slot, name, done);
        }

        public static void JoinByCode(string server, string code, Action<string> done)
        {
            Prepare(server);
            WhenReady(delegate(string error)
            {
                if (error != null) done(error);
                else RoomsPanel.Instance.JoinOpen(code, done);
            });
        }

        public static void EnterRoom(string room, Action<string> done)
        {
            Prepare("");
            WhenReady(delegate(string error)
            {
                if (error != null) done(error);
                else RoomsPanel.Instance.EnterOpenRoom(room, done);
            });
        }

        public static void DeleteRoom(string room, Action<string> done)
        {
            Prepare("");
            WhenReady(delegate(string error)
            {
                if (error != null) done(error);
                else RoomsPanel.Instance.DeleteOpenRoom(room, done);
            });
        }

        // Esc on a busy 我的房间 page: the running create / join / enter is abandoned (no done callback, no entering
        // the room afterwards).
        public static void Cancel()
        {
            if (RoomsPanel.Instance != null) RoomsPanel.Instance.CancelMyRooms();
        }

        public static bool InRoom
        {
            get
            {
                RoomsPanel p = RoomsPanel.Instance;
                return p != null && p.Session != null && p.Session.Active && p.Info != null && p.Info.GetString("open") == "1";
            }
        }

        // Pending on the server and 开始 not pressed on this PC yet (a pressed 开始 whose confirm is still being retried
        // counts as confirmed, review F24).
        public static bool Pending
        {
            get
            {
                return InRoom && RoomsPanel.Instance.Info.GetString("pending") == "1"
                    && !RoomsPanel.Instance.ConfirmWanted(RoomsPanel.Instance.Auth == null ? null : RoomsPanel.Instance.Auth.Room);
            }
        }

        static bool ServerPending { get { return InRoom && RoomsPanel.Instance.Info.GetString("pending") == "1"; } }

        // Seconds the reopened invite code has left, by the room service's clock (review F28).
        public static long CodeSecondsLeft
        {
            get
            {
                if (!InRoom) return 0;
                return MyRoomsRules.CodeSecondsLeft(RoomsPanel.Instance.Info.GetLong("code_expires", 0),
                    RoomsPanel.Instance.ServerOffset, RoomWire.Now);
            }
        }

        public static string Code
        {
            get
            {
                if (!InRoom) return "";
                RoomWire.Fields info = RoomsPanel.Instance.Info;
                if (Pending) return info.GetString("code");
                if (CodeSecondsLeft <= 0) return "";
                return info.GetString("code");
            }
        }

        // The code's expiry on this PC's clock (server expiry minus the server's clock lead), so "CodeExpires - local
        // now" is the time left whatever this PC's clock says. 0 while no code expires (none, or a pending room's).
        public static long CodeExpires
        {
            get
            {
                return Code.Length == 0 ? 0 : MyRoomsRules.LocalCodeExpiry(RoomsPanel.Instance.Info.GetLong("code_expires", 0),
                    RoomsPanel.Instance.ServerOffset);
            }
        }

        // 开放邀请码 is offered: a confirmed open room with fewer room-service members than seats.
        public static bool CanOpenCode
        {
            get { return InRoom && !Pending && MyRoomsRules.CanOpenCode(RoomsPanel.Instance.Info); }
        }

        public static string RoomName { get { return InRoom ? RoomsPanel.Instance.Info.GetString("name") : ""; } }

        public static void RefreshRoom()
        {
            if (!InRoom || Busy || UnityEngine.Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = UnityEngine.Time.realtimeSinceStartup + 5;
            RoomsPanel.Instance.RefreshOpenRoom();
        }

        // 开始 by the creator: confirm (resent on later info of the room, menu and room page refreshes and an in-game
        // check while the room session runs, until the server has it).
        public static void Confirm()
        {
            if (ServerPending) RoomsPanel.Instance.ConfirmOpenRoom();
        }

        public static void OpenCode()
        {
            if (InRoom) RoomsPanel.Instance.OpenInviteCode();
        }

        // T-173 (room-v2-a): the room page's [E] 改名 in a 我的房间 room also renames it on the room service.
        // done(ok) runs with the room service's reply (not at all when this PC is in no 我的房间 room).
        public static void Rename(string name, Action<bool> done)
        {
            if (InRoom) RoomsPanel.Instance.RenameOpenRoom(name, done);
        }

        public static void AbandonPending()
        {
            if (InRoom) RoomsPanel.Instance.AbandonPendingRoom();
        }
    }
}
