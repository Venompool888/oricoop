using System;

namespace OriCoop.Ui
{
    // T-189: when the pause menu offers 房间管理 (the in-game room page). Plain data, no Unity, so the unit tests drive it.
    // Only a C-server session that is linked to its server and playing: a LAN session has no room and no invite code
    // (the others type this PC's address), and single player has no room at all. Showing an item that only says
    // "not available" confuses more than leaving it out; the game's own menus add and drop items the same way.
    internal static class InGameRoomRules
    {
        public const string CServer = "cserver";

        public static bool ShowPauseEntry(bool online, string transport, bool linked, bool inGameplay)
        {
            return online && transport == CServer && linked && inGameplay;
        }

        // The room page's invite line (T-173 rules, used on the title and in the game). A 我的房间 room: its room-service
        // invite code with the time left (m:ss, 0:00 once past its expiry until the room service drops it), or 未开放
        // when closed. A server-list room: its live room code, no clock. "" = nothing to show. full (the in-game page:
        // every seat taken) = 房间已满: nobody else can come in. now / expires are Unix seconds (expires 0 = no expiry known).
        public static string CodeLine(Func<string, string> text, bool mine, string code, long expires, long now, bool full)
        {
            if (full) return text("csroom.room_full");
            code = code ?? "";
            if (mine)
            {
                if (code.Length == 0) return text("csroom.code_closed");
                string clock = "";
                if (expires > 0)
                {
                    long left = Math.Max(0L, expires - now);
                    clock = (left / 60) + ":" + (left % 60).ToString("D2");
                }
                return string.Format(text("csroom.code"), code, clock).TrimEnd();
            }
            return code.Length > 0 ? string.Format(text("csroom.code"), code, "").TrimEnd() : "";
        }
    }
}
