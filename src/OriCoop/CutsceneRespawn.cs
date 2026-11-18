using System;
using System.Reflection;
using Game;
using OriCoop.Seams;
using UnityEngine;

namespace OriCoop
{
    // T-022 (#3): a cutscene that runs DestroyGameplayElementsAction removes the local Ori, and the game only gives it
    // back when the scene it returns to is loaded again (SeinPlaceholder / CharacterFactory on OnSceneRootPreEnabled).
    // Online, a scene can stay loaded through the cutscene (a partner stood in it), so Ori never came back: the camera
    // stayed on an empty scene and the partner lost the puppet for good. EnemySync no longer keeps partner scenes
    // while there is no local Ori; this is the fallback when Ori is still missing once the game is playable again.
    internal static class CutsceneRespawn
    {
        public const float Delay = 2f;
        // ORICOOP_RESPAWN_OFF=1: the whole T-022 fix off (mutation switch for NetKuroTest).
        public static readonly bool Off = Environment.GetEnvironmentVariable("ORICOOP_RESPAWN_OFF") == "1";
        public static int Removed;      // RemoveGameplayObjects seen while online
        public static int Respawns;     // fallback spawns done
        public static string LastHow = "";
        static bool s_pending;
        static float s_missingSince = -1f;

        static float Now { get { return Time.realtimeSinceStartup; } }

        static bool Online { get { return Services.Net.Enabled && Services.Net.Connected; } }

        // Called from Plugin's RemoveGameplayObjects prefix, before Ori is destroyed.
        public static void NoteRemoved()
        {
            if (!Online) return;
            Removed++;
            if (Off) return;
            s_pending = true; s_missingSince = -1f;
            EnemySync.ReleasePartnerPins();
        }

        public static void Tick()
        {
            if (!s_pending) return;
            if (!Online) { s_pending = false; return; }
            if (Characters.Current as Component != null) { s_pending = false; s_missingSince = -1f; return; }
            if (!Playable()) { s_missingSince = -1f; return; }
            if (s_missingSince < 0f) { s_missingSince = Now; return; }
            if (Now - s_missingSince < Delay) return;
            s_pending = false; s_missingSince = -1f;
            Respawn();
        }

        // Back in the game with nothing running: not loading, not a title/cutscene state, no story playing, no
        // letterbox or input lock (a cutscene sequence still running).
        public static bool Playable()
        {
            GameController gc = GameController.Instance;
            if (gc == null || gc.IsLoadingGame || CharacterFactory.Instance == null) return false;
            GameStateMachine gsm = GameStateMachine.Instance;
            if (gsm == null || gsm.CurrentState != GameStateMachine.State.Game || gsm.IsInExtendedTitleScreen()) return false;
            if (StorySync.StoryActive || StorySync.TakeoverMaskPublic() != 0 || Letterbox.ShowLetterboxes) return false;
            if (gc.LockInput || gc.LockInputByAction) return false;
            if (UI.Cameras.Current == null) return false;
            return true;
        }

        static void Respawn()
        {
            Vector3 cam = UI.Cameras.Current.Camera.transform.position; cam.z = 0f;
            SeinPlaceholder best = null; float bestDistance = float.MaxValue;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(typeof(SeinPlaceholder)))
            {
                SeinPlaceholder p = o as SeinPlaceholder;
                if (p == null || p.Character != CharacterFactory.Characters.Sein || p.SceneRoot == null) continue;
                float d = Vector2.Distance(p.transform.position, cam);
                if (d < bestDistance) { best = p; bestDistance = d; }
            }
            try
            {
                if (best != null && bestDistance < 200f && SpawnAt(best)) LastHow = "placeholder " + best.SceneRoot.name;
                else SpawnAtCamera(cam);
            }
            catch (Exception e) { Services.Log.Warn("respawn fallback failed: " + e); return; }
            Respawns++;
            Services.Log.Info("player respawn.fallback " + LastHow);
            CoopTraceNote(LastHow);
        }

        // The scene's own placeholder: spawns Ori with its gameplay systems (HUD) exactly as a scene load does.
        static bool SpawnAt(SeinPlaceholder p)
        {
            MethodInfo spawn = typeof(SeinPlaceholder).GetMethod("Spawn", BindingFlags.Instance | BindingFlags.NonPublic);
            if (spawn == null) return false;
            GoToSceneController go = GoToSceneController.Instance;
            MoonGuid saved = go != null ? go.StartInScene : null;
            // CheckStartInScene compares by reference: point it at this scene so the placeholder accepts the spawn.
            if (go != null) go.StartInScene = p.SceneRoot.MetaData.SceneMoonGuid;
            try { spawn.Invoke(p, null); }
            finally { if (go != null) go.StartInScene = saved; }
            return Characters.Current as Component != null;
        }

        static void SpawnAtCamera(Vector3 at)
        {
            CharacterFactory.Instance.SpawnCharacter(CharacterFactory.Characters.Sein, null, at, delegate
            {
                if (Characters.Current == null) return;
                Characters.Current.Activate(true);
                Characters.Current.Position = at;
                Characters.Current.PlaceOnGround();
                if (Characters.Sein != null && Characters.Sein.Prefabs != null) Characters.Sein.Prefabs.EnsureRightPrefabsAreThereForAbilities();
            });
            if (Characters.Current as Component != null)
            {
                UI.Cameras.Current.Target = Characters.Current.Transform;
                UI.Cameras.Current.MoveCameraToTargetInstantly();
            }
            // RemoveGameplayObjects also destroyed the HUD (SeinUI); a scene load brings it back with the placeholder's
            // GameplaySystems. Without it SeinSoulFlame and others throw every frame.
            if (UI.SeinUI == null)
                foreach (SeinPlaceholder p in Resources.FindObjectsOfTypeAll<SeinPlaceholder>())
                    if (p != null && p.GameplaySystems != null) { UnityEngine.Object.Instantiate(p.GameplaySystems); break; }
            LastHow = "camera";
        }

        static void CoopTraceNote(string text)
        {
            try { CoopTrace.Event("player", "respawn.fallback", "how", text); } catch (Exception) { }
        }
    }
}
