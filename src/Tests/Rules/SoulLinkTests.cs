using System;

using OriCoop.Rules;

// T-041 (#15): death save, revive at the latest soul link (msg 69 RoomLink wire).
static class SoulLinkTests
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
        run("death save halves the creator's health", delegate {
            Check(Near(SoulLinkRules.DeathSaveHealth(16f), 8f), "16 -> " + SoulLinkRules.DeathSaveHealth(16f));
            Check(Near(SoulLinkRules.DeathSaveHealth(10f), 5f), "10 -> " + SoulLinkRules.DeathSaveHealth(10f));
        });
        run("death save keeps at least one cell", delegate {
            Check(Near(SoulLinkRules.DeathSaveHealth(6f), 4f), "6 -> " + SoulLinkRules.DeathSaveHealth(6f));
            Check(Near(SoulLinkRules.DeathSaveHealth(4f), 4f), "4 -> " + SoulLinkRules.DeathSaveHealth(4f));
        });
        run("death save never raises health below a cell", delegate {
            Check(Near(SoulLinkRules.DeathSaveHealth(2f), 2f), "2 -> " + SoulLinkRules.DeathSaveHealth(2f));
        });
        run("soul link made while down: revive with the creator's health", delegate {
            DownedRoster<DownedState> r = Roster(2, 3);
            SoulLinkRules.NoteLink(r, 8f);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 8f), "P2 got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[3], 16f), 8f), "P3 got " + SoulLinkRules.SelfReviveHealth(r.Seats[3], 16f));
        });
        run("no soul link while down: revive with full health", delegate {
            DownedRoster<DownedState> r = Roster(2);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 16f), "got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("soul link made before going down does not count", delegate {
            DownedRoster<DownedState> r = Roster(3);
            SoulLinkRules.NoteLink(r, 8f);          // P3 down at the time, P2 still alive
            r.TryAdd(2, new DownedState { Player = 2 });
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 16f), "P2 went down after the link but got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("a newer soul link replaces the older one's health", delegate {
            DownedRoster<DownedState> r = Roster(2);
            SoulLinkRules.NoteLink(r, 8f);
            SoulLinkRules.NoteLink(r, 5f);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 5f), "got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("revive health is at least 1 and at most max", delegate {
            DownedState d = new DownedState { LinkHealth = 0.4f };
            Check(Near(SoulLinkRules.SelfReviveHealth(d, 16f), 1f), "low link -> " + SoulLinkRules.SelfReviveHealth(d, 16f));
            d.LinkHealth = 20f;
            Check(Near(SoulLinkRules.SelfReviveHealth(d, 12f), 12f), "high link -> " + SoulLinkRules.SelfReviveHealth(d, 12f));
        });
        run("hold Y for 2 s revives; releasing starts over", delegate {
            DownedState d = new DownedState();
            Check(!SoulLinkRules.HoldSelf(d, true, false, 1.5f), "done after 1.5 s");
            Check(!SoulLinkRules.HoldSelf(d, false, false, 0.1f), "done on release");
            Check(!SoulLinkRules.HoldSelf(d, true, false, 1.5f), "release did not restart the hold");
            Check(SoulLinkRules.HoldSelf(d, true, false, 0.6f), "not done after 2.1 s");
        });
        run("hold Y does not count during loading or cutscenes", delegate {
            DownedState d = new DownedState();
            Check(!SoulLinkRules.HoldSelf(d, true, true, 3f), "revived while blocked");
            Check(Near(d.SelfHold, 0f), "blocked hold kept " + d.SelfHold);
        });
        run("self revive cancels a teammate's revive in progress", delegate {
            DownedState d = new DownedState();
            ReviveLogic.BeginHold(d, 3); d.Progress = 0.6f; d.Given = 4f;
            SoulLinkRules.CancelRescue(d);
            Check(!d.Holding && d.Progress == 0f && d.Given == 0f && d.Rescuer == 0, "rescue still running");
        });
    }
}
