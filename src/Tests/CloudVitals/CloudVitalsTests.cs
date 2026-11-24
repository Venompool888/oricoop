using System;
using System.IO;
using OriCoop.Ui;

// Cloud review: a teammate's vitals (msg 79) with a value that is not a number (a broken or malformed sender) must not
// break that teammate's bar. VitalsModel.Tick moved the smoothed edges with Math.Sign(target - edge), which throws
// ArithmeticException for NaN, and a NaN edge never recovered: every later frame threw again.
static class CloudVitalsTests
{
    static int s_failed;

    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { s_failed++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); }
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static bool Finite(float? v) { return v.HasValue && !float.IsNaN(v.Value) && !float.IsInfinity(v.Value); }

    static VitalsSample V(float hp, int maxHp, float en, float maxEn)
    {
        VitalsSample s = new VitalsSample(); s.Health = hp; s.MaxHealth = maxHp; s.Energy = en; s.MaxEnergy = maxEn; s.SoulFlame = 1f; return s;
    }

    // The sample as it comes off the wire (VitalsModel.Read), so the test covers the real path.
    static VitalsSample Wire(VitalsSample s)
    {
        MemoryStream m = new MemoryStream();
        VitalsModel.Write(new BinaryWriter(m), 3, s);
        int seat;
        VitalsSample back = VitalsModel.Read(new BinaryReader(new MemoryStream(m.ToArray())), out seat);
        if (seat != 3) throw new Exception("seat " + seat);
        return back;
    }

    static void AllFinite(VitalsModel m, string when)
    {
        foreach (StatKind k in new[] { StatKind.HealthVisualMin, StatKind.HealthVisualMax, StatKind.HealthVisualMinNormalized,
            StatKind.HealthVisualMaxNormalized, StatKind.MaxEnergy, StatKind.EnergyVisualMin, StatKind.EnergyVisualMax,
            StatKind.EnergyVisualMinNormalized, StatKind.EnergyVisualMaxNormalized, StatKind.SoulFlame })
            Check(Finite(m.Provide(k, 1f)), when + ": " + k + " = " + m.Provide(k, 1f));
    }

    static int Main()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            string tag = bad.ToString();
            Run("health " + tag + " after a good sample: tick does not throw, bars stay numbers", delegate {
                VitalsModel m = new VitalsModel();
                m.Set(Wire(V(8f, 12, 2f, 3f))); m.Tick(0.1f);
                m.Set(Wire(V(bad, 12, 2f, 3f)));
                m.Tick(0.1f); m.Tick(0.1f);
                AllFinite(m, "after the bad sample");
                m.Set(Wire(V(4f, 12, 2f, 3f))); m.Tick(0.1f);
                AllFinite(m, "after the next good sample");
                Check(Math.Abs(m.Now.Health - 4f) < 0.001f, "health follows the good sample: " + m.Now.Health);
            });
            Run("energy and max energy " + tag + ": tick does not throw, bars stay numbers", delegate {
                VitalsModel m = new VitalsModel();
                m.Set(Wire(V(8f, 12, 2f, 3f)));
                m.Set(Wire(V(8f, 12, bad, 3f))); m.Tick(0.1f);
                AllFinite(m, "energy bad");
                m.Set(Wire(V(8f, 12, 2f, bad))); m.Tick(0.1f);
                AllFinite(m, "max energy bad");
                m.Set(Wire(V(8f, 12, 1f, 3f))); m.Tick(2f);
                AllFinite(m, "recovered");
                Check(Math.Abs(m.EnergyMin - 1f) < 0.001f && Math.Abs(m.EnergyMax - 1f) < 0.001f, "energy edges settle on 1: " + m.EnergyMin + " " + m.EnergyMax);
            });
            Run("first sample " + tag + ": no throw, numbers", delegate {
                VitalsModel m = new VitalsModel();
                VitalsSample s = V(bad, 12, bad, bad); s.SoulFlame = bad;
                m.Set(s); m.Tick(0.1f);
                AllFinite(m, "first sample bad");
            });
        }
        Run("good samples are unchanged (control)", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(10f, 12, 3f, 3f)); m.Set(V(6f, 12, 1f, 3f));
            Check(Math.Abs(m.HealthMin - 6f) < 0.001f && Math.Abs(m.HealthMax - 10f) < 0.001f, "health edges " + m.HealthMin + " " + m.HealthMax);
            m.Tick(0.5f);
            Check(Math.Abs(m.HealthMax - 8f) < 0.001f, "health high edge drains 4/s: " + m.HealthMax);
        });
        Console.WriteLine(s_failed == 0 ? "cloud vitals: all passed" : "cloud vitals: " + s_failed + " failed");
        return s_failed == 0 ? 0 : 1;
    }
}
