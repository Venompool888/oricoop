using System;
using OriCoop.Seats;

// T-188: an online PC's primary seat must start with (and, on the menus, follow) the device the player used last there.
// Oracle: the ticket's expected behaviour — controller on the menus => that controller in game; keyboard/mouse on the
// menus => keyboard/mouse; nothing pressed => the old default (keyboard/mouse); an F8 choice is never overridden.
static class PrimaryInputTests
{
    const int KB = -1;
    static int failed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static bool[] Pads(params int[] pressed) { bool[] a = new bool[4]; foreach (int p in pressed) a[p] = true; return a; }

    static int Main()
    {
        // ---- Default: what the primary seat starts with
        Run("controller used on the menus: primary starts on that controller (the reported bug)", delegate {
            int d = PrimaryInput.Default(new SeatInputs(), 0);
            Check(d == 0, "expected pad 0, got " + d);
            Check(PrimaryInput.Default(new SeatInputs(), 3) == 3, "pad 3 (upper boundary)");
        });
        Run("keyboard/mouse used on the menus: primary starts on keyboard/mouse", delegate {
            Check(PrimaryInput.Default(new SeatInputs(), KB) == KB, "keyboard");
        });
        Run("nothing pressed yet: old default keyboard/mouse", delegate {
            Check(PrimaryInput.Default(new SeatInputs(), PrimaryInput.NotSeen) == KB, "keyboard when not seen");
        });
        Run("invalid menu device codes fall back like nothing pressed", delegate {
            Check(PrimaryInput.Default(new SeatInputs(), 4) == KB, "pad 4 does not exist");
            Check(PrimaryInput.Default(new SeatInputs(), -3) == KB, "-3 is not a device");
        });
        Run("menu device already held by another local seat: not taken from it", delegate {
            SeatInputs s = new SeatInputs(); s.Add(3, 0);
            Check(PrimaryInput.Default(s, 0) == KB, "pad 0 belongs to seat 3, primary gets keyboard");
            s.Add(4, KB);
            Check(PrimaryInput.Default(s, 0) == 1, "pad 0 and keyboard taken -> first free pad 1");
        });

        // ---- Follow: on the menus, until an F8 choice / game start
        Run("follow: primary moves from keyboard to the controller pressed on the menus", delegate {
            SeatInputs s = new SeatInputs(); s.Add(2, KB);
            Check(PrimaryInput.Follow(s, 2, 1) && s.Get(2) == 1, "seat 2 now pad 1, got " + s.Get(2));
        });
        Run("follow: and back to keyboard/mouse when the player switches", delegate {
            SeatInputs s = new SeatInputs(); s.Add(1, 0);
            Check(PrimaryInput.Follow(s, 1, KB) && s.Get(1) == KB, "seat 1 now keyboard");
        });
        Run("follow: no change when the device is the same, unknown, or held by another seat", delegate {
            SeatInputs s = new SeatInputs(); s.Add(1, 0); s.Add(2, 1);
            Check(!PrimaryInput.Follow(s, 1, 0) && s.Get(1) == 0, "same device");
            Check(!PrimaryInput.Follow(s, 1, PrimaryInput.NotSeen) && s.Get(1) == 0, "nothing seen");
            Check(!PrimaryInput.Follow(s, 1, 1) && s.Get(1) == 0 && s.Get(2) == 1, "pad 1 stays with seat 2 (no swap)");
        });
        Run("follow: an unassigned seat is left alone", delegate {
            SeatInputs s = new SeatInputs();
            Check(!PrimaryInput.Follow(s, 1, 0) && s.Get(1) == -2, "unassigned stays unassigned");
        });

        // ---- MenuDevice: which device was used last
        Run("tracker: nothing pressed -> NotSeen", delegate {
            MenuDevice m = new MenuDevice();
            Check(!m.Observe(false, Pads()) && m.Last == PrimaryInput.NotSeen, "not seen");
        });
        Run("tracker: a pad press is recorded", delegate {
            MenuDevice m = new MenuDevice();
            Check(m.Observe(false, Pads(2)) && m.Last == 2, "pad 2");
        });
        Run("tracker: a key after the pad switches to keyboard, a new pad press switches back", delegate {
            MenuDevice m = new MenuDevice();
            m.Observe(false, Pads(0)); m.Observe(false, Pads());
            Check(m.Observe(true, Pads()) && m.Last == KB, "keyboard");
            Check(m.Observe(false, Pads(0)) && m.Last == 0, "pad 0 again");
        });
        Run("tracker: a pad held (or a drifting stick) does not override a later key press", delegate {
            MenuDevice m = new MenuDevice();
            m.Observe(false, Pads(1));
            m.Observe(true, Pads(1));
            Check(m.Last == KB, "key wins over a held pad, got " + m.Last);
            m.Observe(false, Pads(1));
            Check(m.Last == KB, "still keyboard while pad 1 stays held, got " + m.Last);
        });
        Run("tracker: pad press and key in the same frame -> the pad", delegate {
            MenuDevice m = new MenuDevice();
            Check(m.Observe(true, Pads(3)) && m.Last == 3, "pad 3");
        });
        Run("tracker: the second pad pressed becomes the device", delegate {
            MenuDevice m = new MenuDevice();
            m.Observe(false, Pads(0));
            Check(m.Observe(false, Pads(0, 1)) && m.Last == 1, "pad 1");
        });
        Run("tracker: Clear forgets everything", delegate {
            MenuDevice m = new MenuDevice(); m.Observe(false, Pads(0)); m.Clear();
            Check(m.Last == PrimaryInput.NotSeen, "cleared");
            Check(m.Observe(false, Pads(0)) && m.Last == 0, "pad 0 counts as a new press after Clear");
        });

        Console.WriteLine(failed == 0 ? "PrimaryInput: all passed" : "PrimaryInput: " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
