using System;

namespace OriCoop.Pads
{
    public static class PsReport
    {
        static readonly ushort[] Dpad = { 0x1, 0x9, 0x8, 0xA, 0x2, 0x6, 0x4, 0x5 };

        public static bool TryParse(byte[] r, int length, bool dualSenseUsb, out PadSnapshot s)
        {
            s = new PadSnapshot();
            if (r == null || length < 1 || length > r.Length) return false;

            int o;
            switch (r[0])
            {
                case 0x01: o = 1; break;
                case 0x11: o = 3; break;
                case 0x31: o = 2; break;
                default: return false;
            }
            bool full = r[0] == 0x31 || (r[0] == 0x01 && dualSenseUsb);
            int b = o + (full ? 7 : 4);
            int l2 = o + (full ? 4 : 7);
            if (length < b + 3 || length <= l2 + 1) return false;

            int hat = r[b] & 0x0F;
            int buttons = hat < Dpad.Length ? Dpad[hat] : 0;
            if ((r[b] & 0x10) != 0) buttons |= 0x4000; // Square -> X
            if ((r[b] & 0x20) != 0) buttons |= 0x1000; // Cross -> A
            if ((r[b] & 0x40) != 0) buttons |= 0x2000; // Circle -> B
            if ((r[b] & 0x80) != 0) buttons |= 0x8000; // Triangle -> Y
            if ((r[b + 1] & 0x01) != 0) buttons |= 0x100; // L1 -> LB
            if ((r[b + 1] & 0x02) != 0) buttons |= 0x200; // R1 -> RB
            if ((r[b + 1] & 0x20) != 0) buttons |= 0x10;  // Options -> Start
            if ((r[b + 1] & 0x10) != 0 || (r[b + 2] & 0x02) != 0) buttons |= 0x20; // Share / touchpad -> Back
            buttons |= r[b + 1] & 0xC0; // L3 / R3 -> LS / RS

            s.Buttons = (ushort)buttons;
            s.LT = r[l2];
            s.RT = r[l2 + 1];
            s.LX = Axis(r[o] - 128);
            s.LY = Axis(128 - r[o + 1]);
            s.RX = Axis(r[o + 2] - 128);
            s.RY = Axis(128 - r[o + 3]);
            return true;
        }

        static short Axis(int delta)
        {
            if (Math.Abs(delta) <= 1) return 0;
            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, delta * 258));
        }

        public static bool IsActive(PadSnapshot s)
        {
            return s.Buttons != 0 || s.LT > 64 || s.RT > 64 ||
                   Math.Abs((int)s.LX) > 16000 || Math.Abs((int)s.LY) > 16000 ||
                   Math.Abs((int)s.RX) > 16000 || Math.Abs((int)s.RY) > 16000;
        }
    }
}
