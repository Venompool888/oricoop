using System;
using OriCoop.Story;

static partial class StoryTests
{
    static int failures;
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Equal<T>(T expected, T actual)
    { if (!object.Equals(expected, actual)) throw new Exception("expected " + expected + ", got " + actual); }

    static int Main()
    {
        Check("shared initial copy", delegate {
            SharedResources r = new SharedResources();
            SharedValues a = r.Merge(3, 4, 2f, 5f, 9, 9, 9f);
            Equal(3, a.Keys); Equal(4, a.Maps); Equal(2f, a.Energy);
        });
        // T-134: offline P2-P4 all share the pool: each extra's change since the last sync reaches P1.
        Check("shared pool merges every extra player", delegate {
            SharedResources r = new SharedResources(); r.Remember(3, 4, 2f);
            SharedValues p1 = new SharedValues(); p1.Keys = 3; p1.Maps = 4; p1.Energy = 2f;
            SharedValues p2 = p1, p3 = p1, p4 = p1;
            p2.Keys = 4;        // P2 picked up a keystone
            p3.Energy = 3f;     // P3 picked up energy
            p4.Maps = 5;        // P4 picked up a map stone
            SharedValues m = r.MergeAll(p1, 5f, new[] { p2, p3, p4 });
            Equal(4, m.Keys); Equal(5, m.Maps); Equal(3f, m.Energy);
            p1.Energy = 1f;     // P1 spent one at the same time: both changes count
            m = r.MergeAll(p1, 5f, new[] { p2, p3, p4 });
            Equal(2f, m.Energy);
        });
        Check("shared pool: a joining player starts from the last sync", delegate {
            SharedResources r = new SharedResources(); r.Remember(3, 4, 2f);
            SharedValues joined = r.Baseline;
            SharedValues p1 = new SharedValues(); p1.Keys = 3; p1.Maps = 4; p1.Energy = 1.5f;   // P1 spent 0.5 meanwhile
            SharedValues m = r.MergeAll(p1, 5f, new[] { joined });
            Equal(3, m.Keys); Equal(4, m.Maps); Equal(1.5f, m.Energy);
            Equal(p1.Energy, new SharedResources().MergeAll(p1, 5f, new[] { joined }).Energy);   // first sync: P1 as is
        });
        IntroCases();
        StoryCases();
        ProgressCases();
        ObjectCases();
        GateCases();
        AnimCases();
        SkillCases();
        SeatSkillCases();
        PuppetGateCases();
        MirrorBaselineCases();   // T-175
        StoryLockRepairCases();   // T-177
        MirrorStartsCases();   // T-183
        SceneTripRuleCases();   // T-185
        return failures == 0 ? 0 : 1;
    }
}
