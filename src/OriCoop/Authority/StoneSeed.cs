using System;

namespace OriCoop.Authority
{
    internal sealed class StoneSeed
    {
        static readonly bool s_reseedUnpublished = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STONE_RESEED_UNPUBLISHED") == "1";
        IAuthority soloRoom;
        StateUpdate lastPublished;
        uint vacantEpoch;
        float lastX, lastY, lastAngle;

        public void Unloaded() { soloRoom = null; lastPublished = null; }

        public void LastPeerLeft(IAuthority room, string key, float x, float y, float angle)
        {
            soloRoom = room;
            vacantEpoch = room.EpochOf(key);
            // The roster and the release grant can be processed in either order.
            if (room.OwnerOf(key) != 0) vacantEpoch++;
            lastPublished = room.LatestState(key);
            lastX = x; lastY = y; lastAngle = angle;
        }

        public StateUpdate ForReseed(IAuthority room, string key, float x, float y, float angle)
        {
            StateUpdate latest = room.LatestState(key);
            if (!s_reseedUnpublished && room == soloRoom && latest == lastPublished &&
                (x != lastX || y != lastY || angle != lastAngle))
            {
                // The server keeps the same publication while the unowned stone moves locally. The returning
                // guest may arrive just before or after the host receives the first lease to publish that move.
                uint epoch = room.EpochOf(key);
                if ((epoch == vacantEpoch && room.OwnerOf(key) == 0) ||
                    (epoch == unchecked(vacantEpoch + 1) && room.Owns(key))) return null;
            }
            return latest;
        }
    }
}
