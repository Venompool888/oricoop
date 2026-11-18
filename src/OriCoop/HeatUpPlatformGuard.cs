using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // T-090: HeatUpPlatform.OnCollision (Mount Horu) reads componentInChildren.Sein before its null check, so while the
    // platform is Hot any collider without a SeinDamageReciever (the co-op puppet's parts, carried objects) throws a
    // NullReferenceException every physics step (scene sweep T-070: mountHoruHubMid). Keep the trigger bookkeeping the
    // game does and skip only the damage, which needs a receiver anyway.
    [HarmonyPatch(typeof(HeatUpPlatform), "OnCollision")]
    static class HeatUpPlatformGuard
    {
        static readonly FieldInfo Triggered = AccessTools.Field(typeof(HeatUpPlatform), "m_beingTriggered");

        static bool Prefix(HeatUpPlatform __instance, Collision collision)
        {
            if (collision == null || collision.gameObject == null || __instance.CurrentState != HeatUpPlatform.State.Hot) return true;
            SeinDamageReciever receiver = collision.gameObject.GetComponentInChildren<SeinDamageReciever>();
            if (receiver != null && receiver.Sein != null && receiver.Sein.Controller != null) return true;
            if (collision.gameObject.CompareTag("Player")) Triggered.SetValue(__instance, true);
            return false;
        }
    }
}
