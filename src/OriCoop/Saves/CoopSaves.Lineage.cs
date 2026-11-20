using System;
using System.IO;
using OriCoop.Seams;
using OriCoop.Saves;

namespace OriCoop
{
    public static partial class CoopSaves
    {
        public static bool HostGate() { return s_machine.HostGate(); }
        public static void HostAdoptFinished() { s_machine.AdoptionFinished(); }

        static void Commit() { Commit(-1); }

        // seat < 0: every guest (the decision, every host save). seat >= 0: only that seat, which joined after the
        // lineage was settled (T-129); the slot file and its link are the same as at the last broadcast.
        static void Commit(int seat)
        {
            if (!Services.Net.IsHost || !Services.Net.Connected || s_hostLink == null) return;
            try
            {
                byte[] bytes = ReadSlot(s_hostSlot);
                string hash = Hash(bytes);
                SaveHeader h = ReadHeader(bytes);
                if (h != null) s_hostLink.SaveId = h.Id;
                s_hostLink.Synced = hash;
                s_hostLink.SyncedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                if (bytes != null) s_hostLink.Write(s_hostSlot);
                s_hostHash = hash; LastCommitHash = hash;
                if (seat < 0) Commits++; else LateCommits++;
                string coop = s_hostLink.Coop, saveId = s_hostLink.SaveId;
                Action<BinaryWriter> body = delegate(BinaryWriter w)
                {
                    LineageCommit commit = new LineageCommit();
                    commit.Coop = coop; commit.PlayerId = PlayerId; commit.Name = LocalName; commit.SaveId = saveId; commit.Bytes = bytes;
                    LineageWire.WriteCommit(w, commit);
                };
                if (seat < 0) Services.Net.Send(LineageWire.Commit, true, body);
                else Services.Net.SendTo(seat, LineageWire.Commit, true, body);
                Services.Log.Info("coop save: commit " + (bytes == null ? "(no file yet)" : bytes.Length + " bytes hash " + Short(hash))
                    + (seat < 0 ? " to " + s_hostLink.Partner : " to seat " + seat + " (joined after the lineage was settled)"));
            }
            catch (Exception e) { Services.Log.Warn("coop save: commit failed: " + e); }
        }

        sealed class LineageActions : ILineageActions
        {
            public string PlayerId { get { return CoopSaves.PlayerId; } }
            public string LocalName { get { return CoopSaves.LocalName; } }
            public string HostHash { get { return s_hostHash; } }

            public LineageOffer PrepareOffer()
            {
                s_hostSlot = SaveSlotsManager.CurrentSlotIndex;
                byte[] bytes = ReadSlot(s_hostSlot);
                s_hostHash = Hash(bytes);
                s_hostSummary = bytes == null ? SavesHost.Current.Translate("saves.new_game") : Summary(bytes);
                s_hostLink = bytes == null ? null : ValidLink(s_hostSlot, bytes);
                if (s_hostLink == null)
                {
                    s_hostLink = new CoopLink();
                    s_hostLink.Coop = Guid.NewGuid().ToString("N");
                    SaveHeader h = ReadHeader(bytes);
                    s_hostLink.SaveId = h != null ? h.Id : "";
                    s_hostChanged = false;
                }
                else s_hostChanged = s_hostLink.Synced.Length > 0 && s_hostLink.Synced != s_hostHash;
                CoopId = s_hostLink.Coop;
                LineageOffer offer = new LineageOffer();
                offer.Coop = s_hostLink.Coop; offer.Hash = s_hostHash; offer.Summary = s_hostSummary;
                return offer;
            }

            public void OfferSent(LineageOffer offer)
            {
                Services.Log.Info("coop save: offered lineage " + offer.Coop + " slot " + s_hostSlot + " hash " + Short(s_hostHash) + " changedSinceSync=" + s_hostChanged);
            }
            public void OfferFailed(Exception e)
            {
                Services.Log.Warn("coop save: offer failed, host save wins: " + e);
            }

            public LineageReply PrepareReply(LineageOffer offer)
            {
                s_offerCoop = offer.Coop; s_offerHostId = offer.PlayerId; s_offerHostName = PartnerName(offer.Name);
                CoopId = s_offerCoop;
                s_guestMatch = -1;
                string hash = "", last = "", summary = "";
                bool free = false;
                int current = SafeCurrentSlot();
                for (int i = 0; i < SlotCount; i++)
                {
                    byte[] bytes = ReadSlot(i);
                    if (bytes == null) { if (i != current || GuestJoiningEmpty) free = true; continue; }
                    CoopLink l = ValidLink(i, bytes);
                    if (l != null && l.Coop == s_offerCoop && s_guestMatch < 0)
                    { s_guestMatch = i; hash = Hash(bytes); last = l.Synced; summary = Summary(bytes); }
                }
                LineageReply reply = new LineageReply();
                reply.Found = s_guestMatch >= 0; reply.Slot = s_guestMatch; reply.Hash = hash; reply.LastSync = last;
                reply.Summary = summary; reply.FreeSlot = free;
                return reply;
            }

            public void ReplySent(LineageOffer offer, LineageReply reply)
            {
                Services.Log.Info("coop save: host " + s_offerHostName + " offered " + s_offerCoop + "; local match slot " + reply.Slot + " hash " + Short(reply.Hash));
            }
            public void ReplyReceived(LineageReply reply)
            {
                s_reply = reply;
                s_hostLink.Partner = PartnerName(reply.Name); s_hostLink.PartnerId = reply.PlayerId;
                Services.Log.Info("coop save: partner " + s_hostLink.Partner + " found=" + reply.Found + " slot=" + reply.Slot + " hash=" + Short(reply.Hash) + " host=" + Short(s_hostHash));
            }
            public void NewReply(LineageReply reply)
            {
                Outcome = "new";
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.new"), "");
                if (!reply.FreeSlot) SavesHost.Current.Toast(SavesHost.Current.Translate("saves.partner_no_slot"), "");
            }
            public void SameReply()
            {
                Outcome = "same";
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.match"), "");
            }
            public void BeginChoosing(LineageReply reply)
            {
                DivergenceSeen = true;
                s_partnerChanged = reply.LastSync.Length == 0 || reply.Hash != reply.LastSync;
                s_focus = 0;
            }
            public void ChoosingSent()
            {
                Services.Log.Info("coop save: lineage diverged (hostChanged=" + s_hostChanged + " partnerChanged=" + s_partnerChanged + "); asking the host");
            }
            public void HostChosen(bool diverged) { if (diverged) Outcome = "diverged-host"; }
            public void Commit() { CoopSaves.Commit(); }
            public void CommitTo(int seat) { CoopSaves.Commit(seat); }
            public void SetCancelOutcome() { Outcome = "cancel"; }
            public void Cancelled()
            {
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.cancelled"), "");
                Services.Log.Info("coop save: host cancelled at the divergence choice");
            }

            public bool ValidUpload(int length, byte[] bytes)
            {
                if (length > 0 && bytes.Length == length && ReadHeader(bytes) != null) return true;
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.invalid_progress"), "");
                return false;
            }
            public void PrepareAdoption(byte[] bytes)
            {
                Backup(s_hostSlot);
                WriteSlotFile(s_hostSlot, bytes);
                s_hostHash = Hash(bytes);
                SaveHeader h = ReadHeader(bytes);
                s_hostLink.SaveId = h.Id;
                Outcome = "diverged-partner";
            }
            public void Adopt(byte[] bytes)
            {
                Services.Log.Info("coop save: adopting partner progress " + bytes.Length + " bytes hash " + Short(s_hostHash));
                SaveSync.HostAdopt(bytes);
            }
            public void AdoptionFailed(Exception e)
            {
                Services.Log.Warn("coop save: adopting partner progress failed: " + e);
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.load_failed"), "");
            }
            public void AdoptionFinished()
            {
                try { AdoptedHours = SaveSlotsManager.CurrentSaveSlot.Hours; } catch (Exception) { }
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.using_partner"), "");
            }

            public void CommitReceived(LineageCommit commit)
            {
                CommitsReceived++;
                string coop = commit.Coop, hostId = commit.PlayerId, hostName = PartnerName(commit.Name), saveId = commit.SaveId;
                byte[] bytes = commit.Bytes;
                CoopId = coop;
                if (bytes == null || ReadHeader(bytes) == null)
                {
                    Services.Log.Info("coop save: host has no save file yet; nothing to keep");
                    return;
                }
                s_commitCoop = coop; s_commitHostId = hostId; s_commitHostName = hostName; s_commitSaveId = saveId; s_commitBytes = bytes;
                KeepCopy(-1);
            }
            public byte[] ReadUpload() { return s_guestMatch >= 0 ? ReadSlot(s_guestMatch) : null; }
            public void UploadSent(byte[] bytes)
            {
                Services.Log.Info("coop save: host chose partner progress; sent slot " + s_guestMatch + " (" + (bytes == null ? 0 : bytes.Length) + " bytes)");
            }
            public void RequestChoosing() { SavesHost.Current.Toast(SavesHost.Current.Translate("saves.waiting_choice"), ""); }
            public void RequestCancel()
            {
                CancelledByHost = true;
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.host_cancelled"), "");
            }
            public void ReplyTimeout()
            {
                Services.Log.Warn("coop save: no lineage reply (older partner mod?); host save wins");
                Outcome = "legacy";
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.check_timeout"), "");
            }
            public void UploadTimeout()
            {
                Services.Log.Warn("coop save: partner progress never arrived; host save wins");
                SavesHost.Current.Toast(SavesHost.Current.Translate("saves.upload_timeout"), "");
            }
            public void End() { SavesHost.Current.End("co-op save choice cancelled"); }
            public void Reset() { CoopSaves.Reset(); }
        }
    }
}
