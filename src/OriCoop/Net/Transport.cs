using System;
using System.Net;
using System.Net.Sockets;

namespace OriCoop.Net
{
    public interface IDatagramTransport : IDisposable
    {
        void SendTo(byte[] data, int offset, int length, EndPoint to);
        // Nonblocking; zero means that no datagram is available.
        int Receive(byte[] buffer, out EndPoint from);
        int LocalPort { get; }
    }

    public sealed class UdpTransport : IDatagramTransport
    {
        private readonly Socket socket;
        private readonly int localPort;

        public UdpTransport(int bindPort)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ReceiveBufferSize = 1024 * 1024;
                socket.SendBufferSize = 1024 * 1024;
                socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));
                socket.Blocking = false;
                localPort = ((IPEndPoint)socket.LocalEndPoint).Port;
                // Windows otherwise reports ICMP port-unreachable as a receive error.
                try { socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); }
                catch (Exception) { }
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        public int LocalPort { get { return localPort; } }

        public void SendTo(byte[] data, int offset, int length, EndPoint to)
        {
            socket.SendTo(data, offset, length, SocketFlags.None, to);
        }

        // ReceiveFrom replaces the endpoint it is given; this template itself is never changed.
        private static readonly EndPoint AnyEndPoint = new IPEndPoint(IPAddress.Any, 0);

        public int Receive(byte[] buffer, out EndPoint from)
        {
            from = null;
            try
            {
                // A nonblocking ReceiveFrom on an empty socket throws (would block). Every poll ends that way, so
                // ask first instead of paying for a SocketException (and an endpoint) once per peer per frame.
                if (!socket.Poll(0, SelectMode.SelectRead)) return 0;
                EndPoint sender = AnyEndPoint;
                int length = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref sender);
                from = sender;
                return length;
            }
            catch (SocketException) { from = null; return 0; }
        }

        public void Dispose() { socket.Close(); }
    }
}
