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
