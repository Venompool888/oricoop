using System;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // T-101: spawning a puppet must not move the local view. SeinCharacter.Awake makes the new copy "the current
    // character" and SeinController.Awake then calls ChangeTargetToCurrentCharacter(), so the local camera targeted the
    // puppet until the co-op camera took it back 50-170 ms later; the puppet is then moved to the partner's real
    // position, so the view could flash over there. SeinController.Start also snapped the camera to its target.
    public static class PuppetCameraGuard
    {
        // Spawns whose Awake still retargeted the camera (SpawnPuppet restores it); the net test requires 0.
        public static int Steals;
        static bool s_puppetStart;
        static readonly bool s_mutate = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PUPPET_CAMERA") == "1";

        // Called by SpawnPuppet right after Instantiate: if the copy's Awake still took the camera, count it and give
        // the view back to whatever it followed before (the co-op target, P1, or a scripted target).
        internal static void AfterSpawn(GameplayCamera cam, Transform before, SeinCharacter spawned)
        {
            if (cam == null || spawned == null || cam.Target != spawned.Transform) return;
            Steals++;
            Plugin.Log.LogWarning("puppet spawn took the camera target; restored to " + (before == null ? "null" : before.name));
            if (!s_mutate) cam.Target = before;
        }

        static bool IsPuppet(SeinCharacter s)
        {
            return s != null && (s == P2Context.P2 || Players.OfSein(s) != null);
        }

        [HarmonyPatch(typeof(GameplayCamera), "ChangeTargetToCurrentCharacter")]
        static class KeepTargetWhileSpawning
        {
            static bool Prefix() { return s_mutate || !P2Lifecycle.SpawningP2; }
        }

        [HarmonyPatch(typeof(SeinController), "Start")]
        static class PuppetStart
        {
            static void Prefix(SeinController __instance) { s_puppetStart = !s_mutate && IsPuppet(__instance.Sein); }
            static Exception Finalizer(Exception __exception) { s_puppetStart = false; return __exception; }
        }

        [HarmonyPatch(typeof(GameplayCamera), "MoveCameraToTargetInstantly")]
        static class NoSnapForPuppetStart
        {
            static bool Prefix() { return !s_puppetStart; }
        }
    }
}
