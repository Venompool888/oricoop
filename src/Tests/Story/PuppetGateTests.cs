using System;
using OriCoop.Story;

static partial class StoryTests
{
    // T-137: a camera puppet the scene keeps driving is left alone; a stuck one is still repaired.
    static void PuppetGateCases()
    {
        Check("puppet gate: stuck puppet keeps being repairable", delegate {
            PuppetRepairGate g = new PuppetRepairGate();
            Equal(true, g.ShouldRepair(7, 10f));
            g.Repaired(7, 10f);
            Equal(false, g.Observe(0, 10.1f));           // nothing came back
            Equal(false, g.Observe(7, 10.8f));           // too late to count as "came straight back"
            Equal(true, g.ShouldRepair(7, 12f));
            Equal(0, g.DrivenDetections);
        });
        Check("puppet gate: puppet set again right after a repair is driven by the scene", delegate {
            PuppetRepairGate g = new PuppetRepairGate();
            g.Repaired(7, 10f);
            Equal(true, g.Observe(7, 10.02f));
            Equal(1, g.DrivenDetections);
            Equal(false, g.ShouldRepair(7, 12f));
            Equal(false, g.ShouldRepair(7, 39.9f));
            Equal(true, g.ShouldRepair(7, 40.1f));       // hold over: repairable again
            Equal(true, g.ShouldRepair(8, 12f));         // another puppet is unaffected
        });
        Check("puppet gate: a different puppet coming in is not a come-back", delegate {
            PuppetRepairGate g = new PuppetRepairGate();
            g.Repaired(7, 10f);
            Equal(false, g.Observe(9, 10.05f));
            Equal(true, g.ShouldRepair(9, 10.1f));
        });
        Check("puppet gate: disabled (mutation) always repairs", delegate {
            PuppetRepairGate g = new PuppetRepairGate(); g.Disabled = true;
            g.Repaired(7, 10f); g.Observe(7, 10.02f);
            Equal(true, g.ShouldRepair(7, 12f));
        });
    }
}
