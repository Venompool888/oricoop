using System;
using System.IO;
using OriCoop.Rules;

// Cloud review: revive messages (60 downed, 61 revive, 62 revived, 63 progress, 69 room link) carrying a value that is
// not a number must be refused by the reader (InvalidDataException: the bus drops the message as "bad message"), not
// handed to the game as a NaN skull position, NaN health or a progress ring that can never fill.
static class CloudReviveWireTests
{
    static int s_failed;

    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { s_failed++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); }
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static BinaryReader Bytes(Action<BinaryWriter> write)
    {
        MemoryStream m = new MemoryStream(); BinaryWriter w = new BinaryWriter(m); write(w); w.Flush();
        return new BinaryReader(new MemoryStream(m.ToArray()));
    }

    static void Refused(string what, Action<BinaryReader> read, Action<BinaryWriter> write)
    {
        try { read(Bytes(write)); }
        catch (InvalidDataException) { return; }
        throw new Exception(what + " was read without complaint");
    }

    static int Main()
    {
        float[] bad = { float.NaN, float.PositiveInfinity, float.NegativeInfinity };
        foreach (float b in bad)
        {
            float v = b;
            string tag = " (" + v + ")";
            Run("downed / revived position" + tag + " is refused", delegate {
                Refused("x", r => ReviveWire.ReadPosition(r), w => ReviveWire.WritePosition(w, 2, v, 1f, 0f));
                Refused("y", r => ReviveWire.ReadPosition(r), w => ReviveWire.WritePosition(w, 2, 1f, v, 0f));
                Refused("z", r => ReviveWire.ReadPosition(r), w => ReviveWire.WritePosition(w, 2, 1f, 1f, v));
            });
            Run("revive health" + tag + " is refused", delegate {
                Refused("health", r => ReviveWire.ReadRevive(r), w => ReviveWire.WriteRevive(w, 3, v));
            });
            Run("revive progress" + tag + " is refused", delegate {
                Refused("value", r => ReviveWire.ReadProgress(r), w => ReviveWire.WriteProgress(w, 3, 2, v, 1f));
                Refused("given", r => ReviveWire.ReadProgress(r), w => ReviveWire.WriteProgress(w, 3, 2, 0.5f, v));
            });
            Run("room link" + tag + " is refused", delegate {
                Refused("x", r => ReviveWire.ReadLink(r), w => ReviveWire.WriteLink(w, 4, v, 1f, 0f, 3f));
                Refused("health", r => ReviveWire.ReadLink(r), w => ReviveWire.WriteLink(w, 4, 1f, 1f, 0f, v));
                ReviveWire.Link l = new ReviveWire.Link { Seat = 4, X = 1f, Y = 2f, Z = 0f, Health = 3f, MarkerKnown = true,
                    Flame = new ReviveWire.Marker { X = v, Y = 2f, Z = 0f, Scene = Guid.NewGuid() } };
                Refused("flame marker", r => ReviveWire.ReadLink(r), w => ReviveWire.WriteLink(w, l));
            });
        }
        Run("finite values still read back (control)", delegate {
            ReviveWire.Position p = ReviveWire.ReadPosition(Bytes(w => ReviveWire.WritePosition(w, 4, -152f, -130.5f, 0f)));
            Check(p.Seat == 4 && p.X == -152f && p.Y == -130.5f && p.Z == 0f, "position");
            ReviveWire.Revive rv = ReviveWire.ReadRevive(Bytes(w => ReviveWire.WriteRevive(w, 3, 2.5f)));
            Check(rv.Seat == 3 && rv.Health == 2.5f, "revive");
            ReviveWire.Progress pr = ReviveWire.ReadProgress(Bytes(w => ReviveWire.WriteProgress(w, 3, 1, 0.75f, 1.5f)));
            Check(pr.Seat == 3 && pr.Rescuer == 1 && pr.Value == 0.75f && pr.Given == 1.5f, "progress");
            Guid scene = Guid.NewGuid();
            ReviveWire.Link l = new ReviveWire.Link { Seat = 2, X = 1f, Y = 2f, Z = 3f, Health = 4f, MarkerKnown = true,
                Flame = new ReviveWire.Marker { X = 5f, Y = 6f, Z = 7f, Scene = scene } };
            ReviveWire.Link back = ReviveWire.ReadLink(Bytes(w => ReviveWire.WriteLink(w, l)));
            Check(back.Seat == 2 && back.Health == 4f && back.Flame.HasValue && back.Flame.Value.X == 5f && back.Flame.Value.Scene == scene, "link");
        });
        Console.WriteLine(s_failed == 0 ? "cloud revive wire: all passed" : "cloud revive wire: " + s_failed + " failed");
        return s_failed == 0 ? 0 : 1;
    }
}
