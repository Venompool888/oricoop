using System.Collections;
using UnityEngine;

namespace OriCoop
{
    public static partial class NetTestHarness
    {
        static IEnumerator OpenKeystoneMenu(TestHarness h, bool permitted)
        {
            CoopMenu.Open();
            const string action = "OriCoopNativeGui_coop.settings.action.";
            float by = Time.realtimeSinceStartup + 4f;
            while (GameObject.Find(action + "1") == null && Time.realtimeSinceStartup < by) yield return null;
            // Allow one complete repaint after the close row is present, including removal of a former owner's row.
            yield return null;
            yield return null;
            h.Check(CoopMenu.IsOpen && CoopMenu.Page == CoopMenu.PageSettings && GameObject.Find(action + "1") != null,
                "T-259 F8 settings and close action are visible on seat " + Players.LocalSeat);
            bool drawn = GameObject.Find(action + "2") != null;
            h.Check((CoopMenu.KeystoneRow >= 0) == permitted && drawn == permitted,
                "T-259 F8 recovery action " + (permitted ? "shown to the host" : "hidden from the guest")
                + " (seat " + Players.LocalSeat + ", row " + CoopMenu.KeystoneRow + ", drawn " + drawn + ")");
        }

        static void CheckRecoveryDenied(TestHarness h, int staleRow)
        {
            int before = Own.Inventory.Keystones;
            CoopMenu.Activate(staleRow);
            bool granted = KeystoneRecovery.TryGrant(CoopMenu.OpeningSeat);
            h.Check(!granted && Own.Inventory.Keystones == before,
                "T-259 a guest cannot grant through a stale F8 action or its handler (seat " + Players.LocalSeat + ")");
        }

        // Run between two live seats, also after P1 leaves a C-server room and P2 takes over.
        static IEnumerator CheckKeystoneRecovery(TestHarness h, int hostSeat, int step)
        {
            bool host = Players.LocalSeat == hostSeat;
            try
            {
                yield return OpenKeystoneMenu(h, host);
                if (!host) CheckRecoveryDenied(h, CoopMenu.SettingsCloseRow + 1);
                int before = Own.Inventory.Keystones;
                PublishWorldBarrier(h, step, false);
                yield return WorldBarrier(h, step);
                WorldTestPacket peer;
                if (!s_worldPackets.TryGetValue(step, out peer)) yield break;
                h.Check(peer.Keys == before, "T-259 wallets agree before manual recovery (local " + before + ", peer " + peer.Keys + ")");
                if (host && CoopMenu.KeystoneRow >= 0) CoopMenu.Activate(CoopMenu.KeystoneRow);
                float by = Time.realtimeSinceStartup + 5f;
                while (Own.Inventory.Keystones != before + 1 && Time.realtimeSinceStartup < by) yield return null;
                PublishWorldBarrier(h, step + 1, false);
                yield return WorldBarrier(h, step + 1);
                if (s_worldPackets.TryGetValue(step + 1, out peer))
                    h.Check(Own.Inventory.Keystones == before + 1 && peer.Keys == before + 1,
                        "T-259 one F8 press gives exactly one shared keystone on both PCs (host P" + hostSeat
                        + ", before " + before + ", local " + Own.Inventory.Keystones + ", peer " + peer.Keys + ")");
            }
            finally { CoopMenu.Close(); }
        }
    }
}
