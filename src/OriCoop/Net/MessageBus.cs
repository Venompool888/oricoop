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
        // T-149: connection seat -> the seat that device stamped before the host renumbered its primary.
        readonly Dictionary<int, int> renumbered = new Dictionary<int, int>();

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

        // T-149: the host moved a connected device's primary seat (SeatTable.Add gives it the lowest free seat). The
        // device keeps stamping its old seat until our seat table reaches it, so for that one connection the old
        // Sender is accepted and re-stamped (handlers and forwarding see the new seat) until its first reliable
        // message under the new seat. Reliable delivery is ordered, so nothing it sent earlier is still on the way.
        public void SeatRenumbered(int oldSeat, int newSeat)
        {
            if (!context.IsHost || context.FactorySession || oldSeat == newSeat) return;
            int original;
            if (renumbered.TryGetValue(oldSeat, out original)) { renumbered.Remove(oldSeat); oldSeat = original; }
            renumbered[newSeat] = oldSeat;
        }
        public void ClearRenumbered() { renumbered.Clear(); }

        byte[] Restamp(byte[] message, bool reliable, int sourceSeat)
        {
            int oldSeat;
            if (sourceSeat == 0 || !renumbered.TryGetValue(sourceSeat, out oldSeat)) return message;
            if (message[1] == sourceSeat) { if (reliable) renumbered.Remove(sourceSeat); return message; }
            if (message[1] != oldSeat) return message;
            byte[] copy = (byte[])message.Clone();
            copy[1] = (byte)sourceSeat;
            return copy;
        }

        public void ForgetSeat(int seat)
        {
            foreach (int key in new List<int>(renumbered.Keys)) if (key == seat || renumbered[key] == seat) renumbered.Remove(key);
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
            if (message.Length < 2) return;
            if (renumbered.Count != 0) message = Restamp(message, reliable, sourceSeat);
            if (Validate != null && !Validate(message, sourceSeat)) return;
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
                    // T-213: a seat is introduced by a reliable message (its Hello, or one that overtook the Hello). An
                    // unreliable one can be a straggler of a seat that already left: the reliable "left" notice overtakes
                    // that seat's last frames packet. An entry made for it never said hello and never leaves, so when the
                    // last real teammate left, the session did not end.
                    if (!reliable) return;
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
