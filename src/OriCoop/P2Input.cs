using J2i.Net.XInputWrapper;
using UnityEngine;

namespace OriCoop
{
    // Reads one XInput pad and fills an InputState the same way PlayerInput.FixedUpdate/RefreshControls
    // fill Core.Input for player 1 (bindings from PlayerInput.AddControllerControls).
    public static class P2Input
    {
        const int A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000, BACK = 0x20, START = 0x10,
            LB = 0x100, RB = 0x200, LS = 0x40, RS = 0x80, DUP = 1, DDOWN = 2, DLEFT = 4, DRIGHT = 8;

        static XInputState s_state = new XInputState();

        public static bool Connected;
        public static bool StartPressedThisStep;
        public static bool StartLatched;
        static bool s_anyWasDown;
        public static bool HoldStill;   // P1 is held by a script/cutscene; P2's input is ignored meanwhile

        static float Simplify(float v) { return Mathf.Abs(v) < 0.001f ? 0f : v; }

        static void Deadzone(ref float x, ref float y)
        {
            // Radial deadzone; PlayerInput.ApplyDeadzone uses a similar scheme.
            Vector2 v = new Vector2(x, y);
            float m = v.magnitude;
            const float dz = 0.25f;
            if (m < dz) { x = 0f; y = 0f; return; }
            v = v.normalized * Mathf.Clamp01((m - dz) / (1f - dz));
            x = v.x; y = v.y;
        }

        // Test bot: when BotActive, these replace the physical pad.
        public static bool BotActive;
        public static bool BotRT;
        public static int BotButtons;
        public static float BotLX, BotLY;
        public const int BTN_A = A, BTN_B = B, BTN_X = X, BTN_Y = Y, BTN_START = START;
        internal struct PadBot
        {
            public bool Active;
            public float LX;
            public int Buttons;
        }
        internal static readonly PadBot[] BotPads = new PadBot[4];

        static XInputState s_probe = new XInputState();
        public static bool IsConnected(int padIndex) { return XInput.XInputGetState(padIndex, ref s_probe) == 0; }

        public static void Sample(int padIndex, InputState s)
        {
            SamplePad(padIndex, s, true);
        }

        internal static void SampleExtra(int padIndex, InputState s)
        {
            SamplePad(padIndex, s, false);
        }

        static void SamplePad(int padIndex, InputState s, bool trackJoin)
        {
            bool wasStart = s.Start.IsPressed;
            bool connected = XInput.XInputGetState(padIndex, ref s_state) == 0;
            if (trackJoin) Connected = connected;
            XInputGamepad g = s_state.Gamepad;
            int b = connected ? g.wButtons : 0;
            float lx = connected ? g.sThumbLX / 32768f : 0f, ly = connected ? g.sThumbLY / 32768f : 0f;
            float rx = connected ? g.sThumbRX / 32768f : 0f, ry = connected ? g.sThumbRY / 32768f : 0f;
            bool lt = connected && g.bLeftTrigger > 20, rt = connected && g.bRightTrigger > 20;
            if (padIndex >= 0 && padIndex < BotPads.Length && BotPads[padIndex].Active)
            {
                b = BotPads[padIndex].Buttons; lx = BotPads[padIndex].LX;
                ly = rx = ry = 0f; lt = rt = false;
            }
            else if (BotActive) { b = BotButtons; lx = BotLX; ly = BotLY; rx = 0f; ry = 0f; lt = false; rt = BotRT; }
            int rawButtons = b; bool rawLt = lt, rawRt = rt;
            if (HoldStill) { b = 0; lx = ly = rx = ry = 0f; lt = rt = false; }

            Fill(s, b, lx, ly, rx, ry, lt, rt, true);

            if (!trackJoin) return;
            StartPressedThisStep = s.Start.IsPressed && !wasStart;
            // Several FixedUpdates can run in one frame; latch the press until Update consumes it.
            if (StartPressedThisStep) StartLatched = true;
            // Any button / trigger press also counts as "join" (modern pads label Start as Menu / Options).
            bool any = rawButtons != 0 || rawLt || rawRt;
            if (any && !s_anyWasDown) StartLatched = true;
            s_anyWasDown = any;
        }

        // Fills an InputState from pad-style values (XInput button bits, sticks -1..1, triggers), the way
        // PlayerInput fills Core.Input. Network play uses it too, with sticks already dead-zoned by the sender.
        public static void Fill(InputState s, int b, float lx, float ly, float rx, float ry, bool lt, bool rt, bool deadzone)
        {
            s.HorizontalAnalogLeft = Simplify(lx); s.VerticalAnalogLeft = Simplify(ly);
            if (deadzone) Deadzone(ref s.HorizontalAnalogLeft, ref s.VerticalAnalogLeft);
            s.HorizontalAnalogRight = Simplify(rx); s.VerticalAnalogRight = Simplify(ry);
            if (deadzone) Deadzone(ref s.HorizontalAnalogRight, ref s.VerticalAnalogRight);
            s.HorizontalDigiPad = ((b & DRIGHT) != 0 ? 1 : 0) - ((b & DLEFT) != 0 ? 1 : 0);
            s.VerticalDigiPad = ((b & DUP) != 0 ? 1 : 0) - ((b & DDOWN) != 0 ? 1 : 0);

            s.Jump.Update((b & A) != 0);
            s.SpiritFlame.Update((b & X) != 0);
            s.SoulFlame.Update((b & B) != 0);
            s.Bash.Update((b & Y) != 0);
            s.ChargeJump.Update(lt);
            s.ZoomOut.Update(lt);
            s.Glide.Update(rt);
            s.Grab.Update(rt);
            s.ZoomIn.Update(rt);
            s.LeftShoulder.Update((b & LB) != 0);
            s.RightShoulder.Update((b & RB) != 0);
            s.Select.Update((b & BACK) != 0);
            s.Start.Update((b & START) != 0);
            s.AnyStart.Update((b & START) != 0);
            s.LeftStick.Update((b & LS) != 0);
            s.RightStick.Update((b & RS) != 0);
            s.ActionButtonA.Update((b & A) != 0);
            s.Cancel.Update((b & B) != 0);
            float dy = s.VerticalDigiPad, dx = s.HorizontalDigiPad;
            s.MenuDown.Update(dy < -0.5f || s.VerticalAnalogLeft < -0.5f);
            s.MenuUp.Update(dy > 0.5f || s.VerticalAnalogLeft > 0.5f);
            s.MenuLeft.Update(dx < -0.5f || s.HorizontalAnalogLeft < -0.5f);
            s.MenuRight.Update(dx > 0.5f || s.HorizontalAnalogLeft > 0.5f);
            s.MenuPageLeft.Update(lt);
            s.MenuPageRight.Update(rt);

            // RefreshControls equivalent.
            s.Horizontal = Mathf.Clamp(s.HorizontalDigiPad + s.HorizontalAnalogLeft, -1f, 1f);
            s.Vertical = Mathf.Clamp(s.VerticalDigiPad + s.VerticalAnalogLeft, -1f, 1f);
            s.Down.Update(s.Vertical < -0.6f);
            s.Up.Update(s.Vertical > 0.6f);
            s.Left.Update(s.Horizontal < -0.4f);
            s.Right.Update(s.Horizontal > 0.4f);
            for (int i = 0; i < s.Buttons.Length; i++) s.Buttons[i].Used = false;
        }
    }
}
