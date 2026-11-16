using System;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Shared resources between the two Oris. P1's save is the source of truth.
    // - Keystones, map stones and energy are one pool: a change on either Ori since the last sync is applied
    //   to P1, then P1's value is copied to P2. (Health stays per-Ori.)
    // - P2 touching a pickup collects it for P1 (rewards land in P1's save; the pool sync shares them).
    public static class SharedProgress
    {
        static int s_keys, s_maps;
        static float s_energy;
        static bool s_init;

        public static void Reset() { s_init = false; }

        public static void Sync(SeinCharacter p1, SeinCharacter p2)
        {
            if (p1 == null || p2 == null || p1.Inventory == null || p2.Inventory == null || p1.Energy == null || p2.Energy == null) return;
            if (!s_init)
            {
                s_init = true;
                p2.Inventory.Keystones = p1.Inventory.Keystones;
                p2.Inventory.MapStones = p1.Inventory.MapStones;
                p2.Energy.Current = p1.Energy.Current;
            }
            else
            {
                p1.Inventory.Keystones = Mathf.Max(0, p1.Inventory.Keystones + (p2.Inventory.Keystones - s_keys));
                p1.Inventory.MapStones = Mathf.Max(0, p1.Inventory.MapStones + (p2.Inventory.MapStones - s_maps));
                p1.Energy.Current = Mathf.Clamp(p1.Energy.Current + (p2.Energy.Current - s_energy), 0f, p1.Energy.Max);
                p2.Inventory.Keystones = p1.Inventory.Keystones;
                p2.Inventory.MapStones = p1.Inventory.MapStones;
                p2.Energy.Current = p1.Energy.Current;
            }
            s_keys = p2.Inventory.Keystones;
            s_maps = p2.Inventory.MapStones;
            s_energy = p2.Energy.Current;
        }

        public static int PickupsByP2;

        static readonly System.Reflection.FieldInfo s_timeField = AccessTools.Field(typeof(PickupBase), "m_currentTime");

        [HarmonyPatch(typeof(PickupBase), "FixedUpdate")]
        static class PickupPatch
        {
            static void Postfix(PickupBase __instance)
            {
                SeinCharacter p1 = P2Context.P1, p2 = P2Context.P2;
                if (p1 == null || p2 == null || !p2.gameObject.activeInHierarchy || __instance.IsCollected || !__instance.gameObject.activeInHierarchy) return;
                if (Vector3.Distance(__instance.transform.position, p2.Position) >= __instance.Radius) return;
                if ((float)s_timeField.GetValue(__instance) < __instance.DelayBeforeCollectable) return;
                __instance.OnCollectorCandidateTouch(p1.gameObject);
                if (__instance.IsCollected) PickupsByP2++;
            }
        }
    }
}
