using System;
using System.IO;
using OriCoop.Story;

// T-203: the shared new-game prologue. Expected values come from the ticket's rules (docs\tickets\T-203.md, 设计):
//   2  a guest that waits on the title starts its own copy when the host starts, within 5 s; never twice
//   5  the host leaving the Prologue state ends it for every guest; a copy that ends first also stops
//   6  later than 5 s: no copy, the guest waits (and knows the host is in its prologue)
//   8  the host leaving: every guest stops and gives up the join; a short link drop (under 10 s) does not
//   10 a host that loads an existing save never announces anything, a guest never starts anything
//   3  the host's input reaches the guest one state per physics step; stale input is neutral
static class PrologueTests
{
    static int failures;
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Equal<T>(T expected, T actual, string what)
    { if (!object.Equals(expected, actual)) throw new Exception(what + ": expected " + expected + ", got " + actual); }
    static void Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception(what + ": expected " + typeof(T).Name);
    }

    static PrologueGuestInput In(float now, ProloguePhase phase, float elapsed, uint generation = 1)
    {
        return new PrologueGuestInput { HaveHost = phase != ProloguePhase.None, Host = new PrologueState { Generation = generation, Phase = phase, Elapsed = elapsed },
            HostHere = true, Waiting = true, CanStart = true, Returned = true, Now = now };
    }
    static PrologueGuest Watching()
    {
        PrologueGuest g = new PrologueGuest();
        Equal(PrologueAction.Start, g.Tick(In(0f, ProloguePhase.Playing, 0.2f)), "start");
        return g;
    }
    static PrologueInput Input(uint sequence, int horizontal, ushort buttons, uint generation = 1)
    { return new PrologueInput { Generation = generation, Sequence = sequence, Horizontal = (sbyte)horizontal, Buttons = buttons }; }

    static int Main()
    {
        // ---- host
        Check("host: an existing save announces nothing", delegate {
            PrologueHost h = new PrologueHost();
            for (int i = 0; i < 100; i++) Equal(false, h.Tick(false, i % 2 == 0, 1, i * 0.1f), "changed");
            Equal(ProloguePhase.None, h.State.Phase, "phase"); Equal(0u, h.State.Generation, "generation");
        });
        Check("host: the new-game action starts it, before the state machine reaches Prologue", delegate {
            PrologueHost h = new PrologueHost();
            h.Started(10f, 2);
            Equal(ProloguePhase.Playing, h.State.Phase, "phase"); Equal(1u, h.State.Generation, "generation"); Equal((byte)2, h.State.Difficulty, "difficulty");
            h.Tick(false, true, 2, 12f);   // still on the title scene: the sequence has not set the state yet
            Equal(ProloguePhase.Playing, h.State.Phase, "phase while starting"); Equal(2f, h.State.Elapsed, "elapsed");
            h.Tick(true, false, 2, 30f);
            Equal(ProloguePhase.Playing, h.State.Phase, "phase in the prologue"); Equal(20f, h.State.Elapsed, "elapsed in the prologue");
            h.Started(31f, 0);       // a second call changes nothing
            Equal(1u, h.State.Generation, "generation kept"); Equal((byte)2, h.State.Difficulty, "difficulty kept");
        });
        Check("host: leaving the Prologue state ends it (skip or played to the end)", delegate {
            PrologueHost h = new PrologueHost();
            h.Started(0f, 1); h.Tick(true, false, 1, 5f);
            Equal(true, h.Tick(false, false, 1, 85f), "changed"); Equal(ProloguePhase.Ended, h.State.Phase, "phase"); Equal(1u, h.State.Generation, "generation");
            Equal(false, h.Tick(false, false, 1, 86f), "changed again"); Equal(ProloguePhase.Ended, h.State.Phase, "stays ended");
        });
        Check("host: no hook seen: the Prologue state itself starts it", delegate {
            PrologueHost h = new PrologueHost();
            Equal(true, h.Tick(true, false, 3, 7f), "changed"); Equal(ProloguePhase.Playing, h.State.Phase, "phase"); Equal((byte)3, h.State.Difficulty, "difficulty");
        });
        Check("host: a start that never reaches the prologue is given up after the grace", delegate {
            PrologueHost h = new PrologueHost();
            h.Started(0f, 1);
            h.Tick(false, false, 1, 59.9f); Equal(ProloguePhase.Playing, h.State.Phase, "inside the grace");
            h.Tick(false, false, 1, 60f); Equal(ProloguePhase.Ended, h.State.Phase, "after the grace");
        });
        Check("host: an ended prologue stays 'ended' in the game and is forgotten back on the title; the generation keeps counting", delegate {
            PrologueHost h = new PrologueHost();
            h.Started(0f, 1); h.Tick(true, false, 1, 1f); h.Tick(false, false, 1, 85f);
            h.Tick(false, false, 1, 3600f);   // an hour into the game: a guest joining now still hears "ended", never "playing"
            Equal(ProloguePhase.Ended, h.State.Phase, "in the game");
            Equal(true, h.Tick(false, true, 1, 3700f), "changed on the title"); Equal(ProloguePhase.None, h.State.Phase, "on the title");
            Equal(1u, h.State.Generation, "generation");
            h.Started(3800f, 1); Equal(2u, h.State.Generation, "next generation"); Equal(0f, h.State.Elapsed, "its own clock");
        });
        Check("host: the elapsed time is the game's, whatever joins or leaves (a guest connecting mid-prologue is late)", delegate {
            PrologueHost h = new PrologueHost();
            h.Started(100f, 1); h.Tick(true, false, 1, 105f);
            h.Tick(true, false, 1, 140f);     // a session begins here: nothing in the tracker depends on it
            Equal(40f, h.State.Elapsed, "elapsed");   // far past the guests' 5 s start window
        });

        // ---- guest: start
        Check("guest: waiting on the title when the host starts: play along, once", delegate {
            PrologueGuest g = new PrologueGuest();
            Equal(PrologueAction.Start, g.Tick(In(0f, ProloguePhase.Playing, 0.2f)), "first tick");
            Equal(PrologueGuest.Mode.Watching, g.Current, "mode");
            PrologueGuestInput next = In(1f, ProloguePhase.Playing, 1.2f); next.Returned = false;
            Equal(PrologueAction.None, g.Tick(next), "second tick");
        });
        Check("guest: the 5 s window: at 5.0 s still along, later it waits", delegate {
            Equal(PrologueAction.Start, new PrologueGuest().Tick(In(0f, ProloguePhase.Playing, 5f)), "at the limit");
            PrologueGuest late = new PrologueGuest();
            Equal(PrologueAction.None, late.Tick(In(0f, ProloguePhase.Playing, 5.01f)), "past the limit");
            Equal(PrologueGuest.Mode.Idle, late.Current, "mode"); Equal(true, late.HostPlaying, "knows the host is in its prologue");
            Equal(PrologueAction.None, late.Tick(In(60f, ProloguePhase.Playing, 65f)), "a minute later: still waiting");
        });
        // T-205 (docs\tickets\T-205.md 设计 1): a host that sends its story position can be joined at any time.
        Check("guest (T-205): the host sends its story: a late guest plays along too, however late", delegate {
            PrologueGuestInput late = In(0f, ProloguePhase.Playing, 40f); late.HostStory = true;
            Equal(PrologueAction.Start, new PrologueGuest().Tick(late), "40 s late");
            PrologueGuestInput veryLate = In(0f, ProloguePhase.Playing, 540f); veryLate.HostStory = true;
            Equal(PrologueAction.Start, new PrologueGuest().Tick(veryLate), "9 minutes late");
        });
        Check("guest (T-205): the host's state arrives before its first clock: the late guest starts once the clock is here", delegate {
            PrologueGuest g = new PrologueGuest();
            Equal(PrologueAction.None, g.Tick(In(0f, ProloguePhase.Playing, 40f)), "state only");
            PrologueGuestInput withStory = In(0.1f, ProloguePhase.Playing, 40.1f); withStory.HostStory = true;
            Equal(PrologueAction.Start, g.Tick(withStory), "clock arrived");
            withStory.Returned = false;
            Equal(PrologueAction.None, g.Tick(withStory), "once");
        });
        Check("guest (T-205): a late guest whose title is not up yet starts as soon as it is (no window)", delegate {
            PrologueGuest g = new PrologueGuest();
            PrologueGuestInput notReady = In(0f, ProloguePhase.Playing, 40f); notReady.HostStory = true; notReady.CanStart = false;
            Equal(PrologueAction.None, g.Tick(notReady), "title not ready");
            notReady.Now = 30f; notReady.Host.Elapsed = 70f;
            Equal(PrologueAction.None, g.Tick(notReady), "30 s later, still not ready");
            PrologueGuestInput ready = In(31f, ProloguePhase.Playing, 71f); ready.HostStory = true;
            Equal(PrologueAction.Start, g.Tick(ready), "ready");
        });
        Check("guest: not on the title yet when the host starts: asks again until the window closes", delegate {
            PrologueGuest g = new PrologueGuest();
            PrologueGuestInput notReady = In(0f, ProloguePhase.Playing, 1f); notReady.CanStart = false;
            Equal(PrologueAction.None, g.Tick(notReady), "title not ready");
            PrologueGuestInput notWaiting = In(1f, ProloguePhase.Playing, 2f); notWaiting.Waiting = false;
            Equal(PrologueAction.None, g.Tick(notWaiting), "not waiting for a save");
            Equal(PrologueAction.Start, g.Tick(In(2f, ProloguePhase.Playing, 3f)), "ready inside the window");
        });
        Check("guest: an existing save (no state, or phase None / Ended): nothing ever starts", delegate {
            PrologueGuest g = new PrologueGuest();
            Equal(PrologueAction.None, g.Tick(In(0f, ProloguePhase.None, 0f)), "no state");
            PrologueGuestInput none = In(1f, ProloguePhase.None, 0f); none.HaveHost = true;
            Equal(PrologueAction.None, g.Tick(none), "phase None");
            Equal(PrologueAction.None, g.Tick(In(2f, ProloguePhase.Ended, 0f)), "phase Ended");
            Equal(false, g.HostPlaying, "host not in a prologue"); Equal(PrologueGuest.Mode.Idle, g.Current, "mode");
        });

        // ---- guest: stop
        Check("guest: the host's prologue ended: stop, wait for the title, never restart it", delegate {
            PrologueGuest g = Watching();
            PrologueGuestInput ended = In(85f, ProloguePhase.Ended, 0f); ended.Returned = false;
            Equal(PrologueAction.Stop, g.Tick(ended), "stop"); Equal(PrologueGuest.Mode.Returning, g.Current, "returning");
            Equal(PrologueAction.None, g.Tick(ended), "while returning"); Equal(PrologueGuest.Mode.Returning, g.Current, "still returning");
            Equal(PrologueAction.None, g.Tick(In(88f, ProloguePhase.Ended, 0f)), "back on the title"); Equal(PrologueGuest.Mode.Idle, g.Current, "idle");
            Equal(PrologueAction.None, g.Tick(In(89f, ProloguePhase.Playing, 1f)), "the same generation playing again");
        });
        Check("guest: its own copy reached the end first: stop", delegate {
            PrologueGuest g = Watching();
            PrologueGuestInput own = In(600f, ProloguePhase.Playing, 600f); own.LocalEnded = true; own.Returned = false;
            Equal(PrologueAction.Stop, g.Tick(own), "stop");
        });
        Check("guest: the join was given up elsewhere: stop", delegate {
            PrologueGuest g = Watching();
            PrologueGuestInput i = In(3f, ProloguePhase.Playing, 3f); i.Waiting = false; i.Returned = false;
            Equal(PrologueAction.Stop, g.Tick(i), "stop");
        });
        Check("guest: a later prologue of the host (new generation) can be played along again", delegate {
            PrologueGuest g = Watching();
            PrologueGuestInput ended = In(85f, ProloguePhase.Ended, 0f); ended.Returned = false;
            g.Tick(ended); g.Tick(In(88f, ProloguePhase.Ended, 0f));
            Equal(PrologueAction.Start, g.Tick(In(200f, ProloguePhase.Playing, 0.1f, 2)), "generation 2");
        });

        // ---- guest: the host leaves
        Check("guest: the host left while this PC played along: stop and give up the join, reported once", delegate {
            PrologueGuest g = Watching();
            PrologueGuestInput left = In(30f, ProloguePhase.Playing, 30f); left.HostLeft = true; left.Returned = false;
            Equal(PrologueAction.HostLeft, g.Tick(left), "host left");
            Equal(PrologueAction.None, g.Tick(left), "not twice");
            left.Returned = true; g.Tick(left);
            Equal(PrologueAction.None, g.Tick(left), "not again once back on the title");
        });
        Check("guest: a link drop under 10 s keeps the copy; 10 s without the host ends it", delegate {
            PrologueGuest g = Watching();
            PrologueGuestInput drop = In(20f, ProloguePhase.Playing, 20f); drop.HostHere = false; drop.Returned = false;
            Equal(PrologueAction.None, g.Tick(drop), "drop starts");
            drop.Now = 29.9f;
            Equal(PrologueAction.None, g.Tick(drop), "inside the grace");
            PrologueGuestInput back = In(29.95f, ProloguePhase.Playing, 29.95f); back.Returned = false;
            Equal(PrologueAction.None, g.Tick(back), "link back");
            drop.Now = 40f; Equal(PrologueAction.None, g.Tick(drop), "second drop starts its own grace");
            drop.Now = 50f;
            Equal(PrologueAction.HostLeft, g.Tick(drop), "gone for the whole grace");
        });
        Check("guest: a late guest on the waiting screen gives up when the host leaves its prologue", delegate {
            PrologueGuest g = new PrologueGuest();
            g.Tick(In(0f, ProloguePhase.Playing, 40f));   // too late: waits
            PrologueGuestInput left = In(5f, ProloguePhase.Playing, 45f); left.HostLeft = true;
            Equal(PrologueAction.HostLeft, g.Tick(left), "host left"); Equal(false, g.HostPlaying, "no longer playing");
            Equal(PrologueAction.None, g.Tick(left), "once");
        });
        Check("guest: a host that leaves without ever being in a prologue is not this module's business", delegate {
            PrologueGuest g = new PrologueGuest();
            PrologueGuestInput left = In(0f, ProloguePhase.None, 0f); left.HostLeft = true;
            Equal(PrologueAction.None, g.Tick(left), "existing-save host left");
        });
        Check("guest: a new session starts from scratch", delegate {
            PrologueGuest g = Watching(); g.Reset();
            Equal(PrologueGuest.Mode.Idle, g.Current, "mode"); Equal(false, g.HostPlaying, "host playing");
            Equal(PrologueAction.Start, g.Tick(In(0f, ProloguePhase.Playing, 0.1f)), "generation 1 of the next session");
        });

        // ---- wire
        Check("wire: state round trip", delegate {
            MemoryStream ms = new MemoryStream(); BinaryWriter w = new BinaryWriter(ms);
            PrologueWire.WriteState(w, new PrologueState { Generation = 7, Phase = ProloguePhase.Playing, Difficulty = 3, Elapsed = 12.5f });
            Equal(11L, ms.Length, "bytes");   // kind + u32 + phase + difficulty + f32
            BinaryReader r = new BinaryReader(new MemoryStream(ms.ToArray()));
            Equal(PrologueWire.KindState, PrologueWire.ReadKind(r), "kind");
            PrologueState s = PrologueWire.ReadState(r);
            Equal(7u, s.Generation, "generation"); Equal(ProloguePhase.Playing, s.Phase, "phase"); Equal((byte)3, s.Difficulty, "difficulty"); Equal(12.5f, s.Elapsed, "elapsed");
        });
        Check("wire: input round trip", delegate {
            MemoryStream ms = new MemoryStream(); BinaryWriter w = new BinaryWriter(ms);
            PrologueInput sent = new PrologueInput { Generation = 2, Sequence = 99, Horizontal = -20, Vertical = 13, Buttons = 0x4010, Actor = PrologueActor.Naru, X = -2255.76f, Y = -181.29f };
            PrologueWire.WriteInput(w, sent);
            Equal(22L, ms.Length, "bytes");   // kind + 2 u32 + 2 i8 + u16 + actor + 2 f32
            BinaryReader r = new BinaryReader(new MemoryStream(ms.ToArray()));
            Equal(PrologueWire.KindInput, PrologueWire.ReadKind(r), "kind");
            PrologueInput got = PrologueWire.ReadInput(r);
            Equal(sent.Generation, got.Generation, "generation"); Equal(sent.Sequence, got.Sequence, "sequence"); Equal(sent.Horizontal, got.Horizontal, "horizontal");
            Equal(sent.Vertical, got.Vertical, "vertical"); Equal(sent.Buttons, got.Buttons, "buttons"); Equal(sent.Actor, got.Actor, "actor");
            Equal(sent.X, got.X, "x"); Equal(sent.Y, got.Y, "y");
        });
        Check("wire: invalid values are refused", delegate {
            Func<PrologueState, BinaryReader> state = delegate(PrologueState s) {
                MemoryStream ms = new MemoryStream(); PrologueWire.WriteState(new BinaryWriter(ms), s);
                BinaryReader r = new BinaryReader(new MemoryStream(ms.ToArray())); r.ReadByte(); return r; };
            Throws<InvalidDataException>(delegate { PrologueWire.ReadState(state(new PrologueState { Phase = (ProloguePhase)3 })); }, "phase 3");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadState(state(new PrologueState { Difficulty = 4 })); }, "difficulty 4");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadState(state(new PrologueState { Elapsed = float.NaN })); }, "elapsed NaN");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadState(state(new PrologueState { Elapsed = -1f })); }, "elapsed negative");
            Func<PrologueInput, BinaryReader> input = delegate(PrologueInput i) {
                MemoryStream ms = new MemoryStream(); PrologueWire.WriteInput(new BinaryWriter(ms), i);
                BinaryReader r = new BinaryReader(new MemoryStream(ms.ToArray())); r.ReadByte(); return r; };
            Throws<InvalidDataException>(delegate { PrologueWire.ReadInput(input(new PrologueInput { Actor = 4 })); }, "actor 4");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadInput(input(new PrologueInput { X = float.PositiveInfinity })); }, "x infinite");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadInput(input(new PrologueInput { Horizontal = 21 })); }, "horizontal 21");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadInput(input(new PrologueInput { Vertical = -21 })); }, "vertical -21");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadInput(input(new PrologueInput { Buttons = 0x8000 })); }, "button bit 15 (only 15 buttons)");
            PrologueWire.ReadInput(input(new PrologueInput { Horizontal = 20, Vertical = -20, Buttons = 0x7FFF, Actor = 3 }));   // the limits themselves are fine
        });

        // ---- buttons and axes
        Check("buttons: gameplay buttons are carried, menu buttons never", delegate {
            // Core.Input.Buttons order (decomp Core\Input.cs:145): 4 Jump, 13 Start, 14 AnyStart, 15 Select, 18-23 Menu*, 24 ActionButtonA, 27 Cancel.
            Equal(true, PrologueButtons.IsMirrored(4), "Jump"); Equal(4, PrologueButtons.Mirrored[PrologueButtons.JumpBit], "jump bit");
            foreach (int menu in new[] { 13, 14, 15, 18, 19, 20, 21, 22, 23, 24, 27, 28, 29, 30, 31, 32 })
                Equal(false, PrologueButtons.IsMirrored(menu), "button " + menu);
            Equal(15, PrologueButtons.Mirrored.Length, "count fits the 16-bit mask");
        });
        Check("axis: thresholds survive (0.4 walk, 0.6 look), sign and limits", delegate {
            Equal(0f, PrologueButtons.Axis(PrologueButtons.Quantise(0f)), "zero");
            Equal(1f, PrologueButtons.Axis(PrologueButtons.Quantise(1f)), "full right"); Equal(-1f, PrologueButtons.Axis(PrologueButtons.Quantise(-1f)), "full left");
            Equal(1f, PrologueButtons.Axis(PrologueButtons.Quantise(3f)), "clamped"); Equal(0f, PrologueButtons.Axis(PrologueButtons.Quantise(float.NaN)), "NaN");
            // The game walks when |axis| > 0.4: both sides of the border give the same answer on the guest.
            Equal(false, PrologueButtons.Axis(PrologueButtons.Quantise(0.4f)) > 0.4f, "0.40 does not walk");
            Equal(true, PrologueButtons.Axis(PrologueButtons.Quantise(0.41f)) > 0.4f, "0.41 walks");
            Equal(true, PrologueButtons.Axis(PrologueButtons.Quantise(-0.41f)) < -0.4f, "-0.41 walks left");
            Equal(false, PrologueButtons.Axis(PrologueButtons.Quantise(0.6f)) > 0.6f, "0.60 does not look");
            Equal(true, PrologueButtons.Axis(PrologueButtons.Quantise(0.61f)) > 0.6f, "0.61 looks");
            Equal(true, PrologueButtons.Axis(PrologueButtons.Quantise(0.02f)) > 0f, "a small push is not lost");
        });

        // ---- input queue
        Check("queue: a press and a release that arrive together are two physics steps", delegate {
            PrologueInputQueue q = new PrologueInputQueue();
            ushort jump = (ushort)(1 << PrologueButtons.JumpBit);
            q.Push(Input(1, 0, jump), 10f); q.Push(Input(2, 0, 0), 10f);
            Equal(false, q.CaughtUp, "two waiting");
            Equal(jump, q.Next(10f).Buttons, "step 1: pressed"); Equal((ushort)0, q.Next(10f).Buttons, "step 2: released");
            Equal(true, q.CaughtUp, "caught up");
        });
        Check("queue: the last input is held between messages, neutral once it is stale", delegate {
            PrologueInputQueue q = new PrologueInputQueue();
            q.Push(Input(1, 20, 0), 10f);
            Equal((sbyte)20, q.Next(10f).Horizontal, "applied"); Equal((sbyte)20, q.Next(10.9f).Horizontal, "held under 1 s");
            Equal((sbyte)0, q.Next(11.01f).Horizontal, "neutral after 1 s without the host");
        });
        Check("queue: nothing received yet is neutral", delegate {
            PrologueInput none = new PrologueInputQueue().Next(5f);
            Equal(true, none.Neutral, "neutral");
        });
        Check("queue: repeated and out-of-order messages are dropped, a new generation replaces the old", delegate {
            PrologueInputQueue q = new PrologueInputQueue();
            q.Push(Input(5, 20, 0), 1f); q.Push(Input(5, -20, 0), 1f); q.Push(Input(4, -20, 0), 1f);
            Equal((sbyte)20, q.Next(1f).Horizontal, "first"); Equal(true, q.CaughtUp, "the repeats were dropped");
            q.Push(Input(6, 10, 0), 2f); q.Push(Input(1, -5, 0, 2), 2f);
            Equal((sbyte)-5, q.Next(2f).Horizontal, "generation 2 replaced the queue");
            q.Push(Input(9, 7, 0, 1), 3f);
            Equal((sbyte)-5, q.Next(3f).Horizontal, "an older generation is ignored");
        });
        Check("queue: overflow keeps the newest " + PrologueInputQueue.Max, delegate {
            PrologueInputQueue q = new PrologueInputQueue();
            for (uint i = 1; i <= PrologueInputQueue.Max + 3; i++) q.Push(Input(i, 0, (ushort)(i % 2)), 1f);
            Equal(3, q.Dropped, "dropped"); Equal(4u, q.Next(1f).Sequence, "oldest kept");
        });
        Check("queue: the host is at rest after 0.5 s of neutral input", delegate {
            PrologueInputQueue q = new PrologueInputQueue();
            q.Push(Input(1, 20, 0), 1f); Equal(false, q.HostResting(5f), "moving");
            q.Push(Input(2, 0, 0), 5f); Equal(false, q.HostResting(5.4f), "0.4 s neutral"); Equal(true, q.HostResting(5.5f), "0.5 s neutral");
            q.Push(Input(3, 0, 0), 5.6f); Equal(true, q.HostResting(5.7f), "a heartbeat keeps the rest clock");
            q.Push(Input(4, 0, 16), 6f); Equal(false, q.HostResting(9f), "a button is not rest");
        });

        // ---- drift
        Check("drift: moving allows 3 units, at rest 0.75; never while inputs are still queued", delegate {
            Equal(false, PrologueDrift.ShouldSnap(3f, 0f, false, true), "3.0 while moving");
            Equal(true, PrologueDrift.ShouldSnap(3.01f, 0f, false, true), "3.01 while moving");
            Equal(true, PrologueDrift.ShouldSnap(0f, -3.01f, false, true), "vertical counts");
            Equal(false, PrologueDrift.ShouldSnap(0.75f, 0f, true, true), "0.75 at rest");
            Equal(true, PrologueDrift.ShouldSnap(-0.76f, 0f, true, true), "0.76 at rest");
            Equal(false, PrologueDrift.ShouldSnap(50f, 0f, true, false), "inputs still queued");
            Equal(false, PrologueDrift.ShouldSnap(float.NaN, 0f, true, true), "NaN");
        });

        // ---- T-209 (3): the host's input belongs to the character the host controls
        Check("input (T-209): the host's input for another character is neutral here", delegate {
            ushort jump = (ushort)(1 << PrologueButtons.JumpBit);
            PrologueInput host = new PrologueInput { Horizontal = 20, Vertical = -7, Buttons = jump, Actor = PrologueActor.Naru, X = 5f, Y = 6f };
            PrologueInput same = host.For(PrologueActor.Naru);
            Equal((sbyte)20, same.Horizontal, "same character: horizontal"); Equal(jump, same.Buttons, "same character: buttons");
            PrologueInput other = host.For(PrologueActor.BabyOri);
            Equal(true, other.Neutral, "the guest's copy controls the small Ori: neutral");
            Equal(true, host.For(PrologueActor.None).Neutral, "the guest's copy has no character yet: neutral");
            PrologueInput cinematic = new PrologueInput { Buttons = jump, Actor = PrologueActor.None };
            Equal(true, cinematic.For(PrologueActor.Ori).Neutral, "the host is in a cinematic, this copy still has a character: neutral");
            Equal(jump, cinematic.For(PrologueActor.None).Buttons, "no character on either PC: the same part, carried");
        });
        // ---- T-209 (4): an old position report corrects nothing
        Check("queue (T-209): a position report older than 1 s is not used (a lost link must not pull the character back)", delegate {
            PrologueInputQueue q = new PrologueInputQueue();
            PrologueInput report = Input(1, 0, 0); report.Actor = PrologueActor.Naru; report.X = 10f;
            q.Push(report, 100f);
            PrologueInput got;
            Equal(true, q.TryNewest(100.9f, out got), "0.9 s old: used"); Equal(10f, got.X, "its position");
            Equal(true, q.TryNewest(101f, out got), "1.0 s old: still used");
            Equal(false, q.TryNewest(101.01f, out got), "older than 1 s: not used");
            Equal(false, q.TryNewest(109f, out got), "the whole 10 s grace: not used");
            q.Push(Input(2, 0, 0), 109.5f);
            Equal(true, q.TryNewest(109.6f, out got), "the link is back: used again");
            Equal(false, new PrologueInputQueue().TryNewest(1f, out got), "nothing received");
        });

        // ---- T-205: the host's story (ticket 设计 2-4)
        Check("story: the clock counts physics steps, not the ones frozen for a scene load", delegate {
            PrologueStory s = new PrologueStory();
            s.Begin();
            for (int i = 0; i < 90; i++) s.Tick(false);
            for (int i = 0; i < 300; i++) s.Tick(true);    // a 5 s load: the story stands still
            for (int i = 0; i < 30; i++) s.Tick(false);
            Equal(120u, s.Step, "2 s of story");
            Equal(0, s.Add(PrologueEventKind.Trigger, 77u, PrologueActor.Naru, 1.5f, -2f), "first entry");
            Equal(120u, s[0].Step, "entry step"); Equal(77u, s[0].Id, "entry id"); Equal(PrologueActor.Naru, s[0].Actor, "entry actor"); Equal(1.5f, s[0].X, "entry x");
            s.Begin();
            Equal(0u, s.Step, "a new prologue starts at 0"); Equal(0, s.Count, "with an empty log");
        });
        Check("story: the log holds 512 entries, then refuses", delegate {
            PrologueStory s = new PrologueStory();
            for (int i = 0; i < 512; i++) Equal(i, s.Add(PrologueEventKind.Trigger, (uint)i, 0, 0f, 0f), "entry " + i);
            Equal(-1, s.Add(PrologueEventKind.Trigger, 999u, 0, 0f, 0f), "entry 513");
            Equal(512, s.Count, "count");
        });
        Check("hash: FNV-1a 32 (published test vectors), the same on every PC", delegate {
            Equal(0x811C9DC5u, PrologueHash.Of(""), "empty"); Equal(0xE40C292Cu, PrologueHash.Of("a"), "a"); Equal(0xBF9CF968u, PrologueHash.Of("foobar"), "foobar");
            Equal(false, PrologueHash.Of("swallowsNestCaveBefore") == PrologueHash.Of("swallowsNestCaveSleepCutscene"), "two scenes differ");
        });

        Func<uint, byte, uint, PrologueEvent> ev = delegate(uint step, byte kind, uint id) { return new PrologueEvent { Step = step, Kind = kind, Id = id }; };
        Check("follower: the copy's clock counts its own steps; frozen steps do not count", delegate {
            PrologueFollower f = new PrologueFollower();
            for (int i = 0; i < 100; i++) f.Tick(false);
            for (int i = 0; i < 50; i++) f.Tick(true);
            Equal(100u, f.Step, "step"); Equal(400, f.Lag(500u), "behind the host"); Equal(-40, f.Lag(60u), "ahead of the host");
        });
        Check("follower: the clock waits at a host event until the copy has done it", delegate {
            PrologueFollower f = new PrologueFollower();
            f.Receive(0, new[] { ev(600u, PrologueEventKind.Trigger, 5u) });
            PrologueEvent due;
            for (int i = 0; i < 599; i++) f.Tick(false);
            Equal(false, f.Due(out due), "one step before");
            f.Tick(false);
            Equal(true, f.Due(out due), "at its step"); Equal(5u, due.Id, "which one");
            for (int i = 0; i < 200; i++) f.Tick(false);    // its object is still loading here
            Equal(600u, f.Step, "the clock waited"); Equal(true, f.Due(out due), "still due");
            f.Done();
            Equal(600u, f.Step, "done at the host's step"); Equal(false, f.Due(out due), "nothing due"); Equal(1, f.Applied, "applied");
            f.Tick(false); Equal(601u, f.Step, "and on");
        });
        Check("follower: an event that arrives after the copy passed its step sets the clock back to it", delegate {
            PrologueFollower f = new PrologueFollower();
            for (int i = 0; i < 1003; i++) f.Tick(false);   // live: the host fired at 1000, the message took 3 steps
            f.Receive(0, new[] { ev(1000u, PrologueEventKind.Button, 9u) });
            PrologueEvent due;
            Equal(true, f.Due(out due), "due at once");
            f.Done();
            Equal(1000u, f.Step, "the story after the event starts at the host's step"); Equal(3, f.Lag(1003u), "3 steps behind now");
        });
        Check("follower: a scene load lines the clocks up, whichever end loads first", delegate {
            // The host loaded at its step 7000; this copy loads 90 steps of its own later.
            PrologueFollower behind = new PrologueFollower();
            behind.Receive(0, new[] { ev(7000u, PrologueEventKind.Load, 42u) });
            for (int i = 0; i < 7000; i++) behind.Tick(false);
            for (int i = 0; i < 90; i++) behind.Tick(false);      // waits at the load it has not reached (a slow sprite, a slow disk)
            Equal(7000u, behind.Step, "waits at the host's load");
            behind.LocalLoad(42u);
            Equal(7000u, behind.Step, "its own load is the host's step 7000"); Equal(1, behind.Applied, "done");
            behind.Tick(false); Equal(7001u, behind.Step, "and on");
            // This copy loads first; the host's load is reported 30 of this copy's steps later.
            PrologueFollower ahead = new PrologueFollower();
            for (int i = 0; i < 6950; i++) ahead.Tick(false);
            ahead.LocalLoad(42u);
            for (int i = 0; i < 30; i++) ahead.Tick(false);
            Equal(6980u, ahead.Step, "own clock so far");
            ahead.Receive(0, new[] { ev(7000u, PrologueEventKind.Load, 42u) });
            Equal(7030u, ahead.Step, "the host's step of the load plus the 30 steps played since"); Equal(-30, ahead.Lag(7000u), "30 steps ahead of the host");
        });
        Check("follower: events are done in the host's order (a load does not jump over a gate)", delegate {
            PrologueFollower f = new PrologueFollower();
            f.Receive(0, new[] { ev(100u, PrologueEventKind.Trigger, 1u), ev(400u, PrologueEventKind.Load, 2u) });
            f.LocalLoad(2u);                                  // cannot really happen before the gate; it must not settle
            Equal(0, f.Applied, "nothing applied");
            for (int i = 0; i < 100; i++) f.Tick(false);
            f.Done();
            Equal(2, f.Applied, "the gate, then the load that was already made");
        });
        Check("follower: the host's log arrives in pieces: repeats are skipped, a gap is refused", delegate {
            PrologueFollower f = new PrologueFollower();
            Equal(false, f.Known, "before anything");
            Equal(true, f.Receive(0, new[] { ev(10u, PrologueEventKind.Trigger, 1u), ev(20u, PrologueEventKind.Trigger, 2u) }), "first two"); Equal(2, f.Count, "count");
            Equal(true, f.Known, "the host sends its story");
            Equal(true, f.Receive(2, new[] { ev(30u, PrologueEventKind.Trigger, 3u) }), "the next one"); Equal(3, f.Count, "count");
            Equal(false, f.Receive(5, new[] { ev(60u, PrologueEventKind.Trigger, 6u) }), "a gap"); Equal(3, f.Count, "nothing taken");
            Equal(true, f.Receive(0, new[] { ev(10u, PrologueEventKind.Trigger, 1u), ev(20u, PrologueEventKind.Trigger, 2u), ev(30u, PrologueEventKind.Trigger, 3u), ev(40u, PrologueEventKind.Trigger, 4u) }), "the whole log again");
            Equal(4, f.Count, "only the new entry was added");
            Equal(true, f.Receive(1, new[] { ev(20u, PrologueEventKind.Trigger, 2u) }), "an old piece"); Equal(4, f.Count, "unchanged");
        });
        Check("follower: a new copy starts at step 0 with the host's log kept; a new prologue forgets the log", delegate {
            PrologueFollower f = new PrologueFollower();
            f.Receive(0, new[] { ev(5u, PrologueEventKind.Trigger, 1u) });
            for (int i = 0; i < 5; i++) f.Tick(false);
            f.Done(); for (int i = 0; i < 20; i++) f.Tick(false);
            f.Restart();
            Equal(0u, f.Step, "step"); Equal(0, f.Applied, "applied"); Equal(1, f.Count, "log kept"); Equal(true, f.Known, "still known");
            f.Reset();
            Equal(0, f.Count, "log gone"); Equal(false, f.Known, "not known");
        });
        Check("follower: an event that cannot be done keeps the clock waiting (nothing is ever skipped)", delegate {
            PrologueFollower f = new PrologueFollower();
            f.Receive(0, new[] { ev(10u, PrologueEventKind.Trigger, 1u), ev(50u, PrologueEventKind.Trigger, 2u) });
            for (int i = 0; i < 600; i++) f.Tick(false);
            PrologueEvent due;
            Equal(10u, f.Step, "still at its step"); Equal(true, f.Due(out due), "still due"); Equal(1u, due.Id, "the same one"); Equal(0, f.Applied, "nothing applied");
            Equal(590, f.Lag(600u), "the copy falls behind the host meanwhile");
        });
        Check("follower: a copy started 50 s late reaches the host through its log", delegate {
            // The host is at step 3000 and has passed a trigger (600), a button (1500) and a scene load (2400).
            PrologueFollower f = new PrologueFollower();
            f.Receive(0, new[] { ev(600u, PrologueEventKind.Trigger, 1u), ev(1500u, PrologueEventKind.Button, 2u), ev(2400u, PrologueEventKind.Load, 3u) });
            int steps = 0; PrologueEvent due;
            while (f.Step < 3000u && steps < 10000)
            {
                f.Tick(false); steps++;
                if (f.Due(out due))
                {
                    if (due.Kind == PrologueEventKind.Load) f.LocalLoad(due.Id); else f.Done();
                }
            }
            Equal(3000u, f.Step, "level with the host"); Equal(3, f.Applied, "all three done"); Equal(3000, steps, "in exactly as many steps as the story is long");
            Equal(0, f.Lag(3000u), "lag");
        });

        // ---- T-205, merge review (docs\tickets\T-205.md 合并前初审)
        Check("review 2: nothing of the host's log is done in a physics step in which the game is frozen (a scene loads)", delegate {
            PrologueFollower f = new PrologueFollower();
            f.Receive(0, new[] { ev(10u, PrologueEventKind.Trigger, 1u) });
            for (int i = 0; i < 10; i++) f.Tick(false);
            PrologueEvent due;
            Equal(true, f.Due(false, out due), "its step has come, the game runs: due");
            Equal(false, f.Due(true, out due), "the game is frozen for a scene load: not now");
            for (int i = 0; i < 120; i++) f.Tick(true);
            Equal(false, f.Due(true, out due), "still frozen: still not");
            Equal(true, f.Due(false, out due), "the load is over: due again"); Equal(1u, due.Id, "the same point");
        });
        Check("review 3: host and copy on different sides of a scene load: the host's live input and position do not apply", delegate {
            // The host loaded the next scene (a hard cut: other coordinates); this copy has not reached that load yet.
            PrologueFollower behind = new PrologueFollower();
            behind.Receive(0, new[] { ev(100u, PrologueEventKind.Trigger, 1u) });
            Equal(false, behind.AcrossLoad, "only a trigger ahead: the same scene");
            behind.Receive(1, new[] { ev(130u, PrologueEventKind.Load, 9u) });
            Equal(true, behind.AcrossLoad, "the host loaded scene 9, this copy has not");
            for (int i = 0; i < 100; i++) behind.Tick(false);
            behind.Done();
            Equal(true, behind.AcrossLoad, "the trigger is done, the load is not yet");
            for (int i = 0; i < 30; i++) behind.Tick(false);
            behind.LocalLoad(9u);
            Equal(false, behind.AcrossLoad, "both have loaded it");
            // The copy loads first (it runs a moment ahead of the host).
            PrologueFollower ahead = new PrologueFollower();
            for (int i = 0; i < 100; i++) ahead.Tick(false);
            ahead.LocalLoad(8u);
            Equal(false, ahead.AcrossLoad, "a host that does not send its story (before T-205): never, its input applies as before");
            ahead.Restart(); ahead.HostKnown();
            for (int i = 0; i < 100; i++) ahead.Tick(false);
            ahead.LocalLoad(9u);
            Equal(true, ahead.AcrossLoad, "this copy loaded scene 9, the host has not reported it");
            ahead.Receive(0, new[] { ev(105u, PrologueEventKind.Load, 9u) });
            Equal(false, ahead.AcrossLoad, "the host's load arrived");
            ahead.Restart();
            Equal(true, ahead.AcrossLoad, "a new copy on this PC starts in the first scene: the host's load of scene 9 lies ahead of it again");
            // A load of this copy that the host never reports (it cannot be more than 2 s ahead of the host).
            PrologueFollower alone = new PrologueFollower();
            alone.HostKnown();
            alone.LocalLoad(7u);
            for (int i = 0; i < (int)PrologueFollower.AheadLimit; i++) alone.Tick(false);
            Equal(true, alone.AcrossLoad, "2 s after its own load: the host may still be about to load it");
            alone.Tick(false);
            Equal(false, alone.AcrossLoad, "later: the host is not going to; its input applies again");
        });
        Check("review 4: the host has logged more points than this copy has received: it stands still, covered when far behind", delegate {
            ProloguePace p = new ProloguePace();
            // A late joiner: the clock says step 30000 and 50 points, the log has not arrived.
            Equal(PrologueRate.Hold, p.Decide(30000, true), "the log is missing: no fast-forward by the step count alone"); Equal(true, p.Covered, "far behind: covered");
            Equal(PrologueRate.Hold, p.Decide(5, true), "even when the step count says level"); Equal(true, p.Covered, "the cover stays");
            Equal(PrologueRate.Fast, p.Decide(30000, false), "the log is here: fast-forward"); Equal(true, p.Covered, "covered");
            Equal(PrologueRate.Normal, p.Decide(5, false), "level"); Equal(false, p.Covered, "uncovered");
            // An on-time guest that is level: a moment without the newest point is not a cover.
            ProloguePace live = new ProloguePace();
            Equal(PrologueRate.Hold, live.Decide(3, true), "waits for the point"); Equal(false, live.Covered, "not covered");
            Equal(PrologueRate.Normal, live.Decide(3, false), "goes on");
            PrologueHostClock c = new PrologueHostClock();
            c.Report(600u, 7, 10f);
            Equal(7, c.Events, "the host's count is kept"); Equal(600u, c.Now(10f), "with its step");
        });
        Check("review 6: a piece of the log whose steps go back behind what is already here is refused", delegate {
            PrologueFollower f = new PrologueFollower();
            Equal(true, f.Receive(0, new[] { ev(100u, PrologueEventKind.Trigger, 1u), ev(200u, PrologueEventKind.Trigger, 2u) }), "first piece");
            Equal(false, f.Receive(2, new[] { ev(150u, PrologueEventKind.Trigger, 3u) }), "the next piece starts before step 200"); Equal(2, f.Count, "nothing taken");
            Equal(true, f.Receive(2, new[] { ev(200u, PrologueEventKind.Trigger, 3u), ev(260u, PrologueEventKind.Load, 4u) }), "the same step as the last one is fine"); Equal(4, f.Count, "taken");
            Equal(false, f.Receive(3, new[] { ev(260u, PrologueEventKind.Load, 4u), ev(10u, PrologueEventKind.Trigger, 5u) }), "a repeat followed by a step that goes back"); Equal(4, f.Count, "nothing taken");
        });

        // ---- T-205: pace (ticket 设计 5; physics steps, 60 per second)
        Check("pace: 2 s behind fast-forwards (60x) until within 0.2 s; covered only from 5 s behind", delegate {
            ProloguePace p = new ProloguePace();
            Equal(PrologueRate.Quick, p.Decide(119), "1.98 s behind: not yet");
            Equal(PrologueRate.Fast, p.Decide(120), "2 s behind"); Equal(false, p.Covered, "2 s: the picture stays");
            Equal(PrologueRate.Fast, p.Decide(299), "4.98 s"); Equal(false, p.Covered, "4.98 s: the picture stays");
            Equal(PrologueRate.Fast, p.Decide(300), "5 s"); Equal(true, p.Covered, "5 s: covered");
            Equal(PrologueRate.Fast, p.Decide(13), "0.22 s behind: still fast"); Equal(true, p.Covered, "covered until it is over");
            Equal(PrologueRate.Normal, p.Decide(12), "0.2 s behind: done"); Equal(false, p.Covered, "uncovered");
            Equal(60f, ProloguePace.Scale(PrologueRate.Fast, false), "60x"); Equal(0.02f, ProloguePace.Scale(PrologueRate.Fast, true), "nothing while a scene loads");
        });
        Check("pace: a late joiner (minutes behind) is covered from the first frame", delegate {
            ProloguePace p = new ProloguePace();
            Equal(PrologueRate.Fast, p.Decide(1800), "30 s behind"); Equal(true, p.Covered, "covered");
        });
        Check("pace: 0.33 s behind runs 2x until within 0.05 s", delegate {
            ProloguePace p = new ProloguePace();
            Equal(PrologueRate.Normal, p.Decide(19), "0.32 s: normal"); Equal(PrologueRate.Quick, p.Decide(20), "0.33 s: quick");
            Equal(PrologueRate.Quick, p.Decide(4), "0.07 s: still quick"); Equal(PrologueRate.Normal, p.Decide(3), "0.05 s: normal");
            Equal(2f, ProloguePace.Scale(PrologueRate.Quick, false), "2x"); Equal(2f, ProloguePace.Scale(PrologueRate.Quick, true), "also while a scene loads");
            Equal(1f, ProloguePace.Scale(PrologueRate.Normal, false), "normal is 1x");
        });
        Check("pace: 0.33 s ahead runs 0.5x until within 0.05 s; 2 s ahead stands still until under 0.33 s", delegate {
            ProloguePace p = new ProloguePace();
            Equal(PrologueRate.Normal, p.Decide(-19), "0.32 s ahead: normal"); Equal(PrologueRate.Slow, p.Decide(-20), "0.33 s ahead: slow");
            Equal(PrologueRate.Slow, p.Decide(-4), "still slow"); Equal(PrologueRate.Normal, p.Decide(-3), "level");
            Equal(PrologueRate.Slow, p.Decide(-119), "1.98 s ahead: slow"); Equal(PrologueRate.Hold, p.Decide(-120), "2 s ahead: hold");
            Equal(PrologueRate.Hold, p.Decide(-20), "0.33 s ahead: still held"); Equal(PrologueRate.Slow, p.Decide(-19), "under 0.33 s: slow again");
            Equal(PrologueRate.Normal, p.Decide(0), "level");
            Equal(0.5f, ProloguePace.Scale(PrologueRate.Slow, false), "0.5x"); Equal(0.02f, ProloguePace.Scale(PrologueRate.Hold, false), "held");
            Equal(false, p.Covered, "never covered for being ahead");
        });
        Check("pace: a new copy starts from the normal rate", delegate {
            ProloguePace p = new ProloguePace();
            p.Decide(5000); p.Reset();
            Equal(PrologueRate.Normal, p.Rate, "rate"); Equal(false, p.Covered, "covered");
        });
        Check("host clock: carried forward between reports, at most 0.3 s (a host loading a scene is not far ahead)", delegate {
            PrologueHostClock c = new PrologueHostClock();
            Equal(false, c.Have, "nothing yet"); Equal(0u, c.Now(5f), "nothing yet: 0");
            c.Report(600u, 10f);
            Equal(600u, c.Now(10f), "at the report"); Equal(606u, c.Now(10.1f), "0.1 s later: 6 steps"); Equal(618u, c.Now(10.3f), "0.3 s later: 18 steps");
            Equal(618u, c.Now(12f), "2 s without a report: still 18");
            c.Report(615u, 10.25f); Equal(615u, c.Now(10.25f), "the next report replaces the guess");
        });
        Check("host clock: no report for more than 1 s is not fresh (a lost link: the copy keeps its normal speed)", delegate {
            PrologueHostClock c = new PrologueHostClock();
            Equal(false, c.Fresh(0f), "nothing yet");
            c.Report(600u, 10f);
            Equal(true, c.Fresh(10.25f), "between two reports"); Equal(true, c.Fresh(11f), "1.0 s"); Equal(false, c.Fresh(11.01f), "more than 1 s");
            c.Report(700u, 15f); Equal(true, c.Fresh(15.1f), "reports are back");
            c.Reset(); Equal(false, c.Fresh(15.1f), "after a reset");
        });

        // ---- T-205: wire (message 96 kinds 2 and 3)
        Check("wire: clock round trip", delegate {
            MemoryStream ms = new MemoryStream();
            PrologueWire.WriteClock(new BinaryWriter(ms), 3u, 123456u, 17);
            Equal(11L, ms.Length, "bytes");   // kind + u32 + u32 + u16
            BinaryReader r = new BinaryReader(new MemoryStream(ms.ToArray()));
            Equal(PrologueWire.KindClock, PrologueWire.ReadKind(r), "kind"); Equal((byte)2, PrologueWire.KindClock, "kind 2");
            uint generation, step; int events;
            PrologueWire.ReadClock(r, out generation, out step, out events);
            Equal(3u, generation, "generation"); Equal(123456u, step, "step"); Equal(17, events, "events");
        });
        Check("wire: log round trip", delegate {
            PrologueStory s = new PrologueStory();
            s.Begin();
            for (int i = 0; i < 100; i++) s.Tick(false);
            s.Add(PrologueEventKind.Load, 0xAABBCCDDu, PrologueActor.None, 0f, 0f);
            for (int i = 0; i < 50; i++) s.Tick(false);
            s.Add(PrologueEventKind.Trigger, 7u, PrologueActor.Naru, -2187.5f, -192.7f);
            s.Add(PrologueEventKind.Cutscene, 8u, PrologueActor.Ori, 1f, 2f);
            MemoryStream ms = new MemoryStream();
            PrologueWire.WriteLog(new BinaryWriter(ms), 4u, s, 1, 2);
            Equal(44L, ms.Length, "bytes");   // kind + u32 + u16 + count, then 2 x (u32 + kind + u32 + actor + 2 f32)
            BinaryReader r = new BinaryReader(new MemoryStream(ms.ToArray()));
            Equal(PrologueWire.KindLog, PrologueWire.ReadKind(r), "kind"); Equal((byte)3, PrologueWire.KindLog, "kind 3");
            uint generation; int first;
            PrologueEvent[] got = PrologueWire.ReadLog(r, out generation, out first);
            Equal(4u, generation, "generation"); Equal(1, first, "first"); Equal(2, got.Length, "count");
            Equal(150u, got[0].Step, "step"); Equal(PrologueEventKind.Trigger, got[0].Kind, "kind"); Equal(7u, got[0].Id, "id");
            Equal(PrologueActor.Naru, got[0].Actor, "actor"); Equal(-2187.5f, got[0].X, "x"); Equal(-192.7f, got[0].Y, "y");
            Equal(PrologueEventKind.Cutscene, got[1].Kind, "second kind"); Equal(PrologueActor.Ori, got[1].Actor, "second actor");
        });
        Check("wire: an invalid log is refused", delegate {
            Func<byte[], BinaryReader> body = delegate(byte[] bytes) { BinaryReader r = new BinaryReader(new MemoryStream(bytes)); r.ReadByte(); return r; };
            Func<int, int, PrologueEvent[], byte[]> log = delegate(int first, int count, PrologueEvent[] entries) {
                MemoryStream ms = new MemoryStream(); BinaryWriter w = new BinaryWriter(ms);
                w.Write(PrologueWire.KindLog); w.Write(1u); w.Write((ushort)first); w.Write((byte)count);
                foreach (PrologueEvent e in entries) { w.Write(e.Step); w.Write(e.Kind); w.Write(e.Id); w.Write(e.Actor); w.Write(e.X); w.Write(e.Y); }
                return ms.ToArray(); };
            uint g; int f;
            PrologueEvent ok = new PrologueEvent { Step = 5, Kind = PrologueEventKind.Trigger, Id = 1 };
            PrologueWire.ReadLog(body(log(0, 1, new[] { ok })), out g, out f);   // a valid one is fine
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(0, 0, new PrologueEvent[0])), out g, out f); }, "no entries");
            PrologueEvent[] many = new PrologueEvent[33]; for (int i = 0; i < 33; i++) many[i] = ok;
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(0, 33, many)), out g, out f); }, "33 entries in one message");
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(512, 1, new[] { ok })), out g, out f); }, "past the 512 entries");
            PrologueEvent bad = ok; bad.Kind = 0;
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(0, 1, new[] { bad })), out g, out f); }, "kind 0");
            bad = ok; bad.Kind = 5;
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(0, 1, new[] { bad })), out g, out f); }, "kind 5");
            bad = ok; bad.Actor = 4;
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(0, 1, new[] { bad })), out g, out f); }, "actor 4");
            bad = ok; bad.X = float.NaN;
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(0, 1, new[] { bad })), out g, out f); }, "x NaN");
            PrologueEvent earlier = ok; earlier.Step = 4;
            Throws<InvalidDataException>(delegate { PrologueWire.ReadLog(body(log(0, 2, new[] { ok, earlier })), out g, out f); }, "steps going backwards");
            MemoryStream clock = new MemoryStream(); PrologueWire.WriteClock(new BinaryWriter(clock), 1u, 1u, 513);
            uint step; int events;
            Throws<InvalidDataException>(delegate { PrologueWire.ReadClock(body(clock.ToArray()), out g, out step, out events); }, "a clock with 513 events");
        });

        Console.WriteLine(failures == 0 ? "PROLOGUE TESTS PASS" : "PROLOGUE TESTS FAIL: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
