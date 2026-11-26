using System;
using System.Collections.Generic;

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

        // T-235: only a sequence that can lock or unlock input can still own the lock or be the continuation that ends it.
        // Playtest 6: after the Spirit Tree map ended, an unrelated running sequence kept the guest locked for 60 s.
        public const int InputSearchDepth = 4;
        public static bool TouchesInput<T>(T node, Func<T, bool> isInputAction, Func<T, IEnumerable<T>> children) where T : class
        {
            return TouchesInput(node, isInputAction, children, InputSearchDepth, new HashSet<T>());
        }
        static bool TouchesInput<T>(T node, Func<T, bool> isInputAction, Func<T, IEnumerable<T>> children, int depth, HashSet<T> seen)
            where T : class
        {
            if (node == null || !seen.Add(node)) return false;
            if (isInputAction(node)) return true;
            if (depth <= 0) return false;
            IEnumerable<T> next = children(node);
            if (next != null)
                foreach (T child in next) if (TouchesInput(child, isInputAction, children, depth - 1, seen)) return true;
            return false;
        }
    }
}
