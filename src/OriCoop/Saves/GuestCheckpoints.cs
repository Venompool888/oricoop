using System;
using System.IO;
using OriCoop.Seams;
using OriCoop.Story;

namespace OriCoop.Saves
{
    // The native loader can restore one save at a time. The host sends complete saves on one reliable, ordered stream.
    internal sealed class GuestCheckpoints
    {
        sealed class Transfer
        {
            public int Host;
            public byte[] Bytes;
            public bool Wipe;
            public SkillRecord Skills;
        }

        readonly INetBus net;
        readonly Func<bool> enabled;
        readonly Func<SavePosition, byte[], byte[]> accept;
        readonly Func<SkillRecord> roomSkills;
        Transfer pending, active;
        int wipeHost;
        int skillHost;
        SkillRecord nextSkills;

        public GuestCheckpoints(INetBus net, Func<bool> enabled, Func<SavePosition, byte[], byte[]> accept, Func<SkillRecord> roomSkills)
        {
            this.net = net; this.enabled = enabled; this.accept = accept; this.roomSkills = roomSkills;
            net.On(SaveWire.SaveMessage, Receive);
            net.SessionEnded += Clear;
            net.PlayerLeft += Left;
        }

        bool FromCurrentHost(Transfer save)
        {
            return save != null && net.Connected && !net.IsHost && save.Host == net.HostSeat;
        }

        public bool HasPending { get { return FromCurrentHost(pending); } }
        public bool LoadingWipe { get { return FromCurrentHost(active) && active.Wipe; } }
        public SkillRecord LoadingSkills { get { return FromCurrentHost(active) ? active.Skills : null; } }
        public bool CanFinish { get { return FromCurrentHost(active) && !HasPending; } }
        public void ExpectWipe() { wipeHost = net.HostSeat; }

        // The host's optional record immediately precedes its checkpoint on the same reliable stream.
        public void ExpectSkills(int sender, SkillRecord record)
        {
            if (!enabled() || !net.Connected || net.IsHost || sender != net.HostSeat) return;
            skillHost = sender;
            nextSkills = record;
        }

        public void RebindDevice(int previous, int primary)
        {
            if (active != null && active.Host == previous) active.Host = primary;
            if (pending != null && pending.Host == previous) pending.Host = primary;
            if (wipeHost == previous) wipeHost = primary;
            if (skillHost == previous) skillHost = primary;
        }

        void Receive(BinaryReader reader)
        {
            if (!enabled() || !net.Connected || net.IsHost || net.Sender != net.HostSeat) return;
            SkillRecord skills = skillHost == net.Sender ? nextSkills : null;
            nextSkills = null;
            skillHost = 0;
            SavePosition position = SaveWire.ReadPosition(reader);
            byte[] bytes = SaveWire.ReadSaveBytes(reader);
            pending = new Transfer { Host = net.Sender, Bytes = accept(position, bytes), Wipe = wipeHost == net.Sender,
                Skills = skills ?? roomSkills() };
            wipeHost = 0;
        }

        public byte[] Begin()
        {
            if (!HasPending || active != null) return null;
            active = pending;
            pending = null;
            return active.Bytes;
        }

        public bool Finish()
        {
            bool latest = CanFinish;
            active = null;
            return latest;
        }

        void Left(int seat)
        {
            if (seat == wipeHost) wipeHost = 0;
            if (seat == skillHost) { skillHost = 0; nextSkills = null; }
            if (pending != null && pending.Host == seat) pending = null;
            if (active != null && active.Host == seat) active.Host = 0;
        }

        void Clear()
        {
            pending = active = null;
            wipeHost = 0;
            skillHost = 0; nextSkills = null;
        }
    }
}
