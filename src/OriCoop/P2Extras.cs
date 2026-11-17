using System.Collections.Generic;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Single-Ori assumptions in specific game objects, fixed one by one.
    public static class P2Extras
    {
        // ---- P2's own spirit companion -------------------------------------------------------------------
        public static void SpawnCompanion(SeinCharacter sein)
        {
            SpawnCompanion(Players.OfSein(sein));
        }

        public static void SpawnCompanion(Puppet puppet)
        {
            Ori src = Characters.Ori;
            if (src == null || puppet.Sein == null) return;
            GameplayCamera cam = UI.Cameras.Current;
            Transform camTarget = cam != null ? cam.Target : null;
            P2Context.Enter(puppet);
            try
            {
                GameObject go = (GameObject)Object.Instantiate(src.gameObject, puppet.Sein.Position + new Vector3(0f, 1f, 0f), Quaternion.identity);
                go.name = "ori_P" + puppet.Seat;
                Object.DontDestroyOnLoad(go);
                puppet.Companion = go.GetComponent<Ori>();
                Characters.Ori = puppet.Companion;
                P2Context.AddOwned(go);
                ReturnCompanion(puppet, "spawned");
            }
            finally
            {
                P2Context.Exit();
                if (cam != null && camTarget != null) cam.Target = camTarget;
            }
        }

        // Puts P2's companion back to the game's normal "follow the player" state next to P2, as P2.
        public static void ReturnCompanion(string why)
        {
            ReturnCompanion(P2Context.Current ?? Players.First, why);
        }

        public static void ReturnCompanion(Puppet puppet, string why)
        {
            if (puppet == null || puppet.Sein == null) return;
            Ori c = puppet.Companion;
            if (c == null) return;
            GameplayCamera cam = UI.Cameras.Current;
            Transform camTarget = cam != null ? cam.Target : null;
            P2Context.Enter(puppet);
            try { c.BackToPlayerController(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("companion return (" + why + "): " + e.Message); }
            finally { P2Context.Exit(); }
            // Ori.ChangeState(Hovering) also points the camera at "the current character" (P2 here); undo that.
            if (cam != null && camTarget != null) cam.Target = camTarget;
        }

        public static void SetCompanionActive(bool on)
        {
            SetCompanionActive(P2Context.Current ?? Players.First, on);
        }

        public static void SetCompanionActive(Puppet puppet, bool on)
        {
            if (puppet != null && puppet.Companion != null && puppet.Companion.gameObject.activeSelf != on) puppet.Companion.gameObject.SetActive(on);
        }

        // ---- Bash aiming minigame: P2's SeinBashAttack instantiates it outside P2's hierarchy; it reads the Bash
        // button and stick, so it must run with P2's input. -----------------------------------------------------
        public static int OwnedSpawns;
        static readonly System.Type s_bashGame = AccessTools.TypeByName("BashAttackGame");

        static void OnInstantiated(Object result)
        {
            if (!P2Context.Inside || P2Context.P2 == null || result == null) return;
            GameObject go = result as GameObject;
            if (go == null) { Component c = result as Component; if (c != null) go = c.gameObject; }
            if (go == null || s_bashGame == null || go.GetComponentInChildren(s_bashGame) == null) return;
            P2Context.AddOwned(go);
            OwnedSpawns++;
        }

        [HarmonyPatch(typeof(InstantiateUtility), "Instantiate", new System.Type[] { typeof(Object) })]
        static class Instantiate1 { static void Postfix(Object __result) { OnInstantiated(__result); } }

        [HarmonyPatch(typeof(InstantiateUtility), "Instantiate", new System.Type[] { typeof(Object), typeof(Vector3), typeof(Quaternion) })]
        static class Instantiate3 { static void Postfix(Object __result) { OnInstantiated(__result); } }

        // ---- Occupancy: with two Oris, "pressed / inside" must mean "at least one Ori is on it". ------------
        static readonly Dictionary<int, HashSet<int>> s_occupants = new Dictionary<int, HashSet<int>>();

        static HashSet<int> Occ(Object o)
        {
            HashSet<int> s;
            if (!s_occupants.TryGetValue(o.GetInstanceID(), out s)) { s = new HashSet<int>(); s_occupants[o.GetInstanceID()] = s; }
            return s;
        }

        static int OriId(Component c)
        {
            // Only the colliders the game itself reacts to (tag "Player"); an Ori has untagged child colliders too,
            // and counting those would let one of them "leave" for the whole Ori.
            if (c == null || !c.CompareTag("Player")) return 0;
            return ZoneOriId(c);
        }

        public static void ResetOccupancy() { s_occupants.Clear(); s_zones.Clear(); }
        public static string OccupantsOf(Object o)
        {
            HashSet<int> s;
            if (o is SeinEnterExitTrigger)
            {
                Zone z; if (!s_zones.TryGetValue(o.GetInstanceID(), out z) || z.Inside.Count == 0) return "none";
                s = new HashSet<int>(z.Inside.Keys);
            }
            else if (!s_occupants.TryGetValue(o.GetInstanceID(), out s)) return "none";
            string r = ""; foreach (int i in s) r += i + " "; return "[" + r + "]";
        }

        // Enter/exit zones remember the collider each Ori entered with. Unity sends no OnTriggerExit when that collider
        // is disabled, its object deactivated or destroyed, or when it is moved out by a teleport between physics
        // steps (partner death / revive / respawn, a puppet snapping to its network position, scene changes). Such an
        // Ori stayed "inside" forever and the zone never ran its exit action: the guide stone's text stayed on screen
        // after both players had walked away (playtest 2026-11-16, P5). Stale occupants are dropped on every exit and
        // twice a second; when the last one goes the exit action runs.
        sealed class Zone { public SeinEnterExitTrigger Trigger; public Collider Box; public readonly Dictionary<int, Collider> Inside = new Dictionary<int, Collider>(); }
        static readonly Dictionary<int, Zone> s_zones = new Dictionary<int, Zone>();
        static float s_nextZoneCheck;
        public static int StaleZoneExits;   // exit actions run because the last occupant went stale (tests / logs)

        static Zone ZoneOf(SeinEnterExitTrigger t)
        {
            Zone z;
            if (!s_zones.TryGetValue(t.GetInstanceID(), out z)) { z = new Zone(); z.Trigger = t; z.Box = t.GetComponent<Collider>(); s_zones[t.GetInstanceID()] = z; }
            return z;
        }

        // Seat-aware id: the local Ori is 1, every remote / local-P2 puppet 100 + its seat (3-4 players share zones too).
        static int ZoneOriId(Component c)
        {
            if (c == null || !c.CompareTag("Player")) return 0;
            Puppet p = Players.Of(c);
            return p != null ? 100 + p.Seat : 1;
        }

        static bool StillInside(Zone z, Collider c)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) return false;
            if (z.Box == null || !z.Box.enabled) return true;
            Bounds b = z.Box.bounds; b.Expand(0.25f);
            return b.Intersects(c.bounds);
        }

        // Test-only mutation switch: proves the "signpost" checks fail without the stale-occupant / late-material fixes.
        public static readonly bool MutateSignpost = System.Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SIGNPOST") == "1";

        static int Prune(Zone z)
        {
            if (MutateSignpost) return 0;
            List<int> gone = null;
            foreach (KeyValuePair<int, Collider> e in z.Inside)
                if (!StillInside(z, e.Value)) { if (gone == null) gone = new List<int>(); gone.Add(e.Key); }
            if (gone == null) return 0;
            foreach (int id in gone) z.Inside.Remove(id);
            return gone.Count;
        }

        public static void TickZones()
        {
            if (s_zones.Count == 0 || Time.unscaledTime < s_nextZoneCheck) return;
            s_nextZoneCheck = Time.unscaledTime + 0.5f;
            List<int> dead = null;
            foreach (KeyValuePair<int, Zone> e in s_zones)
            {
                Zone z = e.Value;
                if (z.Trigger == null) { if (dead == null) dead = new List<int>(); dead.Add(e.Key); continue; }
                if (z.Inside.Count == 0 || Prune(z) == 0 || z.Inside.Count > 0) continue;
                StaleZoneExits++;
                CoopTrace.Event("zone", "stale.exit", "zone", z.Trigger.name, "pos", z.Trigger.transform.position);
                Plugin.Log.LogInfo("zone: last Ori left '" + z.Trigger.name + "' without an exit event; running its exit action");
                if (z.Trigger.OnExitAction != null) z.Trigger.OnExitAction.Perform(null);
            }
            if (dead != null) foreach (int id in dead) s_zones.Remove(id);
        }

        // Pressure plate: released only when the last Ori steps off.
        [HarmonyPatch(typeof(PressurePlate), "OnCollision")]
        static class PlateOn
        {
            static void Prefix(PressurePlate __instance, Collision collision)
            {
                int id = OriId(collision.collider);
                if (id != 0) Occ(__instance).Add(id);
            }
        }

        [HarmonyPatch(typeof(PressurePlate), "OnCollisionExit")]
        static class PlateOff
        {
            static bool Prefix(PressurePlate __instance, Collision collision)
            {
                int id = OriId(collision.collider);
                if (id == 0) return true;
                HashSet<int> s = Occ(__instance);
                s.Remove(id);
                return s.Count == 0; // another Ori is still standing on it: keep it pressed
            }
        }

        // Enter/exit zones: enter action when the first Ori enters, exit action when the last one leaves.
        [HarmonyPatch(typeof(SeinEnterExitTrigger), "OnTriggerEnter")]
        static class ZoneEnter
        {
            static bool Prefix(SeinEnterExitTrigger __instance, Collider collider)
            {
                int id = ZoneOriId(collider);
                if (id == 0) return true;
                Zone z = ZoneOf(__instance);
                Prune(z);
                bool first = z.Inside.Count == 0;
                z.Inside[id] = collider;
                CoopTrace.Event("zone", "enter", "zone", __instance.name, "ori", id, "first", first, "pos", collider.transform.position);
                if (TestHarness.Enabled) Plugin.Log.LogInfo("zone: enter '" + __instance.name + "' by " + id + " (" + collider.name + ") first=" + first);
                return first;
            }
        }

        [HarmonyPatch(typeof(SeinEnterExitTrigger), "OnTriggerExit")]
        static class ZoneExit
        {
            static bool Prefix(SeinEnterExitTrigger __instance, Collider collider)
            {
                int id = ZoneOriId(collider);
                if (id == 0) return true;
                Zone z = ZoneOf(__instance);
                bool had = z.Inside.Remove(id);
                int stale = Prune(z);
                CoopTrace.Event("zone", "exit", "zone", __instance.name, "ori", id, "had", had, "stale", stale, "left", z.Inside.Count);
                if (TestHarness.Enabled) Plugin.Log.LogInfo("zone: exit '" + __instance.name + "' by " + id + " had=" + had + " stale=" + stale + " left=" + z.Inside.Count);
                return (had || stale > 0) && z.Inside.Count == 0;
            }
        }

        // Movement-constraint zones only listen to P1's movement; don't let P2 standing in one lock P1.
        static readonly System.Reflection.FieldInfo s_zoneSein = AccessTools.Field(typeof(ConstrainPlayerMovementZone), "m_sein");

        [HarmonyPatch(typeof(ConstrainPlayerMovementZone), "ModifyHorizontalPlatformMovementSettings")]
        static class ConstrainZone
        {
            static bool Prefix(ConstrainPlayerMovementZone __instance)
            {
                SeinCharacter occupant = (SeinCharacter)s_zoneSein.GetValue(__instance);
                return occupant == null || occupant == Characters.Sein;
            }
        }

        // Story cutscene triggers are player 1's: P2 walking into one doesn't start the cutscene (it would move P2
        // into position and play the scene while P1 is elsewhere). P1 leads the story; the screen edges keep P2 close.
        // With Story.P2CanTriggerCutscenes on, P2 may start them: P1 is brought to P2 and the trigger runs as P1.
        public static int CutscenesIgnored, CutscenesByP2;

        [HarmonyPatch(typeof(CutsceneTrigger), "OnTriggerEnter")]
        static class CutsceneOnlyP1
        {
            static bool Prefix(CutsceneTrigger __instance, Collider collider)
            {
                Puppet puppet = Players.Of(collider);
                if (puppet == null) return true;
                if (NetPlay.Enabled) { CutscenesIgnored++; return false; }
                SeinCharacter p1 = P2Context.P1;
                if (Plugin.Instance.P2CanTriggerCutscenes && p1 != null && p1.gameObject.activeInHierarchy)
                {
                    Plugin.Teleport(p1, puppet.Sein.Position);
                    CutscenesByP2++;
                    Collider p1col = p1.GetComponent<Collider>();
                    if (p1col != null) __instance.OnTriggerEnter(p1col);
                    return false;
                }
                CutscenesIgnored++;
                return false;
            }
        }
    }
}
