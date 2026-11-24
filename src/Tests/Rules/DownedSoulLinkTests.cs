using System;

using OriCoop.Rules;

// T-248 (#336): a teammate builds a soul link while this player is down; the downed player must still be able to come
// back and to reach the pause menu / co-op panel (settings, quit).
static class DownedSoulLinkTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static bool Near(float a, float b) { return Math.Abs(a - b) < 0.0001f; }

    static DownedRoster<DownedState> Roster(params int[] downed)
    {
        DownedRoster<DownedState> r = new DownedRoster<DownedState>(ReviveLogic.MaxSeats);
        foreach (int seat in downed) r.TryAdd(seat, new DownedState { Player = seat });
        return r;
    }

    internal static void Run(Action<string, Action> run)
    {
        // ---- the downed player's screens ----------------------------------------------------------------------
        run("T-248 a screen opened while down stays open (settings, quit)", delegate {
            DownedScreens s = new DownedScreens(ReviveLogic.MaxSeats);
            Check(!s.Tick(1, false, false), "closing while alive");
            Check(!s.Tick(1, true, false), "closing the first downed frame with nothing open");
            Check(!s.Tick(1, true, true), "the pause menu the downed player opened was closed again");
            Check(!s.Tick(1, true, true), "the pause menu the downed player opened was closed on the next frame");
        });
        run("T-248 the co-op panel of a downed extra local player stays open", delegate {
            DownedScreens s = new DownedScreens(ReviveLogic.MaxSeats);
            s.Tick(1, false, false); s.Tick(3, true, false);
            Check(!s.Tick(3, true, true), "P3's panel was closed after P3 went down");
            Check(!s.Tick(1, false, false), "the living primary seat got a close");
        });
        run("T-248 a screen open at the fatal hit is closed, retried until it is gone", delegate {
            DownedScreens s = new DownedScreens(ReviveLogic.MaxSeats);
            s.Tick(2, false, true);   // the map is open, the Ori alive
            Check(s.Tick(2, true, true) && s.Closing(2), "the map open at the fatal hit was not closed");
            Check(s.Tick(2, true, true), "closing locked for a moment: not retried");
            Check(!s.Tick(2, true, false) && !s.Closing(2), "still closing after the screen went");
            Check(!s.Tick(2, true, true), "reopened after the first close: closed again");
        });
        run("T-248 going down again closes again; reset forgets", delegate {
            DownedScreens s = new DownedScreens(ReviveLogic.MaxSeats);
            s.Tick(1, true, false); s.Tick(1, true, true);
            Check(!s.Tick(1, false, true), "a living player's screen closed");   // revived with the menu open
            Check(s.Tick(1, true, true), "down again with the menu open: not closed");
            s.Reset();
            Check(s.Tick(1, true, true), "after a reset the open screen counts as open at the fatal hit");
            Check(!s.Tick(0, true, true) && !s.Tick(5, true, true), "an out-of-range seat closed something");
        });

        // ---- the soul link a teammate builds meanwhile ------------------------------------------------------------
        run("T-248 a 0-health save point adopted while down: revive with full health", delegate {
            DownedRoster<DownedState> r = Roster(1);
            SoulLinkRules.NoteLink(r, 0f);
            Check(Near(r.Seats[1].LinkHealth, 0f) && Near(SoulLinkRules.SelfReviveHealth(r.Seats[1], 16f), 16f),
                "got " + SoulLinkRules.SelfReviveHealth(r.Seats[1], 16f));
        });
        run("T-248 a 0-health save point does not wipe the soul link built meanwhile", delegate {
            DownedRoster<DownedState> r = Roster(1);
            SoulLinkRules.NoteLink(r, 24f);   // the teammate's soul link (death save)
            SoulLinkRules.NoteLink(r, 0f);    // then a save offer whose creator health is not known
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[1], 32f), 24f), "got " + SoulLinkRules.SelfReviveHealth(r.Seats[1], 32f));
            SoulLinkRules.NoteLink(r, -1f);
            Check(Near(r.Seats[1].LinkHealth, 24f), "a negative health replaced the link's");
        });
        run("T-248 a teammate's soul link after a 0-health one gives its creator's health", delegate {
            DownedRoster<DownedState> r = Roster(1);
            SoulLinkRules.NoteLink(r, 0f);
            SoulLinkRules.NoteLink(r, 24f);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[1], 32f), 24f), "got " + SoulLinkRules.SelfReviveHealth(r.Seats[1], 32f));
        });

        // ---- why a hold does not count (traced) -------------------------------------------------------------------
        run("T-248 hold state: counting, not held, and each refusal", delegate {
            Check(SoulLinkRules.SelfHoldState(true, false, false, false, false, false, false, true) == SoulLinkRules.HoldCounting, "counting");
            Check(SoulLinkRules.SelfHoldState(true, false, false, false, false, false, false, false) == SoulLinkRules.HoldNotHeld, "not held");
            Check(SoulLinkRules.SelfHoldState(false, false, false, false, false, false, false, true) == "no_save_point", "no save point");
            Check(SoulLinkRules.SelfHoldState(true, true, false, false, false, false, false, true) == "escape", "escape");
            Check(SoulLinkRules.SelfHoldState(true, false, true, false, false, false, false, true) == "no_game", "no game");
            Check(SoulLinkRules.SelfHoldState(true, false, false, true, false, false, false, true) == "gameplay_suspended", "suspended");
            Check(SoulLinkRules.SelfHoldState(true, false, false, false, true, false, false, true) == "loading", "loading");
            Check(SoulLinkRules.SelfHoldState(true, false, false, false, false, true, false, true) == "input_locked", "input locked");
            Check(SoulLinkRules.SelfHoldState(true, false, false, false, false, false, true, true) == "paused", "paused");
            Check(SoulLinkRules.SelfHoldState(true, false, false, false, false, true, false, false) == "input_locked", "a refusal hides behind not held");
        });
        run("T-248 refused means the hold cannot count", delegate {
            Check(!SoulLinkRules.SelfHoldRefused(SoulLinkRules.HoldCounting) && !SoulLinkRules.SelfHoldRefused(SoulLinkRules.HoldNotHeld), "counting / not held refused");
            Check(SoulLinkRules.SelfHoldRefused("input_locked") && SoulLinkRules.SelfHoldRefused("no_save_point"), "a refusal not refused");
        });
    }
}
