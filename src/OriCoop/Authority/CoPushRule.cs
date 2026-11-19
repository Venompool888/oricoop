namespace OriCoop.Authority
{
    // T-049: two players pushing / pulling the same stone the same way move it faster.
    // Directions: -1 left, +1 right, 0 not pushing. remoteAge: seconds since the partner last reported its push.
    internal static class CoPushRule
    {
        public const float Boost = 1.5f, Fresh = 0.3f;

        public static float Multiplier(int localDir, int remoteDir, float remoteAge)
        {
            return localDir != 0 && localDir == remoteDir && remoteAge >= 0f && remoteAge <= Fresh ? Boost : 1f;
        }

        // T-115: 3-4 players keep one report per remote seat. Boosted when any fresh remote push goes our way, so a third
        // player pushing the other way no longer flips the single stored direction (and the boost) every report.
        public static float Multiplier(int localDir, System.Collections.Generic.IList<int> remoteDirs, System.Collections.Generic.IList<float> remoteAges)
        {
            for (int i = 0; i < remoteDirs.Count && i < remoteAges.Count; i++)
                if (Multiplier(localDir, remoteDirs[i], remoteAges[i]) > 1f) return Boost;
            return 1f;
        }
    }
}
