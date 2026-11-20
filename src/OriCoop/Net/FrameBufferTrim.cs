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
    }
}
