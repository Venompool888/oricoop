using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Drop-through (one-way) platforms, per Ori.
    // The game decides "can the Ori pass through platforms right now?" every physics step and applies it with
    // Physics.IgnoreLayerCollision(player, platform): one global, per-layer switch (plus a static cache). With two
    // Oris on the same layer the last one to run decides for both, so P1 fell through platforms whenever P2 was not
    // standing on one. Here the same decision is applied per collider pair instead (this Ori's colliders vs each
    // platform collider), and the global layer pair is left colliding. Each Ori only changes its own pairs.
    public static class PlatformFix
    {
        class State { public bool Ignore; public int PlatformCount = -1; public float NextRefresh; }
        static readonly Dictionary<int, State> s_state = new Dictionary<int, State>();
        static bool s_layerReset;
        public static int Applies;
        static readonly bool s_mutate = System.Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_PLATFORMFIX") == "1";

        static void Apply(GoThroughPlatformHandler h, bool ignore, bool force)
        {
            State st;
            if (!s_state.TryGetValue(h.GetInstanceID(), out st)) { st = new State(); st.Ignore = !ignore; s_state[h.GetInstanceID()] = st; }
            int count = GoThroughPlatformManager.GoThroughPlatforms.Count;
            // Unity drops per-pair ignores when a collider is deactivated, and platforms come and go with scenes:
            // re-apply on any change, and once a second anyway.
            if (!force && st.Ignore == ignore && st.PlatformCount == count && Time.time < st.NextRefresh) return;
            st.Ignore = ignore; st.PlatformCount = count; st.NextRefresh = Time.time + 1f;
            Applies++;
            Collider[] mine = h.GetComponents<Collider>();
            for (int i = 0; i < count; i++)
            {
                GoThroughPlatform p = GoThroughPlatformManager.GoThroughPlatforms[i];
                if (p == null || p.Colliders == null) continue;
                for (int j = 0; j < p.Colliders.Length; j++)
                {
                    Collider c = p.Colliders[j];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                    for (int k = 0; k < mine.Length; k++)
                        if (mine[k] != null && mine[k].enabled && mine[k].gameObject.activeInHierarchy)
                            Physics.IgnoreCollision(mine[k], c, ignore);
                }
            }
        }

        public static bool IgnoringFor(GoThroughPlatformHandler h)
        {
            State st;
            return s_state.TryGetValue(h.GetInstanceID(), out st) && st.Ignore;
        }

        [HarmonyPatch(typeof(GoThroughPlatformHandler), "UpdateLayerCollision")]
        static class PerOri
        {
            static bool Prefix(GoThroughPlatformHandler __instance, bool ignore)
            {
                if (s_mutate) return true; // test-only: original global switch
                if (!s_layerReset)
                {
                    s_layerReset = true;
                    int player = LayerMask.NameToLayer("player"), platform = LayerMask.NameToLayer("platform");
                    if (player > 0 && platform > 0) Physics.IgnoreLayerCollision(player, platform, false);
                }
                if (__instance == null || !__instance.gameObject.activeInHierarchy) return false;
                Apply(__instance, ignore, false);
                return false; // never touch the global layer switch
            }
        }

        // A checkpoint restore / scene change can reset collision state; make every Ori re-apply next step.
        public static void Invalidate() { s_state.Clear(); }
    }
}
