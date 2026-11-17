using System;
using System.IO;
using OriCoop.Seams;

namespace OriCoop.Saves
{
    public sealed class LineageOffer
    {
        public byte Version = 1;
        public string Coop = "", PlayerId = "", Name = "", Hash = "", Summary = "";
    }

    public sealed class LineageReply
    {
        public bool Found, FreeSlot;
        public int Slot;
        public string Hash = "", LastSync = "", Summary = "", PlayerId = "", Name = "";
    }

    public sealed class LineageCommit
    {
        public string Coop = "", PlayerId = "", Name = "", SaveId = "";
        public byte[] Bytes;
    }

    public static class LineageWire
    {
        public const byte Offer = 16, Reply = 17, Commit = 18, Request = 19, Upload = 23;
        public const byte UploadRequest = 1, CancelRequest = 2, ChoosingRequest = 3;

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
        }
        public static LineageReply ReadReply(BinaryReader r)
        {
            LineageReply value = new LineageReply();
            value.Found = r.ReadBoolean(); value.Slot = r.ReadInt32(); value.Hash = r.ReadString(); value.LastSync = r.ReadString();
            value.Summary = r.ReadString(); value.PlayerId = r.ReadString(); value.Name = r.ReadString(); value.FreeSlot = r.ReadBoolean(); return value;
        }
        public static void WriteCommit(BinaryWriter w, LineageCommit value)
        {
            w.Write(value.Coop); w.Write(value.PlayerId); w.Write(value.Name); w.Write(value.SaveId);
            w.Write(value.Bytes != null); if (value.Bytes != null) { w.Write(value.Bytes.Length); w.Write(value.Bytes); }
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
        }

        void Reset() { m_stage = LineageStage.Idle; m_actions.Reset(); }

        public bool HostGate()
        {
            switch (m_stage)
            {
                case LineageStage.Idle: Offer(); return false;
                case LineageStage.Proceed: case LineageStage.Done: return true;
                case LineageStage.Offered:
                    if (m_clock.RealTime - m_stageAt > 10f)
                    { m_actions.ReplyTimeout(); SetStage(LineageStage.Proceed); return true; }
                    return false;
                case LineageStage.Uploading:
                    if (m_clock.RealTime - m_stageAt > 15f)
                    { m_actions.UploadTimeout(); Decide(false); }
                    return false;
                default: return false;
            }
        }

        void Offer()
        {
            try
            {
                LineageOffer offer = m_actions.PrepareOffer();
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
            if (m_net.IsHost) return;
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
            LineageReply reply = LineageWire.ReadReply(r);
            m_actions.ReplyReceived(reply);
            if (!reply.Found) { m_actions.NewReply(reply); Decide(false); }
            else if (reply.Hash == m_actions.HostHash) { m_actions.SameReply(); Decide(false); }
            else
            {
                m_actions.BeginChoosing(reply);
                SetStage(LineageStage.Choosing);
                m_net.Send(LineageWire.Request, true, delegate(BinaryWriter w) { LineageWire.WriteRequest(w, LineageWire.ChoosingRequest); });
                m_actions.ChoosingSent();
            }
        }

        public void Decide(bool partner)
        {
            if (partner)
            {
                SetStage(LineageStage.Uploading);
                m_net.Send(LineageWire.Request, true, delegate(BinaryWriter w) { LineageWire.WriteRequest(w, LineageWire.UploadRequest); });
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
            if (m_net.IsHost) return;
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
            if (m_net.IsHost) return;
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
