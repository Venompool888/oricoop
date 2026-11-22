namespace OriCoop.Net
{
    // The online protocol version (sent in Hello, shown on the HUD when the two ends differ). Unity-free so the unit tests
    // can pin each wire format to it (T-140): change any message's bytes -> bump this and the pinned sizes in the tests.
    // net4: the intro snapshot (msg 54) carries the late-seat byte (T-105); cutscene animation keys are name|hash|count (T-121).
    // net5 (T-168): GameController / GameWorld sync item by item (arbiter event kind 0x57); the world snapshot end marker
    // (msg 12 kind 2) carries a u32 watermark. World sync refuses to run with a PC on another version.
    // net6: SaveOffer (msg 87, guest -> host) carries a guest's save point to the room's save authority (T-043).
    // net7: SeatSkills (msg 88) carries the per-seat skill record (T-043).
    // net8: RoomLink (msg 69) carries the room's latest save point and the saver's health (T-041).
    // net9: EscapeDone (msg 92): one player reached an escape's end, the team is brought there (T-169).
    // net10: LobbyState / LobbyStart (msgs 94 / 95): the C-server room page's ready, ping, room save and start (T-171).
    internal static class ProtocolVersion
    {
        public const string Mod = "0.2.0-net10";
    }
}
