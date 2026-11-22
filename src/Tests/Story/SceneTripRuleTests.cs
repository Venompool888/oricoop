using OriCoop.Story;

static partial class StoryTests
{
    // T-185: a mirrored start waits while this PC's own scene trip is pending (the game re-places the character when
    // the trip completes), but never longer than SceneTripRule.MaxWait after the pending trip was first seen.
    // Expected values from the ticket: run H-5's mirrored 04 arrived in the frame after the trip was asked for.
    static void SceneTripRuleCases()
    {
        Check("scene trip: H-5 — the partner's 04 arrives right after the trip back was asked for: it waits", delegate {
            Equal(true, SceneTripRule.Holds(true, 0f));
            Equal(true, SceneTripRule.Holds(true, 0.01f));
        });
        Check("scene trip: no trip pending: a mirrored start goes ahead", delegate {
            Equal(false, SceneTripRule.Holds(false, 0f));
            Equal(false, SceneTripRule.Holds(false, 5f));
        });
        Check("scene trip: the wait is bounded (at MaxWait still waits, past it mirroring goes on)", delegate {
            Equal(10f, SceneTripRule.MaxWait);
            Equal(true, SceneTripRule.Holds(true, 9.9f));
            Equal(true, SceneTripRule.Holds(true, 10f));
            Equal(false, SceneTripRule.Holds(true, 10.1f));
            Equal(false, SceneTripRule.Holds(true, 60f));
        });
        Check("scene trip: a clock that went backwards never holds", delegate {
            Equal(false, SceneTripRule.Holds(true, -0.5f));
        });
    }
}
