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
    }
}
