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
            // The decision stands: no start later in the same prologue, whatever the elapsed time says.
            Equal(PrologueAction.None, late.Tick(In(1f, ProloguePhase.Playing, 0.5f)), "same generation again");
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

        Console.WriteLine(failures == 0 ? "PROLOGUE TESTS PASS" : "PROLOGUE TESTS FAIL: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
