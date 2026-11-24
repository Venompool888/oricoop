using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Net test checks for co-op save lineage (CoopSaves). Hooked onto NetTestHarness.Run so the shared harness file
    // stays untouched:
    // - normal nettest: after the suite, the guest holds a tagged copy of the host's slot file, the host's slot is tagged.
    // - ORICOOP_COOPSAVE_TEST=1 (src\run-coopsave.ps1): only the lineage handshake runs, then the expected outcome
    //   (ORICOOP_COOPSAVE_EXPECT = new | same | diverged-host | diverged-partner | cancel) is checked.
    static class CoopSaveTest
    {
        static readonly bool s_only = Environment.GetEnvironmentVariable("ORICOOP_COOPSAVE_TEST") == "1";
        static readonly string s_expect = Environment.GetEnvironmentVariable("ORICOOP_COOPSAVE_EXPECT") ?? "";
        static readonly string s_partnerHash = Environment.GetEnvironmentVariable("ORICOOP_COOPSAVE_PARTNER_HASH") ?? "";

        [HarmonyPatch(typeof(NetTestHarness), "Run")]
        static class RunPatch
        {
            static void Postfix(TestHarness h, ref IEnumerator __result)
            {
                __result = s_only ? Scenario(h) : Then(__result, AfterSuite(h));
            }
        }

        static IEnumerator Then(IEnumerator first, IEnumerator second)
        {
            while (first.MoveNext()) yield return first.Current;
            while (second.MoveNext()) yield return second.Current;
        }

        static IEnumerator Wait(float s) { float end = Time.realtimeSinceStartup + s; while (Time.realtimeSinceStartup < end) yield return null; }

        static IEnumerator AfterSuite(TestHarness h)
        {
            if (!NetPlay.Enabled) yield break;
            // T-111: after P1 left a server-mode room (3P host-leave check) the save lineage after a host handover is a
            // separate question (follow-up in T-111); the remaining seats skip phase L there.
            if (NetTestHarness.HostLeftInServerMode)
            {
                if (NetPlay.IsHost) { IEnumerator p = PromotedHostSave(h); while (p.MoveNext()) yield return p.Current; }
                else h.Note("phase L skipped: P1 left the server-mode room");
                yield break;
            }
            h.Note("phase L co-op save lineage");
            // T-111: in server mode the arbiter moves to seat 2 when P1 leaves, but the save lineage stays P1's: a seat
            // that only became the arbiter checks its guest copy, not a host slot it never had.
            bool saveHost = NetPlay.IsHost && (!OriCoop.Authority.NetRoute.ServerMode || Players.LocalSeat == 1);
            if (saveHost)
            {
                h.Check(CoopSaves.Outcome == "new" || CoopSaves.Outcome == "same", "L lineage outcome on a fresh pair = " + CoopSaves.Outcome);
                CheckHostLink(h);
                yield return NetTestHarness.CheckKeyAfterExit(h);
            }
            else
            {
                // T-129: every guest, also a 3rd/4th seat joining after the lineage was settled, gets the host's co-op save
                // before its checkpoint (else a title join loads into the first empty slot or slot 0 with no copy).
                h.Check(SaveSync.CheckpointsBeforeCommit == 0 && CoopSaves.CommitsReceived > 0,
                    "L guest P" + Players.LocalSeat + " got the co-op save commit before its checkpoint (checkpoints first "
                    + SaveSync.CheckpointsBeforeCommit + ", commits " + CoopSaves.CommitsReceived + ")");
                float until = Time.realtimeSinceStartup + 10f;
                while (CoopSaves.GuestWrites == 0 && Time.realtimeSinceStartup < until) yield return null;
                CheckGuestCopy(h);
                // T-129: the late guest really joins after the lineage settled (NetTestHarness.LateJoinGate waits for the host's
                // lineage marker; d194dd8's version without the gate failed unmutated and was backed out in af0c29c).
                if (LateGuest) CheckLateGuest(h);
            }
        }

        // T-126: seat 2 became the host when P1 left the server-mode room. A save now must land in its co-op copy, never in
        // the slot it played before joining (its own single-player save), and must go out as a commit.
        static IEnumerator PromotedHostSave(TestHarness h)
        {
            h.Note("phase L: promoted host save (T-126)");
            int from = CoopSaves.PromotedFrom, to = SaveSlotsManager.CurrentSlotIndex;
            h.Check(from >= 0 && !CoopSaves.SavesBlocked && to == CoopSaves.SessionSlot,
                "L promoted host saves to its co-op copy slot " + CoopSaves.SessionSlot + " (playing " + to + ", before " + from + ", blocked " + CoopSaves.SavesBlocked + ")");
            string fromHash = from >= 0 && File.Exists(Path(from)) ? CoopSaves.Hash(File.ReadAllBytes(Path(from))) : "";
            int commits = CoopSaves.Commits;
            float until = Time.realtimeSinceStartup + 15f;
            while (!GameController.Instance.SaveGameController.CanPerformSave() && Time.realtimeSinceStartup < until) yield return null;
            GameController.Instance.SaveGameController.PerformSave();
            yield return Wait(1f);
            if (from >= 0 && from != to)
                h.Check((File.Exists(Path(from)) ? CoopSaves.Hash(File.ReadAllBytes(Path(from))) : "") == fromHash,
                    "L promoted host's own slot " + from + " is untouched by the save");
            byte[] bytes = File.Exists(Path(to)) ? File.ReadAllBytes(Path(to)) : null;
            h.Check(bytes != null && CoopSaves.Commits == commits + 1 && CoopSaves.LastCommitHash == CoopSaves.Hash(bytes),
                "L promoted host committed the co-op save it wrote to slot " + to + " (commits " + commits + " -> " + CoopSaves.Commits + ")");
        }

        // net-3p / net-4p: the last guest connects LateJoinSec after the others (run-nettest.ps1, ORICOOP_NET_CONNECT_AT).
        static bool LateGuest
        {
            get { long at; return long.TryParse(Environment.GetEnvironmentVariable("ORICOOP_NET_CONNECT_AT"), out at) && at > 0; }
        }

        // T-129: the late guest joins after the host settled the lineage with the others, so it never sees the offer. It
        // must still get the co-op save before its checkpoint and have kept it in its co-op slot by then (the bug: no
        // commit until the host's next save; from the title that meant the first empty slot or slot 0).
        static void CheckLateGuest(TestHarness h)
        {
            h.Check(CoopSaves.OffersSeen == 0,
                "L late guest P" + Players.LocalSeat + " joined after the lineage was settled (offers seen " + CoopSaves.OffersSeen + "; else this check proves nothing)");
            h.Check(SaveSync.CheckpointsBeforeCommit == 0 && SaveSync.CopySlotAtCheckpoint >= 0 && SaveSync.CopySlotAtCheckpoint == CoopSaves.GuestSlot,
                "L late guest got the co-op save before its checkpoint and had kept it in its co-op slot " + CoopSaves.GuestSlot
                + " (checkpoints first " + SaveSync.CheckpointsBeforeCommit + ", copy slot at checkpoint " + SaveSync.CopySlotAtCheckpoint + ")");
        }

        static void CheckHostLink(TestHarness h)
        {
            int slot = SaveSlotsManager.CurrentSlotIndex;
            byte[] bytes = File.Exists(Path(slot)) ? File.ReadAllBytes(Path(slot)) : null;
            CoopSaves.CoopLink l = CoopSaves.ValidLink(slot, bytes);
            h.Check(l != null && l.Coop == CoopSaves.CoopId && l.Coop.Length == 32, "L host slot " + slot + " tagged with co-op id " + CoopSaves.CoopId);
            h.Check(l != null && l.Synced == CoopSaves.Hash(bytes), "L host link fingerprint matches its slot file");
            h.Check(l != null && l.Partner.Length > 0 && l.PartnerId.Length == 32, "L host link names the partner (" + (l == null ? "-" : l.Partner) + ")");
        }

        static void CheckGuestCopy(TestHarness h)
        {
            int slot = CoopSaves.GuestSlot;
            // Joined from an empty slot (GuestJoin.cs): that picked slot IS the co-op copy; otherwise a different free slot.
            bool fromEmpty = GuestJoin.TitleLoads > 0;
            h.Check(slot >= 0 && (fromEmpty ? slot == SaveSlotsManager.CurrentSlotIndex : slot != SaveSlotsManager.CurrentSlotIndex),
                "L guest kept a copy in " + (fromEmpty ? "the picked empty slot " : "its own free slot ") + slot + " (playing slot " + SaveSlotsManager.CurrentSlotIndex + ")");
            if (slot < 0) return;
            byte[] bytes = File.Exists(Path(slot)) ? File.ReadAllBytes(Path(slot)) : null;
            CoopSaves.CoopLink l = CoopSaves.ValidLink(slot, bytes);
            h.Check(bytes != null && CoopSaves.Hash(bytes) == CoopSaves.LastCommitHash, "L guest slot file equals the host's slot file (" + CoopSaves.LastCommitHash + ")");
            h.Check(l != null && l.Coop == CoopSaves.CoopId && l.Synced == CoopSaves.LastCommitHash, "L guest slot tagged with the same co-op id");
            h.Check(SaveSlotsManager.SlotByIndex(slot) != null, "L guest slot list shows the kept copy");
        }

        static string Path(int slot) { return GameController.Instance.SaveGameController.GetSaveFilePath(slot); }

        static IEnumerator Scenario(TestHarness h)
        {
            bool host = NetPlay.IsHost;
            h.Note("coop save scenario role=" + (host ? "host" : "guest") + " expect=" + s_expect);
            CoopSaves.TestReady = true;
            float until = Time.realtimeSinceStartup + 90f;
            if (host)
            {
                while (Time.realtimeSinceStartup < until)
                {
                    if (CoopSaves.Outcome == "cancel" && !NetPlay.Enabled) break;
                    if (CoopSaves.Outcome.Length > 0 && CoopSaves.Outcome != "cancel" && SaveSync.Ready) break;
                    yield return null;
                }
                h.Check(CoopSaves.Outcome == s_expect, "CS host outcome " + CoopSaves.Outcome + " (expected " + s_expect + ")");
                bool diverged = s_expect.StartsWith("diverged") || s_expect == "cancel";
                h.Check(CoopSaves.DivergenceSeen == diverged, "CS divergence detected = " + CoopSaves.DivergenceSeen);
                if (s_expect == "cancel")
                {
                    h.Check(!NetPlay.Enabled, "CS cancel ended online play");
                    h.Check(CoopSaves.Commits == 0, "CS cancel wrote nothing (commits " + CoopSaves.Commits + ")");
                }
                else
                {
                    h.Check(SaveSync.Ready, "CS guest joined after the choice");
                    CheckHostLink(h);
                }
                if (s_expect == "diverged-partner")
                {
                    int slot = SaveSlotsManager.CurrentSlotIndex;
                    byte[] bytes = File.ReadAllBytes(Path(slot));
                    h.Check(CoopSaves.Hash(bytes) == s_partnerHash, "CS host slot now holds the partner's progress");
                    h.Check(CoopSaves.BackupsMade > 0, "CS host slot backed up before overwrite (" + CoopSaves.BackupsMade + ")");
                    // run-coopsave.ps1 adds 5 hours to the partner's copy: only a real load shows that play time.
                    CoopSaves.SaveHeader ph = CoopSaves.ReadHeader(bytes);
                    h.Check(ph != null && CoopSaves.AdoptedHours == ph.Hours, "CS host game loaded the partner's progress (hours " + CoopSaves.AdoptedHours + " = " + (ph == null ? -1 : ph.Hours) + ")");
                }
                // Quitting ends the session and resets the guest's SaveSync.Ready: stay until the guest has checked.
                if (NetPlay.Enabled)
                {
                    float by = Time.realtimeSinceStartup + 60f;
                    while (NetPlay.LastTestSignal != "cs-done" && NetPlay.Connected && Time.realtimeSinceStartup < by) yield return null;
                    h.Check(NetPlay.LastTestSignal == "cs-done", "CS guest finished its checks");
                }
            }
            else
            {
                while (Time.realtimeSinceStartup < until)
                {
                    if (CoopSaves.CancelledByHost) break;
                    if (CoopSaves.GuestWrites > 0 && SaveSync.Ready) break;
                    yield return null;
                }
                if (s_expect == "cancel")
                {
                    h.Check(CoopSaves.CancelledByHost, "CS guest told the host cancelled");
                    h.Check(CoopSaves.GuestWrites == 0 && CoopSaves.BackupsMade == 0, "CS guest save untouched on cancel");
                    yield return Wait(1.5f);
                }
                else
                {
                    h.Check(SaveSync.Ready, "CS guest loaded the chosen progress");
                    CheckGuestCopy(h);
                    if (s_expect == "new") h.Check(CoopSaves.BackupsMade == 0, "CS new lineage overwrote nothing");
                    if (s_expect == "diverged-host") h.Check(CoopSaves.BackupsMade > 0, "CS guest's solo copy backed up before the host's progress replaced it");
                    if (s_expect == "diverged-partner") h.Check(CoopSaves.LastCommitHash == s_partnerHash, "CS guest copy = its own progress written back by the host");
                    NetPlay.SendTestSignal("cs-done");
                }
            }
            yield return Wait(2f);
        }
    }
}
