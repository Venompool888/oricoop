using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Seats;
using OriCoop.Net;

namespace OriCoop
{
    public static partial class NetPlay
    {
        // 6/7 are automatic add/release commands, not a player-facing approval workflow.
        const byte MsgLocalAdd = 6, MsgLocalRelease = 7, MsgSeatTable = 8, MsgSeatFrames = 9;
        static readonly SeatTable s_seats = new SeatTable();
        static readonly int[] s_lastOwners = new int[5];   // the table at the last ReconcileSeats (T-133 renames)
        static bool s_seatChangePending;
        internal static event Action<int, int> DevicePrimaryChanged;
        public static bool MultiSeatSupported { get { return !s_factorySession; } }
        public static bool CanAddLocal { get { EnsureHostSeats(); return Enabled && MultiSeatSupported && !s_seatChangePending && s_seats.Count < 4 && (IsHost || !WaitingForSeat && Connected); } }
        public static int SeatOwner(int seat) { return s_seats.Owner(seat); }
        public static int DeviceSeat(int seat) { int owner = SeatOwner(seat); return owner == 0 ? seat : owner; }
        public static int[] LocalSeats
        {
            get
            {
                EnsureHostSeats();
                int[] seats = s_seats.SeatsOf(Players.LocalSeat);
                return seats.Length == 0 ? new int[] { Players.LocalSeat } : seats;
            }
        }
        static void EnsureHostSeats() { if (Enabled && IsHost && !s_factorySession && s_seats.Count == 0) s_seats.Add(1); }
        static void RetainHostLocalSeats()
        {
            s_seats.KeepDevice(1); s_seats.CopyOwners(s_lastOwners); s_seatChangePending = false;
            s_extraSent.Clear(); Array.Clear(s_extraCount, 0, s_extraCount.Length);
        }
        static void ResetLocalSeats()
        {
            s_seats.Clear(); s_seats.CopyOwners(s_lastOwners); s_seatChangePending = false;
            s_extraSent.Clear(); Array.Clear(s_extraCount, 0, s_extraCount.Length);
            foreach (Puppet puppet in new List<Puppet>(Players.Remotes)) if (puppet.IsLocal) Plugin.DespawnPuppet(puppet);
            OnlineLocalPlayers.Reset();
        }
        static void InitLocalSeats()
        {
            s_bus.Validate = ValidateSeatPacket;
            On(MsgLocalAdd, delegate(BinaryReader r) {
                if (!IsHost || s_factorySession) return;
                int device = Sender;
                if (s_seats.Owner(device) != device) return;
                int added = s_seats.Add(device);
                if (added != 0) ReconcileSeats();
                PublishSeats(); // Also acknowledges a full-room race.
            });
            On(MsgLocalRelease, delegate(BinaryReader r) {
                if (!IsHost || s_factorySession) return;
                int seat = r.ReadByte();
                if (seat == Sender) return; // Leaving a device uses the normal disconnect path.
                if (s_seats.Release(Sender, seat)) ReconcileSeats();
                PublishSeats();
            });
            On(MsgSeatTable, delegate(BinaryReader r) {
                if (IsHost || Sender != 1 || s_factorySession) return;
                s_seats.Read(r);
                // Steam has no legacy MsgSeat handshake. The targeted table bootstraps its primary too.
                if (r.BaseStream.Position < r.BaseStream.Length)
                {
                    int assigned = r.ReadByte();
                    if (UsesSteam && Players.AssignedSeat == 0 && assigned >= 1 && assigned <= 4 && s_seats.Owner(assigned) == assigned)
                    { AssignSeat(assigned); s_statusDirty = true; }
                }
                s_seatChangePending = false; ReconcileSeats();
            });
            On(MsgSeatFrames, delegate(BinaryReader r) {
                foreach (SeatStream stream in FrameStreams.Decode(r))
                {
                    RemoteState remote;
                    if (TryGetRemote(stream.Seat, out remote))
                        HandleCore(3, new BinaryReader(new MemoryStream(stream.Body)), stream.Body, remote);
                }
            });
        }
        static bool ValidateSeatPacket(byte[] message, int source)
        {
            if (message[0] != MsgSeatFrames) return true;
            if (s_factorySession) return false; // TODO C-server: validate/route per-seat streams on the server first.
            int sender = message[1];
            if (IsHost && source != sender) return false;
            try
            {
                SeatStream[] streams = FrameStreams.Decode(new BinaryReader(new MemoryStream(message, 2, message.Length - 2)));
                foreach (SeatStream stream in streams)
                    if (!s_seats.Accepts(sender, sender, stream.Seat)) return false;
                return true;
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
        }
        internal static bool AddLocalSeat()
        {
            if (!CanAddLocal) return false;
            if (IsHost) { if (s_seats.Add(1) == 0) return false; ReconcileSeats(); PublishSeats(); }
            else { s_seatChangePending = true; Send(MsgLocalAdd, true, delegate(BinaryWriter w) { }); }
            return true;
        }
        internal static void ReleaseLocalSeat(int seat)
        {
            if (seat == Players.LocalSeat || Array.IndexOf(LocalSeats, seat) < 0 || s_seatChangePending) return;
            if (IsHost) { s_seats.Release(1, seat); ReconcileSeats(); PublishSeats(); }
            else { s_seatChangePending = true; Send(MsgLocalRelease, true, delegate(BinaryWriter w) { w.Write((byte)seat); }); }
        }
        static void PublishSeats()
        {
            if (!IsHost || s_factorySession) return;
            foreach (RemoteState remote in s_remotes.Values)
            {
                if (remote.Peer == null) continue;
                int device = remote.Seat;
                SendTo(device, MsgSeatTable, true, delegate(BinaryWriter w) { s_seats.Write(w); w.Write((byte)device); });
            }
        }
        static void CopyDeviceStatus(RemoteState primary)
        {
            foreach (RemoteState other in s_remotes.Values)
                if (other != primary && DeviceSeat(other.Seat) == primary.Seat)
                {
                    other.Hello = primary.Hello; other.InGameplay = primary.InGameplay; other.Scene = primary.Scene; other.Version = primary.Version;
                    BindMovement(other, primary.AdvertisedFrames);
                    // T-213 (6): a PC that is not in gameplay (loading) takes all its seats along. The extra local players
                    // kept their buffered frames and last position: after the load their Oris appeared at the old place and
                    // replayed the old frames before jumping to the new one.
                    if (!other.InGameplay && !s_mutateExtraKeepsStream) ClearStream(other);
                }
        }
        static void ReconcileSeats()
        {
            int previous = Players.LocalSeat;
            int primary = s_seats.Owner(previous);
            if (primary != 0 && primary != previous)
            { OnlineLocalPlayers.ChangePrimary(previous, primary); Players.AssignedSeat = primary; }
            OnlineLocalPlayers.Reconcile();
            // A new table may arrive before the owner's next Hello. Retire the old occupant's advertised
            // ID at release, even if that same device subsequently fills the same secondary seat.
            for (int seat = 1; seat <= MovementEpochs.Seats; seat++)
            {
                int before = s_lastOwners[seat], now = s_seats.Owner(seat);
                if (before == 0 || before == now || now != 0 && s_seats.Owner(before) == now) continue;
                RemoteState oldDevice;
                if (TryGetRemote(before, out oldDevice) && oldDevice.AdvertisedFrames != null)
                    oldDevice.AdvertisedFrames.Id[seat] = 0;
            }
            for (int seat = 1; seat <= 4; seat++)
                if (!Players.IsLocal(seat)) { s_extraCount[seat] = 0; s_extraSent.Remove(seat); }
            // Only primary remotes carry a peer. The others share device status, never their own world/save handshake.
            Dictionary<int, Net.NetPeer> peers = new Dictionary<int, Net.NetPeer>();
            Dictionary<int, RemoteState> devices = new Dictionary<int, RemoteState>();
            foreach (RemoteState remote in s_remotes.Values)
            {
                int owner = s_seats.Owner(remote.Seat);
                if (owner == 0) continue;
                if (!devices.ContainsKey(owner) || remote.Seat == owner || !devices[owner].Hello && remote.Hello) devices[owner] = remote;
                if (remote.Peer != null) peers[owner] = remote.Peer;
            }
            foreach (RemoteState remote in new List<SessionMachine.Remote>(s_remotes.Values))
            {
                int owner = s_seats.Owner(remote.Seat);
                if (owner == 0 || Array.IndexOf(LocalSeats, remote.Seat) >= 0)
                { Leave(remote, "seat released"); continue; }
                if (owner != s_lastOwners[remote.Seat] && remote.Puppet != null) PartnerSkillFx.StopGlow(remote.Puppet);
                remote.Peer = null;
            }
            for (int seat = 1; seat <= 4; seat++)
            {
                if (s_seats.Owner(seat) == 0 || Array.IndexOf(LocalSeats, seat) >= 0) continue;
                RemoteState remote;
                if (!TryGetRemote(seat, out remote)) { remote = new RemoteState { Seat = seat }; s_remotes.Add(seat, remote); }
                Net.NetPeer peer;
                if (seat == s_seats.Owner(seat))
                {
                    if (peers.TryGetValue(seat, out peer)) remote.Peer = peer;
                    RemoteState old;
                    if (devices.TryGetValue(seat, out old))
                    {
                        remote.Hello = old.Hello; remote.InGameplay = old.InGameplay; remote.Scene = old.Scene; remote.Version = old.Version;
                        remote.AdvertisedFrames = old.AdvertisedFrames;
                        BindMovement(remote, remote.AdvertisedFrames);
                        if (IsHost && old.Seat != seat)
                        {
                            s_bus.SeatRenumbered(old.Seat, seat); // T-149: accept its old Sender until it uses the new seat
                            if (DevicePrimaryChanged != null) DevicePrimaryChanged(old.Seat, seat);
                        }
                    }
                }
            }
            foreach (RemoteState remote in s_remotes.Values) if (DeviceSeat(remote.Seat) == remote.Seat) CopyDeviceStatus(remote);
            // T-133: on every PC (host, the renumbered guest, the other guests) whatever is keyed by a device's primary seat
            // (enemy ownership, presence, symbols) follows it to its new number.
            int[] renamed = s_seats.Renames(s_lastOwners);
            s_seats.CopyOwners(s_lastOwners);
            if (Array.Exists(renamed, s => s != 0)) Players.RaiseSeatsRenamed(renamed);
            if (primary != 0 && primary != previous) { s_statusDirty = true; s_sentCount = 0; }
            if (RefreshLocalFrameEpochs() && !WaitingForSeat) SendReliable(MsgHello, WriteHello);
        }
    }
}
