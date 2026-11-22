using System;
using System.Collections.Generic;
using OriCoop.World;

// T-172: the Spirit Tree story (playtest 3). The host runs "01. reachSpiritTree", the guest mirrors it. Its last
// action is RunActionOnce(02): m_hasRun is save state, synced by WorldSync. The guest got there 1.3 s first, sent
// "already run", and the host's own 01 then skipped 02 (no Kuro cutscene, input locked until the 20 s repair).
// The model below is WorldSync's propose/apply loop (Capture -> changed vs known -> send; Apply -> live = known).
static class MirrorHoldTests
{
    sealed class Peer
    {
        public string Name;
        public bool Once, Chained, Known;          // RunActionOnce.m_hasRun, did it start 02, what the partners know
        public bool Mirroring;                      // this PC only mirrors the story
        public readonly MirrorHold Hold = new MirrorHold();
        public int Sent;
        public bool UseHold = true;                 // false = before T-172
        public Peer Other;
        // RunActionOnce.Perform (decomp RunActionOnce.cs): runs the action only while m_hasRun is false.
        public void RunOnce() { if (!Once) { Chained = true; Once = true; } }
        // WorldSync.Capture + SendBlobs for this one object.
        public void Capture()
        {
            if (Once == Known) { Hold.Hold("once", false); return; }
            if (UseHold && Hold.Hold("once", Mirroring)) return;
            Sent++; Known = Once; Other.Apply(Once);
        }
        // WorldSync.Apply: the partner's value becomes live and known here.
        public void Apply(bool value) { Once = value; Known = value; Hold.Applied("once"); }
    }

    static void Pair(bool useHold, out Peer host, out Peer guest)
    {
        host = new Peer { Name = "host", UseHold = useHold };
        guest = new Peer { Name = "guest", Mirroring = true, UseHold = useHold };
        host.Other = guest; guest.Other = host;
    }

    // The playtest order: the mirror reaches the action first, a capture tick runs on both, then the runner gets there.
    static void MirrorFirst(Peer host, Peer guest)
    {
        guest.RunOnce();
        guest.Capture(); host.Capture();
        host.RunOnce();
        host.Capture(); guest.Capture();
        guest.Mirroring = false;                    // the host's story ended: the mirror is over
        guest.Capture(); host.Capture();
    }

    public static void Register(Action<string, Action> run)
    {
        run("T-172 mirror reaches RunActionOnce first: the running host still starts the chained story", delegate {
            Peer host, guest; Pair(true, out host, out guest);
            MirrorFirst(host, guest);
            Check(host.Chained, "host skipped the chained sequence (02)");
            Check(guest.Chained, "guest did not start 02");
            Check(host.Once && guest.Once && host.Known && guest.Known, "both end with m_hasRun=true");
            Check(guest.Sent == 0, "guest proposed its mirror's state " + guest.Sent + "x");
            Check(host.Sent == 1, "host shared its own value " + host.Sent + "x");
            Check(guest.Hold.Count == 0 && guest.Hold.HeldTotal == 1, "held once, released by the host's value");
        });
        // The same model without MirrorHold must reproduce the playtest bug, or the test above proves nothing.
        run("T-172 without MirrorHold the host skips 02 (the playtest bug)", delegate {
            Peer host, guest; Pair(false, out host, out guest);
            MirrorFirst(host, guest);
            Check(!host.Chained, "model no longer reproduces the bug");
            Check(guest.Chained && guest.Sent == 1, "guest proposed its state");
        });
        run("T-172 a held change that still differs after the mirror is proposed", delegate {
            // The runner never got to the action (story cut short): the mirror's value is the only one left.
            Peer host, guest; Pair(true, out host, out guest);
            guest.RunOnce(); guest.Capture();
            Check(guest.Sent == 0 && guest.Hold.IsHeld("once"), "held while mirroring");
            guest.Mirroring = false; guest.Capture();
            Check(guest.Sent == 1 && host.Once && !guest.Hold.IsHeld("once"), "proposed once the mirror is over");
        });
        run("T-172 a change outside a mirrored story is proposed at once", delegate {
            Peer host, guest; Pair(true, out host, out guest);
            guest.Mirroring = false;
            guest.RunOnce(); guest.Capture();
            Check(guest.Sent == 1 && host.Once && guest.Hold.HeldTotal == 0, "sent " + guest.Sent);
        });
        run("T-172 roles swapped: guest runs the story, host mirrors it", delegate {
            Peer host, guest; Pair(true, out host, out guest);
            host.Mirroring = true; guest.Mirroring = false;
            host.RunOnce(); host.Capture(); guest.Capture();
            guest.RunOnce(); guest.Capture(); host.Capture();
            host.Mirroring = false; host.Capture();
            Check(host.Chained && guest.Chained, "both started 02");
            Check(host.Sent == 0 && guest.Sent == 1, "only the runner proposed (host " + host.Sent + ", guest " + guest.Sent + ")");
        });
    }

    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }
}
