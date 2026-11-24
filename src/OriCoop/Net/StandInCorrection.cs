namespace OriCoop.Net
{
    // T-251: how the partner's stand-in (the puppet that replays its owner's inputs here) follows the owner's position.
    // Unity-free so the decisions are unit tested; NetPlay.Correct / PlayRemote apply them, and the wall test is a
    // seam (IWallProbe: a line cast against the level's colliders in game, a fake in the unit tests).
    //
    // Playtest 6 (34% loss, "it slides around, I saw you go through the wall"): a small position error was pulled in by
    // writing the position directly, 20% per fixed step, with no collision. When the owner was on the other side of a
    // wall (the replay had stopped against it, or a stall left the stand-in behind), that pull dragged it through the
    // wall over a dozen steps. Now a pull whose straight path crosses a wall becomes one jump to the owner's position.
    public static class StandInCorrection
    {
        public enum Fix { None, Blend, Snap }

        // Errors below this are left alone (as before); between it and NoWallCheckBelow the pull moves the stand-in a
        // few millimetres per step, which cannot visibly cross a wall, so no line cast is spent on them.
        public const float MinError = 0.01f;
        public const float NoWallCheckBelow = 0.1f;

        // A stall: how many fixed steps in a row the last input is repeated while no frame plays (lost ticks and a
        // starved stream together). Then the stand-in lets go of the buttons until frames play again. Before, only the
        // starved path had this cap: after a stall had already let go, a burst whose first ticks were lost replayed the
        // stale input again for up to TargetDelay + 6 steps (~300 ms more of running the old way: extrapolation).
        public const int RepeatSteps = 6;

        public interface IWallProbe
        {
            // True when the straight segment between the two points crosses level geometry (walls, floors, ceilings).
            bool Blocked(float fromX, float fromY, float toX, float toY);
        }

        public static bool MutateNoWallSnap;   // test only: ORICOOP_MUTATE_STANDIN_SLIDES (the old pull through walls)

        // error = |owner position - stand-in position|; from = stand-in, to = owner.
        public static Fix Decide(float error, float snapDistance, float fromX, float fromY, float toX, float toY, IWallProbe walls)
        {
            if (error > snapDistance) return Fix.Snap;
            if (error <= MinError) return Fix.None;
            if (error < NoWallCheckBelow || MutateNoWallSnap || walls == null) return Fix.Blend;
            return walls.Blocked(fromX, fromY, toX, toY) ? Fix.Snap : Fix.Blend;
        }

        // One fixed step of the stand-in's input. have: this tick's frame is in the buffer; newer: a later frame is
        // (so this tick was lost; otherwise the stream is starved). missingRun counts the steps since a frame last played.
        // Lost: repeat the last input and move on; late: repeat it and wait. Either way only RepeatSteps steps in a row,
        // then hold (neutral buttons; a lost tick still moves on).
        public enum Step { Play, RepeatLost, RepeatLate, HoldLost, HoldLate }
        public static Step NextStep(bool have, bool newer, ref int missingRun)
        {
            if (have) { missingRun = 0; return Step.Play; }
            missingRun++;
            bool repeat = missingRun <= RepeatSteps;
            if (newer) return repeat ? Step.RepeatLost : Step.HoldLost;
            return repeat ? Step.RepeatLate : Step.HoldLate;
        }
        public static bool Lost(Step step) { return step == Step.RepeatLost || step == Step.HoldLost; }
        public static bool Holds(Step step) { return step == Step.HoldLost || step == Step.HoldLate; }
    }
}
