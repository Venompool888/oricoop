using System;

using OriCoop.Rules;

// T-215 (#301): downed / revive leftovers. Expected values come from the ticket's "玩家看到" lines turned around:
// a revived player has health and stands somewhere sensible, a player who is already up is not rolled back, the skull
// is where the player died, a save point is announced where it was made, and an old soul link gives full health.
static class ReviveLeftoverTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static bool Near(float a, float b) { return Math.Abs(a - b) < 0.0001f; }

    static ReviveWire.Link At(int seat, float x, float y, float health)
    {
        ReviveWire.Link l; l.Seat = seat; l.X = x; l.Y = y; l.Z = 0f; l.Health = health; return l;
    }

    internal static void Run(Action<string, Action> run)
    {
        // ---- #1: the rescuer's Ori is gone when its "revived you" arrives ----------------------------------------
        run("T-215 #1 rescuer here: the revived Ori appears beside it, on the side it faces away from", delegate {
            float x, y;
            ReviveLogic.ReviveSpot(true, 10f, 5f, true, 50f, 60f, out x, out y);
            Check(Near(x, 11f) && Near(y, 5f), "facing left: " + x + "," + y);
            ReviveLogic.ReviveSpot(true, 10f, 5f, false, 50f, 60f, out x, out y);
            Check(Near(x, 9f) && Near(y, 5f), "facing right: " + x + "," + y);
        });
        run("T-215 #1 rescuer gone: the revived Ori appears at its own skull", delegate {
            float x, y;
            ReviveLogic.ReviveSpot(false, 0f, 0f, false, 50f, 60f, out x, out y);
            Check(Near(x, 50f) && Near(y, 60f), "got " + x + "," + y);
        });
        run("T-215 #1 a revived Ori never has less than 1 health or more than its maximum", delegate {
            Check(Near(ReviveLogic.RevivedHealth(0f, 16f), 1f), "0 given -> " + ReviveLogic.RevivedHealth(0f, 16f));
            Check(Near(ReviveLogic.RevivedHealth(0.99f, 16f), 1f), "0.99 given -> " + ReviveLogic.RevivedHealth(0.99f, 16f));
            Check(Near(ReviveLogic.RevivedHealth(1f, 16f), 1f), "1 given -> " + ReviveLogic.RevivedHealth(1f, 16f));
            Check(Near(ReviveLogic.RevivedHealth(6f, 16f), 6f), "6 given -> " + ReviveLogic.RevivedHealth(6f, 16f));
            Check(Near(ReviveLogic.RevivedHealth(16f, 16f), 16f), "16 given -> " + ReviveLogic.RevivedHealth(16f, 16f));
            Check(Near(ReviveLogic.RevivedHealth(20f, 16f), 16f), "20 given -> " + ReviveLogic.RevivedHealth(20f, 16f));
        });

        // ---- #2: "everyone is down" has to last before the host declares the wipe ---------------------------------
        run("T-215 #2 everyone down: the wipe is not declared at once", delegate {
            WipeGrace g = new WipeGrace();
            Check(!g.Tick(true, 100f, WipeGrace.Seconds) && g.Armed, "declared on the first frame");
            Check(!g.Tick(true, 100f + WipeGrace.Seconds - 0.01f, WipeGrace.Seconds), "declared just before the grace ended");
        });
        run("T-215 #2 a late 'I'm back' inside the grace cancels the wipe", delegate {
            WipeGrace g = new WipeGrace();
            g.Tick(true, 100f, WipeGrace.Seconds);            // B's "down" arrives: everyone looks down
            Check(!g.Tick(false, 100.3f, WipeGrace.Seconds) && !g.Armed, "still armed after A came back");
            Check(!g.Tick(false, 105f, WipeGrace.Seconds), "wipe declared although A is up");
        });
        run("T-215 #2 still everyone down after the grace: the wipe is declared once", delegate {
            WipeGrace g = new WipeGrace();
            g.Tick(true, 100f, WipeGrace.Seconds);
            Check(g.Tick(true, 100f + WipeGrace.Seconds, WipeGrace.Seconds), "not declared when the grace ended");
            Check(!g.Tick(true, 100f + WipeGrace.Seconds + 0.01f, WipeGrace.Seconds), "declared twice in a row");
        });
        run("T-215 #2 a cancelled grace starts over the next time everyone is down", delegate {
            WipeGrace g = new WipeGrace();
            g.Tick(true, 100f, WipeGrace.Seconds);
            g.Tick(false, 100.5f, WipeGrace.Seconds);
            Check(!g.Tick(true, 100.7f, WipeGrace.Seconds), "the old deadline was kept");
            Check(!g.Tick(true, 100.7f + WipeGrace.Seconds - 0.01f, WipeGrace.Seconds), "declared before the new grace ended");
            Check(g.Tick(true, 100.7f + WipeGrace.Seconds, WipeGrace.Seconds), "not declared after the new grace");
        });
        run("T-215 #2 the grace is at least a 400 ms one-way lag", delegate {
            Check(WipeGrace.Seconds >= 0.4f && WipeGrace.Seconds <= 1.5f, "grace is " + WipeGrace.Seconds + " s");
        });

        // ---- #4: the skull goes where the player died once it left its last solid ground behind -------------------
        run("T-215 #4 walking, falling and dashing are not a relocation", delegate {
            Check(!SafeGroundRules.Relocated(0f, 0f), "standing still");
            Check(!SafeGroundRules.Relocated(3f, -8f), "a fast fall between two looks");
            Check(!SafeGroundRules.Relocated(SafeGroundRules.RelocatedDistance, 0f), "exactly the limit");
        });
        run("T-215 #4 a jump no movement can make is a relocation", delegate {
            Check(SafeGroundRules.Relocated(SafeGroundRules.RelocatedDistance + 0.01f, 0f), "just over the limit");
            Check(SafeGroundRules.Relocated(-40f, 25f), "another area");
        });
        run("T-215 #4 the skull uses the last solid ground only while the Ori is still around it", delegate {
            Check(SafeGroundRules.UseSafeGround(true, true, 2f, -6f), "fell off its ledge: the skull stays on the ledge");
            Check(!SafeGroundRules.UseSafeGround(true, true, 80f, 0f), "changed area, killed before it stood anywhere: old ground used");
            Check(!SafeGroundRules.UseSafeGround(false, true, 0f, 0f), "no solid ground known");
            Check(SafeGroundRules.UseSafeGround(true, false, 0f, 0f), "never looked at yet: trust the ground");
        });

        // ---- #6: a save offer's spot serves that offer only -----------------------------------------------------
        run("T-215 #6 an offered spot is used for the offer it came with", delegate {
            OfferedSpots s = new OfferedSpots(ReviveLogic.MaxSeats);
            s.Note(2, At(2, 10f, 20f, 8f));
            ReviveWire.Link l;
            Check(s.Take(2, out l) && Near(l.X, 10f) && Near(l.Y, 20f) && Near(l.Health, 8f) && l.Seat == 2, "spot " + l.X + "," + l.Y);
        });
        run("T-215 #6 a later offer without a spot does not get the earlier one", delegate {
            OfferedSpots s = new OfferedSpots(ReviveLogic.MaxSeats);
            s.Note(2, At(2, 10f, 20f, 8f));
            ReviveWire.Link l;
            s.Take(2, out l);
            Check(!s.Take(2, out l), "the first offer's spot was reused at " + l.X + "," + l.Y);
        });
        run("T-215 #6 a newer spot replaces the one not used yet; devices do not mix", delegate {
            OfferedSpots s = new OfferedSpots(ReviveLogic.MaxSeats);
            s.Note(2, At(2, 10f, 20f, 8f));
            s.Note(2, At(2, 30f, 40f, 6f));
            s.Note(3, At(3, 70f, 80f, 4f));
            ReviveWire.Link l;
            Check(s.Take(2, out l) && Near(l.X, 30f), "device 2 got " + l.X);
            Check(s.Take(3, out l) && Near(l.X, 70f), "device 3 got " + l.X);
            Check(!s.Take(4, out l) && !s.Take(0, out l) && !s.Take(99, out l), "a spot for a device that sent none");
        });
        run("T-215 #6 the session's end forgets every spot", delegate {
            OfferedSpots s = new OfferedSpots(ReviveLogic.MaxSeats);
            s.Note(2, At(2, 10f, 20f, 8f));
            s.Clear();
            ReviveWire.Link l;
            Check(!s.Take(2, out l), "spot kept across sessions");
        });

        // ---- #7: telling a soul link again is not making it again ------------------------------------------------
        run("T-215 #7 a new soul link is announced with its creator's health", delegate {
            Check(Near(SoulLinkRules.AnnouncedHealth(6f, false), 6f), "got " + SoulLinkRules.AnnouncedHealth(6f, false));
        });
        run("T-215 #7 a repeat for a PC that joined does not count as made while someone is down", delegate {
            DownedRoster<DownedState> r = new DownedRoster<DownedState>(ReviveLogic.MaxSeats);
            float announced = SoulLinkRules.AnnouncedHealth(6f, false);
            if (announced > 0f) SoulLinkRules.NoteLink(r, announced);      // made with nobody down
            r.TryAdd(2, new DownedState { Player = 2 });                    // P2 goes down afterwards
            float repeat = SoulLinkRules.AnnouncedHealth(6f, true);         // P3 joins: the host tells everyone again
            if (repeat > 0f) SoulLinkRules.NoteLink(r, repeat);             // what Revive.NoteSavePoint does on receipt
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 16f), "P2 comes back with " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("T-215 #7 a repeat does not take away what a soul link made while down gives", delegate {
            DownedRoster<DownedState> r = new DownedRoster<DownedState>(ReviveLogic.MaxSeats);
            r.TryAdd(2, new DownedState { Player = 2 });
            SoulLinkRules.NoteLink(r, SoulLinkRules.AnnouncedHealth(6f, false));   // made while P2 is down
            float repeat = SoulLinkRules.AnnouncedHealth(6f, true);
            if (repeat > 0f) SoulLinkRules.NoteLink(r, repeat);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 6f), "P2 comes back with " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
    }
}
