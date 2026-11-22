namespace OriCoop.Story
{
    // T-175: what a mirror's cleanup (StorySync.CleanupNative) puts back for a takeover flag: letterbox on, UI hidden,
    // a character animation playing, a movement or input lock. Cleanup should undo only what the mirror itself added.
    // The flag captured when the mirror began is the original only if a story of this PC was running then and owned
    // it. Otherwise it was left over from a cutscene the mirror took over (after Kuro the host still had its own
    // spirit-tree letterbox and animation when it began mirroring the guest's 03), and the original is plain gameplay
    // (flag off). If a story of this PC started during the mirror, that story owns the flag now and cleanup leaves it.
    public static class MirrorBaseline
    {
        // null = leave the flag as it is now; otherwise the value cleanup puts back.
        public static bool? Restore(bool captured, bool ownStoryAtCapture, bool ownStoryNow)
        {
            if (ownStoryAtCapture) return captured;
            if (ownStoryNow) return null;
            return false;
        }
    }
}
