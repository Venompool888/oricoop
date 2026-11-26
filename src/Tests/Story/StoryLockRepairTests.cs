using System;
using System.Collections.Generic;
using OriCoop.Story;

static partial class StoryTests
{
    // An orphaned lock is repaired in a few seconds, but a running sequence and the gap of a story chain are protected.
    static void StoryLockRepairCases()
    {
        Check("lock repair: orphaned lock is stale at 3 seconds", delegate {
            Equal(false, StoryLockRepair.Due(2.9f, false));
            Equal(true, StoryLockRepair.Due(3f, false));
            Equal(true, StoryLockRepair.Due(3.1f, false));
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
        Check("lock repair: the world-map chain keeps its lock across a short gap", delegate {
            Equal(false, StoryLockRepair.Due(3f, false, 0.02f));
            Equal(false, StoryLockRepair.Due(3f, false, 1.99f));
            Equal(true, StoryLockRepair.Due(3f, false, 2f));
            Equal(true, StoryLockRepair.Due(3f, false, float.MaxValue));
            Equal(false, StoryLockRepair.Due(3f, true, 30f));
            Equal(true, StoryLockRepair.Due(60f, false, 0.02f));
        });
        Check("lock repair: fresh lock is never repaired", delegate {
            Equal(false, StoryLockRepair.Due(0f, false));
            Equal(false, StoryLockRepair.Due(0f, true));
        });
        Check("lock repair: pausing prevents repair even past the timeout", delegate {
            Func<bool> lookup = delegate { throw new Exception("looked up sequences while paused"); };
            Equal(false, StoryLockRepair.Due(5f, false, float.MaxValue, true, lookup));
            Equal(false, StoryLockRepair.Due(61f, true, float.MaxValue, true, lookup));
            Equal(true, StoryLockRepair.Due(5f, false, float.MaxValue, false, () => false));
        });
        Check("lock repair: a running native continuation without a story entry keeps input locked", delegate {
            bool continuationRunning = true;
            Func<bool> lookup = () => continuationRunning;
            Equal(false, StoryLockRepair.Due(5f, false, 3f, false, lookup));
            continuationRunning = false;
            Equal(true, StoryLockRepair.Due(5f, false, 3f, false, lookup));
        });
        Action<string> noTimer = delegate(string action) { throw new Exception("unexpected timer stop: " + action); };
        Check("sequence end releases only its own manual lock", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockManual("A", false); l.LockManual("B", true);
            Equal((bool?)null, l.End("unrelated", null, false, noTimer));
            Equal((bool?)true, l.End("A", null, false, noTimer));
            Equal((bool?)false, l.End("B", null, false, noTimer));
            Equal((bool?)null, l.End("B", null, false, noTimer));
        });
        Check("sequence end preserves a preexisting native lock", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockManual("A", true);
            Equal((bool?)true, l.End("A", null, false, noTimer));
            Equal(false, l.UnlockManual(null));
        });
        Check("sequence end hands a manual lock to its running continuation", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockManual("A", false);
            Equal((bool?)true, l.End("A", "B", false, noTimer));
            Equal(false, l.UnlockManual("B"));
            Equal((bool?)null, l.End("B", null, false, noTimer));
            l.LockManual("C", false);
            Equal((bool?)true, l.End("C", null, true, noTimer));
            Equal(false, l.UnlockManual("after scene trip"));
        });
        Check("overtaken mirror passes its lock before Stop and the continuation's first action", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockManual("03", false);
            l.HandOffManual("03", "04");
            Equal((bool?)null, l.End("03", null, false, noTimer));
            Equal(false, l.UnlockManual("04"));
            Equal((bool?)null, l.End("04", null, false, noTimer));
        });
        Check("sequence end stops its own timer but leaves a later standalone native timer locked", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockTimed("A", "sequence timer", false);
            l.LockTimed(null, "native timer", true);
            string stopped = null;
            Equal((bool?)true, l.End("A", null, false, action => stopped = action));
            Equal("sequence timer", stopped);
            Equal(true, l.TimerRunning(action => action == "native timer"));
            Equal(false, l.TimerRunning(action => false));
            Equal(false, l.EndTimed("native timer"));
        });
        Check("standalone timer expiry preserves a sequence's lock and other timers", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockTimed(null, "first timer", false);
            l.LockManual("A", true);
            l.LockTimed(null, "second timer", true);
            Equal(true, l.EndTimed("first timer"));
            Equal((bool?)true, l.End("A", null, false, noTimer));
            Equal(false, l.EndTimed("second timer"));
        });
        Check("stopping an untracked timer does not release a preexisting native lock", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockManual("A", true);
            Equal(true, l.EndTimed("untracked"));
            Equal((bool?)true, l.End("A", null, false, noTimer));
        });
        Check("sequence end stops its timer but leaves another sequence locked", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockTimed("A", "only A's timer", false); l.LockManual("B", true);
            string stopped = null;
            Equal((bool?)true, l.End("A", null, false, delegate(string action) { stopped = action; }));
            Equal("only A's timer", stopped);
            Equal(false, l.UnlockManual("B"));
        });
        // T-235: the tree of a sequence: its actions, nested sequences and RunActionOnce targets.
        Dictionary<string, string[]> tree = new Dictionary<string, string[]>();
        tree["ambient loop"] = new[] { "animate", "wait", "animate" };
        tree["map 04"] = new[] { "camera", "unlock input" };
        tree["outer"] = new[] { "camera", "nested" };
        tree["nested"] = new[] { "once" };
        tree["once"] = new[] { "deep" };
        tree["deep"] = new[] { "lock input" };
        tree["loop a"] = new[] { "loop b" };
        tree["loop b"] = new[] { "loop a" };
        Func<string, bool> input = n => n == "lock input" || n == "unlock input";
        Func<string, IEnumerable<string>> children = n => tree.ContainsKey(n) ? tree[n] : null;
        Check("lock repair: an ambient sequence without input actions does not keep a stale lock", delegate {
            Equal(false, StoryLockRepair.TouchesInput("ambient loop", input, children));
            Equal(false, StoryLockRepair.TouchesInput<string>(null, input, children));
        });
        Check("lock repair: a continuation that unlocks input still keeps the lock", delegate {
            Equal(true, StoryLockRepair.TouchesInput("map 04", input, children));
            Equal(true, StoryLockRepair.TouchesInput("outer", input, children));
        });
        Check("lock repair: input search stops at cycles", delegate {
            Equal(false, StoryLockRepair.TouchesInput("loop a", input, children));
        });
        Check("lock repair: guard only waits for running sequences that touch input (playtest 6 map)", delegate {
            string[] running = { "ambient loop" };
            Func<bool> lookup = delegate {
                foreach (string name in running) if (StoryLockRepair.TouchesInput(name, input, children)) return true;
                return false;
            };
            Equal(true, StoryLockRepair.Due(5f, false, 3f, false, lookup));
            running = new[] { "ambient loop", "map 04" };
            Equal(false, StoryLockRepair.Due(5f, false, 3f, false, lookup));
        });
    }
}
