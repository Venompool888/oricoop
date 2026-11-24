using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using OriCoop.Net;

// UdpTransport.Receive is called once per peer per frame and almost always finds nothing. It must say so without
// throwing (and allocating) a SocketException each time, and still deliver every datagram that is there.
static class UdpPollTests
{
    static int failures;

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    // GC.GetAllocatedBytesForCurrentThread exists on Mono and .NET Core, not on .NET Framework 3.5/4.x.
    static readonly MethodInfo AllocatedBytes = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);

    static int WaitReceive(UdpTransport transport, byte[] buffer, out EndPoint from)
    {
        for (int i = 0; i < 200; i++)
        {
            int length = transport.Receive(buffer, out from);
            if (length > 0) return length;
            Thread.Sleep(5);
        }
        from = null;
        return 0;
    }

    static void EmptyReceive()
    {
        using (UdpTransport transport = new UdpTransport(0))
        {
            byte[] buffer = new byte[65536]; EndPoint from;
            for (int i = 0; i < 50; i++)
            {
                Check(transport.Receive(buffer, out from) == 0, "an empty socket returned data");
                Check(from == null, "an empty receive named a sender");
            }
        }
    }

    static void EmptyReceiveAllocatesNothing()
    {
        if (AllocatedBytes == null) { Console.WriteLine("  (no allocation counter on this runtime: only the result is checked)"); return; }
        using (UdpTransport transport = new UdpTransport(0))
        {
            byte[] buffer = new byte[65536]; EndPoint from;
            for (int i = 0; i < 100; i++) transport.Receive(buffer, out from);   // warm up
            long before = (long)AllocatedBytes.Invoke(null, null);
            for (int i = 0; i < 1000; i++) transport.Receive(buffer, out from);
            long perCall = ((long)AllocatedBytes.Invoke(null, null) - before) / 1000;
            Check(perCall < 32, "an empty receive allocated " + perCall + " bytes per call");
        }
    }

    static void DatagramsArrive()
    {
        using (UdpTransport transport = new UdpTransport(0))
        using (Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int senderPort = ((IPEndPoint)sender.LocalEndPoint).Port;
            EndPoint to = new IPEndPoint(IPAddress.Loopback, transport.LocalPort);
            for (int i = 0; i < 5; i++) sender.SendTo(new byte[] { (byte)i, 7, 7 }, to);
            byte[] buffer = new byte[65536];
            for (int i = 0; i < 5; i++)
            {
                EndPoint from;
                int length = WaitReceive(transport, buffer, out from);
                Check(length == 3 && buffer[0] == i, "datagram " + i + " missing or out of order (length " + length + ")");
                Check(from is IPEndPoint && ((IPEndPoint)from).Port == senderPort, "wrong sender endpoint " + from);
            }
            EndPoint none;
            Check(transport.Receive(buffer, out none) == 0 && none == null, "data after the last datagram");
        }
    }

    // An empty datagram reads as "nothing this time" (as before); the one behind it still comes on the next call.
    static void EmptyDatagramDoesNotBlock()
    {
        using (UdpTransport transport = new UdpTransport(0))
        using (Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            EndPoint to = new IPEndPoint(IPAddress.Loopback, transport.LocalPort);
            sender.SendTo(new byte[0], to);
            sender.SendTo(new byte[] { 42 }, to);
            Thread.Sleep(50);
            byte[] buffer = new byte[65536]; EndPoint from; int length = 0;
            for (int i = 0; i < 5 && length == 0; i++) length = transport.Receive(buffer, out from);
            Check(length == 1 && buffer[0] == 42, "the datagram behind an empty one never arrived");
        }
    }

    static int Main()
    {
        Run("an empty socket reports no datagram", EmptyReceive);
        Run("an empty receive allocates nothing (no SocketException per poll)", EmptyReceiveAllocatesNothing);
        Run("datagrams arrive in order with their sender", DatagramsArrive);
        Run("an empty datagram does not hold back the next one", EmptyDatagramDoesNotBlock);
        return failures == 0 ? 0 : 1;
    }
}
