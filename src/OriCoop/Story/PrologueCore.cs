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

    internal static class PrologueWire
    {
        public const byte Message = 96;   // 0x60, docs/net-msg-ids.md
        public const byte KindState = 0, KindInput = 1;
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
        public float Now;
    }

    // Guest: one decision per host prologue (generation): play along from the start, or (too late / not ready) wait.
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
                    if (i.Host.Elapsed > StartWindow) { Decide(i.Host.Generation); return PrologueAction.None; }
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
        // The newest input received (its Actor / X / Y are the host's latest position report).
        public bool TryNewest(out PrologueInput input) { input = newest; return have; }
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
