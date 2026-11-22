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
    // Two layers, because Unity 5.3's IMECompositionMode alone does not detach the IME from the window:
    //   - Input.imeCompositionMode = Off (Unity's own switch; IMGUI may set it back to Auto, so it is re-applied);
    //   - ImmAssociateContext(game window, NULL): Windows sends no keys to an IME for this window.
    // Re-applied every fixed step (cheap check) and again after every focus change.
    public static class ImeGuard
    {
        public static int Detaches;          // successful ImmAssociateContext(NULL) calls (tests, logs)
        public static string LastResult = "";
        // Test-only mutation switch: proves the IME check fails without this guard.
        static readonly bool s_mutate = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_IME_ON") == "1";
        static IntPtr s_hwnd;
        static bool s_lastFocus, s_applied;
        static float s_nextTry;

        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class EveryStep { static void Postfix() { SubsystemGuard.Run("ImeGuard.Tick", s_tick); } }
        static readonly Action s_tick = Tick;

        static void Tick()
        {
            if (s_mutate) return;
            if (Input.imeCompositionMode != IMECompositionMode.Off) Input.imeCompositionMode = IMECompositionMode.Off;
            bool focus = GameController.IsFocused;
            if (s_applied && focus == s_lastFocus) return;
            if (Time.realtimeSinceStartup < s_nextTry) return;
            s_lastFocus = focus;
            s_applied = Detach();
            if (!s_applied) s_nextTry = Time.realtimeSinceStartup + 2f;   // window not created yet / call failed: retry
        }

        static bool Detach()
        {
            try
            {
                if (s_hwnd == IntPtr.Zero || !IsWindow(s_hwnd)) s_hwnd = FindGameWindow();
                if (s_hwnd == IntPtr.Zero) { Report("no game window yet"); return false; }
                ImmAssociateContext(s_hwnd, IntPtr.Zero);   // returns the previous context (NULL when none), not success
                if (WindowHasContext(s_hwnd)) { Report("IME still attached after ImmAssociateContext"); return false; }
                Detaches++;
                Report("IME off (window context detached, compositionMode=" + Input.imeCompositionMode + ")");
                return true;
            }
            catch (Exception e) { Report("failed: " + e.GetType().Name + ": " + e.Message); return false; }
        }

        static void Report(string result)
        {
            if (result == LastResult) return;
            LastResult = result;
            if (Plugin.Log != null) Plugin.Log.LogInfo("input method: " + result);
        }

        // For tests: is an IME input context attached to the game window right now?
        public static bool WindowHasContext() { IntPtr w = FindGameWindow(); return w != IntPtr.Zero && WindowHasContext(w); }
        public static bool WindowFound { get { return FindGameWindow() != IntPtr.Zero; } }

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
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }
}
