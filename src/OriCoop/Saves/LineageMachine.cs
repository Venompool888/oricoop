using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Seams;

namespace OriCoop.Saves
{
    public sealed class LineageOffer
    {
        public byte Version = 1;
        public string Coop = "", PlayerId = "", Name = "", Hash = "", Summary = "";
        // Local facts about the previous run, used by the host when comparing the replies.
        public string PreviousRound = "";
        public bool WasLast;
    }

    public sealed class LineageReply
    {
        public bool Found, FreeSlot;
        public int Slot;
        public string Hash = "", LastSync = "", Summary = "", PlayerId = "", Name = "";
        public string PreviousRound = "";
        public bool WasLast;
    }

    public sealed class LineageCommit
    {
        public string Coop = "", PlayerId = "", Name = "", SaveId = "";
        public string Round = "";
        public byte[] Bytes;
    }

    public static class LineageWire
    {
        public const byte Offer = 16, Reply = 17, Commit = 18, Request = 19, Upload = 23;
        public const byte UploadRequest = 1, CancelRequest = 2, ChoosingRequest = 3;

        // Append-only extensions. Older readers finish at the original message boundary and ignore the tail.
        static void WriteRound(BinaryWriter w, string round)
        {
            w.Write(new Guid(round).ToByteArray());
        }
        static string ReadRound(BinaryReader r, int size)
        {
            if (r.BaseStream.Length - r.BaseStream.Position != size) return "";
            Guid round = new Guid(r.ReadBytes(16));
            return round == Guid.Empty ? "" : round.ToString("N");
        }

        public static void WriteOffer(BinaryWriter w, LineageOffer value)
        {
            w.Write(value.Version); w.Write(value.Coop); w.Write(value.PlayerId); w.Write(value.Name); w.Write(value.Hash); w.Write(value.Summary);
        }
        public static LineageOffer ReadOffer(BinaryReader r)
        {
            LineageOffer value = new LineageOffer();
            value.Version = r.ReadByte(); value.Coop = r.ReadString(); value.PlayerId = r.ReadString(); value.Name = r.ReadString();
            value.Hash = r.ReadString(); value.Summary = r.ReadString(); return value;
        }
        public static void WriteReply(BinaryWriter w, LineageReply value)
        {
            w.Write(value.Found); w.Write(value.Slot); w.Write(value.Hash); w.Write(value.LastSync); w.Write(value.Summary);
            w.Write(value.PlayerId); w.Write(value.Name); w.Write(value.FreeSlot);
            if (!string.IsNullOrEmpty(value.PreviousRound)) { WriteRound(w, value.PreviousRound); w.Write(value.WasLast); }
        }
        public static LineageReply ReadReply(BinaryReader r)
        {
            LineageReply value = new LineageReply();
            value.Found = r.ReadBoolean(); value.Slot = r.ReadInt32(); value.Hash = r.ReadString(); value.LastSync = r.ReadString();
            value.Summary = r.ReadString(); value.PlayerId = r.ReadString(); value.Name = r.ReadString(); value.FreeSlot = r.ReadBoolean();
            value.PreviousRound = ReadRound(r, 17);
            if (value.PreviousRound != "") value.WasLast = r.ReadBoolean();
            return value;
        }
        public static void WriteCommit(BinaryWriter w, LineageCommit value)
        {
            w.Write(value.Coop); w.Write(value.PlayerId); w.Write(value.Name); w.Write(value.SaveId);
            w.Write(value.Bytes != null); if (value.Bytes != null) { w.Write(value.Bytes.Length); w.Write(value.Bytes); }
            if (!string.IsNullOrEmpty(value.Round)) WriteRound(w, value.Round);
        }
        public static LineageCommit ReadCommit(BinaryReader r)
        {
            LineageCommit value = new LineageCommit();
            value.Coop = r.ReadString(); value.PlayerId = r.ReadString(); value.Name = r.ReadString(); value.SaveId = r.ReadString();
            if (r.ReadBoolean())
            {
                int n = r.ReadInt32();
                if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) return null;
                value.Bytes = r.ReadBytes(n); if (value.Bytes.Length != n) return null;
            }
            value.Round = ReadRound(r, 16);
            return value;
        }
        public static void WriteRequest(BinaryWriter w, byte kind) { w.Write(kind); }
        public static byte ReadRequest(BinaryReader r) { return r.ReadByte(); }
        public static void WriteUpload(BinaryWriter w, byte[] bytes)
        {
            if (bytes == null) { w.Write(0); return; }
            w.Write(bytes.Length); w.Write(bytes);
        }
        public static byte[] ReadUpload(BinaryReader r, out int length)
        {
            length = r.ReadInt32();
            return length > 0 && length <= r.BaseStream.Length - r.BaseStream.Position ? r.ReadBytes(length) : new byte[0];
        }
    }

    public enum LineageStage { Idle, Offered, Choosing, Uploading, Adopting, Proceed, Cancelling, Done }

    public interface ILineageActions
    {
        string PlayerId { get; }
        string LocalName { get; }
        string HostHash { get; }
        LineageOffer PrepareOffer();
        void OfferSent(LineageOffer offer);
        void OfferFailed(Exception error);
        LineageReply PrepareReply(LineageOffer offer);
        void ReplySent(LineageOffer offer, LineageReply reply);
        void ReplyReceived(LineageReply reply);
        void NewReply(LineageReply reply);
        void SameReply();
        void BeginChoosing(LineageReply reply);
        void ChoosingSent();
        void HostChosen(bool diverged);
        void Commit();
        void CommitTo(int seat);
        void SetCancelOutcome();
        void Cancelled();
        bool ValidUpload(int length, byte[] bytes);
        void PrepareAdoption(byte[] bytes);
        void Adopt(byte[] bytes);
        void AdoptionFailed(Exception error);
        void AdoptionFinished();
        void CommitReceived(LineageCommit commit);
        byte[] ReadUpload();
        void UploadSent(byte[] bytes);
        void RequestChoosing();
        void RequestCancel();
        void ReplyTimeout();
        void UploadTimeout();
        void End();
        void Reset();
    }

    public sealed class LineageMachine
    {
        readonly INetBus m_net;
        readonly IClock m_clock;
        readonly ILineageActions m_actions;
        LineageStage m_stage;
        float m_stageAt;
        // T-128: with 3-4 players every guest present at the offer answers. The host waits for all of them (or the reply
        // timeout): one guest's diverged copy must reach the host's choice even if another guest answered "no copy" first.
        readonly HashSet<int> m_seats = new HashSet<int>();     // host: guest device seats in the session
        readonly HashSet<int> m_waiting = new HashSet<int>();   // host: seats whose reply to the current offer is missing
        LineageReply m_same, m_new;
        readonly SortedDictionary<int, LineageReply> m_answers = new SortedDictionary<int, LineageReply>();
        readonly List<int> m_forks = new List<int>();
        LineageOffer m_offer;
        public int ForkCount { get { return m_forks.Count; } }
        public int ForkSeat(int index) { return m_forks[index]; }
        public LineageReply Fork(int index) { return m_answers[m_forks[index]]; }
        // -1 means no usable evidence, 0 is the host, and 1..N are the guest choices in seat order.
        public int Suggested { get; private set; }
        int m_replies, m_choiceSeat = -1;                      // m_choiceSeat: the guest whose diverged copy is on offer

        public LineageMachine(INetBus net, IClock clock, ILineageActions actions)
        { m_net = net; m_clock = clock; m_actions = actions; }
        public LineageStage Stage { get { return m_stage; } }
        public float StageAt { get { return m_stageAt; } }
        void SetStage(LineageStage stage) { m_stage = stage; m_stageAt = m_clock.RealTime; }

        public void Init()
        {
            m_net.On(LineageWire.Offer, ReceiveOffer);
            m_net.On(LineageWire.Reply, ReceiveReply);
            m_net.On(LineageWire.Commit, ReceiveCommit);
            m_net.On(LineageWire.Request, ReceiveRequest);
            m_net.On(LineageWire.Upload, ReceiveUpload);
            m_net.SessionEnded += Reset;
            m_net.PlayerJoined += SeatJoined;
            m_net.PlayerLeft += SeatLeft;
        }

        // T-129: a seat that joins after the lineage was settled (3rd/4th player, server mode) never saw the offer or the
        // commit broadcast. Give it the co-op save now, before its checkpoint (same reliable channel, so it arrives
        // first): a guest joining from the title then keeps the copy in its linked/empty slot or is asked for one.
        // While the lineage is still open (Offered..Adopting) the commit broadcast at the decision reaches it anyway.
        // T-128: the host also tracks the guest seats, to wait for every reply to an offer.
        void SeatJoined(int seat)
        {
            if (!m_net.IsHost) return;
            if (!m_seats.Add(seat)) return;
            if (m_stage == LineageStage.Choosing) { Offer(); return; }
            if (m_stage == LineageStage.Offered)
            {
                m_waiting.Add(seat);
                m_net.SendTo(seat, LineageWire.Offer, true, delegate(BinaryWriter w) { LineageWire.WriteOffer(w, m_offer); });
            }
            if (m_stage == LineageStage.Proceed) m_actions.CommitTo(seat);
        }

        void Reset()
        {
            m_forks.Clear(); m_answers.Clear(); Suggested = -1;
            m_stage = LineageStage.Idle; m_seats.Clear(); m_waiting.Clear(); m_same = m_new = null; m_replies = 0; m_choiceSeat = -1;
            m_actions.Reset();
        }

        void SeatLeft(int seat)
        {
            m_seats.Remove(seat);
            bool hadReply = m_answers.Remove(seat);
            if ((m_stage == LineageStage.Choosing && hadReply) || (m_stage == LineageStage.Uploading && seat == m_choiceSeat)
                || (m_stage == LineageStage.Offered && hadReply))
            {
                m_choiceSeat = -1;
                // Another remaining guest may also have a fork. Ask again before committing over its copy.
                if (m_seats.Count != 0) Offer();
                else Decide(false);
                return;
            }
            if (m_waiting.Remove(seat)) FinishRepliesIfComplete();
        }

        // A device's primary seat changed (NetPlay.DevicePrimaryChanged): its messages now come from the new seat.
        public void Rebind(int previous, int primary)
        {
            if (previous == primary) return;
            if (m_seats.Remove(previous)) m_seats.Add(primary);
            if (m_waiting.Remove(previous)) m_waiting.Add(primary);
            if (m_choiceSeat == previous) m_choiceSeat = primary;
            LineageReply answer;
            if (m_answers.TryGetValue(previous, out answer))
            {
                m_answers.Remove(previous); m_answers[primary] = answer;
                int index = m_forks.IndexOf(previous);
                if (index >= 0) m_forks[index] = primary;
            }
        }

        public bool HostGate()
        {
            switch (m_stage)
            {
                case LineageStage.Idle: Offer(); return false;
                case LineageStage.Proceed: case LineageStage.Done: return true;
                case LineageStage.Offered:
                    if (m_clock.RealTime - m_stageAt > 10f)
                    {
                        // Some guests answered (a seat that never does runs an older mod): decide on those answers.
                        if (m_replies > 0) { FinishReplies(); return m_stage == LineageStage.Proceed; }
                        m_actions.ReplyTimeout(); SetStage(LineageStage.Proceed); return true;
                    }
                    return false;
                case LineageStage.Uploading:
                    if (m_clock.RealTime - m_stageAt > 15f)
                    { m_actions.UploadTimeout(); Decide(false); }
                    return false;
                default: return false;
            }
        }

        // T-126: this guest became the host mid-session (server-mode arbiter change). The lineage was already settled
        // with the old host; offering now would send this player's own slot as the "co-op save" to the next joiner.
        public void Promote() { SetStage(LineageStage.Proceed); }

        void Offer()
        {
            try
            {
                LineageOffer offer = m_actions.PrepareOffer();
                m_offer = offer; m_answers.Clear(); m_forks.Clear(); Suggested = -1;
                m_waiting.Clear(); foreach (int seat in m_seats) m_waiting.Add(seat);
                m_same = m_new = null; m_replies = 0; m_choiceSeat = -1;
                m_net.Send(LineageWire.Offer, true, delegate(BinaryWriter w)
                {
                    offer.PlayerId = m_actions.PlayerId; offer.Name = m_actions.LocalName;
                    LineageWire.WriteOffer(w, offer);
                });
                SetStage(LineageStage.Offered);
                m_actions.OfferSent(offer);
            }
            catch (Exception e) { m_actions.OfferFailed(e); SetStage(LineageStage.Proceed); }
        }

        void ReceiveOffer(BinaryReader r)
        {
            if (m_net.IsHost || m_net.Sender != m_net.HostSeat) return;
            LineageOffer offer = LineageWire.ReadOffer(r);
            LineageReply reply = m_actions.PrepareReply(offer);
            m_net.Send(LineageWire.Reply, true, delegate(BinaryWriter w)
            {
                reply.PlayerId = m_actions.PlayerId; reply.Name = m_actions.LocalName;
                LineageWire.WriteReply(w, reply);
            });
            m_actions.ReplySent(offer, reply);
        }

        void ReceiveReply(BinaryReader r)
        {
            if (!m_net.IsHost || m_stage != LineageStage.Offered) return;
            int seat = m_net.Sender;
            if (!m_waiting.Contains(seat)) return;
            LineageReply reply = LineageWire.ReadReply(r);
            m_actions.ReplyReceived(reply);
            m_waiting.Remove(seat); m_replies++;
            m_answers[seat] = reply;
            if (reply.Found) { if (reply.Hash == m_actions.HostHash && m_same == null) m_same = reply; }
            else if (m_new == null || (m_new.FreeSlot && !reply.FreeSlot)) m_new = reply;   // keep a "no free slot" answer for its warning
            FinishRepliesIfComplete();
        }

        void FinishRepliesIfComplete()
        {
            if (m_stage == LineageStage.Offered && m_waiting.Count == 0 && m_replies > 0) FinishReplies();
        }

        // Every guest answered without a diverged copy: the same lineage if anyone holds it, else a new one.
        void FinishReplies()
        {
            foreach (KeyValuePair<int, LineageReply> answer in m_answers)
                if (answer.Value.Found && answer.Value.Hash != m_actions.HostHash) m_forks.Add(answer.Key);
            if (m_forks.Count != 0)
            {
                Suggested = SuggestedChoice();
                int index = Suggested > 0 ? Suggested - 1 : 0;
                m_choiceSeat = m_forks[index];
                m_actions.BeginChoosing(Fork(index));
                SetStage(LineageStage.Choosing);
                m_net.Send(LineageWire.Request, true, delegate(BinaryWriter w) { LineageWire.WriteRequest(w, LineageWire.ChoosingRequest); });
                m_actions.ChoosingSent();
                return;
            }
            if (m_same != null) m_actions.SameReply();
            else m_actions.NewReply(m_new);
            Decide(false);
        }

        int SuggestedChoice()
        {
            if (m_waiting.Count != 0 || string.IsNullOrEmpty(m_offer.PreviousRound)) return -1;
            int choice = m_offer.WasLast ? 0 : -1;
            foreach (KeyValuePair<int, LineageReply> answer in m_answers)
            {
                LineageReply reply = answer.Value;
                if (!reply.Found) continue;
                if (reply.PreviousRound != m_offer.PreviousRound) return -1;
                if (!reply.WasLast) continue;
                if (choice != -1) return -1;
                choice = m_forks.IndexOf(answer.Key) + 1; // Its unchanged copy matches the host.
            }
            return choice;
        }

        public void ChooseFork(int index)
        {
            if (m_stage != LineageStage.Choosing || index < 0 || index >= m_forks.Count) return;
            m_choiceSeat = m_forks[index];
            Decide(true);
        }

        public void Decide(bool partner)
        {
            if (partner)
            {
                SetStage(LineageStage.Uploading);
                // Only the guest whose copy was on offer uploads: another guest has no matching slot and would send an
                // empty upload that could arrive first and void the host's choice (T-128).
                Action<BinaryWriter> body = delegate(BinaryWriter w) { LineageWire.WriteRequest(w, LineageWire.UploadRequest); };
                if (m_choiceSeat >= 0) m_net.SendTo(m_choiceSeat, LineageWire.Request, true, body);
                else m_net.Send(LineageWire.Request, true, body);
                return;
            }
            m_actions.HostChosen(m_stage == LineageStage.Choosing || m_stage == LineageStage.Uploading);
            m_actions.Commit();
            SetStage(LineageStage.Proceed);
        }

        public void Cancel()
        {
            m_actions.SetCancelOutcome();
            m_net.Send(LineageWire.Request, true, delegate(BinaryWriter w) { LineageWire.WriteRequest(w, LineageWire.CancelRequest); });
            SetStage(LineageStage.Cancelling);
            m_actions.Cancelled();
        }

        void ReceiveRequest(BinaryReader r)
        {
            if (m_net.IsHost || m_net.Sender != m_net.HostSeat) return;
            byte kind = LineageWire.ReadRequest(r);
            if (kind == LineageWire.ChoosingRequest) m_actions.RequestChoosing();
            else if (kind == LineageWire.CancelRequest) m_actions.RequestCancel();
            else if (kind == LineageWire.UploadRequest)
            {
                byte[] bytes = m_actions.ReadUpload();
                m_net.Send(LineageWire.Upload, true, delegate(BinaryWriter w) { LineageWire.WriteUpload(w, bytes); });
                m_actions.UploadSent(bytes);
            }
        }

        void ReceiveUpload(BinaryReader r)
        {
            if (!m_net.IsHost || m_stage != LineageStage.Uploading) return;
            if (m_choiceSeat >= 0 && m_net.Sender != m_choiceSeat) return;
            int length; byte[] bytes = LineageWire.ReadUpload(r, out length);
            if (!m_actions.ValidUpload(length, bytes)) { Decide(false); return; }
            try
            {
                m_actions.PrepareAdoption(bytes);
                SetStage(LineageStage.Adopting);
                m_actions.Adopt(bytes);
            }
            catch (Exception e) { m_actions.AdoptionFailed(e); Decide(false); }
        }

        void ReceiveCommit(BinaryReader r)
        {
            if (m_net.IsHost || m_net.Sender != m_net.HostSeat) return;
            LineageCommit commit = LineageWire.ReadCommit(r);
            if (commit != null) m_actions.CommitReceived(commit);
        }

        public void AdoptionFinished()
        {
            if (m_stage != LineageStage.Adopting) return;
            m_actions.AdoptionFinished(); m_actions.Commit(); SetStage(LineageStage.Proceed);
        }

        public bool Tick()
        {
            if (m_stage == LineageStage.Cancelling && m_clock.RealTime - m_stageAt > 0.6f)
            { SetStage(LineageStage.Done); m_actions.End(); return true; }
            return false;
        }
    }
}
