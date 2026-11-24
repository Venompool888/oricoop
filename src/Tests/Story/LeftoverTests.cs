using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Story;

// T-207 / T-212: story mirror, gate and "local only" leftovers. Expected values come from the two tickets.
static partial class StoryTests
{
    static void LeftoverCases()
    {
        // T-212 (1): the session ended while a mirrored story had the screen faded to black.
        Check("T-212.1 black after the session: faded back in once black for 4 s (boundary both sides)", delegate {
            Equal(false, SessionEndRepair.Due(5f, 3.9f));
            Equal(true, SessionEndRepair.Due(5f, 4f));
            Equal(true, SessionEndRepair.Due(5f, 9f));
        });
        Check("T-212.1 black after the session: only for 30 s after the session, never without one", delegate {
            Equal(true, SessionEndRepair.Watching(0f)); Equal(true, SessionEndRepair.Watching(29.9f));
            Equal(false, SessionEndRepair.Watching(30f));
            Equal(false, SessionEndRepair.Due(30f, 10f));     // long offline: a black screen is the game's own business
            Equal(false, SessionEndRepair.Watching(-1f));     // never online in this process (plain single player)
            Equal(false, SessionEndRepair.Due(-1f, 10f));
        });

        // Review finding 9: the single player's own cutscene goes black 10 s after the session ended, for 6 s.
        Check("review 9: only a screen already going black when the session ended is faded back in", delegate {
            Equal(true, SessionEndRepair.Due(5f, 4f, true));     // the stopped mirror's black
            Equal(false, SessionEndRepair.Due(16f, 6f, false));  // the game's own, begun after the session: left alone
            Equal(false, SessionEndRepair.Due(5f, 3.9f, true)); Equal(false, SessionEndRepair.Due(30f, 10f, true));
        });

        // T-212 (2): a forced fire for a trigger the host has not loaded.
        Check("T-212.2 force wire: the trigger position travels with the request", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteForce(w, "洞穴", -164.6f, -262.1f, -0.1f); }))
            {
                bool has; float x, y, z; Equal("洞穴", GateWire.ReadForce(r, out has, out x, out y, out z));
                Equal(true, has); Equal(-164.6f, x); Equal(-262.1f, y); Equal(-0.1f, z);
            }
        });
        Check("T-212.2 force wire: a request without a position (older build) still reads", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteForce(w, "a"); }))
            {
                bool has; float x, y, z; Equal("a", GateWire.ReadForce(r, out has, out x, out y, out z));
                Equal(false, has); Equal(0f, x);
            }
            // The old reader still reads the key of a new request.
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteForce(w, "a", 1f, 2f, 3f); })) Equal("a", GateWire.ReadForce(r));
        });
        Check("T-212.2 force wire: a position that is not a number is refused", delegate {
            GateInvalid(delegate(BinaryWriter w) { GateWire.WriteForce(w, "a", float.NaN, 0f, 0f); },
                delegate(BinaryReader r) { bool has; float x, y, z; GateWire.ReadForce(r, out has, out x, out y, out z); });
        });
        Check("T-212.2 force: the host gives up after 3 s without a position, 10 s after it moved there (boundaries)", delegate {
            Equal(false, GateForce.GiveUp(2.9f, false)); Equal(true, GateForce.GiveUp(3f, false));
            Equal(false, GateForce.GiveUp(3f, true)); Equal(false, GateForce.GiveUp(9.9f, true)); Equal(true, GateForce.GiveUp(10f, true));
        });
        Check("T-212.2 force: the ring can be held again 4 s after a force that fired nothing", delegate {
            Equal(false, GateForce.Rearm(3.9f, false)); Equal(true, GateForce.Rearm(4f, false));
            Equal(false, GateForce.Rearm(4f, true)); Equal(false, GateForce.Rearm(60f, true));   // it fired: nothing to retry
        });

        // T-212 (3): three players; seats 2 and 3 each started a story that seat 1 mirrors; seat 3 dropped.
        Check("T-212.3 dropped teammate: only its stories are ended, and its script is stopped while another mirror runs", delegate {
            StoryLedger<Run> l = new StoryLedger<Run>();
            l.TrackRemote(new Run { Key = "a", Sender = 2, Token = 1 }, false, 0f);
            l.TrackRemote(new Run { Key = "b", Sender = 3, Token = 1 }, false, 0f);
            List<ulong> gone = l.RemoteSeatTokens(3);
            Equal(1, gone.Count); Equal(StoryLedger<Run>.TokenKey(3, 1), gone[0]);
            l.Remote.Remove(gone[0]);
            Equal(StoryCleanup.StoryOnly, StoryLedger<Run>.Cleanup(l.Remote.Count));   // seat 2's story still runs
            l.Remote.Clear();
            Equal(StoryCleanup.All, StoryLedger<Run>.Cleanup(l.Remote.Count));         // the last one: the full cleanup
        });

        // T-212 (4): the story's start went out; then a partner's lever pull opened the "local only" window here.
        Check("T-212.4 story end: owed whenever the start was sent, also inside a local-only window", delegate {
            Run announced = new Run { StartSent = true }, never = new Run();
            Equal(true, StoryLedger<Run>.EndOwed(announced, true));
            Equal(true, StoryLedger<Run>.EndOwed(announced, false));
            Equal(false, StoryLedger<Run>.EndOwed(never, true));    // nobody heard of it: nothing to end
            Equal(true, StoryLedger<Run>.EndOwed(never, false));    // the rule before T-212 for this case
        });

        // T-212 (5): the guest was in a scene trip when the start arrived; the short story's end came first.
        Check("T-212.5 late start: an unstarted story is kept for 8 s after its end arrived (boundary both sides)", delegate {
            Run r = new Run { Began = 10f };
            Equal(true, StoryLedger<Run>.KeepForLateStart(r, 10.5f)); Equal(true, StoryLedger<Run>.KeepForLateStart(r, 17.9f));
            Equal(false, StoryLedger<Run>.KeepForLateStart(r, 18f));
            r.Resolved = true; Equal(false, StoryLedger<Run>.KeepForLateStart(r, 10.5f));   // its mirror runs: the usual end
        });
        Check("T-212.5 late start: an ended-early story keeps retrying, then is dropped; it is never watched", delegate {
            Run r = new Run { Began = 10f, EndedEarly = true, Classified = true };
            Equal(StoryResolution.Retry, StoryLedger<Run>.Resolution(r, 12.5f, false));   // a live story would be "watch" here
            Equal(StoryResolution.Retry, StoryLedger<Run>.Resolution(r, 17.9f, false));
            Equal(StoryResolution.Drop, StoryLedger<Run>.Resolution(r, 18f, false));
            Run live = new Run { Began = 10f, Classified = true };
            Equal(StoryResolution.Watch, StoryLedger<Run>.Resolution(live, 12.5f, false));   // unchanged for a live story
        });
        Check("T-212.5 late start: an ended-early story neither hides the own Ori nor blocks own stories", delegate {
            StoryLedger<Run> l = new StoryLedger<Run>(); Run r = new Run { Key = "a", Sender = 2, Token = 1, Classified = true };
            l.TrackRemote(r, false, 0f); Equal(true, l.Watching); Equal(1, l.LiveRemotes);
            r.EndedEarly = true; Equal(false, l.Watching); Equal(0, l.LiveRemotes);
        });

        // T-207 (2): the partner pulled a lever (this PC replays it, window open for 12 s); then the partner started
        // an unrelated story (a map stone) whose start arrives here inside the window.
        Check("T-207.2 window: a partner's unrelated story is mirrored inside the window; this PC's own replay is not doubled", delegate {
            Equal(true, StoryLedger<Run>.MirrorInsideWindow(false, false));    // the map stone: before, dropped
            Equal(false, StoryLedger<Run>.MirrorInsideWindow(true, false));    // the lever's own camera story, playing here already
            Equal(false, StoryLedger<Run>.MirrorInsideWindow(false, true));    // started natively here a moment ago
            Equal(false, StoryLedger<Run>.MirrorInsideWindow(true, true));
        });
        Check("T-207.2 window: 'started natively here' is forgotten after 5 s and with the session", delegate {
            RecentObjects<string> starts = new RecentObjects<string>(); starts.Mark("story", 10f);
            Equal(true, starts.Recent("story", 14.9f, 5f)); Equal(false, starts.Recent("story", 15f, 5f));
            Equal(false, starts.Recent("other", 11f, 5f));
            starts.Mark("story", 20f); starts.Clear(); Equal(false, starts.Recent("story", 20.5f, 5f));
        });

        // T-207 (3): the gate fired at 10 and the game's trigger refused it; an unrelated story began at 11.
        Check("T-207.3 gate release: the story clock guesses wrong when another story began; the host's release frees the area", delegate {
            GateTable t = new GateTable(); t.MarkFired("a", 10f);
            Equal(false, t.Fizzled("a", 13f, 11f));          // the old guess: "a story followed", so the fire worked
            Equal(true, t.RecentlyFired("a", 13f));          // ... and the area stayed dead until 25
            t.Release("a");                                  // the host's GateRelease
            Equal(false, t.RecentlyFired("a", 10.7f));
            t.SetInside("a", 1, true, 10.7f); t.SetInside("a", 2, true, 10.7f); Equal(true, t.AllHere("a", 2, 10.7f));
        });
        Check("T-207.3 gate release wire: round trip, other areas untouched, oversized key refused", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteRelease(w, "洞穴/trigger[0]"); }))
                Equal("洞穴/trigger[0]", GateWire.ReadRelease(r));
            GateTable t = new GateTable(); t.MarkFired("a", 10f); t.MarkFired("b", 10f); t.Release("a");
            Equal(false, t.RecentlyFired("a", 11f)); Equal(true, t.RecentlyFired("b", 11f));
            GateInvalid(delegate(BinaryWriter w) { GateWire.WriteRelease(w, new string('x', 1025)); }, delegate(BinaryReader r) { GateWire.ReadRelease(r); });
        });

        // T-207 (4): a partner waits at an area this PC has not loaded; its trigger cannot be found here.
        Check("T-207.4 gate lookup: an unresolved trigger is looked for at once, then at most every 0.5 s", delegate {
            Equal(true, GateResolve.Due(100f, -1f));       // never looked for
            Equal(false, GateResolve.Due(100.016f, 100f)); // the next frame (60 fps): not again
            Equal(false, GateResolve.Due(100.49f, 100f));
            Equal(true, GateResolve.Due(100.5f, 100f));
            int scans = 0; float last = -1f;               // two callers a frame for 2 s at 60 fps
            for (int frame = 0; frame < 120; frame++)
                for (int caller = 0; caller < 2; caller++)
                    if (GateResolve.Due(frame / 60f, last)) { last = frame / 60f; scans++; }
            Equal(4, scans);                               // before: 240
        });

        // T-212 (7): pedestal cutscene played at 100; a save from before it loaded at 105; the partner activates at 110.
        Check("T-212.7 pedestal: a loaded save forgets the recent plays; without a load the 30 s rule still holds", delegate {
            RecentObjects<string> played = new RecentObjects<string>(); played.Mark("pedestal", 100f);
            Equal(true, played.Recent("pedestal", 110f, 30f));     // same play echoed back: not shown twice
            Equal(false, played.Recent("pedestal", 130f, 30f));
            played.Clear();
            Equal(false, played.Recent("pedestal", 110f, 30f));    // after the load the partner's activation plays here
            played.Mark("pedestal", 110f); Equal(true, played.Recent("pedestal", 111f, 30f));
        });

        // Review of T-207 / T-212, finding 1: the guest forced at 10; the host moved its players there and waits for the
        // area to load. No story can have begun yet.
        Check("review 1: a fire still in the host's queue is not taken for one that came to nothing", delegate {
            GateTable t = new GateTable(); t.MarkFired("a", 10f); t.SetQueued("a", true);
            Equal(false, t.Fizzled("a", 13f, -1f)); Equal(0, t.FizzledKeys(14f, -1f).Count);
            Equal(true, t.RecentlyFired("a", 14f)); Equal(true, t.Queued("a"));   // a second request for it is refused
            t.SetQueued("a", false);                                                // the trigger was found and fired
            Equal(true, t.Fizzled("a", 14f, -1f));                                  // from now on the fallback may judge it
            t.SetQueued("b", true); t.MarkFired("b", 10f); t.ClearFired();          // a checkpoint restore drops the queue
            Equal(false, t.Queued("b")); Equal(false, t.RecentlyFired("b", 11f));
        });
        // Finding 2: the request's position must be where the forcing player stands (40 units; a gate is a few wide).
        Check("review 2: the host goes to a force position only near the forcing player (boundary both sides)", delegate {
            Equal(true, GateForce.HintNear(0f, 0f)); Equal(true, GateForce.HintNear(40f, 0f)); Equal(true, GateForce.HintNear(-24f, 32f));
            Equal(false, GateForce.HintNear(40.1f, 0f)); Equal(false, GateForce.HintNear(30f, 30f)); Equal(false, GateForce.HintNear(0f, -500f));
        });

        // Finding 4: the scene was not ready, so after 2 s the story was only "watched" (Resolved + Watch, no mirror);
        // its end arrives at 13 (3 s in). Expected: still kept until 18 so the mirror can start once the scene is ready.
        Check("review 4: a story only watched when its end arrives is kept for a late start; a started or locally played one is not", delegate {
            Run watched = new Run { Began = 10f, Resolved = true, Watch = true };
            Equal(true, StoryLedger<Run>.KeepForLateStart(watched, 13f)); Equal(false, StoryLedger<Run>.KeepForLateStart(watched, 18f));
            Run mirrored = new Run { Began = 10f, Resolved = true, Started = true };
            Equal(false, StoryLedger<Run>.KeepForLateStart(mirrored, 13f));
            Run failed = new Run { Began = 10f, Resolved = true, Watch = true, Started = true };   // the mirror threw half-way
            Equal(false, StoryLedger<Run>.KeepForLateStart(failed, 13f));
            Run local = new Run { Began = 10f, Resolved = true };                                  // this PC plays it itself
            Equal(false, StoryLedger<Run>.KeepForLateStart(local, 13f));
        });
        Check("review 6: a mirrored lock action that has run out does not keep the input-lock flag", delegate {
            SequenceInputLocks<string, string> l = new SequenceInputLocks<string, string>();
            l.LockTimed("mirror 2", "timer 2", false); l.LockTimed("mirror 3", "timer 3", true);
            Equal(true, l.EndTimed("timer 2"));
            string stopped = null;
            Equal((bool?)false, l.End("mirror 3", null, false, delegate(string action) { stopped = action; }));
            Equal("timer 3", stopped);
        });

        // T-212 (6): the gate fired at 10, the players died, the checkpoint was restored at 13.
        Check("T-212.6 gate: a checkpoint restore clears the fired marks; the area counts players at once", delegate {
            GateTable t = new GateTable(); t.MarkFired("a", 10f); t.MarkFired("b", 12f);
            t.SetInside("a", 1, true, 13f); Equal(0, t.Count("a", 13f));        // before: ignored until 25
            t.ClearFired();
            Equal(false, t.RecentlyFired("a", 13f)); Equal(false, t.RecentlyFired("b", 13f));
            t.SetInside("a", 1, true, 13f); t.SetInside("a", 2, true, 13f);
            Equal(2, t.Count("a", 13f)); Equal(true, t.AllHere("a", 2, 13f));
            t.MarkFired("a", 13.5f); Equal(true, t.RecentlyFired("a", 14f));    // a new fire is marked as usual
        });
    }
}
