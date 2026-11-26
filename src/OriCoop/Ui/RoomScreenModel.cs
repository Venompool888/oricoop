using System;
using System.Collections.Generic;

namespace OriCoop.Ui
{
    // T-174: everything the room page (OnlineRoomScreen) draws with GUILayout, read once per frame. IMGUI calls Draw
    // for Layout and again for Repaint / input events, and GUILayout needs the same controls in every one of them.
    // The page used to read seat owners, the room code, save files etc. live on each event; when one of them changed
    // between Layout and Repaint (the Steam friends lobby landing on the room's first frame), Repaint asked for a member
    // row Layout never measured: "Getting control 0's position in a group with only 0 controls".
    internal sealed class RoomScreenModel
    {
        internal bool IsHost, ShowInvite, AddEnabled, ServerNote;
        internal bool Steam, InGame, SteamReady, ShowFriends, HasSpace, RetryHost;
        internal string SteamTitle = "", SteamStatus = "";
        internal readonly List<string> SteamMembers = new List<string>();
        internal readonly List<KeyValuePair<ulong, string>> Friends = new List<KeyValuePair<ulong, string>>();
        internal string RoomCode = "";
        internal string[] Addresses = new string[0];
        internal readonly List<RoomSaveRow> Saves = new List<RoomSaveRow>();
        internal readonly List<RoomMemberRow> Members = new List<RoomMemberRow>();

        internal int SeatMask
        {
            get { int mask = 0; foreach (RoomMemberRow m in Members) mask |= 1 << m.Seat; return mask; }
        }
    }

    internal sealed class RoomSaveRow
    {
        internal int Slot;
        internal bool Current;
        internal string Summary;
    }

    internal sealed class RoomMemberRow
    {
        internal int Seat, Owner;
        internal bool Local, Removable;
    }

    internal static class RoomScreenRows
    {
        // Same rules the page always had: with multi-seat support only owned seats; on a session without it (C-server
        // factory session) the own seat and the remote seats, each its own owner. Extra local seats can be removed.
        internal static void Members(List<RoomMemberRow> rows, Func<int, int> seatOwner, bool multiSeat, int localSeat,
            int[] remoteSeats, Func<int, bool> isLocal)
        {
            rows.Clear();
            for (int seat = 1; seat <= 4; seat++)
            {
                int owner = seatOwner(seat);
                if (owner == 0 && multiSeat) continue;
                if (owner == 0 && seat != localSeat && Array.IndexOf(remoteSeats, seat) < 0) continue;
                if (owner == 0) owner = seat;
                bool local = isLocal(seat);
                rows.Add(new RoomMemberRow { Seat = seat, Owner = owner, Local = local, Removable = local && seat != localSeat });
            }
        }
    }

    // One value per IMGUI frame: built on the Layout event and handed back unchanged to the events after it. A page
    // that was not there at Layout (opened mid-frame) gets null and draws nothing until the next Layout.
    internal sealed class LayoutSnapshot<T> where T : class
    {
        T m_value;

        internal T Get(bool layoutEvent, Func<T> build)
        {
            if (layoutEvent) m_value = build();
            return m_value;
        }

        internal void Clear() { m_value = null; }
    }
}
