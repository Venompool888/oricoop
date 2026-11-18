using System;
using OriCoop.Rules;

static class SpectateTests
{
    static readonly int[] All = { 1, 2, 3, 4 };

    static SpectateState Start(int[] local, int[] present, int[] downed)
    {
        SpectateState state = new SpectateState();
        state.Tick(local, present, downed);
        return state;
    }

    static void Target(SpectateState state, int expected)
    {
        if (state.Active != (expected != 0) || state.Target != expected)
            throw new Exception("expected " + (expected == 0 ? "inactive" : "P" + expected)
                + ", got " + (state.Active ? "P" + state.Target : "inactive") + " (target " + state.Target + ")");
    }

    internal static void Run(Action<string, Action> run)
    {
        run("spectate inactive while local P1 is alive", delegate {
            Target(Start(new[] { 1 }, new[] { 1, 2 }, new[] { 2 }), 0);
        });
        run("spectate inactive when first of two locals is alive", delegate {
            Target(Start(new[] { 1, 2 }, new[] { 1, 2, 3 }, new[] { 2 }), 0);
        });
        run("spectate inactive when second of two locals is alive", delegate {
            Target(Start(new[] { 1, 2 }, new[] { 1, 2, 3 }, new[] { 1 }), 0);
        });
        run("spectate starts when both locals are down and remote P3 is alive", delegate {
            Target(Start(new[] { 1, 2 }, new[] { 1, 2, 3 }, new[] { 1, 2 }), 3);
        });
        run("spectate inactive when all remotes are down", delegate {
            Target(Start(new[] { 2 }, new[] { 1, 2, 3 }, new[] { 1, 2, 3 }), 0);
        });
        run("spectate inactive when no remote is present", delegate {
            Target(Start(new[] { 1 }, new[] { 1 }, new[] { 1 }), 0);
        });
        run("spectate default is first alive seat after local P2", delegate {
            Target(Start(new[] { 2 }, All, new[] { 2 }), 3);
        });
        run("spectate default wraps from P4 to P1", delegate {
            Target(Start(new[] { 4 }, All, new[] { 4 }), 1);
        });
        run("spectate default uses own seat before the other local seat", delegate {
            Target(Start(new[] { 3, 1 }, All, new[] { 1, 3 }), 4);
        });
        run("spectate default skips absent and downed seats", delegate {
            Target(Start(new[] { 2 }, new[] { 4, 2, 1 }, new[] { 2, 4 }), 1);
        });
        run("spectate two seats next and previous stay on P1", delegate {
            SpectateState s = Start(new[] { 2 }, new[] { 1, 2 }, new[] { 2 });
            Target(s, 1); s.Next(); Target(s, 1); s.Prev(); Target(s, 1);
        });
        run("spectate three seats next cycles P3 P1 P3", delegate {
            SpectateState s = Start(new[] { 2 }, new[] { 3, 1, 2 }, new[] { 2 });
            Target(s, 3); s.Next(); Target(s, 1); s.Next(); Target(s, 3);
        });
        run("spectate three seats previous cycles P3 P1 P3", delegate {
            SpectateState s = Start(new[] { 2 }, new[] { 1, 2, 3 }, new[] { 2 });
            Target(s, 3); s.Prev(); Target(s, 1); s.Prev(); Target(s, 3);
        });
        run("spectate four seats next cycles P3 P4 P1 P3", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 });
            Target(s, 3); s.Next(); Target(s, 4); s.Next(); Target(s, 1); s.Next(); Target(s, 3);
        });
        run("spectate four seats previous cycles P3 P1 P4 P3", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 });
            Target(s, 3); s.Prev(); Target(s, 1); s.Prev(); Target(s, 4); s.Prev(); Target(s, 3);
        });
        run("spectate cycling skips another downed seat", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2, 4 });
            Target(s, 3); s.Next(); Target(s, 1); s.Prev(); Target(s, 3);
        });
        run("spectate never targets either local seat", delegate {
            SpectateState s = Start(new[] { 2, 4 }, All, new[] { 2, 4 });
            Target(s, 3); s.Next(); Target(s, 1); s.Next(); Target(s, 3);
        });
        run("spectate retains selected P1 across ticks", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 });
            s.Prev(); s.Tick(new[] { 2 }, All, new[] { 2 }); Target(s, 1);
        });
        run("spectate target P3 dies and advances to P4", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 });
            s.Tick(new[] { 2 }, All, new[] { 2, 3 }); Target(s, 4);
        });
        run("spectate target P4 dies and wraps to P1", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 }); s.Next(); Target(s, 4);
            s.Tick(new[] { 2 }, All, new[] { 2, 4 }); Target(s, 1);
        });
        run("spectate target P3 leaves and advances to P4", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 });
            s.Tick(new[] { 2 }, new[] { 1, 2, 4 }, new[] { 2 }); Target(s, 4);
        });
        run("spectate target P4 leaves and wraps to P1", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 }); s.Next(); Target(s, 4);
            s.Tick(new[] { 2 }, new[] { 1, 2, 3 }, new[] { 2 }); Target(s, 1);
        });
        run("spectate keeps P3 when another target dies", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 });
            s.Tick(new[] { 2 }, All, new[] { 1, 2 }); Target(s, 3);
        });
        run("spectate ends when last living remote dies", delegate {
            SpectateState s = Start(new[] { 2 }, new[] { 1, 2 }, new[] { 2 }); Target(s, 1);
            s.Tick(new[] { 2 }, new[] { 1, 2 }, new[] { 1, 2 }); Target(s, 0);
        });
        run("spectate ends when last living remote leaves", delegate {
            SpectateState s = Start(new[] { 2 }, new[] { 1, 2 }, new[] { 2 }); Target(s, 1);
            s.Tick(new[] { 2 }, new[] { 2 }, new[] { 2 }); Target(s, 0);
        });
        run("spectate ends when local P2 is revived", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 }); Target(s, 3);
            s.Tick(new[] { 2 }, All, new int[0]); Target(s, 0);
        });
        run("spectate ends when either local seat is revived", delegate {
            SpectateState s = Start(new[] { 1, 2 }, All, new[] { 1, 2 });
            Target(s, 3); s.Tick(new[] { 1, 2 }, All, new[] { 1 }); Target(s, 0);
        });
        run("spectate local same-screen never starts for either downed seat", delegate {
            int[] local = { 1, 2 };
            Target(Start(local, local, new int[0]), 0);
            Target(Start(local, local, new[] { 1 }), 0);
            Target(Start(local, local, new[] { 2 }), 0);
            Target(Start(local, local, new[] { 1, 2 }), 0);
        });
        run("spectate starts again from default after revival", delegate {
            SpectateState s = Start(new[] { 2 }, All, new[] { 2 }); s.Next(); Target(s, 4);
            s.Tick(new[] { 2 }, All, new int[0]); Target(s, 0);
            s.Tick(new[] { 2 }, All, new[] { 2 }); Target(s, 3);
        });
        run("spectate next and previous do nothing while inactive", delegate {
            SpectateState s = Start(new[] { 1 }, All, new int[0]);
            s.Next(); s.Prev(); Target(s, 0);
        });
    }
}
