using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Ui;

// T-061: unit tests for the teammates' bottom status bars (StatusBarCore). Expected values come from the ticket and
// from the game's own rules (SeinHealthController / SeinEnergy smoothing: loss sets the low edge at once, the high edge
// drains at 4 health / 1 energy per second; gain sets the high edge at once).
static class StatusBarTests
{
    static int s_failed, s_cases;

    static void Run(string name, Action test)
    {
        s_cases++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { s_failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Near(float expected, float actual, string what)
    {
        if (Math.Abs(expected - actual) > 0.001f) throw new Exception(what + ": expected " + expected + ", got " + actual);
    }
    static void Near(float expected, float? actual, string what)
    {
        if (!actual.HasValue) throw new Exception(what + ": expected " + expected + ", got null");
        Near(expected, actual.Value, what);
    }

    static VitalsSample V(float hp, int maxHp, float en, float maxEn)
    {
        VitalsSample s = new VitalsSample(); s.Health = hp; s.MaxHealth = maxHp; s.Energy = en; s.MaxEnergy = maxEn; s.SoulFlame = 1f; return s;
    }

    static int Main()
    {
        // ---- model: what a provider of each kind returns
        Run("no data yet: every provider falls back to the game (null)", delegate {
            VitalsModel m = new VitalsModel();
            Check(!m.Have, "Have before Set");
            foreach (StatKind k in Enum.GetValues(typeof(StatKind))) Check(!m.Provide(k, 1f).HasValue, k + " not null before data");
        });
        Run("first sample: visual edges equal the values", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(8f, 12, 2f, 3f));
            Near(8f, m.Provide(StatKind.HealthVisualMin, 1f), "health min");
            Near(8f, m.Provide(StatKind.HealthVisualMax, 1f), "health max");
            Near(12f, m.Provide(StatKind.MaxHealth, 1f), "max health");
            Near(2f, m.Provide(StatKind.EnergyVisualMin, 1f), "energy min");
            Near(3f, m.Provide(StatKind.MaxEnergy, 1f), "max energy");
        });
        Run("DivideBy is applied like the game's providers (health cells = 4 points)", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(8f, 12, 2f, 3f));
            Near(3f, m.Provide(StatKind.MaxHealth, 4f), "max health / 4");
            Near(2f, m.Provide(StatKind.HealthVisualMin, 4f), "health / 4");
            Near(1.5f, m.Provide(StatKind.MaxEnergy, 2f), "max energy / 2");
            Near(12f, m.Provide(StatKind.MaxHealth, 0f), "DivideBy 0 treated as 1");
        });
        Run("normalized kinds divide by the maximum; zero maximum gives 0, not NaN", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(6f, 12, 1f, 4f));
            Near(0.5f, m.Provide(StatKind.HealthVisualMinNormalized, 1f), "health norm");
            Near(0.25f, m.Provide(StatKind.EnergyVisualMaxNormalized, 1f), "energy norm");
            VitalsModel z = new VitalsModel();
            z.Set(V(0f, 0, 0f, 0f));
            Near(0f, z.Provide(StatKind.HealthVisualMinNormalized, 1f), "health norm, max 0");
            Near(0f, z.Provide(StatKind.EnergyVisualMinNormalized, 1f), "energy norm, max 0");
        });
        Run("shared progress kinds (None) are never overridden", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(4f, 12, 1f, 3f));
            Check(!m.Provide(StatKind.None, 1f).HasValue, "None must fall back to the game (team values)");
        });
        Run("breath full, charge jump empty, soul flame from the sample", delegate {
            VitalsModel m = new VitalsModel();
            VitalsSample s = V(4f, 12, 1f, 3f); s.SoulFlame = 0.4f;
            m.Set(s);
            Near(1f, m.Provide(StatKind.Breath, 1f), "breath");
            Near(0f, m.Provide(StatKind.ChargeJump, 1f), "charge jump");
            Near(0.4f, m.Provide(StatKind.SoulFlame, 1f), "soul flame");
        });

        // ---- model: smoothing like the game
        Run("damage: low edge drops at once, high edge drains at 4 per second", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(12f, 12, 3f, 3f));
            m.Set(V(4f, 12, 3f, 3f));
            Near(4f, m.HealthMin, "low edge after hit");
            Near(12f, m.HealthMax, "high edge right after hit");
            m.Tick(1f);
            Near(8f, m.HealthMax, "high edge after 1 s");
            m.Tick(1f);
            Near(4f, m.HealthMax, "high edge after 2 s");
            m.Tick(1f);
            Near(4f, m.HealthMax, "high edge never passes the value");
        });
        Run("heal: high edge jumps, low edge rises at 4 per second", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(4f, 12, 3f, 3f));
            m.Set(V(12f, 12, 3f, 3f));
            Near(12f, m.HealthMax, "high edge after heal");
            Near(4f, m.HealthMin, "low edge right after heal");
            m.Tick(0.5f);
            Near(6f, m.HealthMin, "low edge after 0.5 s");
        });
        Run("energy use: low edge at once, high edge drains at 1 per second", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(12f, 12, 3f, 3f));
            m.Set(V(12f, 12, 1f, 3f));
            Near(1f, m.EnergyMin, "energy low edge");
            m.Tick(0.5f);
            Near(2.5f, m.EnergyMax, "energy high edge after 0.5 s");
        });
        Run("values are clamped to 0..max; a lower maximum clamps the edges", delegate {
            VitalsModel m = new VitalsModel();
            m.Set(V(20f, 12, -1f, 3f));
            Near(12f, m.Now.Health, "health clamped to max");
            Near(0f, m.Now.Energy, "energy clamped to 0");
            m.Set(V(12f, 8, 0f, 3f));
            Check(m.HealthMax <= 8f && m.HealthMin <= 8f, "edges above the new maximum: " + m.HealthMin + "/" + m.HealthMax);
            VitalsSample neg = V(1f, -4, 1f, -2f);
            m.Set(neg);
            Check(m.Now.MaxHealth == 0 && m.Now.MaxEnergy == 0f && m.Now.Health == 0f && m.Now.Energy == 0f, "negative maxima become 0");
        });
        Run("tick with no time or no data changes nothing", delegate {
            VitalsModel m = new VitalsModel();
            m.Tick(1f);
            Check(!m.Have, "tick must not invent data");
            m.Set(V(12f, 12, 3f, 3f)); m.Set(V(4f, 12, 3f, 3f));
            m.Tick(0f); m.Tick(-1f);
            Near(12f, m.HealthMax, "no drain for dt <= 0");
        });

        // ---- provider type names (the game's classes)
        Run("every per-player provider of the game's HUD maps to a kind", delegate {
            Dictionary<string, StatKind> expect = new Dictionary<string, StatKind> {
                { "SeinMaxHealthValueProvider", StatKind.MaxHealth }, { "SeinHealthVisualMinProvider", StatKind.HealthVisualMin },
                { "SeinHealthVisualMaxProvider", StatKind.HealthVisualMax }, { "SeinMaxEnergyValueProvider", StatKind.MaxEnergy },
                { "SeinEnergyMinVisualProvider", StatKind.EnergyVisualMin }, { "SeinEnergyMaxVisualProvider", StatKind.EnergyVisualMax },
                { "SeinSoulFlameCooldownValueProvider", StatKind.SoulFlame },
                { "SeinHealthVisualMinNormalizedProvider", StatKind.HealthVisualMinNormalized },
                { "SeinEnergyMaxVisualNormalizedProvider", StatKind.EnergyVisualMaxNormalized } };
            foreach (KeyValuePair<string, StatKind> e in expect) Check(VitalsModel.KindOf(e.Key) == e.Value, e.Key + " -> " + VitalsModel.KindOf(e.Key));
        });
        Run("team providers (ability points, spirit light, level, keystones, map stones) stay the game's", delegate {
            foreach (string n in new[] { "SeinSkillPointsProvider", "SkillPointsFloatProvider", "SeinExperienceVisualMaxNormalizedProvider",
                "SeinExperienceVisualMinNormalizedProvider", "SeinLevelValueProvider", "SeinKeystonesFloatProvider", "SeinMapstonesFloatProvider", "", "Unknown" })
                Check(VitalsModel.KindOf(n) == StatKind.None, n + " must be None");
        });

        // ---- wire format
        Run("vitals message round trip", delegate {
            VitalsSample s = V(7.5f, 16, 2.25f, 5f); s.SoulFlame = 0.5f; s.Downed = true;
            MemoryStream ms = new MemoryStream();
            VitalsModel.Write(new BinaryWriter(ms), 3, s);
            ms.Position = 0;
            int seat;
            VitalsSample r = VitalsModel.Read(new BinaryReader(ms), out seat);
            Check(seat == 3, "seat " + seat);
            Near(7.5f, r.Health, "health"); Check(r.MaxHealth == 16, "max health " + r.MaxHealth);
            Near(2.25f, r.Energy, "energy"); Near(5f, r.MaxEnergy, "max energy");
            Check(Math.Abs(r.SoulFlame - 0.5f) < 0.005f, "soul flame " + r.SoulFlame);
            Check(r.Downed, "downed");
            Check(ms.Position == ms.Length && ms.Length == 17, "17 bytes, all read (" + ms.Length + ")");
        });

        // ---- when to send
        Run("sender: first sample always, change after 0.1 s, heartbeat after 1 s", delegate {
            VitalsSender t = new VitalsSender();
            VitalsSample a = V(12f, 12, 3f, 3f), b = V(8f, 12, 3f, 3f);
            Check(t.Due(a, 5f), "first");
            t.MarkSent(a, 5f);
            Check(!t.Due(a, 5.5f), "no change before heartbeat");
            Check(!t.Due(b, 5.05f), "change within 0.1 s waits");
            Check(t.Due(b, 5.1f), "change at 0.1 s goes");
            Check(t.Due(a, 6f), "heartbeat at 1 s");
            Check(t.Due(a, 4f), "clock went back: resend");
            t.Reset();
            Check(t.Due(a, 5.01f), "after reset");
        });
        Run("sender: downed flag and max changes count as changes; tiny float noise does not", delegate {
            VitalsSender t = new VitalsSender();
            VitalsSample a = V(12f, 12, 3f, 3f);
            t.MarkSent(a, 0f);
            VitalsSample d = a; d.Downed = true;
            Check(t.Due(d, 0.2f), "downed");
            VitalsSample m = a; m.MaxHealth = 16;
            Check(t.Due(m, 0.2f), "max health");
            VitalsSample n = a; n.Health += 0.001f;
            Check(!t.Due(n, 0.2f), "noise");
        });

        // ---- layout (fractions of the screen; native strip measured in the game at 960x540: x 222-738, y 22-77)
        float nl = 222f / 960f, nr = 738f / 960f, nb = 22f / 540f, nh = 55f / 540f;
        Run("layout: 1-3 teammates never overlap each other or the native HUD, and stay on screen", delegate {
            for (int count = 1; count <= 3; count++)
            {
                List<SlotRect> rects = new List<SlotRect>();
                for (int i = 0; i < count; i++) rects.Add(StatusBarLayout.Place(i, count, nl, nr, nb, nh));
                for (int i = 0; i < count; i++)
                {
                    SlotRect r = rects[i];
                    Check(r.Left >= 0f && r.Right <= 1f && r.Bottom >= 0f && r.Top + StatusBarLayout.LabelSpace <= 1f, "on screen: " + count + "/" + i);
                    Check(!StatusBarLayout.OverlapsNative(r, nl, nr, nb, nh), "covers native HUD: " + count + "/" + i);
                    for (int j = 0; j < i; j++) Check(!StatusBarLayout.Overlaps(r, rects[j]), "copies " + i + " and " + j + " overlap at " + count);
                }
            }
        });
        Run("layout: P2 left of the native HUD, P3 right, P4 above P2", delegate {
            SlotRect p2 = StatusBarLayout.Place(0, 3, nl, nr, nb, nh), p3 = StatusBarLayout.Place(1, 3, nl, nr, nb, nh), p4 = StatusBarLayout.Place(2, 3, nl, nr, nb, nh);
            Check(p2.Right <= nl, "P2 left"); Check(p3.Left >= nr, "P3 right");
            Check(Math.Abs(p4.CenterX - p2.CenterX) < 1e-5f && p4.Bottom >= p2.Top + StatusBarLayout.LabelSpace, "P4 above P2");
            Near(nb, p2.Bottom, "P2 sits on the native HUD's bottom line"); Near(nb, p3.Bottom, "P3 bottom line");
        });
        Run("layout: same fractions at 1080p and 4K (resolution independent), sensible scale", delegate {
            // 4K: the native strip has the same screen fraction (the HUD camera is orthographic and fixed in height).
            float nl4 = 888f / 3840f, nr4 = 2952f / 3840f, nb4 = 88f / 2160f, nh4 = 220f / 2160f;
            SlotRect a = StatusBarLayout.Place(0, 3, nl, nr, nb, nh), b = StatusBarLayout.Place(0, 3, nl4, nr4, nb4, nh4);
            Near(a.CenterX, b.CenterX, "centre x"); Near(a.Scale, b.Scale, "scale");
            Check(a.Scale >= 0.3f && a.Scale <= StatusBarLayout.MaxScale, "scale " + a.Scale);
        });
        Run("layout: narrow screens (4:3, 5:4) still do not overlap; scale clamps at the minimum", delegate {
            // On 4:3 the strip is a larger share of the width.
            float nl43 = 0.15f, nr43 = 0.85f;
            for (int i = 0; i < 3; i++)
            {
                SlotRect r = StatusBarLayout.Place(i, 3, nl43, nr43, nb, nh);
                Check(!StatusBarLayout.OverlapsNative(r, nl43, nr43, nb, nh), "4:3 covers native " + i);
                Check(r.Scale >= StatusBarLayout.MinScale, "scale >= min");
            }
        });
        Run("layout: ultrawide caps the copies at the maximum scale", delegate {
            SlotRect r = StatusBarLayout.Place(0, 1, 0.38f, 0.62f, nb, nh);
            Near(StatusBarLayout.MaxScale, r.Scale, "max scale");
        });

        Console.WriteLine(s_failed == 0 ? "StatusBar: " + s_cases + " passed" : "StatusBar: " + s_failed + " of " + s_cases + " FAILED");
        return s_failed == 0 ? 0 : 1;
    }
}
