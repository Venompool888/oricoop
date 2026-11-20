namespace OriCoop.Net
{
    // The online protocol version (sent in Hello, shown on the HUD when the two ends differ). Unity-free so the unit tests
    // can pin each wire format to it (T-140): change any message's bytes -> bump this and the pinned sizes in the tests.
    // net4: the intro snapshot (msg 54) carries the late-seat byte (T-105); cutscene animation keys are name|hash|count (T-121).
    internal static class ProtocolVersion
    {
        public const string Mod = "0.2.0-net4";
    }
}
