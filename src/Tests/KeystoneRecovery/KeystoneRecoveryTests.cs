using System;
using Game;
using OriCoop;

namespace UnityEngine { static class Time { public static int frameCount; } }
namespace Game
{
    static class Characters { public static readonly Character Sein = new Character(); }
    class Character { public SeinInventory Inventory = new SeinInventory(); }
}
class SeinInventory
{
    public int Keystones;
    public void CollectKeystones(int count) { Keystones += count; }
}
namespace OriCoop
{
    static class NetPlay
    {
        public static bool Enabled, IsHost, UsesCServer, Playing;
        public static bool LocalInGameplay() { return Playing; }
    }
    static class SaveSync { public static bool HostAdopting; }
    static class CsLobby { public static bool IsManager; public static int ManagerSeat; }
    static class CoopMenu
    {
        public const int PageSettings = 0;
        public static int Page, OpeningSeat;
        public static bool IsOpen;
    }
    static class ProgressSync { public static int Sent; public static void SendSnapshot() { Sent++; } }
}

class KeystoneRecoveryTests
{
    static int s_count;
    static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        s_count++;
        Console.WriteLine("PASS " + message);
    }

    static void Main()
    {
        NetPlay.Playing = CoopMenu.IsOpen = true;
        CoopMenu.OpeningSeat = 1;
        Check(!KeystoneRecovery.CanGrant(1), "offline play has no room host");
        NetPlay.Enabled = true;
        Check(!KeystoneRecovery.TryGrant(1), "a room member cannot grant");
        NetPlay.IsHost = true;
        Check(KeystoneRecovery.CanGrant(1) && !KeystoneRecovery.CanGrant(2), "only the host seat can open the action");
        CoopMenu.IsOpen = false;
        Check(!KeystoneRecovery.TryGrant(1), "closing F8 cancels a pending activation");
        CoopMenu.IsOpen = true; CoopMenu.Page = 1;
        Check(!KeystoneRecovery.TryGrant(1), "a different menu page cannot grant");
        CoopMenu.Page = CoopMenu.PageSettings; CoopMenu.OpeningSeat = 2;
        Check(!KeystoneRecovery.TryGrant(1), "a couch guest's menu cannot act as the host");
        CoopMenu.OpeningSeat = 1; SaveSync.HostAdopting = true;
        Check(!KeystoneRecovery.TryGrant(1), "an incoming save cannot overwrite a just-granted stone");
        SaveSync.HostAdopting = false; NetPlay.Playing = false;
        Check(!KeystoneRecovery.TryGrant(1), "the action cannot run during loading");
        NetPlay.Playing = true;
        Check(KeystoneRecovery.TryGrant(1) && !KeystoneRecovery.TryGrant(1)
            && Characters.Sein.Inventory.Keystones == 1 && ProgressSync.Sent == 1,
            "two input routes for one press collect and publish exactly one stone");
        UnityEngine.Time.frameCount++; NetPlay.UsesCServer = true; CsLobby.ManagerSeat = 3;
        Check(!KeystoneRecovery.TryGrant(1), "the old C-server manager loses permission immediately");
        CsLobby.IsManager = true; CoopMenu.OpeningSeat = 3;
        Check(KeystoneRecovery.TryGrant(3) && Characters.Sein.Inventory.Keystones == 2 && ProgressSync.Sent == 2,
            "the promoted manager on P3 grants and publishes the next stone");
        UnityEngine.Time.frameCount++; Characters.Sein.Inventory.Keystones = int.MaxValue;
        Check(!KeystoneRecovery.TryGrant(3) && Characters.Sein.Inventory.Keystones == int.MaxValue && ProgressSync.Sent == 2,
            "a full counter never rolls over or publishes an extra update");
        Console.WriteLine("Keystone recovery: " + s_count + " cases passed");
    }
}
