namespace OriCoop.Ui
{
    internal enum RoomEntryKind { None, TitleHost, MenuHost, ListJoin, CodeJoin, MyRoomsJoin }
    internal enum RoomLeaveDestination { None, SaveSlots, RoomList, CodeEntry, MyRooms }

    // T-176: return to the page that opened the room (or the slot list for a title host).
    internal static class RoomLeaveFlow
    {
        public static RoomLeaveDestination Destination(RoomEntryKind entry)
        {
            switch (entry)
            {
                case RoomEntryKind.TitleHost: return RoomLeaveDestination.SaveSlots;
                case RoomEntryKind.MenuHost:
                case RoomEntryKind.ListJoin: return RoomLeaveDestination.RoomList;
                case RoomEntryKind.CodeJoin: return RoomLeaveDestination.CodeEntry;
                case RoomEntryKind.MyRoomsJoin: return RoomLeaveDestination.MyRooms;
                default: return RoomLeaveDestination.None;
            }
        }
    }
}
