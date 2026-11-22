using System;
using OriCoop.Ui;

// T-176: leaving returns to the page that owns the next action, not the dead host / join page.
static class RoomLeaveTests
{
    static void GoesTo(RoomEntryKind entry, RoomLeaveDestination expected)
    {
        RoomLeaveDestination actual = RoomLeaveFlow.Destination(entry);
        if (actual != expected) throw new Exception("expected '" + expected + "', got '" + actual + "'");
    }

    public static void Register(Action<string, Action> run)
    {
        run("room leave: title host returns to save slots", delegate { GoesTo(RoomEntryKind.TitleHost, RoomLeaveDestination.SaveSlots); });
        run("room leave: mode menu host returns to the room list", delegate { GoesTo(RoomEntryKind.MenuHost, RoomLeaveDestination.RoomList); });
        run("room leave: listed room join returns to the room list", delegate { GoesTo(RoomEntryKind.ListJoin, RoomLeaveDestination.RoomList); });
        run("room leave: code join returns to code entry", delegate { GoesTo(RoomEntryKind.CodeJoin, RoomLeaveDestination.CodeEntry); });
        run("room leave: My Rooms join returns to My Rooms", delegate { GoesTo(RoomEntryKind.MyRoomsJoin, RoomLeaveDestination.MyRooms); });
        run("room leave: no orb entry leaves without navigating", delegate { GoesTo(RoomEntryKind.None, RoomLeaveDestination.None); });
    }
}
