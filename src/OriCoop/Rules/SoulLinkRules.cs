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

    // T-202 (#285): the room's save point, i.e. where holding Y / Q brings a downed player back. A downed player must
    // always have one, with or without a teammate and with or without a soul link made after it went down.
    // - A room save (anyone's soul link, spirit well, skill purchase or story save that became the room's save; the
    //   checkpoint a wipe restored) is the save point until the next room save. That is T-041 and does not change.
    // - Until the room has had one (the host loaded or started the game before its teammates were there), the save
    //   point is the save authority's latest checkpoint: where a team wipe would bring everyone back. It follows that
    //   PC's newer checkpoints; the first room save replaces it, and checkpoints no longer move it after that.
    // Only the save authority decides (offline: this PC; online: the host). Guests are told (msg 69 RoomLink).
    public sealed class SavePointState
    {
        public bool Have;              // a save point is known on this PC
        public bool FromCheckpoint;    // it is the authority's latest checkpoint: the room has had no save yet
        public ReviveWire.Link Point;
        bool m_haveCheckpoint, m_checkpointUsed;
        ReviveWire.Link m_checkpoint;

        public bool HaveCheckpoint { get { return m_haveCheckpoint; } }
        public ReviveWire.Link Checkpoint { get { return m_checkpoint; } }

        // This PC made or restored a checkpoint (any kind, with or without a save): who stands where, with what health.
        public void NoteCheckpoint(ReviveWire.Link at) { m_checkpoint = at; m_haveCheckpoint = true; m_checkpointUsed = false; }

        // The game went back to the title: the next game's checkpoints are other places.
        public void ForgetCheckpoint() { m_haveCheckpoint = false; m_checkpointUsed = false; }

        // A room save, or the host's word about the room's save point.
        public void NoteSave(ReviveWire.Link at) { Point = at; Have = true; FromCheckpoint = false; }

        // Every frame on the save authority. true = the save point just became this PC's latest checkpoint (tell the
        // others). Health 0: it was not made while anyone was down, so coming back there gives full health.
        public bool Resolve(bool authority)
        {
            if (!authority || !m_haveCheckpoint || m_checkpointUsed || Have && !FromCheckpoint) return false;
            Point = m_checkpoint; Point.Health = 0f;
            Have = true; FromCheckpoint = true; m_checkpointUsed = true;
            return true;
        }

        // The session is over: the room's save point is forgotten, this PC's own checkpoint is not.
        public void SessionEnded() { Have = false; FromCheckpoint = false; m_checkpointUsed = false; }
    }
}
