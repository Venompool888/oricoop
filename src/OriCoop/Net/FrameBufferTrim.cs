using System.Collections.Generic;

namespace OriCoop.Net
{
    // T-150: a partner's input frames keep arriving (~60/s) while this PC cannot play them (title screen,
    // loading, no Sein, so no puppet). Keep only the newest few so the buffer stays bounded and the puppet
    // starts near the live stream instead of catching up on minutes of frames in one step.
    public static class FrameBufferTrim
    {
        // More than the deepest jitter buffer (12) plus the catch-up slack (6) in NetPlay.PlayRemote.
        public const int IdleKeep = 32;

        // Drops frames more than keep-1 ticks older than newestTick; returns the play tick to resume from.
        // Signed tick distance, so a stream near uint.MaxValue that wraps is handled like any other.
        public static uint TrimIdle<T>(SortedDictionary<uint, T> buffer, uint newestTick, uint playTick, int keep)
        {
            if (buffer.Count > keep)
            {
                List<uint> stale = null;
                foreach (uint tick in buffer.Keys)
                    if ((int)(newestTick - tick) >= keep) { if (stale == null) stale = new List<uint>(); stale.Add(tick); }
                if (stale != null) foreach (uint tick in stale) buffer.Remove(tick);
            }
            if ((int)(newestTick - playTick) >= keep) playTick = newestTick - (uint)(keep - 1);
            return playTick;
        }

        // T-213 (4): what to do with an arriving frame, given the stream's play position. A frame behind the play
        // position is normally a late or redundant copy and is dropped; those are at most about 36 ticks back (4
        // frames per packet, and the connection drops packets more than 32 behind). A frame more than RestartGap ticks
        // (about 2 s) behind is no copy: the seat's stream started over at a lower tick. That happens when a new player
        // takes a seat and a straggler of the previous player (a higher tick) had set the play position: without a
        // restart every frame of the new player was dropped until its ticks caught up, minutes later.
        // The gap is small on purpose (review of T-213): with 1024, a previous player that had sent under 1024 frames
        // left the new one frozen for up to 17 s. What is left: under 128 frames, up to 2 s.
        public enum Admit { Take, Drop, Restart }
        public const int RestartGap = 128;
        public static bool MutateNoRestart;   // test only: ORICOOP_MUTATE_NO_STREAM_RESTART
        public static Admit Judge(bool havePlay, uint playTick, uint tick)
        {
            if (!havePlay) return Admit.Take;
            int behind = (int)(playTick - tick);   // signed distance, like TrimIdle
            if (behind <= 0) return Admit.Take;
            return behind > RestartGap && !MutateNoRestart ? Admit.Restart : Admit.Drop;
        }
    }
}
