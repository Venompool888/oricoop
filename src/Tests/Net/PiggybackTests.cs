using System;
using System.Net;
using OriCoop.Net;

static partial class NetTests
{
    // Packets the host puts on the wire while reading a guest's reliable message and answering it in the same frame.
    static int HostPacketsForReply(bool deferFlush, out NetTests.PeerRig rig)
    {
        PeerRig r = new PeerRig(); rig = r;
        r.Connect(); r.Pump(200);
        int hostPackets = 0;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            if (from.Equals(r.HostTransport.Endpoint)) hostPackets++;
            return false;
        };
        byte[] ask = Payload(40, 3), answer = Payload(60, 9);
        Reliable(r.Guest, ask); r.Guest.Flush(); r.Clock.Advance(1);
        hostPackets = 0;
        if (deferFlush) r.Host.Poll(false); else r.Host.Poll();
        ExpectMessage(r.Host, ask, true);
        Reliable(r.Host, answer);
        r.Host.Flush();
        int count = hostPackets;
        r.Network.Drop = null;
        for (int i = 0; i < 5; i++) { r.Guest.Poll(); r.Host.Poll(); r.Clock.Advance(10); }
        ExpectMessage(r.Guest, answer, true);
        ExpectEmpty(r.Guest); ExpectEmpty(r.Host);
        return count;
    }

    static void AckRidesWithReply()
    {
        PeerRig plain, deferred;
        int before = HostPacketsForReply(false, out plain);
        int after = HostPacketsForReply(true, out deferred);
        Check(before == 2, "Poll() should still ack at once (got " + before + " packets)");
        Check(after == 1, "Poll(false)+Flush should send ack and reply in one packet (got " + after + ")");
        Check(deferred.Guest.Stats.ReliableResends == 0, "guest resent although the combined packet acked it");
    }

    static void PollWithoutFlushSendsNothing()
    {
        PeerRig r = new PeerRig(); r.Connect(); r.Pump(200);
        Reliable(r.Guest, Payload(20, 1)); r.Guest.Flush(); r.Clock.Advance(1);
        int hostPackets = 0;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            if (from.Equals(r.HostTransport.Endpoint)) hostPackets++;
            return false;
        };
        r.Clock.Advance(2000);   // past the keepalive too
        r.Host.Poll(false);
        Check(hostPackets == 0, "Poll(false) sent " + hostPackets + " packet(s)");
        Check(r.Host.State == PeerState.Connected, "Poll(false) dropped the session");
        r.Host.Flush();
        Check(hostPackets == 1, "Flush after Poll(false) should send the pending ack once (got " + hostPackets + ")");
    }

    static void DeferredPollKeepsReliableFlow()
    {
        // A whole session where both sides read with Poll(false) and flush after answering: everything arrives once.
        PeerRig r = new PeerRig(); r.Connect();
        r.Network.LossPercent = 10; r.Network.JitterMs = 15;
        int got = 0; byte[] msg; bool reliable;
        for (int i = 0; i < 300; i++)
        {
            if (i < 100) Reliable(r.Guest, Payload(30 + i % 50, i));
            r.Host.Poll(false); r.Guest.Poll(false);
            while (r.Host.TryReceive(out msg, out reliable)) { got++; Reliable(r.Host, msg); }
            while (r.Guest.TryReceive(out msg, out reliable)) { }
            r.Host.Flush(); r.Guest.Flush();
            r.Clock.Advance(16);
        }
        for (int i = 0; i < 300 && got < 100; i++)
        {
            r.Host.Poll(false); r.Guest.Poll(false);
            while (r.Host.TryReceive(out msg, out reliable)) got++;
            r.Host.Flush(); r.Guest.Flush(); r.Clock.Advance(16);
        }
        Check(got == 100, "host got " + got + " of 100 reliable messages");
        Check(r.Host.State == PeerState.Connected && r.Guest.State == PeerState.Connected, "session dropped");
    }
}
