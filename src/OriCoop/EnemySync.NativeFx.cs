using System;
using Core;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using OriCoop.Enemies;

namespace OriCoop
{
    public static partial class EnemySync
    {
        const string FloorExplosion = "stompableFloorExplosionA";
        static readonly FieldInfo s_lastSpawn = AccessTools.Field(typeof(PrefabSpawner), "m_prefab");
        static readonly Func<GameObject, string> s_fxName = go => go == null ? null : go.name;

        static GameObject SpawnerPrefab(PrefabSpawner spawner)
        { return spawner == null || spawner.Prefab == null ? null : spawner.Prefab.Prefab(null); }

        static IEnumerable<GameObject> EnemyEffects(Enemy enemy)
        {
            if (enemy == null) yield break;
            JumperEnemy jumper = enemy as JumperEnemy;
            if (jumper != null) { yield return jumper.StompEffect; yield return jumper.LandEffect; }
            AcidSlugEnemy acid = enemy as AcidSlugEnemy;
            if (acid != null) yield return acid.Settings.ShootEffect;
            StarSlugEnemy star = enemy as StarSlugEnemy;
            if (star != null) yield return star.Settings.ShootEffect;
            KamikazeSootEnemy kamikaze = enemy as KamikazeSootEnemy;
            if (kamikaze != null) yield return kamikaze.KamikazeExplosion;
            SpitterEnemy spitter = enemy as SpitterEnemy;
            if (spitter != null) yield return SpawnerPrefab(spitter.SpitEffect);
            RammingEnemy ram = enemy as RammingEnemy;
            if (ram != null) yield return SpawnerPrefab(ram.HitWallEffect);
            ArmouredRammingEnemy armoured = enemy as ArmouredRammingEnemy;
            if (armoured != null) yield return SpawnerPrefab(armoured.ShatterEffect);
        }

        // T-233: effects a family list above doesn't name (the DropSlug's dropSlugEnemyExplosion, 6th playtest) are still
        // referenced by the enemy's own scripts or by its death action, as a GameObject field or a PrefabSpawner, so the
        // receiver can find them on its copy of the enemy, or on any loaded enemy of the same class (including prefabs).
        static readonly Dictionary<Type, FieldInfo[]> s_effectFields = new Dictionary<Type, FieldInfo[]>();

        static FieldInfo[] EffectFields(Type type)
        {
            FieldInfo[] fields;
            if (s_effectFields.TryGetValue(type, out fields)) return fields;
            var list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(GameObject) || f.FieldType == typeof(PrefabSpawner)) list.Add(f);
            return s_effectFields[type] = list.ToArray();
        }

        static IEnumerable<GameObject> ReferencedEffects(Component root)
        {
            if (root == null) yield break;
            foreach (PrefabSpawner spawner in root.GetComponentsInChildren<PrefabSpawner>(true)) yield return SpawnerPrefab(spawner);
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                foreach (FieldInfo f in EffectFields(behaviour.GetType()))
                {
                    object value;
                    try { value = f.GetValue(behaviour); } catch (Exception) { continue; }
                    GameObject go = value as GameObject;
                    if (go != null) { yield return go; continue; }
                    PrefabSpawner spawner = value as PrefabSpawner;
                    if (spawner != null) yield return SpawnerPrefab(spawner);
                }
            }
        }

        static IEnumerable<GameObject> EnemyAndDeathEffects(Enemy enemy)
        {
            if (enemy == null) yield break;
            foreach (GameObject go in ReferencedEffects(enemy)) yield return go;
            var receiver = enemy.DamageReciever;
            if (receiver != null && receiver.DeathAction != null)
                foreach (GameObject go in ReferencedEffects(receiver.DeathAction as Component)) yield return go;
        }

        static GameObject FloorEffect(Vector3 position, out bool useNativeBreak)
        {
            useNativeBreak = false;
            GameObject prefab = null;
            foreach (StompableFloor floor in Resources.FindObjectsOfTypeAll<StompableFloor>())
                foreach (PrefabSpawner spawner in floor.GetComponentsInChildren<PrefabSpawner>(true))
                {
                    GameObject candidate = SpawnerPrefab(spawner);
                    if (candidate == null || candidate.name != FloorExplosion) continue;
                    prefab = candidate;
                    if (!floor.gameObject.scene.IsValid() || (spawner.transform.position - position).sqrMagnitude > 0.25f) continue;
                    // World damage precedes this effect, but the native death action can run later.
                    EntityDamageReciever receiver = floor.DamageReciever;
                    GameObject instance = (GameObject)s_lastSpawn.GetValue(spawner);
                    useNativeBreak = WorldObjSync.HasDeferredDamage(receiver)
                        || (WorldObjSync.Online && receiver != null && receiver.NoHealthLeft && receiver.DeathAction != null)
                        || (instance != null && instance.activeInHierarchy && (instance.transform.position - position).sqrMagnitude <= 0.25f);
                    return prefab;
                }
            return prefab;
        }

        static GameObject FindEffect(Actor actor, string name, Vector3 position, out bool useNativeBreak)
        {
            useNativeBreak = false;
            GameObject prefab;
            if (name == FloorExplosion && (prefab = FloorEffect(position, out useNativeBreak)) != null) return prefab;
            if (s_fxName(actor.KamikazeExplosion) == name) return actor.KamikazeExplosion;
            prefab = EnemyVisuals.FindByName(name, EnemyEffects(actor.Entity), s_fxName);
            if (prefab != null) return prefab;
            if (s_fxPrefabs.TryGetValue(name, out prefab) && prefab != null) return prefab;

            // A native reference can be available even before that placeholder spawns, or after its enemy dies.
            foreach (Enemy enemy in Resources.FindObjectsOfTypeAll<Enemy>())
            {
                prefab = EnemyVisuals.FindByName(name, EnemyEffects(enemy), s_fxName);
                if (prefab != null) return s_fxPrefabs[name] = prefab;
            }
            prefab = EnemyVisuals.FindByName(name, EnemyAndDeathEffects(actor.Entity), s_fxName);
            if (prefab != null) return s_fxPrefabs[name] = prefab;
            foreach (Enemy enemy in Resources.FindObjectsOfTypeAll<Enemy>())
            {
                if (enemy == null || enemy.GetType().Name != actor.ClassName) continue;
                prefab = EnemyVisuals.FindByName(name, EnemyAndDeathEffects(enemy), s_fxName);
                if (prefab != null) return s_fxPrefabs[name] = prefab;
            }
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go.name == name && (string.IsNullOrEmpty(go.scene.name) || !go.scene.IsValid())) return s_fxPrefabs[name] = go;
            return null;
        }

        internal static void TestEmitJumperEffects(string key, Vector3 at)
        {
            Actor previous = s_active; s_active = s_actors[key];
            try
            {
                JumperEnemy enemy = (JumperEnemy)s_active.Entity;
                InstantiateUtility.Instantiate(enemy.StompEffect, at, Quaternion.identity);
                InstantiateUtility.Instantiate(enemy.LandEffect, at, Quaternion.identity);
            }
            finally { s_active = previous; }
        }

        internal static bool TestHasEffect(string name, Vector3 at)
        {
            foreach (ProjectileIdentity tag in s_copies)
                if (tag != null && tag.Cosmetic && tag.FxName == name && tag.gameObject.activeInHierarchy
                    && (tag.transform.position - at).sqrMagnitude < 1f) return true;
            return false;
        }
    }
}
