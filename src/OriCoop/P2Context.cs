using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // While a P2 component runs, Characters.Sein / Characters.Current and Core.Input point at P2.
    // Every Unity callback of every component type found on P2 is wrapped by Prefix/Finalizer below;
    // the prefix checks whether *this instance* belongs to P2, so P1's copies of the same types are unaffected.
    public static class P2Context
    {
        public static SeinCharacter P1;
        static Puppet s_spare = new Puppet();
        // TODO(4p): phase 1 removes
        public static SeinCharacter P2
        {
            get { Puppet p = Current ?? Players.First; return p == null ? null : p.Sein; }
            set
            {
                Puppet p = Players.First;
                if (value == null)
                {
                    if (p != null) { p.Sein = null; Players.Remotes.Remove(p); s_spare = p; }
                    return;
                }
                if (p == null) { p = s_spare; Players.Remotes.Add(p); }
                p.Seat = Roles.Partner; p.Sein = value;
            }
        }
        // TODO(4p): phase 1 removes
        public static InputState P2Input { get { return (Current ?? Players.First ?? s_spare).Input; } }
        // TODO(4p): phase 1 removes
        public static HashSet<int> P2Ids { get { return (Current ?? Players.First ?? s_spare).Ids; } }

        struct Context
        {
            public Puppet Puppet;
            public SeinCharacter Sein;
            public ICharacter Character;
            public Ori Ori;
        }
        static readonly Stack<Context> s_contexts = new Stack<Context>();
        static readonly Stack<bool> s_entered = new Stack<bool>();
        static readonly HashSet<MethodBase> s_patched = new HashSet<MethodBase>();
        static readonly string[] Messages = { "Awake", "Start", "OnEnable", "OnDisable", "Update", "FixedUpdate", "LateUpdate",
            "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
            "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D", "OnAnimatorMove", "SetReferenceToSein" };

        public static bool Inside { get { return s_contexts.Count > 0; } }
        public static Puppet Current { get { return Inside ? s_contexts.Peek().Puppet : null; } }

        // P2's own spirit companion (a clone of P1's). Inside P2 context it is "the" companion (Characters.Ori),
        // so P2's Spirit Flame comes from it and it follows P2.
        // TODO(4p): phase 1 removes
        public static Ori P2Companion
        {
            get { return (Current ?? Players.First ?? s_spare).Companion; }
            set { (Current ?? Players.First ?? s_spare).Companion = value; }
        }

        // TODO(4p): phase 1 removes
        public static void Enter()
        {
            Enter(Current ?? Players.First ?? s_spare);
        }

        public static void Enter(Puppet p)
        {
            Puppet previous = Current;
            Context saved = new Context();
            saved.Puppet = p; saved.Sein = Characters.Sein; saved.Character = Characters.Current; saved.Ori = Characters.Ori;
            s_contexts.Push(saved);
            if (previous == p) return;
            if (previous != null) previous.Input.Swap();
            Characters.Sein = p.Sein;
            Characters.Current = p.Sein;
            if (p.Companion != null) Characters.Ori = p.Companion;
            p.Input.Swap();
        }

        public static void Exit()
        {
            Context saved = s_contexts.Pop();
            Puppet previous = Current;
            if (previous == saved.Puppet) return;
            saved.Puppet.Input.Swap();
            if (previous != null) previous.Input.Swap();
            Characters.Sein = saved.Sein;
            Characters.Current = saved.Character;
            Characters.Ori = saved.Ori;
        }

        // Objects outside P2's hierarchy that belong to P2 (its companion, its Bash aiming minigame).
        public static void AddOwned(GameObject go)
        {
            Puppet p = Current ?? Players.First ?? s_spare;
            if (go == null || p.Owned.Contains(go)) return;
            p.Owned.Add(go);
            foreach (Component c in go.GetComponentsInChildren<Component>(true)) if (c != null) p.Ids.Add(c.GetInstanceID());
            PatchTypesOn(go, Plugin.H);
        }

        public static void RefreshIds()
        {
            if (Players.First == null) { s_spare.Ids.Clear(); return; }
            foreach (Puppet p in Players.Remotes)
                RefreshIds(p);
        }

        public static void RefreshIds(Puppet p)
        {
            p.Ids.Clear();
            if (p.Sein == null) return;
            foreach (Component c in p.Sein.GetComponentsInChildren<Component>(true))
                if (c != null) p.Ids.Add(c.GetInstanceID());
            p.Owned.RemoveAll(g => g == null);
            foreach (GameObject g in p.Owned)
                foreach (Component c in g.GetComponentsInChildren<Component>(true))
                    if (c != null) p.Ids.Add(c.GetInstanceID());
        }

        // TODO(4p): phase 1 removes
        public static bool BelongsToP2(Component c)
        {
            return Players.Of(c) != null;
        }

        // ---- Touch context: any world object's trigger/collision callback whose "other" collider is P2 runs as if
        // P2 were "the" Sein, with P2's buttons. Covers doors (enter with Up), springs, portals, levers, water,
        // checkpoints, damage zones... anything that asks "is this Characters.Sein?" about the toucher.
        static readonly HashSet<MethodBase> s_touchPatched = new HashSet<MethodBase>();
        static readonly string[] TouchMessages = { "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit" };

        public static void PatchTouchTypes(Harmony h)
        {
            // One typed prefix per argument type: an `object[] __args` prefix made Harmony allocate an array on
            // every OnTrigger*/OnCollision* call of every game type, every physics step, in single player too.
            MethodInfo colliderPrefix = typeof(P2Context).GetMethod("TouchPrefixCollider", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo collisionPrefix = typeof(P2Context).GetMethod("TouchPrefixCollision", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo fin = typeof(P2Context).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assembly game = typeof(SeinCharacter).Assembly;
            Type[] all;
            try { all = game.GetTypes(); }
            catch (ReflectionTypeLoadException e) { all = Array.FindAll(e.Types, t => t != null); }
            int n = 0;
            foreach (Type t in all)
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(t) || t.IsGenericTypeDefinition) continue;
                foreach (string name in TouchMessages)
                {
                    MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m == null || m.IsAbstract || m.IsGenericMethodDefinition || s_touchPatched.Contains(m)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1 || (ps[0].ParameterType != typeof(Collider) && ps[0].ParameterType != typeof(Collision))) continue;
                    MethodInfo prefix = ps[0].ParameterType == typeof(Collider) ? colliderPrefix : collisionPrefix;
                    try { HarmonyMethod guard = new HarmonyMethod(prefix); guard.priority = Priority.First; h.Patch(m, guard, null, null, new HarmonyMethod(fin), null); s_touchPatched.Add(m); n++; }
                    catch (Exception e) { Plugin.Log.LogWarning("touch patch " + t.Name + "." + name + " failed: " + e.Message); }
                }
            }
            Plugin.Log.LogInfo("touch context: wrapped " + n + " trigger/collision callbacks");
        }

        public static int TouchEnters;

        // Only physical movement callbacks may act on an online puppet. Water/float and most constraint
        // zones use position registries instead of touches; listing them documents the permitted policy.
        // MovingPlatformsController runs on the Ori's own PlatformMovement, so it is not blocked here.
        static readonly Type[] MovementTouchTypes = { typeof(Spring), typeof(WaterZone), typeof(FloatZone),
            typeof(WindArea), typeof(GravityDirectionChangerZone), typeof(MoveHorizontallyZone),
            typeof(RestrictJumpingZone), typeof(StopRunningInDirectionZone), typeof(ForceGrabReleaseZone),
            typeof(ConstrainPlayerMovementZone) };

        static bool MovementTouch(Component component)
        {
            if (component == null) return false;
            // Exact types: a subclass may add a story action to an otherwise physical zone.
            Type type = component.GetType();
            foreach (Type allowed in MovementTouchTypes) if (type == allowed) return true;
            return false;
        }

        static bool TouchPrefixCollider(object __instance, Collider __0) { return Touch(__instance as Component, __0); }
        static bool TouchPrefixCollision(object __instance, Collision __0) { return Touch(__instance as Component, __0 != null ? __0.collider : null); }

        static bool Touch(Component self, Component other)
        {
            Puppet puppet = null;
            // Most touches are not by a puppet (none at all in single player): ask that first, so the
            // self-is-an-Ori hierarchy walks below only run when a puppet really is the toucher.
            Puppet touched = Players.Of(other);
            if (touched != null)
            {
                // Not for the Oris' own components: P1's sensors touching P2 must stay P1's.
                bool selfIsOri = self != null && ((P1 != null && self.transform.IsChildOf(P1.transform)) || BelongsToP2(self));
                bool puppetTouch = !selfIsOri && touched.Sein.gameObject.activeInHierarchy;
                // Enter/exit zones (the guide stone's text, T-024) count every Ori, puppets included (P2Extras zones):
                // skipping them here meant a partner standing there alone showed nothing on this screen.
                if (puppetTouch && NetPlay.Enabled && !MovementTouch(self) && !EnemySync.ProjectileTouch(self) && !(self is SeinEnterExitTrigger))
                {
                    s_entered.Push(false); // Finalizer still runs for a skipped callback.
                    return false;
                }
                if (puppetTouch && !Inside) puppet = touched;
            }
            s_entered.Push(puppet != null);
            if (puppet != null) { TouchEnters++; Enter(puppet); }
            return true;
        }

        public static void PatchTypesOn(GameObject root, Harmony h)
        {
            MethodInfo prefix = typeof(P2Context).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo fin = typeof(P2Context).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assembly game = typeof(SeinCharacter).Assembly;
            HashSet<Type> types = new HashSet<Type>();
            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) types.Add(mb.GetType());
            int n = 0;
            foreach (Type start in types)
            {
                // Walk up the hierarchy so inherited callbacks (declared on a base class) get wrapped too.
                for (Type t = start; t != null && t.Assembly == game; t = t.BaseType)
                {
                    foreach (string name in Messages)
                    {
                        MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (m == null || m.IsAbstract || m.IsGenericMethodDefinition || s_patched.Contains(m)) continue;
                        try { h.Patch(m, new HarmonyMethod(prefix), null, null, new HarmonyMethod(fin), null); s_patched.Add(m); n++; }
                        catch (Exception e) { Plugin.Log.LogWarning("patch " + t.Name + "." + name + " failed: " + e.Message); }
                    }
                }
            }
            Plugin.Log.LogInfo("P2 context: wrapped " + n + " methods across " + types.Count + " component types");
        }

        static void Prefix(object __instance)
        {
            Component c = __instance as Component;
            Puppet p = c == null ? null : Players.OfInstance(c.GetInstanceID());
            s_entered.Push(p != null);
            if (p != null) Enter(p);
        }

        // ---- Proximity context: world objects (enemies, near-Sein triggers, doors) see whichever Ori is
        // closer as "the" Sein, with that player's input. Stateless with a 1-unit bias toward P1 so two
        // components on the same object agree and the target doesn't flicker at equal distance.
        // TODO(4p): phase 1 removes
        public static bool P2IsNearer(Vector3 pos)
        {
            return Players.NearestPuppet(pos) != null;
        }

        static void ProxPrefix(object __instance)
        {
            Component c = __instance as Component;
            // Online doors and proximity story triggers see only the local owner. Enemy targeting is
            // deliberately left as phase 1 behavior until phase 3 supplies enemy ownership.
            // Runs per object per Update/FixedUpdate: decide that (and "no puppets at all") before any lookup.
            Puppet p = null;
            if (!Inside && c != null && Players.Remotes.Count != 0 && (!NetPlay.Enabled || c is Entity || c is EntityController)
                && Players.OfInstance(c.GetInstanceID()) == null)
                p = Players.NearestPuppet(c.transform.position);
            s_entered.Push(p != null);
            if (p != null) Enter(p);
        }

        // SavePedestal (spirit wells) and MapStone check "is Sein near me" in their own Update, so without these P2
        // could not save at a spirit well or use a map stone. (Online, the filter above keeps them local-owner only.)
        public static readonly Type[] ProximityRoots = { typeof(Entity), typeof(EntityController), typeof(NearSeinTrigger), typeof(DoorWithSlots), typeof(EnergyDoor), typeof(SavePedestal), typeof(MapStone) };

        public static void PatchProximityTypes(Harmony h)
        {
            MethodInfo prefix = typeof(P2Context).GetMethod("ProxPrefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo fin = typeof(P2Context).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assembly game = typeof(SeinCharacter).Assembly;
            Type[] all;
            try { all = game.GetTypes(); }
            catch (ReflectionTypeLoadException e) { all = Array.FindAll(e.Types, t => t != null); }
            int n = 0, types = 0;
            foreach (Type t in all)
            {
                bool match = false;
                foreach (Type r in ProximityRoots) if (r.IsAssignableFrom(t)) { match = true; break; }
                if (!match) continue;
                types++;
                foreach (string name in Messages)
                {
                    if (name == "SetReferenceToSein") continue;
                    MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m == null || m.IsAbstract || m.IsGenericMethodDefinition || s_patched.Contains(m)) continue;
                    try { h.Patch(m, new HarmonyMethod(prefix), null, null, new HarmonyMethod(fin), null); s_patched.Add(m); n++; }
                    catch (Exception e) { Plugin.Log.LogWarning("prox patch " + t.Name + "." + name + " failed: " + e.Message); }
                }
            }
            Plugin.Log.LogInfo("proximity context: wrapped " + n + " methods across " + types + " world types");
        }

        static Exception Finalizer(Exception __exception)
        {
            if (s_entered.Count > 0 && s_entered.Pop()) Exit();
            return __exception;
        }

        // These character states poll global object lists, so a trigger-only guard cannot cover them.
        [HarmonyPatch(typeof(SeinSoulFlame), "UpdateCharacterState")]
        static class PuppetSoulLink
        {
            static bool Prefix(SeinSoulFlame __instance) { return !NetPlay.Enabled || !Players.IsRemote(__instance); }
        }

        [HarmonyPatch(typeof(SeinLever), "UpdateCharacterState")]
        static class PuppetLever
        {
            static bool Prefix(SeinLever __instance) { return !NetPlay.Enabled || !Players.IsRemote(__instance); }
        }

        [HarmonyPatch(typeof(SeinDoorHandler), "OnDoorOverlap")]
        static class PuppetDoorOverlap
        {
            static bool Prefix(SeinDoorHandler __instance) { return !NetPlay.Enabled || !Players.IsRemote(__instance); }
        }

        [HarmonyPatch(typeof(SeinDoorHandler), "EnterIntoDoor")]
        static class PuppetDoorEnter
        {
            static bool Prefix(SeinDoorHandler __instance) { return !NetPlay.Enabled || !Players.IsRemote(__instance); }
        }

        public static void Reset()
        {
            while (Inside) Exit();
            s_entered.Clear();
            foreach (Puppet p in Players.Remotes) Reset(p);
        }

        public static void Reset(Puppet p)
        {
            foreach (GameObject g in p.Owned)
                if (g != null) { g.SetActive(false); UnityEngine.Object.Destroy(g); }
            p.Owned.Clear(); p.Companion = null; p.Ids.Clear();
        }
    }
}
