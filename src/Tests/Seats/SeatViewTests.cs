using System;
using System.Collections.Generic;
using OriCoop;
using OriCoop.Seats;

// A fake device: a seat -> character map plus a local set and inputs, so the invariants can be broken one at a time.
sealed class FakeOri { public string Name; public FakeOri(string n) { Name = n; } }

sealed class FakeView : ISeatView<FakeOri>
{
    public List<int> Local = new List<int>();
    public List<int> Remote = new List<int>();
    public int Primary;
    public Dictionary<int, FakeOri> Oris = new Dictionary<int, FakeOri>();
    public Dictionary<int, InputDevice> Inputs = new Dictionary<int, InputDevice>();
    public int SeatOfOffset;                       // mutation: SeatOf answers the wrong seat
    public int IsLocalLie;                          // mutation: IsLocal lies about this seat

    public int[] LocalSeats { get { return Local.ToArray(); } }
    public int PrimarySeat { get { return Primary; } }
    public int[] RemoteSeats { get { return Remote.ToArray(); } }
    public bool IsLocal(int seat) { return seat == IsLocalLie ? !Local.Contains(seat) : Local.Contains(seat); }
    public FakeOri OriOf(int seat) { FakeOri o; return Oris.TryGetValue(seat, out o) ? o : null; }
    public int SeatOf(FakeOri ori)
    {
        foreach (KeyValuePair<int, FakeOri> kv in Oris) if (kv.Value == ori) return kv.Key + SeatOfOffset;
        return 0;
    }
    public InputDevice InputOf(int seat)
    {
        if (!Local.Contains(seat)) throw new ArgumentException("Not a local seat", "seat");
        InputDevice d; return Inputs.TryGetValue(seat, out d) ? d : new InputDevice(InputDevice.DeviceKind.KeyboardMouse, -1);
    }
    public bool SetInput(int seat, InputDevice device) { if (!Local.Contains(seat)) return false; Inputs[seat] = device; return true; }
    public bool AddLocalPlayer(InputDevice device)
    {
        for (int s = 1; s <= 4; s++)
            if (!Local.Contains(s) && !Remote.Contains(s)) { Local.Add(s); Local.Sort(); Inputs[s] = device; Oris[s] = new FakeOri("o" + s); return true; }
        return false;
    }
    public void RemoveLocalPlayer(int seat) { if (seat == Primary) return; Local.Remove(seat); Inputs.Remove(seat); Oris.Remove(seat); }

    // Guest device of a 2-device / 3-player room: seat 1 remote (host), seats 2 (keyboard) and 3 (pad 0) local.
    public static FakeView Guest()
    {
        FakeView v = new FakeView();
        v.Local.Add(2); v.Local.Add(3); v.Remote.Add(1); v.Primary = 2;
        v.Oris[1] = new FakeOri("host"); v.Oris[2] = new FakeOri("me"); v.Oris[3] = new FakeOri("extra");
        v.Inputs[2] = new InputDevice(InputDevice.DeviceKind.KeyboardMouse, -1);
        v.Inputs[3] = new InputDevice(InputDevice.DeviceKind.Pad, 0);
        return v;
    }
}

static class SeatViewTests
{
    static int cases, failed;
    static void Run(string name, Action test)
    {
        cases++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Clean(FakeView v, bool requireOris)
    {
        List<string> p = SeatViewChecks.Problems(v, requireOris);
        Check(p.Count == 0, "expected no problems, got: " + string.Join("; ", p.ToArray()));
    }
    // Every mutation must be caught, with a message that names it.
    static void Caught(FakeView v, string expect)
    {
        List<string> p = SeatViewChecks.Problems(v, true);
        foreach (string s in p) if (s.Contains(expect)) return;
        throw new Exception("'" + expect + "' not reported; got: " + string.Join("; ", p.ToArray()));
    }

    static int Main()
    {
        Run("a consistent guest view has no problems", delegate { Clean(FakeView.Guest(), true); });
        Run("an offline single player view has no problems", delegate {
            FakeView v = new FakeView(); v.Local.Add(1); v.Primary = 1; v.Oris[1] = new FakeOri("p1"); Clean(v, true);
        });
        Run("add takes the lowest free seat and keeps the invariants", delegate {
            FakeView v = FakeView.Guest(); v.Local.Remove(3); v.Oris.Remove(3); v.Inputs.Remove(3);
            Check(v.AddLocalPlayer(new InputDevice(InputDevice.DeviceKind.Pad, 1)) && v.LocalSeats.Length == 2 && v.LocalSeats[1] == 3, "seat 3 added");
            Clean(v, true);
            Check(v.AddLocalPlayer(new InputDevice(InputDevice.DeviceKind.Pad, 2)) && !v.AddLocalPlayer(new InputDevice(InputDevice.DeviceKind.Pad, 3)), "fourth seat, then full");
            Clean(v, true);
        });
        Run("remove keeps the primary seat", delegate {
            FakeView v = FakeView.Guest(); v.RemoveLocalPlayer(2); v.RemoveLocalPlayer(3);
            Check(v.LocalSeats.Length == 1 && v.LocalSeats[0] == 2, "primary kept"); Clean(v, true);
        });
        Run("missing characters are fine while loading, a problem in gameplay", delegate {
            FakeView v = FakeView.Guest(); v.Oris.Remove(1);
            Clean(v, false); Caught(v, "seat 1 has no character");
        });
        Run("mutation: primary is not the lowest local seat", delegate { FakeView v = FakeView.Guest(); v.Primary = 3; Caught(v, "primary seat 3"); });
        Run("mutation: a seat is both local and remote", delegate { FakeView v = FakeView.Guest(); v.Remote.Add(3); Caught(v, "both local and remote"); });
        Run("mutation: no local seat", delegate { FakeView v = FakeView.Guest(); v.Local.Clear(); Caught(v, "no local seat"); });
        Run("mutation: seat out of range", delegate { FakeView v = FakeView.Guest(); v.Remote.Add(5); Caught(v, "out of range"); });
        Run("mutation: local seats not ascending", delegate { FakeView v = FakeView.Guest(); v.Local.Reverse(); v.Primary = 3; Caught(v, "not ascending"); });
        Run("mutation: IsLocal disagrees with LocalSeats", delegate { FakeView v = FakeView.Guest(); v.IsLocalLie = 4; Caught(v, "IsLocal(4)"); });
        Run("mutation: SeatOf does not invert OriOf", delegate { FakeView v = FakeView.Guest(); v.SeatOfOffset = 1; Caught(v, "SeatOf(OriOf("); });
        Run("mutation: two seats share a character", delegate { FakeView v = FakeView.Guest(); v.Oris[3] = v.Oris[2]; Caught(v, "shares its character"); });
        Run("mutation: two local seats on the keyboard", delegate {
            FakeView v = FakeView.Guest(); v.Inputs[3] = new InputDevice(InputDevice.DeviceKind.KeyboardMouse, -1); Caught(v, "keyboard on more than one");
        });
        Run("mutation: two local seats on one pad", delegate {
            FakeView v = FakeView.Guest(); v.Inputs[2] = new InputDevice(InputDevice.DeviceKind.Pad, 0); Caught(v, "pad 0 on more than one");
        });
        Run("mutation: pad out of range", delegate {
            FakeView v = FakeView.Guest(); v.Inputs[3] = new InputDevice(InputDevice.DeviceKind.Pad, 7); Caught(v, "has pad 7");
        });
        Run("mutation: more than four seats", delegate {
            FakeView v = FakeView.Guest(); v.Remote.Add(4); v.Remote.Add(4); Caught(v, "more than 4 seats");
        });
        Console.WriteLine("Seats: " + cases + " cases, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
