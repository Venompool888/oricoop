namespace OriCoop.Saves
{
    // T-126: in server mode the C-server can hand the host (legacy arbiter) role to a GUEST mid-session
    // (CServerLink.ArbiterChanged sets NetPlay.HostOverride). That guest is playing the co-op world, but the game's
    // current slot is still its own single-player slot (joined in-game) or the title fallback (slot 0). Without a
    // promotion step every host-side save path would write the co-op world over that unrelated slot.
    // SaveSync.CheckPromotion asks Check() each tick and before every save; the first true moves saving to SaveSlot().
    public sealed class HostPromotion
    {
        bool m_joined, m_promoted;

        public bool Promoted { get { return m_promoted; } }

        // The guest finished loading the host's checkpoint this session (SaveSync.FinishJoin).
        public void Joined() { m_joined = true; }
        public void Reset() { m_joined = m_promoted = false; }

        // true exactly once: a guest that joined the host's world is now the host. A player that was host from the
        // start (never joined as a guest) is never "promoted".
        public bool Check(bool isHost)
        {
            if (!isHost || !m_joined || m_promoted) return false;
            m_promoted = true;
            return true;
        }

        // Slot the promoted host saves the co-op world into (-1 = none: saving stays off, nothing is overwritten).
        //   sessionSlot: the slot that holds the co-op copy kept from the old host's commits this session (-1 none).
        //   haveCommit:  a commit with save bytes arrived (the old host had a save file) but may not have been kept.
        //   firstEmpty:  first empty slot, never the slot that was being played (-1 none).
        // The co-op copy wins. A commit that could not be kept (all slots full) means there is no co-op slot and
        // every slot holds someone's save: block. No commit at all (the old host never saved yet): a fresh empty slot.
        public static int SaveSlot(int sessionSlot, bool haveCommit, int firstEmpty)
        {
            if (sessionSlot >= 0) return sessionSlot;
            if (haveCommit) return -1;
            return firstEmpty;
        }
    }
}
