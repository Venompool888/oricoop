namespace OriCoop.Story
{
    // T-185: a scene trip (GoToSceneController: to the world map and back) completes some frames after it was asked
    // for: when the target scenes are enabled the game puts the character at the target scene's placeholder. For the
    // spirit-tree scene that placeholder is Kuro's drop spot (-152, -130). Only after that does the chain's next
    // sequence start natively (04, whose "01. Set Character Position" puts Ori back at the tree).
    // A mirror that started the partner's 04 inside that gap (the partner's PC was a little ahead) had 04's placement
    // overwritten by the placeholder, and its own string trigger for 04 was then consumed by the mirror's start, so
    // nothing placed Ori again: Ori stayed at the drop spot (T-183 run H-5, the host in T-185's first report).
    // Rule: while this PC's own trip is pending, a mirrored start waits (the normal 0.1 s resolve retry). Bounded: a
    // trip flag that never clears must not stop mirroring for good.
    public static class SceneTripRule
    {
        public const float MaxWait = 10f;
        // pending: the game's trip flag is set. seenFor: seconds since a resolve first saw it set (0 at first sight).
        public static bool Holds(bool pending, float seenFor) { return pending && seenFor >= 0f && seenFor <= MaxWait; }
    }
}
