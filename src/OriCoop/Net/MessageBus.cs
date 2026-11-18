using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Net
{
    public interface IMessagePeer
    {
        bool Connected { get; }
        double LastReceiveAgeMs { get; }
        void Send(byte[] message, int length, bool reliable);
    }

    public interface IMessageContext : ISessionContext
    {
        int LocalSeat { get; }
        int MaxSeats { get; }
        bool DisableForward { get; }
        IMessagePeer SinglePeer { get; }
        bool SendToOverride(int seat, byte type, bool reliable, Action<BinaryWriter> body);
        void AssignSeat(int seat);
        SessionMachine.Remote CreateRemote(int seat);
        void HandleCore(byte type, BinaryReader reader, byte[] message, SessionMachine.Remote remote);
    }

    public sealed class MessageBus
    {
        const byte MsgHello = 1, MsgStatus = 2, MsgFrames = 3, MsgSeat = 4, MsgLeft = 5, MsgTest = 99;
        readonly IMessageContext context;
        readonly SessionMachine session;
        readonly Action<byte, Exception> earlyError;
        readonly Dictionary<byte, Action<BinaryReader>> handlers = new Dictionary<byte, Action<BinaryReader>>();
        readonly HashSet<byte> forward = new HashSet<byte>();
        readonly Dictionary<byte, Queue<byte[]>> early = new Dictionary<byte, Queue<byte[]>>();

        public MessageBus(IMessageContext context, SessionMachine session, Action<byte, Exception> earlyError)
        {
            this.context = context; this.session = session; this.earlyError = earlyError;
        }

        public int Sender { get; internal set; }
        // Validated before any handler or forwarding (multi-seat bodies must not bypass ownership checks).
        public Func<byte[], int, bool> Validate;
        public void Forward(params byte[] types) { foreach (byte type in types) forward.Add(type); }

        public void On(byte type, Action<BinaryReader> handler)
        {
            handlers[type] = handler;
            Queue<byte[]> q;
            if (early.TryGetValue(type, out q))
            {
                early.Remove(type);
                while (q.Count > 0)
                {
                    byte[] m = q.Dequeue();
                    int previous = Sender;
                    try { BinaryReader r = new BinaryReader(new MemoryStream(m)); r.ReadByte(); Sender = r.ReadByte(); handler(r); }
                    catch (Exception e) { earlyError(type, e); }
                    finally { Sender = previous; }
                }
            }
        }

        void KeepEarly(byte type, byte[] message)
        {
            Queue<byte[]> q;
            if (!early.TryGetValue(type, out q)) { q = new Queue<byte[]>(); early[type] = q; }
            if (q.Count >= 64) q.Dequeue();
            q.Enqueue(message);
        }

        public void ClearEarly() { early.Clear(); }
        public void ForgetSeat(int seat)
        {
            foreach (Queue<byte[]> queue in early.Values)
            {
                int count = queue.Count;
                while (count-- > 0) { byte[] m = queue.Dequeue(); if (m[1] != seat) queue.Enqueue(m); }
            }
        }

        byte[] Message(byte type, Action<BinaryWriter> body)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                w.Write(type); w.Write((byte)context.LocalSeat); body(w); w.Flush();
                return ms.ToArray();
            }
        }

        public void Send(byte type, bool reliable, Action<BinaryWriter> body)
        {
            if (!context.Connected || session.WaitingForSeat) return;
            byte[] message = Message(type, body);
            Broadcast(message, message.Length, reliable);
        }

        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            if (context.SendToOverride(seat, type, reliable, body)) return;
            SessionMachine.Remote remote;
            if (!context.IsHost || !session.Remotes.TryGetValue(seat, out remote) || remote.Peer == null) return;
            byte[] message = Message(type, body);
            remote.Peer.Send(message, message.Length, reliable);
        }

        public void Broadcast(byte[] message, int length, bool reliable)
        {
            if (!context.HasHub) { if (context.SinglePeer != null) context.SinglePeer.Send(message, length, reliable); return; }
            foreach (SessionMachine.Remote remote in session.Remotes.Values)
                if (remote.Peer != null) remote.Peer.Send(message, length, reliable);
        }

        public void Rebroadcast(BinaryReader reader)
        {
            if (!context.IsHost || context.FactorySession) return;
            byte[] message = ((MemoryStream)reader.BaseStream).ToArray();
            Broadcast(message, message.Length, true);
        }

        void ForwardOthers(SessionMachine.Remote source, byte[] message, bool reliable)
        {
            foreach (SessionMachine.Remote other in session.Remotes.Values)
                if (other != source && other.Peer != null) other.Peer.Send(message, message.Length, reliable);
        }

        public void Dispatch(byte[] message, bool reliable, int sourceSeat)
        {
            if (message.Length < 2 || Validate != null && !Validate(message, sourceSeat)) return;
            BinaryReader r = new BinaryReader(new MemoryStream(message));
            byte type = r.ReadByte();
            int previous = Sender;
            Sender = r.ReadByte();
            try
            {
                if (session.IsFactoryMessage(type))
                {
                    Action<BinaryReader> handler;
                    if (handlers.TryGetValue(type, out handler)) handler(r); else KeepEarly(type, message);
                    return;
                }
                if (type == MsgSeat)
                {
                    if (context.IsHost || context.UsesSteam || Sender != 1) return;
                    context.AssignSeat(r.ReadByte());
                    return;
                }
                if (session.WaitingForSeat) return;
                if (!context.FactorySession && sourceSeat != 0 && Sender != sourceSeat) return;
                if (type == MsgLeft)
                {
                    if (context.IsHost && !context.FactorySession || context.UsesSteam || Sender != 1) return;
                    session.RemoteLeft(r.ReadByte());
                    return;
                }
                if (Sender < 1 || Sender > context.MaxSeats || Sender == context.LocalSeat) return;
                SessionMachine.Remote remote;
                if (!session.Remotes.TryGetValue(Sender, out remote))
                {
                    if (context.IsHost && !context.FactorySession) return;
                    remote = context.CreateRemote(Sender);
                    session.Remotes.Add(Sender, remote);
                }
                session.Heard(remote);
                if (context.IsHost && forward.Contains(type) && !context.DisableForward) ForwardOthers(remote, message, reliable);
                if (type == MsgHello || type == MsgStatus) context.HandleCore(type, r, message, remote);
                else if (handlers.ContainsKey(type)) handlers[type](r);
                else if (type >= 10 && type != MsgTest) KeepEarly(type, message);
                else if (type == MsgTest || type == MsgFrames) context.HandleCore(type, r, message, remote);
                if (context.IsHost && (type == MsgHello || type == MsgStatus || type == MsgFrames)) ForwardOthers(remote, message, reliable);
            }
            finally { Sender = previous; }
        }
    }
}
