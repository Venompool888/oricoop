namespace OriCoop.Rules
{
    // T-041 (#15) death and revive rules that do not depend on Unity. See docs\tickets\T-041.md.
    // Escape levels (mash Y, a random teammate) belong to the escape ticket, not here.
    public static class SoulLinkRules
    {
        public const float CellHealth = 4f;          // one health cell
        public const float SelfHoldSeconds = 2f;     // hold Y / Q to revive at the latest soul link

        // Death save (wheelchair): the creator keeps half its current health, but never drops below one cell
        // (or below what it has, if that is less than a cell). Energy is not touched.
        public static float DeathSaveHealth(float health)
        {
            float half = health / 2f;
            float floor = health < CellHealth ? health : CellHealth;
            return half > floor ? half : floor;
        }

        // A soul link (the room's new save point) was just made: every player downed right now may later revive at it
        // with the creator's health after creating it.
        public static void NoteLink<T>(DownedRoster<T> roster, float creatorHealth) where T : DownedState
        {
            foreach (T d in roster.Seats) if (d != null) d.LinkHealth = creatorHealth;
        }

        // Health for "revive at the latest soul link": the creator's health if that link was made while this player
        // was down, otherwise full.
        public static float SelfReviveHealth(DownedState d, float maxHealth)
        {
            if (d.LinkHealth <= 0f) return maxHealth;
            float h = d.LinkHealth < maxHealth ? d.LinkHealth : maxHealth;
            return h < 1f ? 1f : h;
        }

        // Hold Y / Q: true once held for SelfHoldSeconds. Releasing starts over. Not counted while blocked
        // (loading, cutscene, pause).
        public static bool HoldSelf(DownedState d, bool held, bool blocked, float dt)
        {
            if (!held || blocked) { d.SelfHold = 0f; return false; }
            d.SelfHold += dt;
            return d.SelfHold >= SelfHoldSeconds;
        }

        // Starting the self revive cancels a teammate's revive in progress; the rescuer keeps what it lost.
        public static void CancelRescue(DownedState d)
        {
            d.Progress = 0f; d.Given = 0f; d.Holding = false; d.NeedRelease = false; d.Rescuer = 0;
        }
    }
}
