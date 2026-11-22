using System;

namespace OriCoop.Pads
{
    public static class PadDetector
    {
        const long PairWindowTicks = 250 * TimeSpan.TicksPerMillisecond;
        const long XboxSettleTicks = 120 * TimeSpan.TicksPerMillisecond;
        static long s_lastPsTicks, s_pendingXboxTicks;
        static volatile PadFamily s_last;

        public static PadFamily Last { get { return s_last; } }
        public static bool PlayStationConnected { get { return PsHid.Count > 0; } }

        internal static void Update(long now, long lastPsTicks, bool xinputActive)
        {
            s_lastPsTicks = Math.Max(s_lastPsTicks, lastPsTicks);
            if (s_lastPsTicks != 0 && now - s_lastPsTicks <= PairWindowTicks)
            {
                s_pendingXboxTicks = 0;
                s_last = PadFamily.PlayStation;
            }
            else if (xinputActive && s_pendingXboxTicks == 0)
                s_pendingXboxTicks = now;

            if (s_pendingXboxTicks != 0 && now - s_pendingXboxTicks >= XboxSettleTicks)
            {
                if (s_lastPsTicks == 0 || Math.Abs(s_lastPsTicks - s_pendingXboxTicks) > PairWindowTicks)
                    s_last = PadFamily.Xbox;
                s_pendingXboxTicks = 0;
            }
        }
    }
}
