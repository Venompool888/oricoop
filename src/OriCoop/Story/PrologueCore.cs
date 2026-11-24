using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Story
{
    // T-203: the new-game prologue plays on every PC at the same time. Pure rules and wire format (no Unity types);
    // the game side is SharedPrologue.cs. Unit tests: src\Tests\Prologue.
    //   host:  PrologueHost turns "the new game started / the state machine is in Prologue" into a published state;
    //   guest: PrologueGuest decides when this PC starts its own copy, and when it must stop it;
    //   input: the host's gameplay input (PrologueInput) drives the playable parts on every guest.
    internal enum ProloguePhase : byte { None = 0, Playing = 1, Ended = 2 }

    internal struct PrologueState
    {
        public uint Generation;        // one per prologue the host starts
        public ProloguePhase Phase;
        public byte Difficulty;        // DifficultyMode the host picked (0 easy .. 3 one life)
        public float Elapsed;          // seconds since the host started it, when sent
    }

    internal struct PrologueInput
    {
        public uint Generation, Sequence;
        public sbyte Horizontal, Vertical;   // PrologueButtons.Quantise steps, -20..20
        public ushort Buttons;               // bit n = Core.Input.Buttons[PrologueButtons.Mirrored[n]] is down
        public byte Actor;                   // PrologueActor: the character the host controls now
        public float X, Y;                   // its position (meaningless when Actor is None)
        public bool Neutral { get { return Horizontal == 0 && Vertical == 0 && Buttons == 0; } }
        // T-209 (3): the input as it applies to the character this PC's copy controls now. The host's directions and
        // buttons are meant for the character the host controls: while this copy is in another part (another
        // character, or none against one) they must not drive what happens to be here.
        public PrologueInput For(byte localActor)
        {
            PrologueInput input = this;
            if (Actor != localActor) { input.Horizontal = input.Vertical = 0; input.Buttons = 0; }
            return input;
        }
    }

    internal static class PrologueActor { public const byte None = 0, Naru = 1, BabyOri = 2, Ori = 3; }

    internal static class PrologueButtons
    {
        // Indices into Core.Input.Buttons (decomp Core\Input.cs:145): Down, Up, Left, Right, Jump, SpiritFlame, Bash,
        // SoulFlame, ChargeJump, Glide, Grab, LeftShoulder, RightShoulder, LeftStick, RightStick. Everything else there
        // (Start, Select, the Menu* buttons, ActionButtonA, Cancel, ...) stays the guest's own: its pause menu.
        public static readonly int[] Mirrored = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 16, 17 };
        public const int JumpBit = 4;
        public static bool IsMirrored(int index) { return Array.IndexOf(Mirrored, index) >= 0; }
        // An axis in 20 steps, rounded away from zero: the game's own thresholds (> 0.4 walk, > 0.6 look, 0.8 full
        // speed) sit on step borders, so a value just above one is still above it on the guest.
        public const int AxisSteps = 20;
        public static sbyte Quantise(float axis)
        {
            if (float.IsNaN(axis)) return 0;
            float size = Math.Min(1f, Math.Abs(axis));
            int steps = (int)Math.Ceiling(size * AxisSteps - 1e-3f);
            if (steps < 0) steps = 0;
            return (sbyte)(axis < 0f ? -steps : steps);
        }
        public static float Axis(sbyte value) { return value / (float)AxisSteps; }
    }

    // ---- T-205: where the host's story is, so a copy started late (or one that fell behind) plays the same moment.
    // The prologue is a chain of scenes run by action sequences and timelines that all advance per physics step and
    // stand still while the game is frozen for a scene load. So "story step" = physics steps not frozen since the
    // new-game action, plus the few things that depend on the player (PrologueEventKind), says exactly where a copy is.
    internal static class PrologueEventKind
    {
        public const byte Load = 1;       // the story loaded its next scene (every copy does this by itself: the clocks line up again)
        public const byte Trigger = 2;    // the host's character walked into a trigger
        public const byte Button = 3;     // the host pressed the button a "press a button" wait asked for
        public const byte Cutscene = 4;   // the interactive cutscene (the weak Ori before the Spirit Tree) changed its state
        public static bool Valid(byte kind) { return kind >= Load && kind <= Cutscene; }
    }

    internal struct PrologueEvent
    {
        public uint Step;      // the host's story step when it happened
        public byte Kind;      // PrologueEventKind
        public uint Id;        // PrologueHash of the scene name / of the object's path
        public byte Actor;     // PrologueActor the host controlled then
        public float X, Y;     // where it stood (meaningless when Actor is None)
    }

    internal static class PrologueHash
    {
        // FNV-1a (32 bit) over UTF-8: the same text is the same id on every PC and in every build.
        public static uint Of(string text)
        {
            uint hash = 2166136261u;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text ?? "")) { hash ^= b; hash *= 16777619u; }
            return hash;
        }
    }

    internal static class PrologueWire
    {
        public const byte Message = 96;   // 0x60, docs/net-msg-ids.md
        public const byte KindState = 0, KindInput = 1, KindClock = 2, KindLog = 3;
        public const int MaxLogBatch = 32;
        public static void WriteClock(BinaryWriter w, uint generation, uint step, int events)
        { w.Write(KindClock); w.Write(generation); w.Write(step); w.Write((ushort)events); }
        public static void ReadClock(BinaryReader r, out uint generation, out uint step, out int events)
        {
            generation = r.ReadUInt32(); step = r.ReadUInt32(); events = r.ReadUInt16();
            if (events > PrologueStory.MaxEvents) throw new InvalidDataException("invalid prologue clock");
        }
        // Entries [first, first + count) of the host's log.
        public static void WriteLog(BinaryWriter w, uint generation, PrologueStory story, int first, int count)
        {
            w.Write(KindLog); w.Write(generation); w.Write((ushort)first); w.Write((byte)count);
            for (int i = first; i < first + count; i++)
            {
                PrologueEvent e = story[i];
                w.Write(e.Step); w.Write(e.Kind); w.Write(e.Id); w.Write(e.Actor); w.Write(e.X); w.Write(e.Y);
            }
        }
        public static PrologueEvent[] ReadLog(BinaryReader r, out uint generation, out int first)
        {
            generation = r.ReadUInt32(); first = r.ReadUInt16();
            int count = r.ReadByte();
            if (count < 1 || count > MaxLogBatch || first + count > PrologueStory.MaxEvents) throw new InvalidDataException("invalid prologue log");
            PrologueEvent[] entries = new PrologueEvent[count];
            for (int i = 0; i < count; i++)
            {
                PrologueEvent e = new PrologueEvent { Step = r.ReadUInt32(), Kind = r.ReadByte(), Id = r.ReadUInt32(), Actor = r.ReadByte(), X = r.ReadSingle(), Y = r.ReadSingle() };
                if (!PrologueEventKind.Valid(e.Kind) || e.Actor > PrologueActor.Ori || !Finite(e.X) || !Finite(e.Y) || i > 0 && e.Step < entries[i - 1].Step)
                    throw new InvalidDataException("invalid prologue log entry");
                entries[i] = e;
            }
            return entries;
        }
        public static void WriteState(BinaryWriter w, PrologueState s)
        { w.Write(KindState); w.Write(s.Generation); w.Write((byte)s.Phase); w.Write(s.Difficulty); w.Write(s.Elapsed); }
        public static void WriteInput(BinaryWriter w, PrologueInput i)
        {
            w.Write(KindInput); w.Write(i.Generation); w.Write(i.Sequence); w.Write(i.Horizontal); w.Write(i.Vertical);
            w.Write(i.Buttons); w.Write(i.Actor); w.Write(i.X); w.Write(i.Y);
        }
        public static byte ReadKind(BinaryReader r) { return r.ReadByte(); }
        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        public static PrologueState ReadState(BinaryReader r)
        {
            PrologueState s = new PrologueState { Generation = r.ReadUInt32() };
            byte phase = r.ReadByte(); s.Difficulty = r.ReadByte(); s.Elapsed = r.ReadSingle();
            if (phase > (byte)ProloguePhase.Ended || s.Difficulty > 3 || !Finite(s.Elapsed) || s.Elapsed < 0f)
                throw new InvalidDataException("invalid prologue state");
            s.Phase = (ProloguePhase)phase;
            return s;
        }
        public static PrologueInput ReadInput(BinaryReader r)
        {
            PrologueInput i = new PrologueInput { Generation = r.ReadUInt32(), Sequence = r.ReadUInt32(), Horizontal = r.ReadSByte(),
                Vertical = r.ReadSByte(), Buttons = r.ReadUInt16(), Actor = r.ReadByte(), X = r.ReadSingle(), Y = r.ReadSingle() };
            if (i.Actor > PrologueActor.Ori || !Finite(i.X) || !Finite(i.Y) || Math.Abs((int)i.Horizontal) > PrologueButtons.AxisSteps
                || Math.Abs((int)i.Vertical) > PrologueButtons.AxisSteps || (i.Buttons >> PrologueButtons.Mirrored.Length) != 0)
                throw new InvalidDataException("invalid prologue input");
            return i;
        }
    }

    // This PC's own prologue (what a host tells its guests). It follows the game, not the session: it is tracked
    // offline too, so a guest that connects in the middle gets the true elapsed time. Started() comes from the game's
    // own new-game action (the first frame of the prologue); Tick() sees the state machine. The prologue is over as
    // soon as the state machine has been in Prologue and is not any more (skipped, played to its end, or back on the
    // title); back on the title it is forgotten, so a later game with an existing save announces nothing.
    internal sealed class PrologueHost
    {
        // The new-game sequence starts on the title scene and reaches the Prologue state some seconds in. Only a
        // safety net: a start that never gets there (it has no cancel) is given up after this.
        public const float StartGrace = 60f;
        PrologueState state;
        float startedAt, graceUntil;
        bool sawPrologue;
        public PrologueState State { get { return state; } }
        public void Started(float now, byte difficulty)
        {
            if (state.Phase == ProloguePhase.Playing) return;
            Begin(now, difficulty);
        }
        void Begin(float now, byte difficulty)
        {
            state = new PrologueState { Generation = state.Generation + 1, Phase = ProloguePhase.Playing, Difficulty = difficulty };
            startedAt = now; graceUntil = now + StartGrace; sawPrologue = false;
        }
        // true = the phase changed (publish at once).
        public bool Tick(bool inPrologue, bool atTitle, byte difficulty, float now)
        {
            ProloguePhase before = state.Phase; uint generation = state.Generation;
            if (state.Phase != ProloguePhase.Playing && inPrologue) Begin(now, difficulty);   // no Started() seen
            if (state.Phase == ProloguePhase.Playing)
            {
                if (inPrologue) sawPrologue = true;
                else if (sawPrologue || now >= graceUntil) state.Phase = ProloguePhase.Ended;
            }
            else if (state.Phase == ProloguePhase.Ended && atTitle) state.Phase = ProloguePhase.None;
            state.Elapsed = state.Phase == ProloguePhase.Playing ? Math.Max(0f, now - startedAt) : 0f;
            return state.Phase != before || state.Generation != generation;
        }
    }

    // Host: the story clock and the log of what the player decided. Like PrologueHost it follows the game, online or not.
    internal sealed class PrologueStory
    {
        public const int MaxEvents = 512;   // the whole prologue has about 60
        readonly List<PrologueEvent> log = new List<PrologueEvent>();
        public uint Step { get; private set; }
        public int Count { get { return log.Count; } }
        public PrologueEvent this[int index] { get { return log[index]; } }
        public void Begin() { Step = 0; log.Clear(); }
        // Once per physics step of the host's game.
        public void Tick(bool frozen) { if (!frozen) Step++; }
        // -1 = the log is full (nothing in the real prologue gets near it).
        public int Add(byte kind, uint id, byte actor, float x, float y)
        {
            if (log.Count >= MaxEvents) return -1;
            log.Add(new PrologueEvent { Step = Step, Kind = kind, Id = id, Actor = actor, X = x, Y = y });
            return log.Count - 1;
        }
    }

    // Guest: where this PC's copy is on the host's story clock.
    //   - the copy's clock counts its own physics steps, but never past a host event it has not done yet (Due);
    //   - doing an event (Done) sets the clock to the host's step of that event;
    //   - a Load is done by the copy's own story (LocalLoad): once both ends have done it, the clock is the host's
    //     step of it plus the steps this copy played since, whichever end was first.
    internal sealed class PrologueFollower
    {
        readonly List<PrologueEvent> log = new List<PrologueEvent>();
        readonly Dictionary<uint, uint> loads = new Dictionary<uint, uint>();   // scene id -> this copy's raw step at its own load
        readonly Dictionary<uint, uint> ahead = new Dictionary<uint, uint>();   // ... of the scenes the host's log does not have yet
        // A copy never runs further ahead of the host than this (ProloguePace holds it): a load of its own that the
        // host has not reported after so many steps is not one the host is about to do.
        public const uint AheadLimit = ProloguePace.HoldAt;
        int applied;
        uint step, raw;
        public bool Known { get; private set; }   // the host sends its story (a T-203 host does not: nothing here is used)
        public uint Step { get { return step; } }
        public int Count { get { return log.Count; } }
        public int Applied { get { return applied; } }
        // Everything, the host's log included (another prologue, another online run).
        public void Reset() { log.Clear(); Known = false; Restart(); }
        // A new copy on this PC: it starts at the story's first step; the host's log stays.
        public void Restart() { loads.Clear(); ahead.Clear(); applied = 0; step = raw = 0; }
        public void HostKnown() { Known = true; }
        // Entries [first, first + n) of the host's log. Only what extends the log without a gap is taken (what is
        // already here is skipped); false = a gap: the host sends its whole log again every few seconds. Also false,
        // with nothing taken, when a new entry's step lies before the entry in front of it: the host's clock never
        // goes back, and doing such an entry would set this copy's clock back (PrologueWire.ReadLog checks the order
        // inside one message only).
        public bool Receive(int first, IList<PrologueEvent> entries)
        {
            Known = true;
            if (first < 0 || first > log.Count) return false;
            int from = log.Count - first;
            uint last = log.Count > 0 ? log[log.Count - 1].Step : 0u;
            for (int i = from; i < entries.Count; i++)
            {
                if (entries[i].Step < last) return false;
                last = entries[i].Step;
            }
            for (int i = from; i < entries.Count && log.Count < PrologueStory.MaxEvents; i++)
            {
                log.Add(entries[i]);
                if (entries[i].Kind == PrologueEventKind.Load) ahead.Remove(entries[i].Id);
            }
            Settle();
            return true;
        }
        // The next host event this copy has not done, once its step has come.
        public bool Due(out PrologueEvent e)
        {
            if (applied < log.Count && log[applied].Step <= step) { e = log[applied]; return true; }
            e = new PrologueEvent();
            return false;
        }
        // ... to be done in this physics step of the copy. Never in a step in which the game is frozen for a scene
        // load: the copy's story stands still then, and so does its clock (Tick). Putting a character somewhere or
        // firing a trigger in such a step would change a copy that is in the middle of a load.
        public bool Due(bool frozen, out PrologueEvent e)
        {
            if (!frozen) return Due(out e);
            e = new PrologueEvent();
            return false;
        }
        // The host and this copy are on different sides of a scene load of the story: the host loaded a scene this
        // copy has not loaded yet, or the other way round. The host's live input and position belong to the other
        // scene then (after a hard cut the same kind of character stands at other coordinates).
        public bool AcrossLoad
        {
            get
            {
                if (!Known) return false;
                foreach (KeyValuePair<uint, uint> load in ahead) if (raw - load.Value <= AheadLimit) return true;
                for (int i = applied; i < log.Count; i++) if (log[i].Kind == PrologueEventKind.Load) return true;
                return false;
            }
        }
        // Once per physics step of this copy.
        public void Tick(bool frozen)
        {
            if (frozen) return;
            raw++;
            PrologueEvent e;
            if (!Due(out e)) step++;
            Settle();
        }
        // This copy did the due event (fired the trigger, passed the wait, changed the cutscene state).
        public void Done()
        {
            if (applied >= log.Count) return;
            step = log[applied].Step; applied++;
            Settle();
        }
        // This copy's own story loaded a scene.
        public void LocalLoad(uint scene)
        {
            loads[scene] = raw;
            bool logged = false;
            for (int i = 0; i < log.Count && !logged; i++) logged = log[i].Kind == PrologueEventKind.Load && log[i].Id == scene;
            if (!logged) ahead[scene] = raw;
            Settle();
        }
        void Settle()
        {
            while (applied < log.Count && log[applied].Kind == PrologueEventKind.Load)
            {
                uint at;
                if (!loads.TryGetValue(log[applied].Id, out at)) return;
                step = log[applied].Step + (raw - at);
                applied++;
            }
        }
        // Steps this copy is behind the host (negative: ahead).
        public int Lag(uint hostStep) { return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)hostStep - (long)step)); }
    }

    // Guest: the host's clock between its reports (every 0.25 s). Carried forward at real time for at most
    // MaxCarry: a host that stands still for a scene load must not look far ahead.
    internal sealed class PrologueHostClock
    {
        public const float MaxCarry = 0.3f;
        // No report for this long (the host sends four a second): the link is down. The copy then plays on at its
        // normal speed instead of slowing down against a clock that stands still.
        public const float StaleAfter = 1f;
        uint step; float at; bool have;
        public bool Have { get { return have; } }
        public bool Fresh(float now) { return have && now - at <= StaleAfter; }
        // How many points the host had logged when it sent its last clock.
        public int Events { get; private set; }
        public void Report(uint hostStep, int events, float now) { Events = events; Report(hostStep, now); }
        public void Reset() { have = false; step = 0; at = 0f; Events = 0; }
        public void Report(uint hostStep, float now) { step = hostStep; at = now; have = true; }
        public uint Now(float now)
        {
            if (!have) return 0;
            float carry = Math.Max(0f, Math.Min(MaxCarry, now - at));
            return step + (uint)(carry * 60f);
        }
    }

    internal enum PrologueRate { Hold, Slow, Normal, Quick, Fast }

    // Guest: how fast this copy runs so that it shows what the host shows. Lag in physics steps (60 per second),
    // positive = behind. Each rate is kept until the copy is nearly level again, so it does not flutter.
    //   behind 2 s or more          Fast   60x (nothing while a scene loads), until within 0.2 s
    //   ... and 5 s or more         the picture is covered while it lasts (the "catching up" screen)
    //   behind 0.33 s or more       Quick  2x, until within 0.05 s
    //   ahead 0.33 s or more        Slow   0.5x, until within 0.05 s
    //   ahead 2 s or more           Hold   (almost) stopped, until less than 0.33 s ahead
    internal sealed class ProloguePace
    {
        public const int FastAt = 120, FastUntil = 12, CoverAt = 300, QuickAt = 20, QuickUntil = 3, SlowAt = 20, SlowUntil = 3, HoldAt = 120, HoldUntil = 20;
        public const float FastScale = 60f, QuickScale = 2f, SlowScale = 0.5f, HoldScale = 0.02f;
        public PrologueRate Rate { get; private set; }
        public bool Covered { get; private set; }
        public ProloguePace() { Rate = PrologueRate.Normal; }
        bool waiting;
        public void Reset() { Rate = PrologueRate.Normal; Covered = false; waiting = false; }
        // missing: the host's clock reports more logged points than this copy has received. Without them the step
        // count alone says nothing: the copy would run on to the host's step with its story standing at the first
        // point it does not know, and look level. It stands still until they are here; a cover that is up stays up,
        // and one goes up when the copy is far behind.
        public PrologueRate Decide(int lag, bool missing)
        {
            if (missing)
            {
                waiting = true;
                if (lag >= CoverAt) Covered = true;
                return Rate = PrologueRate.Hold;
            }
            if (waiting) { waiting = false; Rate = PrologueRate.Normal; }
            return Decide(lag);
        }
        public PrologueRate Decide(int lag)
        {
            if (Rate == PrologueRate.Fast && lag <= FastUntil) Rate = PrologueRate.Normal;
            if (Rate == PrologueRate.Quick && lag <= QuickUntil) Rate = PrologueRate.Normal;
            if (Rate == PrologueRate.Slow && lag >= -SlowUntil) Rate = PrologueRate.Normal;
            if (Rate == PrologueRate.Hold && lag > -HoldUntil) Rate = PrologueRate.Slow;
            if (lag >= FastAt) Rate = PrologueRate.Fast;
            else if (lag <= -HoldAt) Rate = PrologueRate.Hold;
            else if (Rate == PrologueRate.Normal && lag >= QuickAt) Rate = PrologueRate.Quick;
            else if (Rate == PrologueRate.Normal && lag <= -SlowAt) Rate = PrologueRate.Slow;
            if (Rate != PrologueRate.Fast) Covered = false;
            else if (lag >= CoverAt) Covered = true;
            return Rate;
        }
        // Time.timeScale for a rate. Fast-forwarding stands still while a scene loads: the story must not reach a
        // scene change before its scene is there.
        public static float Scale(PrologueRate rate, bool sceneLoading)
        {
            switch (rate)
            {
                case PrologueRate.Fast: return sceneLoading ? HoldScale : FastScale;
                case PrologueRate.Quick: return QuickScale;
                case PrologueRate.Slow: return SlowScale;
                case PrologueRate.Hold: return HoldScale;
                default: return 1f;
            }
        }
    }

    internal enum PrologueAction { None, Start, Stop, HostLeft }

    internal struct PrologueGuestInput
    {
        public bool HaveHost;          // a state from the host arrived in this session
        public PrologueState Host;     // the latest one, Elapsed advanced to now
        public bool HostHere;          // the host's seat is in the session and the link is up
        public bool HostLeft;          // the host's seat left the session, or this PC was made the host
        public bool Waiting;           // this PC waits on the title for the host's save (GuestJoin)
        public bool CanStart;          // the title's save-slot screen is up: the game's new-game action can run
        public bool LocalEnded;        // the local copy was in the Prologue state and left it by itself
        public bool Returned;          // after a stop: the local copy is back on the title
        public bool HostStory;         // T-205: the host sends its story position: a copy started late can catch up
        public float Now;
    }

    // Guest: one decision per host prologue (generation): play along, or wait. A host that sends its story position
    // (T-205) can be joined at any time; an older host only within StartWindow of its start.
    //   Start     run the game's new-game action here now
    //   Stop      end the local copy and go back to the title; the join goes on (the host's save comes next)
    //   HostLeft  the host is gone: end the local copy if any, and give up the join
    internal sealed class PrologueGuest
    {
        public const float StartWindow = 5f;      // later than this after the host's start: wait instead of playing along
        public const float HostGoneGrace = 10f;   // a link drop shorter than this does not end the local copy
        public enum Mode { Idle, Watching, Returning }
        public Mode Current { get; private set; }
        // The host is in its prologue right now (waiting-screen text; the join's "no save" timer does not run).
        public bool HostPlaying { get; private set; }
        uint decided;
        bool haveDecided, sawPlaying, leftReported;
        float goneSince = -1f;
        public void Reset()
        { Current = Mode.Idle; HostPlaying = false; haveDecided = sawPlaying = leftReported = false; goneSince = -1f; }
        public PrologueAction Tick(PrologueGuestInput i)
        {
            if (i.HostHere) goneSince = -1f; else if (goneSince < 0f) goneSince = i.Now;
            bool gone = i.HostLeft || goneSince >= 0f && i.Now - goneSince >= HostGoneGrace;
            bool playing = i.HaveHost && i.Host.Phase == ProloguePhase.Playing;
            HostPlaying = playing && !gone;
            if (playing) sawPlaying = true;
            switch (Current)
            {
                case Mode.Watching:
                    if (gone) { Current = Mode.Returning; leftReported = true; return PrologueAction.HostLeft; }
                    if (!i.Waiting || !playing || i.Host.Generation != decided || i.LocalEnded)
                    { Current = Mode.Returning; return PrologueAction.Stop; }
                    return PrologueAction.None;
                case Mode.Returning:
                    if (i.Returned) Current = Mode.Idle;
                    return PrologueAction.None;
                default:
                    // The host left while this PC waited for the end of its prologue: no save will come.
                    if (gone && sawPlaying && i.Waiting && !leftReported) { leftReported = true; return PrologueAction.HostLeft; }
                    if (!playing || gone || haveDecided && decided == i.Host.Generation) return PrologueAction.None;
                    // Too late for a host that does not say where its story is: wait (asked again every frame, so the
                    // order in which the host's state and its first clock arrive does not matter).
                    if (i.Host.Elapsed > StartWindow && !i.HostStory) return PrologueAction.None;
                    if (!i.Waiting || !i.CanStart) return PrologueAction.None;   // ask again next frame, until the window closes
                    Decide(i.Host.Generation); Current = Mode.Watching;
                    return PrologueAction.Start;
            }
        }
        void Decide(uint generation) { decided = generation; haveDecided = true; }
    }

    // Guest: the host's inputs, one per physics step, so a press and a release that arrive in one frame are still two
    // steps (a jump is not lost). No input for StaleAfter seconds (the host sends at least every 0.2 s): neutral.
    internal sealed class PrologueInputQueue
    {
        public const int Max = 32;
        public const float StaleAfter = 1f, RestSeconds = 0.5f;
        readonly Queue<PrologueInput> queue = new Queue<PrologueInput>();
        PrologueInput last, newest;
        bool have;
        float receivedAt, neutralSince = -1f;
        public int Dropped;
        public bool CaughtUp { get { return queue.Count == 0; } }
        // The newest input received (its Actor / X / Y are the host's latest position report). T-209 (4): not once
        // it is stale (the host reports at least every 0.2 s): during a lost link the last position known is not
        // where the character belongs, and the copy would be pulled back to it again and again.
        public bool TryNewest(float now, out PrologueInput input) { input = newest; return have && now - receivedAt <= StaleAfter; }
        // The host's input has been neutral for RestSeconds: its character stands still, so a small offset is real drift.
        public bool HostResting(float now) { return have && neutralSince >= 0f && now - neutralSince >= RestSeconds; }
        public void Clear() { queue.Clear(); have = false; last = newest = new PrologueInput(); neutralSince = -1f; }
        public void Push(PrologueInput input, float now)
        {
            if (have && (input.Generation < newest.Generation || input.Generation == newest.Generation && input.Sequence <= newest.Sequence))
                return;   // late or repeated
            if (have && input.Generation != newest.Generation) queue.Clear();
            if (queue.Count >= Max) { queue.Dequeue(); Dropped++; }
            queue.Enqueue(input);
            newest = input; have = true; receivedAt = now;
            if (!input.Neutral) neutralSince = -1f; else if (neutralSince < 0f) neutralSince = now;
        }
        // Once per physics step: the input to apply now.
        public PrologueInput Next(float now)
        {
            if (queue.Count > 0) last = queue.Dequeue();
            else if (!have || now - receivedAt > StaleAfter) { last.Horizontal = last.Vertical = 0; last.Buttons = 0; }
            return last;
        }
    }

    internal static class PrologueDrift
    {
        public const float MovingLimit = 3f, RestLimit = 0.75f;
        // true = put the guest's character where the host's is. While the host moves, the guest's copy trails by the
        // network delay (a few tenths of a unit at walking speed), so only a large gap counts; at rest a small one does.
        public static bool ShouldSnap(float dx, float dy, bool hostResting, bool caughtUp)
        {
            if (!caughtUp || float.IsNaN(dx) || float.IsNaN(dy)) return false;
            float gap = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return gap > (hostResting ? RestLimit : MovingLimit);
        }
    }
}
