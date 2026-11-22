using System;
using System.Runtime.InteropServices;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // T-181: keep the input method (IME) off for the whole game. With a Chinese IME active, letter keys (D to move)
    // went to the IME candidate box instead of the game. Nothing in the mod needs an IME: every text field (server
    // address, join code, room name) takes ASCII only through Input.inputString, which still works with the IME off.
    // Three layers:
    //   - Input.imeCompositionMode = Off (Unity's own switch; IMGUI may set it back to Auto, so it is re-applied);
    //   - ImmAssociateContext(game window, NULL): Windows sends no keys to an IME for this window. T-187: this only
    //     holds while the window does NOT have the keyboard focus. A focused window gets its input context back at
    //     once (measured: the call succeeds, ImmGetContext still returns the context), and a window detached without
    //     focus has it again after it is focused. So in real play the context is attached;
    //   - T-187: while a context is attached and the window has the focus, the IME is kept closed in alphanumeric
    //     mode, checked every fixed step. The player may have a Chinese IME selected or toggle it (Shift): it is closed
    //     again within one step and any composition is cancelled, so no candidate box stays and letter keys reach the
    //     game. Without focus nothing is changed (the player may be typing Chinese in another window). If the IME
    //     does not take the close, the log says "IME could NOT be closed".
    public static class ImeGuard
    {
        public static int Detaches;          // ImmAssociateContext(NULL) calls that left the window without a context
        public static int Closes;            // times an open / native-mode IME was closed again
        public static string LastResult = "";
        // Test-only mutation switch: proves the IME check fails without this guard.
        static readonly bool s_mutate = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_IME_ON") == "1";
        static IntPtr s_hwnd;
        static bool s_lastFocus, s_tried;
        static float s_nextFind;

        const int IME_CMODE_NATIVE = 0x1, NI_COMPOSITIONSTR = 0x15, CPS_CANCEL = 0x4;

        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class EveryStep { static void Postfix() { SubsystemGuard.Run("ImeGuard.Tick", s_tick); } }
        static readonly Action s_tick = Tick;

        static void Tick()
        {
            if (s_mutate) return;
            if (Input.imeCompositionMode != IMECompositionMode.Off) Input.imeCompositionMode = IMECompositionMode.Off;
            if (s_hwnd == IntPtr.Zero || !IsWindow(s_hwnd))
            {
                if (Time.realtimeSinceStartup < s_nextFind) return;
                s_hwnd = FindGameWindow(); s_tried = false;
                if (s_hwnd == IntPtr.Zero) { s_nextFind = Time.realtimeSinceStartup + 2f; Report("no game window yet"); return; }
            }
            // Detach once, and again after every focus change (not on a timer: each call makes Windows deactivate and
            // reactivate the context of a focused window).
            bool focus = GetFocus() == s_hwnd;
            if (!s_tried || focus != s_lastFocus)
            {
                s_tried = true; s_lastFocus = focus;
                bool had = WindowHasContext(s_hwnd);
                ImmAssociateContext(s_hwnd, IntPtr.Zero);   // returns the previous context (NULL when none), not success
                if (had && !WindowHasContext(s_hwnd)) Detaches++;
            }
            IntPtr ctx = ImmGetContext(s_hwnd);
            if (ctx == IntPtr.Zero) { State(1, focus); return; }
            // Only while the game window has the focus: without it no key reaches the game, and the player may be
            // typing Chinese in another window.
            if (!focus) { ImmReleaseContext(s_hwnd, ctx); State(2, focus); return; }
            int state = 3;
            try
            {
                int conversion, sentence;
                if (Active(ctx, out conversion, out sentence))
                {
                    ImmNotifyIME(ctx, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
                    if ((conversion & IME_CMODE_NATIVE) != 0) ImmSetConversionStatus(ctx, conversion & ~IME_CMODE_NATIVE, sentence);
                    ImmSetOpenStatus(ctx, false);
                    if (Active(ctx, out conversion, out sentence)) state = 4;   // the IME did not take it: say so in the log
                    else Closes++;
                }
            }
            finally { ImmReleaseContext(s_hwnd, ctx); }
            State(state, focus);
        }

        static bool Active(IntPtr ctx, out int conversion, out int sentence)
        {
            bool open = ImmGetOpenStatus(ctx);
            bool native = ImmGetConversionStatus(ctx, out conversion, out sentence) && (conversion & IME_CMODE_NATIVE) != 0;
            return open || native;
        }

        // Builds the log text only when the state changes (this runs every fixed step).
        static int s_state; static bool s_stateFocus;
        static void State(int state, bool focus)
        {
            if (state == s_state && focus == s_stateFocus) return;
            s_state = state; s_stateFocus = focus;
            Report(state == 1 ? "IME off (window context detached, compositionMode=" + Input.imeCompositionMode + ")"
                : state == 2 ? "IME left alone (context attached, window not focused)"
                : state == 3 ? "IME kept closed (context attached, window focused, compositionMode=" + Input.imeCompositionMode + ")"
                : "IME could NOT be closed (context attached, window focused: still open or in native mode)");
        }

        static void Report(string result)
        {
            if (result == LastResult) return;
            LastResult = result;
            if (Plugin.Log != null) Plugin.Log.LogInfo("input method: " + result + " [detaches=" + Detaches + " closes=" + Closes + " layout=" + GetKeyboardLayout(0).ToInt64().ToString("x") + "]");
        }

        // ---- for tests ----
        public static bool WindowFound { get { return FindGameWindow() != IntPtr.Zero; } }
        // Is an IME input context attached to the game window right now?
        public static bool WindowHasContext() { IntPtr w = FindGameWindow(); return w != IntPtr.Zero && WindowHasContext(w); }
        // Can the IME take keys from the game window right now: a context is attached and it is open or in native mode.
        public static bool ImeActive()
        {
            IntPtr w = FindGameWindow(); if (w == IntPtr.Zero) return false;
            IntPtr ctx = ImmGetContext(w); if (ctx == IntPtr.Zero) return false;
            try { int c, s; return Active(ctx, out c, out s); }
            finally { ImmReleaseContext(w, ctx); }
        }
        public static bool OnTestDesktop { get { return DesktopName() == "OriCoopTests"; } }
        // What a player's IME toggle does: open the IME in native (Chinese) mode. True only when both read back (open
        // and native); otherwise there is nothing to switch on here and the check cannot tell.
        public static bool TestOpenIme()
        {
            IntPtr w = FindGameWindow(); if (w == IntPtr.Zero) return false;
            IntPtr ctx = ImmGetContext(w); if (ctx == IntPtr.Zero) return false;
            try
            {
                int c, s;
                ImmSetOpenStatus(ctx, true);
                if (ImmGetConversionStatus(ctx, out c, out s)) ImmSetConversionStatus(ctx, c | IME_CMODE_NATIVE, s);
                return ImmGetOpenStatus(ctx) && ImmGetConversionStatus(ctx, out c, out s) && (c & IME_CMODE_NATIVE) != 0;
            }
            finally { ImmReleaseContext(w, ctx); }
        }
        // Real play has the game window focused, and only then does Windows keep the context attached. Test games are
        // started without focus, so the check gives the window its own thread's keyboard focus first. Only on the
        // hidden test desktop (WinSta0\OriCoopTests): there it cannot take the owner's focus or input.
        public static bool TestFocusWindow()
        {
            IntPtr w = FindGameWindow(); if (w == IntPtr.Zero) return false;
            if (GetFocus() == w) return true;
            if (DesktopName() != "OriCoopTests") return false;
            SetFocus(w);
            return GetFocus() == w;
        }
        public static string TestState()
        {
            IntPtr w = FindGameWindow(); if (w == IntPtr.Zero) return "no window";
            IntPtr ctx = ImmGetContext(w); int c = 0, s = 0; bool open = false;
            if (ctx != IntPtr.Zero) { open = ImmGetOpenStatus(ctx); ImmGetConversionStatus(ctx, out c, out s); ImmReleaseContext(w, ctx); }
            return "attached=" + (ctx != IntPtr.Zero) + " open=" + open + " conversion=" + c + " focused=" + (GetFocus() == w) + " layout=" + GetKeyboardLayout(0).ToInt64().ToString("x")
                + " isIme=" + ImmIsIME(GetKeyboardLayout(0)) + " desktop=" + DesktopName() + " detaches=" + Detaches + " closes=" + Closes + " last='" + LastResult + "'";
        }

        static string DesktopName()
        {
            try
            {
                var sb = new System.Text.StringBuilder(128); uint needed;
                return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2 /* UOI_NAME */, sb, (uint)sb.Capacity * 2, out needed) ? sb.ToString() : "?";
            }
            catch (Exception) { return "?"; }
        }

        static bool WindowHasContext(IntPtr hwnd)
        {
            IntPtr ctx = ImmGetContext(hwnd);
            if (ctx == IntPtr.Zero) return false;
            ImmReleaseContext(hwnd, ctx);
            return true;
        }

        static IntPtr FindGameWindow()
        {
            uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr w = IntPtr.Zero;
            while ((w = FindWindowEx(IntPtr.Zero, w, "UnityWndClass", null)) != IntPtr.Zero)
            {
                uint pid; GetWindowThreadProcessId(w, out pid);
                if (pid == me) return w;
            }
            return IntPtr.Zero;
        }

        [DllImport("imm32.dll")] static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr himc);
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hwnd);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);
        [DllImport("imm32.dll")] static extern bool ImmGetOpenStatus(IntPtr himc);
        [DllImport("imm32.dll")] static extern bool ImmSetOpenStatus(IntPtr himc, bool open);
        [DllImport("imm32.dll")] static extern bool ImmGetConversionStatus(IntPtr himc, out int conversion, out int sentence);
        [DllImport("imm32.dll")] static extern bool ImmSetConversionStatus(IntPtr himc, int conversion, int sentence);
        [DllImport("imm32.dll")] static extern bool ImmNotifyIME(IntPtr himc, int action, int index, int value);
        [DllImport("imm32.dll")] static extern bool ImmIsIME(IntPtr hkl);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")] static extern bool GetUserObjectInformation(IntPtr obj, int index, System.Text.StringBuilder info, uint length, out uint needed);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }
}
