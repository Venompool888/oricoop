namespace OriCoop.Ui
{
    // The HUD's connection signal (T-096: pure, so the boundaries are unit-tested in src\Tests\Ui instead of in the game).
    // Bars: 0 when not connected or silent; 4 under 80 ms and 2 % loss, 3 under 150 ms and 5 %, 2 under 300 ms and 10 %,
    // else 1. Colour tier separates high latencies the bars cannot: 0 cyan below 250 ms, 1 amber 250-500 ms,
    // 2 red from 500 ms or while silent.
    public static class SignalLevels
    {
        public static int Bars(bool connected, bool lost, float rtt, float loss)
        {
            if (!connected || lost) return 0;
            if (rtt < 80f && loss < 2f) return 4;
            if (rtt < 150f && loss < 5f) return 3;
            if (rtt < 300f && loss < 10f) return 2;
            return 1;
        }

        public static int ColorTier(bool lost, float rtt)
        {
            if (lost || rtt >= 500f) return 2;
            if (rtt >= 250f) return 1;
            return 0;
        }
    }
}
