using System;
using System.Net;
using System.Security.Cryptography;

namespace OriCoop.Net
{
    // Optional shared key for direct LAN games. Authenticate before the hub creates a route for a join.
    internal sealed class PairedLanTransport : IDatagramTransport
    {
        const int TagSize = 32;
        const int MaxDatagram = 1200;
        readonly IDatagramTransport socket;
        readonly HMACSHA256 mac;
        readonly byte[] outgoing = new byte[MaxDatagram + TagSize];
        readonly byte[] incoming = new byte[MaxDatagram + TagSize + 1];

        internal static IDatagramTransport Open(int port, string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new UdpTransport(port);
            if (hex.Length != 32) throw new ArgumentException("LAN pairing key must contain 32 hex digits");
            byte[] key = new byte[16];
            for (int i = 0; i < key.Length; i++)
                if (!byte.TryParse(hex.Substring(2 * i, 2), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out key[i]))
                    throw new ArgumentException("LAN pairing key must contain 32 hex digits");
            return new PairedLanTransport(new UdpTransport(port), key);
        }

        internal PairedLanTransport(IDatagramTransport socket, byte[] key)
        {
            this.socket = socket;
            mac = new HMACSHA256(key);
        }

        public int LocalPort { get { return socket.LocalPort; } }

        public void SendTo(byte[] data, int offset, int length, EndPoint to)
        {
            if (length <= 0 || length > MaxDatagram) throw new ArgumentOutOfRangeException("length");
            Buffer.BlockCopy(data, offset, outgoing, 0, length);
            Buffer.BlockCopy(mac.ComputeHash(outgoing, 0, length), 0, outgoing, length, TagSize);
            socket.SendTo(outgoing, 0, length + TagSize, to);
        }

        public int Receive(byte[] buffer, out EndPoint from)
        {
            // Limit discarded datagrams per poll so a busy socket cannot hold the game loop.
            for (int attempt = 0; attempt < 32; attempt++)
            {
                int received = socket.Receive(incoming, out from);
                if (received <= 0) return 0;
                int length = received - TagSize;
                if (length <= 0 || length > MaxDatagram || length > buffer.Length) continue;
                byte[] tag = mac.ComputeHash(incoming, 0, length);
                int different = 0;
                for (int i = 0; i < tag.Length; i++) different |= tag[i] ^ incoming[length + i];
                if (different != 0) continue;
                Buffer.BlockCopy(incoming, 0, buffer, 0, length);
                return length;
            }
            from = null;
            return 0;
        }

        public void Dispose() { mac.Clear(); socket.Dispose(); }
    }
}
