using Core.Devices;
using Game;
using HarmonyLib;
using J2i.Net.XInputWrapper;
using SmartInput;
using UnityEngine;

namespace OriCoop
{
    // Which controller the game's own input (player 1's Core.Input, menus) reads.
    // P1Pad = 0..3: the game reads that pad (plus keyboard/mouse), always.
    // P1Pad = -1 (P1 is keyboard/mouse in co-op), the game reads P2's pad only where that can't move P1:
    //   - in menus (title, save slots, pause/inventory/map, loading) and in single-player mode: fully;
    //   - in co-op gameplay: only Start, so the controller player can open the pause menu;
    //   - while the co-op menu (F8 / mode select) is open: nothing (that menu reads the pads itself).
    // After the co-op menu closes, the pad stays blocked until all its buttons are released, so the press that
    // closed it doesn't also reach the game (e.g. Start opening the pause menu).
    // All game pad reads go through XboxControllerInput.GetAxis/GetButton -> XboxController.RetrieveController.
    public static class KeyboardOnlyP1
    {
        public static int P1Pad;
        public static bool Enabled { get { return P1Pad < 0; } }

        public static int GamePad = -1;   // pad the game reads now, -1 = none
        public static bool StartOnly;     // only the Start button passes
        public static bool HoldUntilReleased;
        public static string State = "";  // for logs/tests

        static XInputState s_state = new XInputState();

        public static void Refresh()
        {
            string was = State;
            if (NetPlay.Enabled && !InMenu())
            {
                InputDevice device = OnlineLocalPlayers.InputOf(Players.LocalSeat);
                int pad = device.Kind == InputDevice.DeviceKind.Pad ? device.Pad : -1;
                Set(pad, false, "online primary input " + pad);
            }
            else if (P1Pad >= 0) Set(P1Pad, false, "P1 pad " + P1Pad);
            else if (CoopMenu.IsOpen) Set(-1, false, "co-op menu open");
            else
            {
                int pad = PadForGame();
                if (PlayMode.Current == PlayMode.Single) Set(pad, false, "single player, pad " + pad);
                else if (InMenu()) Set(pad, false, "menu, pad " + pad);
                else Set(pad, true, "co-op gameplay, Start only, pad " + pad);
            }
            if (HoldUntilReleased)
            {
                if (GamePad < 0 || !PadHeld(GamePad)) HoldUntilReleased = false;
                else { GamePad = -1; State = "waiting for buttons to be released"; }
            }
            if (State != was && Plugin.Log != null) Plugin.Log.LogInfo("game controller input: " + State);
        }

        static void Set(int pad, bool startOnly, string state) { GamePad = pad; StartOnly = startOnly; State = state; }

        // T-188: the device the player pressed last on the menus (-1 keyboard/mouse, 0..3 pad, -2 nothing yet). The online
        // primary seat starts with it (OnlineLocalPlayers / Seats.PrimaryInput). Watches every connected pad, not GamePad:
        // the menus also take pad buttons through Unity's joystick key codes, so the pad in use need not be GamePad.
        static readonly Seats.MenuDevice s_menuDevice = new Seats.MenuDevice();
        static readonly bool[] s_padActive = new bool[4];
        static KeyCode[] s_keyboardCodes;
        public static int MenuDevice { get { return s_menuDevice.Last; } }
        internal static void SetMenuDeviceForTest(int device) { s_menuDevice.Clear(); s_menuDevice.Last = device; }

        // Main thread, every frame (Update: GetKeyDown is only valid there).
        public static void TrackMenuDevice()
        {
            if (!InMenu()) return;
            for (int i = 0; i < 4; i++) s_padActive[i] = PadActive(i);
            if (s_menuDevice.Observe(Input.anyKeyDown && KeyboardOrMouseDown(), s_padActive) && Plugin.Log != null)
                Plugin.Log.LogInfo("menu input device: " + (MenuDevice < 0 ? "keyboard/mouse" : "pad " + MenuDevice));
        }

        // anyKeyDown is also true for joystick buttons; only keyboard keys and mouse buttons count here. Hotkeys a
        // controller player also presses (F8 co-op menu, F9/F10, Steam's F12, Alt-Tab, the Windows key...) don't count.
        static bool KeyboardOrMouseDown()
        {
            if (s_keyboardCodes == null)
            {
                var codes = new System.Collections.Generic.List<KeyCode>();
                foreach (KeyCode k in System.Enum.GetValues(typeof(KeyCode)))
                    if (k != KeyCode.None && k < KeyCode.JoystickButton0 && !(k >= KeyCode.F1 && k <= KeyCode.F15) &&
                        k != KeyCode.LeftAlt && k != KeyCode.RightAlt && k != KeyCode.AltGr && k != KeyCode.Tab &&
                        k != KeyCode.LeftWindows && k != KeyCode.RightWindows && k != KeyCode.Print && k != KeyCode.SysReq &&
                        k != KeyCode.Numlock && k != KeyCode.CapsLock && k != KeyCode.ScrollLock && k != KeyCode.Pause)
                        codes.Add(k);
                s_keyboardCodes = codes.ToArray();
            }
            foreach (KeyCode k in s_keyboardCodes) if (Input.GetKeyDown(k)) return true;
            return false;
        }

        static bool PadActive(int pad)
        {
            if (!Connected(pad) || Pads.VirtualPads.GetState(pad, ref s_state) != 0) return false;
            XInputGamepad g = s_state.Gamepad;
            return g.wButtons != 0 || g.bLeftTrigger > 60 || g.bRightTrigger > 60 ||
                   System.Math.Abs((int)g.sThumbLX) > 16000 || System.Math.Abs((int)g.sThumbLY) > 16000 ||
                   System.Math.Abs((int)g.sThumbRX) > 16000 || System.Math.Abs((int)g.sThumbRY) > 16000;
        }

        // P2's pad if it is plugged in, else any plugged-in pad. (XboxController's state is polled by the game's own
        // thread, so this is cheap; calling XInput on an empty slot is not.)
        static int PadForGame()
        {
            int p2 = Plugin.Instance != null ? Plugin.Instance.P2PadInUse : 0;
            if (p2 >= 0 && p2 <= 3 && Connected(p2)) return p2;
            for (int i = 0; i < 4; i++) if (Connected(i)) return i;
            return p2 >= 0 && p2 <= 3 ? p2 : 0;
        }

        static bool s_raw; // our own lookups skip the index redirect below
        static bool Connected(int pad)
        {
            s_raw = true;
            try { return XboxController.RetrieveController(pad).IsConnected; }
            finally { s_raw = false; }
        }

        static bool PadHeld(int pad)
        {
            if (!Connected(pad)) return false;
            if (Pads.VirtualPads.GetState(pad, ref s_state) != 0) return false;
            XInputGamepad g = s_state.Gamepad;
            return g.wButtons != 0 || g.bLeftTrigger > 20 || g.bRightTrigger > 20;
        }

        public static bool InMenu()
        {
            SeinCharacter s = Characters.Sein;
            if (s == null || !s.gameObject.activeInHierarchy) return true;
            if (UI.MainMenuVisible) return true;
            GameController gc = GameController.Instance;
            if (gc != null && (gc.GameplaySuspendedForUI || gc.IsLoadingGame)) return true;
            if (GameStateMachine.Instance != null && GameStateMachine.Instance.IsInExtendedTitleScreen()) return true;
            return false;
        }

        // Keep the game's primary input sampler (and its button edges) intact. Only filter the other device.
        [HarmonyPatch(typeof(KeyCodeButtonInput), "GetButton")]
        static class PadOnly
        {
            static bool Prefix(ref bool __result)
            {
                if (NetPlay.Enabled)
                {
                    if (InMenu() || OnlineLocalPlayers.InputOf(Players.LocalSeat).Kind != InputDevice.DeviceKind.Pad) return true;
                }
                else if (Players.Remotes.Count <= 1 || P1Pad < 0 || InMenu()) return true;
                __result = false; return false;
            }
        }

        [HarmonyPatch(typeof(XboxControllerInput), "GetAxis")]
        static class AxisPatch
        {
            static bool Prefix(ref float __result)
            {
                if (GamePad >= 0 && !StartOnly) return true;
                __result = 0f;
                return false;
            }
        }

        [HarmonyPatch(typeof(XboxControllerInput), "GetButton")]
        static class ButtonPatch
        {
            static bool Prefix(XboxControllerInput.Button button, ref bool __result)
            {
                if (GamePad >= 0 && (!StartOnly || button == XboxControllerInput.Button.Start)) return true;
                __result = false;
                return false;
            }
        }

        // The game always asks for pad 0 (single-user PC build); point it at the pad chosen above.
        [HarmonyPatch(typeof(XboxController), "RetrieveController")]
        static class PadIndexPatch
        {
            static void Prefix(ref int index)
            {
                if (!s_raw && GamePad >= 0 && GamePad <= 3) index = GamePad;
            }
        }

        // Rumble from P1's gameplay goes to the pad above too; don't shake a pad that isn't P1's.
        [HarmonyPatch(typeof(XboxControllerManager), "SetCurrentMotorVibration")]
        static class RumblePatch
        {
            static void Prefix(ref Vector2 magnitude)
            {
                if (GamePad < 0 || StartOnly) magnitude = Vector2.zero;
            }
        }
    }
}
