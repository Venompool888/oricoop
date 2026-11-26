using Game;
using UnityEngine;

namespace OriCoop
{
    internal static class KeystoneRecovery
    {
        static int s_lastGrantFrame = -1;

        internal static bool CanGrant(int openingSeat)
        {
            if (openingSeat <= 0 || !NetPlay.Enabled || !NetPlay.IsHost || SaveSync.HostAdopting) return false;
            if (NetPlay.UsesCServer)
            {
                if (!CsLobby.IsManager || openingSeat != CsLobby.ManagerSeat) return false;
            }
            else if (openingSeat != 1) return false;
            return NetPlay.LocalInGameplay() && Characters.Sein.Inventory != null;
        }

        internal static bool TryGrant(int openingSeat)
        {
            if (!CoopMenu.IsOpen || CoopMenu.Page != CoopMenu.PageSettings || openingSeat != CoopMenu.OpeningSeat
                || !CanGrant(openingSeat) || s_lastGrantFrame == Time.frameCount) return false;
            SeinInventory inventory = Characters.Sein.Inventory;
            if (inventory.Keystones == int.MaxValue) return false;
            s_lastGrantFrame = Time.frameCount;
            inventory.CollectKeystones(1);
            ProgressSync.SendSnapshot();
            return true;
        }
    }
}
