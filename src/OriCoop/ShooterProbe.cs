using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;
using OriCoop.World;

namespace OriCoop
{
    // T-250 (#338) probe, TRACE ONLY: no behaviour change, nothing on the wire. Level mechanisms that shoot (the Ginso
    // Tree puzzle shooters, sequence "shootProjectile") fire on each PC on its own. Which sync model fits (the owner
    // sends each fire event, or the host's timer phase is followed) depends on game facts the mod cannot see yet:
    // what starts the sequence (a timer, a trigger zone, an animation), whether the shot is aimed at Ori, and what the
    // projectile is. For the first starts of each such sequence per scene visit (ShooterProbeGate) this writes:
    //   phys shooter.start   id seq scene n path guid running index pos ori_dist spawn_action actions caller
    //   phys shooter.action  id action path f_<field>...           (each SpawnProjectileAction once)
    //   phys shooter.holders id count scanned                      (once per sequence: components that reference it)
    //   phys shooter.holder  id holder type field path f_<field>...
    //   phys shooter.spawn   id seq n spawned ori partner p<i>_name/_pos/_vel/_grav/_life/_to_ori_deg/_to_partner_deg
    //   phys shooter.projectile name f_<field>...                  (each projectile prefab name once)
    // id = WorldObjSync.IdOf (same on both PCs, for tools\merge-coop-logs.py). Every hook only reads; all in try/catch.
    public static class ShooterProbe
    {
        const string Sub = "phys";
        const int PerVisit = 4, Total = 300, MaxFields = 24, MaxHolders = 6, MaxHolderScans = 40, MaxProjectiles = 3;
        static readonly ShooterProbeGate s_starts = new ShooterProbeGate(PerVisit, Total);
        static readonly ShooterProbeGate s_spawns = new ShooterProbeGate(PerVisit, Total);
        static readonly Dictionary<int, bool> s_shooters = new Dictionary<int, bool>();
        static readonly HashSet<int> s_described = new HashSet<int>();
        static readonly HashSet<string> s_projectileTypes = new HashSet<string>();
        static readonly Dictionary<Type, FieldInfo[]> s_dumpFields = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, FieldInfo[]> s_refFields = new Dictionary<Type, FieldInfo[]>();
        static readonly List<Projectile> s_spawned = new List<Projectile>();
        // By name, so the probe needs nothing of the game at compile time beyond what the mod already uses.
        static readonly Type s_spawnAction = AccessTools.TypeByName("SpawnProjectileAction");
        static int s_spawnDepth, s_holderScans;
        public static int StartsLogged { get { return s_starts.Logged; } }

        static void Patch(List<string> failed, Type type, string method, Type[] args, string prefix, string postfix, string finalizer)
        {
            try
            {
                if (type == null) throw new MissingMethodException();
                MethodInfo target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                if (target == null) throw new MissingMethodException();
                Plugin.H.Patch(target,
                    prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(ShooterProbe), prefix)),
                    postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(ShooterProbe), postfix)),
                    null,
                    finalizer == null ? null : new HarmonyMethod(AccessTools.Method(typeof(ShooterProbe), finalizer)), null);
            }
            catch { failed.Add("ShooterProbe:" + (type == null ? "?" : type.Name) + "." + method); }
        }

        // From CoopTraceHooks.Init (failures are logged there, once).
        internal static void Init(List<string> failed)
        {
            Patch(failed, typeof(ActionSequence), "Perform", null, null, "SequencePerformed", null);
            Patch(failed, s_spawnAction, "Perform", null, "SpawnBefore", null, "SpawnAfter");
            Patch(failed, typeof(InstantiateUtility), "Instantiate", new Type[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) }, null, "Instantiated", null);
            Patch(failed, typeof(InstantiateUtility), "Instantiate", new Type[] { typeof(UnityEngine.Object) }, null, "Instantiated", null);
            Patch(failed, typeof(Projectile), "OnPoolSpawned", null, null, "PoolSpawned", null);
        }

        // #436 follow-up: the Ginso puzzle shooters are not OnTimerPerformAction (WorldSync's timer census found none
        // there, only TimedDoor, TimedTrigger, ProjectileSpawner, SpawnProjectileAction). Once per object, from that
        // census (the host's first resend after a scene loads, so it does not need Ori near the shooter):
        //   phys shooter.save type path scene active archive_len archive_f0 parent_seq f_<field>...
        // archive_f0: the first 4 bytes of its Archive as a float (its saved timer, if it has one).
        const int MaxInventory = 60;
        static readonly HashSet<int> s_inventoried = new HashSet<int>();
        internal static void Inventory(Component c, byte[] archive)
        {
            try
            {
                if (c == null || s_inventoried.Count >= MaxInventory || !s_inventoried.Add(c.GetInstanceID())) return;
                List<object> kv = new List<object> { "type", c.GetType().Name, "path", PathOf(c.transform), "scene", SceneName(c),
                    "active", c.gameObject.activeInHierarchy, "archive_len", archive == null ? -1 : archive.Length,
                    "archive_f0", archive != null && archive.Length >= 4 ? Num(BitConverter.ToSingle(archive, 0)) : "-" };
                ActionSequence parent = c.GetComponentInParent<ActionSequence>();
                kv.Add("parent_seq"); kv.Add(parent == null ? "none" : parent.name);
                int n = 0;
                foreach (KeyValuePair<string, string> f in Dump(c))
                {
                    if (n++ >= MaxFields) break;
                    kv.Add("f_" + f.Key); kv.Add(f.Value);
                }
                Emit("shooter.save", kv);
            }
            catch { }
        }

        // From CoopTraceHooks.SceneLoaded: a new visit of the scene logs its shooters' first starts again.
        internal static void SceneLoaded(string scene) { s_starts.SceneLoaded(scene); s_spawns.SceneLoaded(scene); }

        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }

        static string SceneName(Component c)
        {
            SceneRoot root = c.GetComponentInParent<SceneRoot>();
            if (root != null && root.MetaData != null) return root.MetaData.SceneName;
            return c.gameObject.scene.name;
        }

        static string PathOf(Transform t)
        {
            string path = "";
            for (; t != null; t = t.parent) path = path.Length == 0 ? t.name : t.name + "/" + path;
            return path;
        }

        static bool IsSpawn(object action) { return action != null && s_spawnAction != null && s_spawnAction.IsInstanceOfType(action); }

        static List<Component> SpawnActions(ActionSequence sequence)
        {
            List<Component> found = new List<Component>();
            if (s_spawnAction == null) return found;
            if (sequence.Actions != null)
                foreach (ActionMethod action in sequence.Actions) if (IsSpawn(action) && !found.Contains(action)) found.Add(action);
            foreach (Component c in sequence.GetComponentsInChildren(s_spawnAction, true)) if (!found.Contains(c)) found.Add(c);
            return found;
        }

        // A non-story sequence that spawns a projectile, or is named like the shooters in the playtest (in case they
        // spawn through another action type).
        static bool IsShooter(ActionSequence sequence)
        {
            int key = sequence.GetInstanceID(); bool shooter;
            if (s_shooters.TryGetValue(key, out shooter)) return shooter;
            shooter = (SpawnActions(sequence).Count > 0 || sequence.name.IndexOf("shootProjectile", StringComparison.OrdinalIgnoreCase) >= 0)
                && !StorySync.IsStoryAction(sequence);
            s_shooters[key] = shooter;
            return shooter;
        }

        static List<string> CallerFrames()
        {
            List<string> frames = new List<string>();
            System.Diagnostics.StackFrame[] all = new System.Diagnostics.StackTrace(1, false).GetFrames();
            if (all == null) return frames;
            foreach (System.Diagnostics.StackFrame f in all)
            {
                MethodBase m = f.GetMethod(); if (m == null) continue;
                frames.Add((m.DeclaringType == null ? "" : m.DeclaringType.FullName) + "." + m.Name);
            }
            return frames;
        }

        static string Num(float f) { return f.ToString("0.###", CultureInfo.InvariantCulture); }

        static string Show(object v)
        {
            if (v == null) return "null";
            UnityEngine.Object o = v as UnityEngine.Object;
            if (!object.ReferenceEquals(o, null)) return o == null ? "destroyed" : o.name + "#" + o.GetInstanceID() + ":" + o.GetType().Name;
            if (v is float) return Num((float)v);
            if (v is Vector2) { Vector2 p = (Vector2)v; return Num(p.x) + "," + Num(p.y); }
            if (v is Vector3) { Vector3 p = (Vector3)v; return Num(p.x) + "," + Num(p.y) + "," + Num(p.z); }
            if (v is string) return (string)v;
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum) return Convert.ToString(v, CultureInfo.InvariantCulture);
            ICollection c = v as ICollection;
            if (c != null) return t.Name + "[" + c.Count + "]";
            if (v is AnimationCurve) { AnimationCurve curve = (AnimationCurve)v; return "curve[" + curve.length + "]"; }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static FieldInfo[] DumpFields(Type type)
        {
            FieldInfo[] fields;
            if (s_dumpFields.TryGetValue(type, out fields)) return fields;
            List<FieldInfo> list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Component) && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.Name.IndexOf('<') < 0) list.Add(f);
            fields = list.ToArray(); s_dumpFields[type] = fields;
            return fields;
        }

        static List<KeyValuePair<string, string>> Dump(object o)
        {
            List<KeyValuePair<string, string>> values = new List<KeyValuePair<string, string>>();
            foreach (FieldInfo f in DumpFields(o.GetType()))
            {
                string value;
                try { value = Show(f.GetValue(o)); } catch (Exception e) { value = "err:" + e.GetType().Name; }
                values.Add(new KeyValuePair<string, string>(f.Name, value));
            }
            return values;
        }

        static void Emit(string name, List<object> kv) { CoopTrace.Event(Sub, name, kv.ToArray()); }

        static void SequencePerformed(ActionSequence __instance)
        {
            try
            {
                if (__instance == null || !CoopTrace.On(Sub, 1) || !IsShooter(__instance)) return;
                if (WorldObjSync.DroppedNow(__instance)) return;   // a guest's own shot dropped for the host's (#444): did not run
                string scene = SceneName(__instance); int id = WorldObjSync.IdOf(__instance);
                int n = s_starts.Next(scene, id.ToString(CultureInfo.InvariantCulture));
                if (n == 0) return;
                List<string> types = new List<string>();
                if (__instance.Actions != null) foreach (ActionMethod a in __instance.Actions) types.Add(a == null ? "null" : a.GetType().Name);
                bool guid = __instance.MoonGuid != null && __instance.MoonGuid != MoonGuid.Empty;
                Vector3 at = __instance.transform.position; SeinCharacter own = Own;
                CoopTrace.Event(Sub, "shooter.start", "id", id, "seq", __instance, "scene", scene, "n", n,
                    "path", PathOf(__instance.transform), "guid", guid ? __instance.MoonGuid.ToString() : "-",
                    "running", __instance.IsRunning, "index", __instance.Index, "pos", at,
                    "ori_dist", own == null ? -1f : Vector3.Distance(own.Position, at), "spawn_action", SpawnActions(__instance).Count,
                    "actions", ShooterProbeText.Types(types, 280), "caller", ShooterProbeText.Frames(CallerFrames(), 8));
                if (s_described.Add(__instance.GetInstanceID())) Describe(__instance, id);
            }
            catch { }
        }

        static void Describe(ActionSequence sequence, int id)
        {
            foreach (Component action in SpawnActions(sequence))
            {
                if (!s_described.Add(action.GetInstanceID())) continue;
                List<object> kv = new List<object> { "id", id, "action", action, "path", PathOf(action.transform) };
                ShooterProbeText.AddFields(kv, "f_", Dump(action), MaxFields);
                Emit("shooter.action", kv);
            }
            if (s_holderScans >= MaxHolderScans) return;
            s_holderScans++;
            int count = 0, scanned = 0;
            foreach (MonoBehaviour c in sequence.transform.root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null || c == sequence) continue;
                scanned++;
                string field = Reference(c, sequence);
                if (field == null) continue;
                count++;
                if (count > MaxHolders) continue;
                List<object> kv = new List<object> { "id", id, "holder", c, "type", c.GetType().FullName, "field", field, "path", PathOf(c.transform) };
                ShooterProbeText.AddFields(kv, "f_", Dump(c), MaxFields);
                Emit("shooter.holder", kv);
            }
            CoopTrace.Event(Sub, "shooter.holders", "id", id, "count", count, "scanned", scanned);
        }

        static FieldInfo[] ReferenceFields(Type type)
        {
            FieldInfo[] fields;
            if (s_refFields.TryGetValue(type, out fields)) return fields;
            List<FieldInfo> list = new List<FieldInfo>();
            foreach (FieldInfo f in DumpFields(type))
            {
                Type ft = f.FieldType;
                if (typeof(ActionMethod).IsAssignableFrom(ft)) list.Add(f);
                else if (ft.IsArray && typeof(ActionMethod).IsAssignableFrom(ft.GetElementType())) list.Add(f);
                else if (ft.IsGenericType && typeof(IEnumerable).IsAssignableFrom(ft))
                    foreach (Type arg in ft.GetGenericArguments()) if (typeof(ActionMethod).IsAssignableFrom(arg)) { list.Add(f); break; }
            }
            fields = list.ToArray(); s_refFields[type] = fields;
            return fields;
        }

        // The name of the field of c that holds the sequence (directly or in a list), or null.
        static string Reference(Component c, ActionSequence sequence)
        {
            foreach (FieldInfo f in ReferenceFields(c.GetType()))
            {
                object v; try { v = f.GetValue(c); } catch { continue; }
                if (object.ReferenceEquals(v, sequence)) return f.Name;
                IEnumerable list = v as IEnumerable;
                if (list != null) foreach (object item in list) if (object.ReferenceEquals(item, sequence)) return f.Name + "[]";
            }
            return null;
        }

        static void SpawnBefore(out int __state)
        {
            __state = -1;
            try { if (!CoopTrace.On(Sub, 1)) return; __state = s_spawned.Count; s_spawnDepth++; } catch { }
        }

        static void Instantiated(UnityEngine.Object __result)
        {
            try
            {
                if (s_spawnDepth == 0 || __result == null) return;
                GameObject go = __result as GameObject;
                if (go == null) { Component c = __result as Component; if (c != null) go = c.gameObject; }
                Projectile p = go == null ? null : go.GetComponent<Projectile>();
                if (p != null && !s_spawned.Contains(p)) s_spawned.Add(p);
            }
            catch { }
        }

        static void PoolSpawned(Projectile __instance)
        {
            try { if (s_spawnDepth > 0 && __instance != null && !s_spawned.Contains(__instance)) s_spawned.Add(__instance); } catch { }
        }

        static void SpawnAfter(Component __instance, int __state)
        {
            if (__state < 0) return;
            try
            {
                s_spawnDepth--;
                if (__instance == null) return;
                string scene = SceneName(__instance); int id = WorldObjSync.IdOf(__instance);
                int n = s_spawns.Next(scene, id.ToString(CultureInfo.InvariantCulture));
                if (n == 0) return;
                ActionSequence sequence = __instance.GetComponentInParent<ActionSequence>();
                SeinCharacter own = Own; Vector3 ori = own == null ? Vector3.zero : own.Position, partner = NetPlay.LatestRemotePos;
                bool online = NetPlay.Enabled && NetPlay.Connected;
                int spawned = Math.Max(0, s_spawned.Count - __state);
                List<object> kv = new List<object> { "id", id, "action", __instance, "seq", sequence == null ? 0 : WorldObjSync.IdOf(sequence),
                    "scene", scene, "n", n, "spawned", spawned, "ori", ori, "partner", online ? (object)partner : "-" };
                for (int i = 0; i < spawned && i < MaxProjectiles; i++)
                {
                    Projectile p = s_spawned[__state + i]; if (p == null) continue;
                    Vector3 pos = p.Position; Vector3 vel = p.SpeedVector; string at = "p" + i + "_";
                    kv.Add(at + "name"); kv.Add(p.name); kv.Add(at + "pos"); kv.Add(pos); kv.Add(at + "vel"); kv.Add(vel);
                    kv.Add(at + "grav"); kv.Add(p.Gravity); kv.Add(at + "life"); kv.Add(p.MaximumLiveTime);
                    kv.Add(at + "to_ori_deg"); kv.Add(own == null ? -1f : ShooterProbeText.AngleDeg(vel.x, vel.y, ori.x - pos.x, ori.y - pos.y));
                    kv.Add(at + "to_partner_deg"); kv.Add(online ? ShooterProbeText.AngleDeg(vel.x, vel.y, partner.x - pos.x, partner.y - pos.y) : -1f);
                }
                Emit("shooter.spawn", kv);
                for (int i = 0; i < spawned && i < MaxProjectiles; i++)
                {
                    Projectile p = s_spawned[__state + i];
                    if (p == null || !s_projectileTypes.Add(p.name)) continue;
                    List<object> pk = new List<object> { "id", id, "name", p.name, "type", p.GetType().FullName };
                    ShooterProbeText.AddFields(pk, "f_", Dump(p), MaxFields);
                    Emit("shooter.projectile", pk);
                }
            }
            catch { }
            finally { if (s_spawnDepth <= 0) { s_spawnDepth = 0; s_spawned.Clear(); } }
        }
    }
}
