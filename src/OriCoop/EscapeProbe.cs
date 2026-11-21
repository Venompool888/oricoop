using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    static class EscapeProbe
    {
        static readonly string[] Scenes = {
            "ginsoTreeWaterRisingBtm", "ginsoTreeWaterRisingMid", "ginsoTreeWaterRisingEnd", "ginsoTreeResurrection",
            "catAndMouseLeft", "catAndMouseMid", "catAndMouseRight", "catAndMouseResurrectionRoom",
            "forlornRuinsKuroHideStreamlined", "forlornRuinsResurrection", "forlornRuinsResurrectionAfter",
            "mountHoruBreakyPathBtm", "mountHoruBreakyPathTop", "mountHoruHubBottom", "mountHoruHubMid", "mountHoruHubTop"
        };
        static readonly string[] SceneWords = { "escape", "rising", "lava", "chase", "catAndMouse" };
        static readonly string[] ActionWords = { "Scene", "Checkpoint", "Teleport", "Rising", "Kill", "Escape", "Sein", "Position", "Story" };

        static bool ContainsAny(string name, string[] words)
        {
            foreach (string word in words)
                if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string Path(Transform transform)
        {
            string path = transform.name;
            for (Transform t = transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        static bool InScene(Component component, string scene)
        {
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                SceneRoot root = t.GetComponent<SceneRoot>();
                if (root != null && root.MetaData != null && root.MetaData.SceneName == scene) return true;
            }
            return false;
        }

        static bool Loaded(string scene)
        {
            foreach (SceneRoot root in Resources.FindObjectsOfTypeAll<SceneRoot>())
                if (root != null && root.MetaData != null && root.MetaData.SceneName == scene) return true;
            return false;
        }

        static string Value(object value)
        {
            if (value == null) return "null";
            Component component = value as Component;
            if (component != null) return component.GetType().Name + ":" + Path(component.transform);
            GameObject go = value as GameObject;
            if (go != null) return "GameObject:" + Path(go.transform);
            UnityEngine.Object obj = value as UnityEngine.Object;
            if (obj != null) return obj.name;
            return value.ToString();
        }

        static string Members(object obj, params string[] names)
        {
            string result = "";
            foreach (string name in names)
            {
                FieldInfo field = AccessTools.Field(obj.GetType(), name);
                if (field != null) result += " " + name + "=" + Value(field.GetValue(obj));
                else
                {
                    PropertyInfo property = AccessTools.Property(obj.GetType(), name);
                    if (property != null) result += " " + name + "=" + Value(property.GetValue(obj, null));
                }
            }
            return result;
        }

        static bool Controller(string name)
        {
            return name == "CatAndMouseRoomTimerController" || name == "CatAndMouseRoomAController"
                || name == "CatAndMouseRoomCController" || name == "CatAndMouseRoomDController"
                || name == "ValleyOfTheWindKuroGameplayController" || name == "MistyWoodsKuroGameplayController";
        }

        static bool Interesting(Component component)
        {
            string name = component.GetType().Name;
            if (component is RisingWater || component is KillPlayer || component is DrownPlayer
                || component is CapsuleCrushDetector || component is DamageDealer || component is Trigger
                || component is ActionSequence || component is InvisibleCheckpoint || Controller(name)
                || name.IndexOf("CheckpointMarker", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return component is ActionMethod && name.IndexOf("RunActionCondition", StringComparison.OrdinalIgnoreCase) < 0
                && (component is SetWorldEventAction || ContainsAny(name, ActionWords));
        }

        static string Describe(Component component)
        {
            string result = component.GetType().Name + " path=" + Path(component.transform)
                + " activeInHierarchy=" + component.gameObject.activeInHierarchy + " position=" + component.transform.position;
            RisingWater water = component as RisingWater;
            if (water != null)
                result += " Speed=" + water.Speed + " IsSuspended=" + water.IsSuspended + " y=" + water.transform.position.y
                    + " SpeedOverDistance.keys=" + (water.SpeedOverDistance == null ? 0 : water.SpeedOverDistance.keys.Length);
            DamageDealer damage = component as DamageDealer;
            if (damage != null)
                result += " Damage=" + damage.Damage + " DamageType=" + damage.DamageType + " lethal=" + (damage.Damage >= 100f);
            Trigger trigger = component as Trigger;
            if (trigger != null)
            {
                result += " ActionToRun=" + Value(trigger.ActionToRun) + " TriggerOnce=" + trigger.TriggerOnce + " Active=" + trigger.Active;
                if (trigger is PlayerCollisionTrigger)
                {
                    result += Members(trigger, "m_bounds");
                    foreach (Collider collider in trigger.GetComponents<Collider>()) result += " collider.bounds=" + collider.bounds;
                }
            }
            ActionSequence sequence = component as ActionSequence;
            if (sequence != null) result += " IsRunning=" + sequence.IsRunning + " actions=" + sequence.Actions.Count;
            if (component is InvisibleCheckpoint || component.GetType().Name.IndexOf("CheckpointMarker", StringComparison.OrdinalIgnoreCase) >= 0)
                result += Members(component, "m_bounds", "Bounds", "RespawnPosition");
            if (component is ActionMethod && sequence == null)
            {
                foreach (FieldInfo field in component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    result += " " + field.Name + "=" + Value(field.GetValue(component));
                LoadSceneAction load = component as LoadSceneAction;
                if (load != null && load.SceneMetaData != null) result += " targetScene=" + load.SceneMetaData.SceneName;
                SetWorldEventAction world = component as SetWorldEventAction;
                if (world != null && world.WorldEvents != null) result += " stateName=" + world.WorldEvents.GetNameFromID(world.State);
            }
            if (Controller(component.GetType().Name))
                result += Members(component, "RoomTime", "Active", "RoomController", "IsSuspended", "m_time", "m_active",
                    "m_runningSequence", "SequenceTime", "CenterLayerTime", "m_escaped", "DeathDelay", "m_hasKilledPlayer",
                    "m_hasBeenSeen", "TimeToHide", "m_currentTime", "m_currentState", "FinishAction");
            return result;
        }

        public static IEnumerator Run(TestHarness h)
        {
            SeinCharacter p1 = P2Context.P1;
            bool immortal = p1.Mortality.DamageReciever.IsImmortal;
            p1.Mortality.DamageReciever.IsImmortal = true;
            try
            {
                List<string> names = new List<string>();
                List<string> targets = new List<string>(Scenes);
                HashSet<string> seen = new HashSet<string>(Scenes, StringComparer.OrdinalIgnoreCase);
                foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                {
                    names.Add(scene.Scene);
                    if ((ContainsAny(scene.Scene, SceneWords) || scene.Scene.StartsWith("mountHoru", StringComparison.OrdinalIgnoreCase))
                        && seen.Add(scene.Scene)) targets.Add(scene.Scene);
                }
                for (int i = 0; i < names.Count; i += 20)
                    h.Note("ESC allScenes " + string.Join(", ", names.GetRange(i, Math.Min(20, names.Count - i)).ToArray()));
                foreach (string name in targets)
                {
                    RuntimeSceneMetaData metadata = null;
                    foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                        if (scene.Scene == name) { metadata = scene; break; }
                    if (metadata == null)
                    {
                        h.Note("ESC scene " + name + " bounds=missing placeholder=missing (not in AllScenes)");
                        continue;
                    }
                    h.Note("ESC scene " + name + " bounds=" + metadata.SceneBounds + " placeholder=" + metadata.PlaceholderPosition);
                    Vector2 center = metadata.SceneBounds.center;
                    Plugin.Teleport(p1, new Vector3(center.x, center.y, 0));
                    Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(metadata.SceneBounds, true, false, false);
                    float end = Time.realtimeSinceStartup + 8f;
                    while (!Loaded(name) && Time.realtimeSinceStartup < end) yield return null;
                    end = Time.realtimeSinceStartup + 2f;
                    while (Time.realtimeSinceStartup < end) yield return null;
                    h.Note("ESC player scene=" + name + " position=" + p1.Position + " StoryActive=" + StorySync.StoryActive);
                    int lines = 2;
                    foreach (Component component in Resources.FindObjectsOfTypeAll<Component>())
                    {
                        if (lines >= 120) break;
                        if (component == null) continue;
                        try
                        {
                            if (!InScene(component, name) || !Interesting(component)) continue;
                            h.Note("ESC " + Describe(component));
                            lines++;
                        }
                        catch (Exception ex)
                        {
                            h.Note("ESC scene=" + name + " object=" + component.GetType().Name + " exception=" + ex.GetType().Name);
                            lines++;
                        }
                    }
                }
                h.Check(true, "escapeprobe ran");
            }
            finally
            {
                if (p1 != null) p1.Mortality.DamageReciever.IsImmortal = immortal;
            }
        }
    }
}
