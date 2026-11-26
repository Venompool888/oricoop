using System;
using System.IO;
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
        if (Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PICKUP_GUID_ONLY") == "1") PickupPlace.MutateGuidOnly = true;
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
        Check("T-258: a missed heartbeat never lets a member advance a host-controlled action", delegate {
            var controls = new StoryControls();
            string key = StoryControls.Key(1, 3, "dialog", 1);
            int confirmed = 0, page;
            Equal(false, controls.ControlsAction(1, key, 6, 0, ref confirmed, out page)); // older host
            Equal(true, controls.Receive(1, 1, key, 2, 7));
            Equal(true, controls.ControlsAction(1, key, 7, 0, ref confirmed, out page));
            controls.Expire(1, 13);
            Equal(true, controls.ControlsAction(1, key, 13, 0, ref confirmed, out page));
            Equal(true, controls.Receive(1, 1, key, StoryControls.Native, 14));
            Equal(false, controls.ControlsAction(1, key, 14, 0, ref confirmed, out page));
            Equal(true, controls.Receive(1, 1, key, 2, 15));
            Equal(true, controls.ControlsAction(1, key, 15, 0, ref confirmed, out page));
            Equal(false, controls.ControlsAction(2, key, 16, 0, ref confirmed, out page));
        });
        Check("T-258: only a matching live control from the current host advances the shared story", delegate {
            var controls = new StoryControls();
            string key = StoryControls.Key(2, 5, "dialog", 1);
            int page;
            Equal(false, controls.TryGet(1, key, 0, out page));
            Equal(false, controls.Receive(3, 1, key, 1, 0));
            Equal(true, controls.Receive(1, 1, key, 1, 1));
            Equal(false, controls.Receive(1, 1, key, 0, 2));
            Equal(true, controls.TryGet(1, key, 3, out page)); Equal(1, page);
            Equal(false, controls.TryGet(1, StoryControls.Key(2, 5, "tutorial", 1), 3, out page));
            Equal(false, controls.TryGet(1, StoryControls.Key(2, 5, "dialog", 2), 3, out page));
            Equal(true, controls.Receive(1, 1, key, StoryControls.Closed, 4));
            Equal(false, controls.Receive(1, 1, key, 0, 5));
            Equal(false, controls.TryGet(2, key, 6, out page));
            Equal(false, controls.TryGet(1, key, 9, out page));
        });
        Check("T-258: only the first request for the current action and page is shown", delegate {
            int shown = int.MinValue;
            Equal(false, StoryControls.AcceptRequest("box", 1, "old box", 1, ref shown));
            Equal(false, StoryControls.AcceptRequest("box", 1, "box", 2, ref shown));
            Equal(true, StoryControls.AcceptRequest("box", 1, "box", 1, ref shown));
            Equal(false, StoryControls.AcceptRequest("box", 1, "box", 1, ref shown));
            Equal(true, StoryControls.AcceptRequest("box", 2, "box", 2, ref shown));
            Equal(false, StoryControls.AcceptRequest("box", 2, "box", 1, ref shown));
        });
        Check("T-258: skip requests bound their UTF-8 key to the received packet", delegate {
            string key; int page;
            string longKey = new string('页', 1000);
            using (var stream = new MemoryStream())
            {
                var writer = new BinaryWriter(stream); writer.Write(longKey); writer.Write(12); stream.Position = 0;
                Equal(true, StoryControls.ReadAction(new BinaryReader(stream), out key, out page));
                Equal(longKey, key); Equal(12, page);
            }
            foreach (byte[] invalid in new[] { new byte[0], new byte[] { 255, 255, 255, 255, 7 }, new byte[] { 12, 65, 0, 0, 0, 0 } })
                using (var reader = new BinaryReader(new MemoryStream(invalid))) Equal(false, StoryControls.ReadAction(reader, out key, out page));
        });
        IntroCases();
        StoryCases();
        Check("spirit well: net12 snapshot records each opened well once", delegate {
            SpiritWellActivations wells = new SpiritWellActivations();
            Equal(true, wells.TryActivate("scene A:opening"));
            Equal(false, wells.TryActivate("scene A:opening"));
            Equal(true, wells.TryActivate("scene B:opening"));
            wells.Clear();
            Equal(true, wells.TryActivate("scene A:opening"));
        });
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
        LeftoverCases();   // T-207, T-212
        return failures == 0 ? 0 : 1;
    }
}
