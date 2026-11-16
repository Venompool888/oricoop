using System.Collections.Generic;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Runtime self-check of what players can see, every frame while P2 is in the game. Instead of trusting each
    // code path to keep P2's visuals right, check the end result: whatever P1 shows, P2 must show too.
    //   companion: if P1's spirit companion is shown, P2's must exist, be shown with the same renderers on, and stay near P2.
    // A violation that lasts past a short grace period is logged once per kind with full details (for diagnosis
    // from the owner's own play sessions) and repaired.
    public static class VisualInvariants
    {
        public static int Violations;
        public static readonly List<string> Kinds = new List<string>();
        static readonly Dictionary<string, float> s_since = new Dictionary<string, float>();
        static float s_graceUntil;
        static readonly Dictionary<int, float> s_moveSince = new Dictionary<int, float>();

        // Teleports, regroups, respawns and joins move things abruptly; give the companion time to catch up.
        public static void Grace(float seconds) { s_graceUntil = Mathf.Max(s_graceUntil, Time.unscaledTime + seconds); }

        static int EnabledRenderers(GameObject g)
        {
            int n = 0;
            foreach (Renderer r in g.GetComponentsInChildren<Renderer>(false)) if (r.enabled) n++;
            return n;
        }

        static bool Bad(string kind, bool bad, string details)
        {
            if (!bad) { s_since.Remove(kind); return false; }
            float t0;
            if (!s_since.TryGetValue(kind, out t0)) { s_since[kind] = Time.unscaledTime; return false; }
            if (Time.unscaledTime - t0 < 1f || Time.unscaledTime < s_graceUntil) return false;
            Violations++;
            if (!Kinds.Contains(kind)) { Kinds.Add(kind); Plugin.Log.LogWarning("VISUAL CHECK " + kind + ": " + details); }
            s_since.Remove(kind);
            return true;
        }

        public static void Check(SeinCharacter p1, SeinCharacter p2)
        {
            if (p1 == null || p2 == null || !p2.gameObject.activeInHierarchy || CoopMenu.IsOpen) return;
            Ori c1 = Characters.Ori;
            bool p1Shows = c1 != null && c1.gameObject.activeInHierarchy && p2.PlayerAbilities.SpiritFlame.HasAbility;
            if (!p1Shows) return;
            Ori c2 = P2Context.P2Companion;
            string where = " (area " + (Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null ? Core.Scenes.Manager.CurrentScene.Scene : "?")
                + ", P1 " + p1.Position + ", P2 " + p2.Position + ")";

            if (Bad("companion missing", c2 == null, "P2 has no companion object" + where))
            { P2Extras.SpawnCompanion(p2); return; }
            if (c2 == null) return;

            if (Bad("companion hidden", !c2.gameObject.activeInHierarchy, "P2's companion is inactive; P1's is shown" + where))
                c2.gameObject.SetActive(true);
            if (!c2.gameObject.activeInHierarchy) return;

            int r1 = EnabledRenderers(c1.gameObject), r2 = EnabledRenderers(c2.gameObject);
            if (Bad("companion renderers off", r2 < r1, "P2's companion has " + r2 + " renderers on, P1's has " + r1 + where))
            {
                Renderer[] a = c1.GetComponentsInChildren<Renderer>(true), b = c2.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < a.Length && i < b.Length; i++) { b[i].enabled = a[i].enabled; b[i].gameObject.SetActive(a[i].gameObject.activeSelf); }
            }

            float d = Vector3.Distance(c2.transform.position, p2.Position);
            if (Bad("companion far from P2", d > 8f, "P2's companion is " + d.ToString("F1") + " from P2 (state " + c2.CurrentState + ")" + where))
            { P2Extras.ReturnCompanion("far from P2"); return; }

            // The game sends "the" companion off on scripted moves (interest zones, doors, map stones) and later calls it
            // back through the global Characters.Ori, which is P1's. P2's must not stay on such a move forever.
            bool stuck = c2.CurrentState != Ori.State.Hovering && c1.CurrentState == Ori.State.Hovering;
            if (stuck) { float t0; if (!s_moveSince.TryGetValue(c2.GetInstanceID(), out t0)) s_moveSince[c2.GetInstanceID()] = Time.unscaledTime; }
            else s_moveSince.Remove(c2.GetInstanceID());
            float since;
            if (stuck && s_moveSince.TryGetValue(c2.GetInstanceID(), out since) && Time.unscaledTime - since > 4f)
            {
                s_moveSince.Remove(c2.GetInstanceID());
                Violations++;
                string k = "companion stuck in a scripted move";
                if (!Kinds.Contains(k)) { Kinds.Add(k); Plugin.Log.LogWarning("VISUAL CHECK " + k + ": state " + c2.CurrentState + " while P1's companion is hovering" + where); }
                P2Extras.ReturnCompanion("stuck in " + c2.CurrentState);
            }
        }

        public static void Reset() { s_since.Clear(); s_moveSince.Clear(); Grace(2f); }
    }
}
