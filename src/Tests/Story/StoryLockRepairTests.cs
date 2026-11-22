using System;
using OriCoop.Story;

static partial class StoryTests
{
    // T-177: CutsceneGuard's stale LockInputByAction repair. Expected values come from the ticket: with no story
    // sequence running the lock is stale after 20 s (killed LockPlayerInputAction coroutine, unchanged); while a story
    // sequence of this PC still runs the lock is the story's own (03 locks, 04 unlocks) and is left alone up to 60 s.
    static void StoryLockRepairCases()
    {
        Check("lock repair: no story running — stale after 20 s (boundary both sides)", delegate {
            Equal(false, StoryLockRepair.Due(19.9f, false));
            Equal(true, StoryLockRepair.Due(20f, false));
            Equal(true, StoryLockRepair.Due(20.1f, false));
        });
        Check("lock repair: playtest/chain — 03→04 still running at 20 s keeps its lock", delegate {
            Equal(false, StoryLockRepair.Due(20f, true));
            Equal(false, StoryLockRepair.Due(35f, true));
            Equal(false, StoryLockRepair.Due(59.9f, true));
        });
        Check("lock repair: a story running 60 s is given up on", delegate {
            Equal(true, StoryLockRepair.Due(60f, true));
            Equal(true, StoryLockRepair.Due(61f, true));
        });
        Check("lock repair: T-183 H-1 — the frame between the world-map sequence and 04 (nothing running for 0.02 s) keeps the lock", delegate {
            Equal(false, StoryLockRepair.Due(20f, false, 0.02f));
            Equal(false, StoryLockRepair.Due(20f, false, 1.99f));
            Equal(true, StoryLockRepair.Due(20f, false, 2f));       // boundary: nothing ran for the whole gap
            Equal(true, StoryLockRepair.Due(20f, false, float.MaxValue));
            Equal(false, StoryLockRepair.Due(20f, true, 30f));
            Equal(true, StoryLockRepair.Due(60f, false, 0.02f));   // the 60 s cap still applies
        });
        Check("lock repair: fresh lock is never repaired", delegate {
            Equal(false, StoryLockRepair.Due(0f, false));
            Equal(false, StoryLockRepair.Due(0f, true));
        });
    }
}
