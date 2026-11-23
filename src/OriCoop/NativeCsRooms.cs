using System;
using System.Collections.Generic;
using System.Net;
using OriCoop.Authority;
using OriCoop.Ui;

namespace OriCoop
{
    // T-171: 自建服务器 -> 服务器列表 -> a server = the room list (owner's sketch): 创建房间 / 加入房间 / the rooms open on
    // that server (房间 <code>  2/4, the room's save under it). Picking a room joins it with its code. The list is read
    // in the background (CServerRoomList, CsList) when the page opens and every few seconds while it is shown.
    public static partial class NativeModeMenu
    {
        // T-173: list state per server; presses map to the rows last drawn (CsRoomListCache, tested in CServerClientTest).
        static readonly CsRoomListCache s_csRooms = new CsRoomListCache();

        static bool CsRoomListPage { get { return s_page == PageRole && s_transport == "cserver"; } }

        static void CsRoomsFetch()
        {
            string address = CServerConfig.Address ?? "";
            int generation = s_csRooms.BeginFetch(address, UnityEngine.Time.realtimeSinceStartup, 5.0);
            if (generation < 0) return;
            // T-201: a thread of its own, not the thread pool: the pool is shared with the HTTP calls of the room service,
            // and a call hanging on an unreachable server keeps its pool thread for the whole connect timeout.
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string error = "no address";
                List<CsRoomListEntry> rooms = null;
                try
                {
                    EndPoint server = CServerLink.Resolve(address.Trim());   // DNS may block: on the worker
                    if (server != null) rooms = CServerRoomList.Fetch(server, 3000, out error);
                }
                catch (Exception e) { error = e.Message; }
                if (rooms == null) Plugin.Log.LogInfo("cs rooms: list failed: " + error);
                if (!s_csRooms.Complete(generation, rooms, error)) Plugin.Log.LogInfo("cs rooms: dropped a list for a previous server");
            });
            worker.IsBackground = true;
            worker.Name = "OriCoop room list";
            worker.Start();
        }

        static void CsRoomListRows(List<string[]> r, bool zh)
        {
            CsRoomsFetch();
            r.Add(new[] { L10n.T("csroom.list.create", zh), L10n.T("csroom.list.create.desc", zh) });
            r.Add(new[] { L10n.T("csroom.list.join", zh), L10n.T("csroom.list.join.desc", zh) });
            List<CsRoomListEntry> rooms = s_csRooms.TakeShown(UnityEngine.Time.frameCount);
            foreach (CsRoomListEntry room in rooms)
            {
                CsRoomSummary summary = CsRoomSummary.Parse(room.Text);
                string name = summary.Name.Length > 0 ? summary.Name : room.Code;   // T-171: the English room name, else the code
                r.Add(new[] { string.Format(L10n.T("csroom.list.room", zh), name, room.Online), CsRoomRowDesc(room, zh) });
            }
            if (rooms.Count == 0)
            {
                // T-201: a failed read says so and why, and stays "failed" while the page reads again in the background;
                // pressing the row reads again at once (CsRoomListPressed), Back leaves the page.
                int state = s_csRooms.State;
                if (state == CsRoomListCache.Failed)
                    r.Add(new[] { L10n.T("csroom.list.failed", zh), L10n.T(CsRoomListCache.FailureKey(s_csRooms.Error), zh) });
                else
                    r.Add(new[] { L10n.T(state == CsRoomListCache.Loaded ? "csroom.list.none" : "csroom.list.loading", zh), L10n.T("csroom.list.create.desc", zh) });
            }
        }

        // A room row's description: the code line (unique per room), the save line, the players.
        static string CsRoomRowDesc(CsRoomListEntry room, bool zh)
        {
            string save = CsRoomSaveLine(CsRoomSummary.Parse(room.Text), zh);
            return string.Format(L10n.T("csroom.list.code", zh), room.Code) + "\n" + (save.Length > 0 ? save + "\n" : "") + CsRoomListCache.PlayersLine(room);
        }

        // The description text ApplyRows draws for a room row (the same language pick as ApplyRows).
        static string CsRoomDrawnDesc(CsRoomListEntry room) { return PickResolved(CsRoomRowDesc(room, true), CsRoomRowDesc(room, false)); }

        // "1: 沉没林地 - 6%  01:10:14" from a room summary (empty without a save).
        internal static string CsRoomSaveLine(CsRoomSummary s, bool zh)
        {
            if (!s.HasSave) return "";
            string area = SaveSlotsScreenshotManager.Instance != null ? SaveSlotsScreenshotManager.Instance.FindAreaName(s.Area) : "";
            if (string.IsNullOrEmpty(area)) area = s.Area;
            string text = (s.Slot >= 0 ? (s.Slot + 1) + ": " : "") + area + (s.Completion >= 0 ? " - " + s.Completion + "%" : "");
            if (s.Seconds >= 0) text += "  " + CsRoomSummary.Clock(s.Seconds);
            return text;
        }

        // A press on the room list: 0 create (the old 开房), 1 join by code (the old 加入), 2.. a listed room.
        static bool CsRoomListPressed(int index)
        {
            if (index < 2) return false;
            // T-173 (review F15): the room whose text is drawn on that row now. A cursor move rebuilds the rows (and takes
            // a newer list) without drawing them, so the row index alone can point into a list the player never saw.
            string drawn = index < s_descCache.Length ? s_descCache[index] : null;
            CsRoomListEntry room = s_csRooms.Drawn(index - 2, drawn, CsRoomDrawnDesc);
            if (room == null) { s_csRooms.ReloadNow(); CsRoomsFetch(); return true; }   // the status row: reload now
            if (!ValidateCServer()) return true;
            s_joinTarget = CServerConfig.Address + "|" + room.Code;
            s_roomEntry = RoomEntryKind.ListJoin;
            GoPage(PageJoin, 1);
            Plugin.Log.LogInfo("mode menu: join listed room " + room.Code);
            BeginCServer(false, s_joinTarget);
            return true;
        }
    }
}
