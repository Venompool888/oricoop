using System;
using System.Collections.Generic;

namespace OriCoop.World
{
    // What a PC does with a partner's world blob (WorldSync message 12). T-141: with 3-4 players over P2P / relay the
    // guests only talk to the host, so when the host ACCEPTS a guest's blob it must pass the accepted blob on to the other
    // guests itself (message 12 is not in NetPlay's forward list on purpose: the host may reject the proposal). It never
    // goes back to the proposer: an echo could arrive after the proposer's next change and revert it there. In C-server
    // mode the server already hands every message to every seat, so the host sends nothing extra (no double apply).
    public sealed class RelayPlan
    {
        public bool Apply;            // store and apply the incoming blob on this PC
        public bool SendOwnToAll;     // host only: its own recent edit wins, broadcast the host's blob instead (proposer too)
        public int[] ForwardTo = new int[0];   // host only: device seats that get the accepted blob (never the proposer)
    }

    public static class WorldRelay
    {
        // The host's own edit of the same object less than this long ago beats a guest's proposal (WorldSync's s_changed).
        public const float LocalWinsSeconds = 0.5f;
        public const float Never = float.NegativeInfinity;

        // isHost: this PC decides. serverForwards: C-server session (the server already relays every seat's message).
        // sender: the proposer's device seat. remoteDevices: the device seats this PC is connected to (primaries only).
        // localChangedAt: when this PC last changed the object itself (Never if not known).
        public static RelayPlan OnRemoteBlob(bool isHost, bool serverForwards, int sender, int[] remoteDevices, float now, float localChangedAt)
        {
            RelayPlan plan = new RelayPlan();
            if (!isHost) { plan.Apply = true; return plan; }
            if (now - localChangedAt < LocalWinsSeconds) { plan.SendOwnToAll = true; return plan; }
            plan.Apply = true;
            if (serverForwards || remoteDevices == null) return plan;
            List<int> targets = new List<int>();
            foreach (int seat in remoteDevices)
                if (seat != sender && !targets.Contains(seat)) targets.Add(seat);
            plan.ForwardTo = targets.ToArray();
            return plan;
        }
    }
}
