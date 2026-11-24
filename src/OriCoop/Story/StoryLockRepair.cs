using System;

namespace OriCoop.Story
{
    // Fallback for input locks without a running sequence, such as a coroutine killed by a scene unload.
    public static class StoryLockRepair
    {
        public const float Stale = 3f;       // orphaned lock
        public const float Longest = 60f;    // a story sequence still running: give up waiting (StoryLedger.LocalEnded)

        public static bool Due(float heldSeconds, bool storySequenceRunning)
        {
            return heldSeconds >= Longest || !storySequenceRunning && heldSeconds >= Stale;
        }

        // Chained sequences may leave a short gap between the lock action and the sequence that unlocks it.
        public const float Gap = 2f;
        public static bool Due(float heldSeconds, bool storySequenceRunning, float secondsSinceStoryRan,
            bool paused = false, Func<bool> anySequenceRunning = null)
        {
            if (paused || !Due(heldSeconds, storySequenceRunning || secondsSinceStoryRan < Gap)) return false;
            return heldSeconds >= Longest || anySequenceRunning == null || !anySequenceRunning();
        }
    }
}
