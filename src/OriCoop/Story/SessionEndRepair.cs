namespace OriCoop.Story
{
    // T-212 (1): a mirrored story that had faded to black when the session ended (partner left or dropped) is stopped by
    // StorySync.Reset; its FadeFromBlack never runs and the screen stays black offline, where CutsceneGuard's online
    // "stuck black" repair no longer looks. For a while after the session the same repair keeps running: fully black
    // for Stuck seconds with nothing loading is faded back in.
    public static class SessionEndRepair
    {
        public const float Stuck = 4f;           // the online repair's "black for 4 s"
        public const float Window = 30f;         // how long after the last online frame the repair keeps looking

        // sinceOnline: seconds since the last frame this PC was in a session (negative: never was).
        public static bool Watching(float sinceOnline) { return sinceOnline >= 0f && sinceOnline < Window; }
        public static bool Due(float sinceOnline, float blackSeconds) { return Watching(sinceOnline) && blackSeconds >= Stuck; }

        // Review finding 9: only a black screen that was already there (fading to black or fully black) in the frame the
        // session ended can be the stopped mirror's. One that begins later is the game's own (a native cutscene of the
        // now single player) and is left alone, however long it lasts.
        public static bool Due(float sinceOnline, float blackSeconds, bool blackWhenSessionEnded)
        { return blackWhenSessionEnded && Due(sinceOnline, blackSeconds); }
    }
}
