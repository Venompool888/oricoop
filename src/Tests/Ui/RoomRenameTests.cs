using System;
using System.Collections.Generic;
using OriCoop;
using OriCoop.Ui;

// T-173 (room page layout room-v2-a, [E] 改名). Expected values come from the ticket / owner rules: room names are English
// only (printable ASCII, no MessageBox markup), at most RoomNames.MaxLength (24); Backspace deletes, Enter confirms with the
// cleaned name, an empty or unchanged name keeps the old one (null), Esc cancels; only the room's manager may rename, and
// not once a game is already running.
static class RoomRenameTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("expected '" + expected + "', got '" + actual + "'");
    }

    static CsRoomModel Model() { return new CsRoomModel(delegate (string key) { return L10n.T(key, true); }); }

    static void Seat(CsRoomModel m, int seat, bool local)
    {
        CsRoomSeat s = m.Seats[seat];
        s.Taken = true; s.Online = true; s.Local = local; s.Nick = "p" + seat;
    }

    public static void Register(Action<string, Action> run)
    {
        run("room rename: not editing until Begin; Begin puts the current name in the box", delegate {
            RoomNameEdit e = new RoomNameEdit();
            Equal(false, e.Editing);
            e.Type("abc");
            Equal("", e.Text);
            Equal<string>(null, e.Confirm());
            e.Begin("Glimmer Hollow");
            Equal(true, e.Editing);
            Equal("Glimmer Hollow", e.Text);
        });

        run("room rename: typing appends printable ASCII, Backspace deletes the last character", delegate {
            RoomNameEdit e = new RoomNameEdit();
            e.Begin("");
            e.Type("Spirit");
            e.Type(" ");
            e.Type("Grovx");
            Equal("Spirit Grovx", e.Text);
            e.Backspace();
            Equal("Spirit Grov", e.Text);
            e.Type("e");
            Equal("Spirit Grove", e.Text);
            e.Backspace(); e.Backspace(); e.Backspace(); e.Backspace(); e.Backspace();
            e.Backspace(); e.Backspace(); e.Backspace(); e.Backspace(); e.Backspace();
            e.Backspace(); e.Backspace(); e.Backspace(); e.Backspace();   // more than there is
            Equal("", e.Text);
            Equal(true, e.Editing);
        });

        run("room rename: Chinese, other non-ASCII, control and markup characters are not typed", delegate {
            RoomNameEdit e = new RoomNameEdit();
            e.Begin("");
            e.Type("林地Orié…\t\n*<b>#;=Wisp");
            Equal("OribWisp", e.Text);
            Equal("OribWisp", e.Confirm());
        });

        run("room rename: at most 24 characters (RoomNames.MaxLength)", delegate {
            Equal(24, RoomNames.MaxLength);
            RoomNameEdit e = new RoomNameEdit();
            e.Begin("");
            e.Type("abcdefghijklmnopqrstuvwxyz0123456789");
            Equal("abcdefghijklmnopqrstuvwx", e.Text);
            e.Type("z");
            Equal(24, e.Text.Length);
            e.Backspace();
            e.Type("YZ");
            Equal("abcdefghijklmnopqrstuvwY", e.Text);
        });

        run("room rename: Enter gives the cleaned name (spaces collapsed and trimmed) and ends editing", delegate {
            RoomNameEdit e = new RoomNameEdit();
            e.Begin("Glimmer Hollow");
            while (e.Text.Length > 0) e.Backspace();
            e.Type("  Spirit    Grove  ");
            Equal("Spirit Grove", e.Confirm());
            Equal(false, e.Editing);
            Equal<string>(null, e.Confirm());   // nothing more to apply
        });

        run("room rename: an empty or unchanged name keeps the old one (null)", delegate {
            RoomNameEdit e = new RoomNameEdit();
            e.Begin("Glimmer Hollow");
            Equal<string>(null, e.Confirm());   // unchanged
            Equal(false, e.Editing);
            e.Begin("Glimmer Hollow");
            e.Type("  ");
            Equal<string>(null, e.Confirm());   // "Glimmer Hollow  " cleans to the same name
            e.Begin("Glimmer Hollow");
            while (e.Text.Length > 0) e.Backspace();
            Equal<string>(null, e.Confirm());   // empty
            e.Begin("Glimmer Hollow");
            while (e.Text.Length > 0) e.Backspace();
            e.Type("   ");
            Equal<string>(null, e.Confirm());   // only spaces = empty
        });

        run("room rename: Esc cancels: not editing, nothing to apply, the next edit starts from the old name", delegate {
            RoomNameEdit e = new RoomNameEdit();
            e.Begin("Glimmer Hollow");
            e.Type(" Two");
            e.Cancel();
            Equal(false, e.Editing);
            Equal<string>(null, e.Confirm());
            e.Type("x");
            Equal("Glimmer Hollow", e.Text);
            e.Begin("Glimmer Hollow");
            Equal("Glimmer Hollow", e.Text);
        });

        run("room rename: a markup name from elsewhere is cleaned when the edit starts", delegate {
            RoomNameEdit e = new RoomNameEdit();
            e.Begin("*Ember* <Grove>");
            Equal("Ember Grove", e.Text);
            Equal<string>(null, e.Confirm());   // unchanged against the cleaned name
        });

        run("cs room: only the room's manager may rename (the lowest online seat is local)", delegate {
            CsRoomModel host = Model();
            Seat(host, 1, true);
            Seat(host, 2, false);
            Equal(true, host.IsManager);
            Equal(true, host.CanRename);

            CsRoomModel guest = Model();
            Seat(guest, 1, false);
            Seat(guest, 2, true);
            Equal(false, guest.IsManager);
            Equal(false, guest.CanRename);

            CsRoomModel empty = Model();
            Equal(false, empty.CanRename);   // no roster yet: nobody manages the room
        });

        run("cs room: the creator gone, the next lowest online seat manages and may rename", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, false);
            Seat(m, 2, true);
            m.Seats[1].Online = false;   // held for a reconnect
            Equal(true, m.IsManager);
            Equal(true, m.CanRename);
        });

        // Review (T-173): the name lives in the room's save line; it must reach every PC and stay the room's name whoever
        // manages the room later.
        run("cs lobby state: the creator renames: its line carries the new name, the rest of the line is kept", delegate {
            CsLobbyState s = new CsLobbyState();
            s.Session(true);
            s.RoomName = "Glimmer Hollow";
            s.PageOpened(true, "name=Glimmer Hollow;slot=1;area=sunkenGlades;pct=6;secs=600;diff=1");
            Equal(true, s.CanCarryName(true));
            Equal(true, s.Rename("  Spirit   Grove ", true));
            Equal("Spirit Grove", s.RoomName);
            Equal("name=Spirit Grove;slot=1;area=sunkenGlades;pct=6;secs=600;diff=1", s.LocalSummary);
            Equal(false, s.Rename("林地", true));   // cleans to nothing: refused, nothing changes
            Equal("Spirit Grove", s.RoomName);
        });

        run("cs lobby state: a stand-in manager renames: it publishes the creator's line with the new name and keeps showing it", delegate {
            CsLobbyState s = new CsLobbyState();   // P2: joined the room, P1 (the creator) is held for a reconnect
            s.Session(true);
            s.PageOpened(false, "name=Mine;slot=2;area=valleyOfTheWind");
            Equal("", s.LocalSummary);
            s.ManagerSummary("name=Glimmer Hollow;slot=1;area=sunkenGlades;pct=6");   // from P1 while it managed
            Equal("Glimmer Hollow", s.RoomName);
            Equal(false, s.CanCarryName(false));   // not the manager: nothing to publish
            Equal(false, s.Rename("Spirit Grove", false));
            Equal("Glimmer Hollow", s.RoomName);
            Equal(true, s.CanCarryName(true));
            Equal(true, s.Rename("Spirit Grove", true));
            Equal("name=Spirit Grove;slot=1;area=sunkenGlades;pct=6", s.LocalSummary);
            Equal("name=Spirit Grove;slot=1;area=sunkenGlades;pct=6", s.RoomSummary);
            s.PageOpened(false, "name=Mine;slot=2;area=valleyOfTheWind");   // the page reopens: a stand-in's own line goes
            Equal("", s.LocalSummary);
            Equal("Spirit Grove", CsRoomSummary.Parse(s.RoomSummary).Name);   // ... it still shows the new name
        });

        run("cs lobby state: a manager with no room line yet cannot rename (the name would reach nobody)", delegate {
            CsLobbyState s = new CsLobbyState();
            s.Session(true);
            s.PageOpened(false, "name=Mine;slot=2");
            Equal(false, s.CanCarryName(true));
            Equal(false, s.Rename("Spirit Grove", true));
            Equal("", s.RoomName);
            Equal("", s.LocalSummary);
        });

        run("cs lobby state: the creator takes a stand-in's rename, so it publishes the new name when it manages again", delegate {
            CsLobbyState s = new CsLobbyState();   // P1, the creator
            s.Session(true);
            s.RoomName = "Glimmer Hollow";
            s.PageOpened(true, "name=Glimmer Hollow;slot=1;area=sunkenGlades;pct=6");
            // P1's seat was held, P2 managed and renamed: its LobbyState carries the creator's line with the new name.
            s.ManagerSummary("name=Spirit Grove;slot=1;area=sunkenGlades;pct=6");
            Equal("Spirit Grove", s.RoomName);
            Equal("name=Spirit Grove;slot=1;area=sunkenGlades;pct=6", s.LocalSummary);   // what P1 publishes once it manages again
            // The creator's own later save switch builds its line from RoomName (CsRoomPage.SlotSummary): the new name.
            // A line without a name (an older mod) changes nothing.
            s.ManagerSummary("slot=1;area=sunkenGlades;pct=6");
            Equal("Spirit Grove", s.RoomName);
            Equal("name=Spirit Grove;slot=1;area=sunkenGlades;pct=6", s.LocalSummary);
            Equal("slot=1;area=sunkenGlades;pct=6", s.RoomSummary);
            s.ManagerSummary("");   // an empty summary (a manager that publishes none) keeps the last one
            Equal("slot=1;area=sunkenGlades;pct=6", s.RoomSummary);
        });

        run("cs room: no rename once a game is already running for this PC", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, true);
            Seat(m, 2, false);
            m.Seats[2].Playing = true;   // the other PC is already in the game
            Equal(true, m.IsManager);
            Equal(true, m.GameRunning);
            Equal(false, m.CanRename);
        });
    }
}
