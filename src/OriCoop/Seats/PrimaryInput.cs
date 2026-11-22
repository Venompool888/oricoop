namespace OriCoop.Seats
{
    // T-188: which device an online PC's primary seat (its own Ori) uses.
    // Device codes as in SeatInputs: -1 = keyboard/mouse, 0..3 = XInput slot; NotSeen = nothing pressed on the menus yet.
    // The primary seat starts with the device its player used last on the menus (title, save slots, room pages), so a
    // controller player stays on the controller in game. Until the game starts, and as long as the player has not picked a
    // device in F8, it keeps following that device. Nothing pressed (e.g. test games) keeps the old default: keyboard/mouse.
    public static class PrimaryInput
    {
        public const int NotSeen = -2;

        static bool Valid(int device) { return device >= -1 && device <= 3; }

        public static int Default(SeatInputs inputs, int menuDevice)
        {
            if (Valid(menuDevice) && !inputs.Used(menuDevice)) return menuDevice;
            if (!inputs.Used(-1)) return -1;
            for (int pad = 0; pad < 4; pad++) if (!inputs.Used(pad)) return pad;
            return 0;
        }

        // Moves `seat` (already assigned) to the menu device if that differs and no other seat holds it.
        public static bool Follow(SeatInputs inputs, int seat, int menuDevice)
        {
            int current = inputs.Get(seat);
            if (current == -2 || !Valid(menuDevice) || menuDevice == current || inputs.Used(menuDevice)) return false;
            return inputs.Set(seat, menuDevice);
        }
    }

    // The device the player pressed last: a pad counts when it goes from resting to pressed/deflected (a drifting stick
    // that never returns to rest does not keep claiming it); keyboard/mouse counts on a key or mouse-button press.
    // When both happen in the same frame the pad wins.
    public sealed class MenuDevice
    {
        readonly bool[] active = new bool[4];
        public int Last = PrimaryInput.NotSeen;

        // padActive[i]: pad i is pressed or deflected now (false when not connected). Returns true when Last changed.
        public bool Observe(bool keyboardDown, bool[] padActive)
        {
            int was = Last, pressed = -1;
            for (int i = 0; i < 4; i++)
            {
                bool now = padActive != null && i < padActive.Length && padActive[i];
                if (now && !active[i] && pressed < 0) pressed = i;
                active[i] = now;
            }
            if (pressed >= 0) Last = pressed;
            else if (keyboardDown) Last = -1;
            return Last != was;
        }

        public void Clear() { Last = PrimaryInput.NotSeen; for (int i = 0; i < 4; i++) active[i] = false; }
    }
}
