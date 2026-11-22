namespace OriCoop.Story
{
    // T-177: when CutsceneGuard may clear GameController.LockInputByAction that has been on for a while.
    // Two actions set it. LockPlayerInputAction's coroutine clears it after a few seconds; if the coroutine is killed
    // with its GameObject (story stopped / deactivated mid-way) the flag stays on and the player can't move: that is
    // what the repair is for. LockPlayerInputManualAction ("Lock player input" / "Unlock player input") has no timer:
    // the story itself clears it, possibly much later. The spirit tree's 03 locks at its step 05 and 04 unlocks at its
    // step 17, after a world map scene and the world-map objective that waits for the player's press, so the lock is
    // legitimately on for well over 20 s. Clearing it there made StorySync end every local story at once while 04
    // was still running (its "14. Stop Character Animation" never ran before the check).
    public static class StoryLockRepair
    {
        public const float Stale = 20f;      // no story sequence of this PC running: a killed coroutine left it
        public const float Longest = 60f;    // a story sequence still running: give up waiting (StoryLedger.LocalEnded)

        public static bool Due(float heldSeconds, bool storySequenceRunning)
        {
            return heldSeconds >= Longest || !storySequenceRunning && heldSeconds >= Stale;
        }

        // T-183 (run H-1): a chain hands over from one sequence to the next across a frame (the world-map sequence ends,
        // 04 starts on the next frame). The repair fired in exactly that frame (lock held 20 s, "story running=False"),
        // cleared 03's lock, and StorySync ended the stories while 04 then waited for its world-map objective.
        // A story sequence that ran within the last Gap seconds still counts as running.
        public const float Gap = 2f;
        public static bool Due(float heldSeconds, bool storySequenceRunning, float secondsSinceStoryRan)
        {
            return Due(heldSeconds, storySequenceRunning || secondsSinceStoryRan < Gap);
        }
    }
}
