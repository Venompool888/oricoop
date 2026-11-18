namespace OriCoop.Seats
{
    // -1 = keyboard/mouse; 0..3 = XInput slots. -2 is unassigned.
    public sealed class SeatInputs
    {
        readonly int[] inputs = { -2, -2, -2, -2, -2 };
        static bool Valid(int seat, int input) { return seat >= 1 && seat <= 4 && input >= -1 && input <= 3; }
        public int Get(int seat) { return seat >= 1 && seat <= 4 ? inputs[seat] : -2; }
        public bool Used(int input) { for (int i = 1; i <= 4; i++) if (inputs[i] == input) return true; return false; }
        public bool Add(int seat, int input)
        {
            if (!Valid(seat, input) || Get(seat) != -2 || Used(input)) return false;
            inputs[seat] = input; return true;
        }
        public bool Set(int seat, int input)
        {
            if (!Valid(seat, input) || Get(seat) == -2) return false;
            int old = inputs[seat];
            for (int i = 1; i <= 4; i++) if (i != seat && inputs[i] == input) inputs[i] = old;
            inputs[seat] = input; return true;
        }
        public bool AddAtNewPrimary(int previous, int primary, int input)
        {
            if (Get(previous) == -2 || primary >= previous) return false;
            return Add(primary, input);
        }
        public void Remove(int seat) { if (seat >= 1 && seat <= 4) inputs[seat] = -2; }
        public void Clear() { for (int i = 1; i <= 4; i++) inputs[i] = -2; }
    }
}
