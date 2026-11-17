using System;
using System.Collections.Generic;
using System.Net;

namespace OriCoop.Net
{
    sealed class RoutedTransport : IDatagramTransport
    {
        readonly IDatagramTransport socket;
        readonly Queue<Datagram> incoming = new Queue<Datagram>();
        sealed class Datagram { public byte[] Bytes; public EndPoint From; }

        public RoutedTransport(IDatagramTransport socket) { this.socket = socket; }
        public int LocalPort { get { return socket.LocalPort; } }
        public void SendTo(byte[] data, int offset, int length, EndPoint to) { socket.SendTo(data, offset, length, to); }
        internal void Enqueue(byte[] data, int length, EndPoint from)
        {
            byte[] bytes = new byte[length]; Buffer.BlockCopy(data, 0, bytes, 0, length);
            incoming.Enqueue(new Datagram { Bytes = bytes, From = from });
        }
        public int Receive(byte[] buffer, out EndPoint from)
        {
            if (incoming.Count == 0) { from = null; return 0; }
            Datagram item = incoming.Dequeue(); from = item.From;
            Buffer.BlockCopy(item.Bytes, 0, buffer, 0, item.Bytes.Length); return item.Bytes.Length;
        }
        public void Dispose() { incoming.Clear(); }
    }
}
