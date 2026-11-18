using System;
using System.Collections.Generic;
using Game;
using OriCoop.Rules;
using UnityEngine;

namespace OriCoop
{
    // Online co-op camera (T-058, GitHub #33; design in docs\multiplayer-model.md §3). Each device follows its own
    // player; teammates near that player pull the view toward them (Rules\InfluenceCamera does the math). Nobody is
    // leashed online and far teammates simply leave the picture. The same-device screen-edge wall (#34) is
    // CoopCamera's job and does not run here.
    //
    // Hand-back: whenever the game (or another mod system) is steering the camera — cutscenes / scripted targets,
    // camera not in Chase motion, input locked, gameplay suspended, or Suspended set by forced sync (#14) or
    // spectating (#16) — this class puts the target back on the local Ori (or leaves a scripted target alone) and
    // restarts from zero offset, so taking over again never jumps.
    public static class OnlineCamera
    {
        public static readonly InfluenceCameraSettings Settings = new InfluenceCameraSettings();
        static readonly InfluenceCamera s_cam = new InfluenceCamera(Settings);
        static readonly List<CamVec> s_mates = new List<CamVec>(4);
        static float s_baseDistance;

        // Set by systems that own the camera for a while (forced togetherness #14, spectating #16).
        public static bool Suspended;
        // Extra camera distance requested from CoopCamera.ZoomPatch (world units added to the game's distance).
        public static float ZoomExtra { get; private set; }
        // Test counters / readouts.
        public static int Updates, TakeOvers, HandBacks;
        public static bool Active { get; private set; }
        public static Vector2 Offset { get { return new Vector2(s_cam.Offset.X, s_cam.Offset.Y); } }
        public static float Zoom { get { return s_cam.Zoom; } }
        public static int Influencing { get; private set; }

        // Test-only mutation switch: teammates are ignored (the camera only follows the local player), to prove the
        // net test fails without the influence.
        static readonly bool s_mutateNoMates = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_CAM_NO_MATES") == "1";

        public static void Reset() { s_cam.Reset(); ZoomExtra = 0f; Active = false; Influencing = 0; }

        // Once per frame (LateUpdate) online, after puppets have been moved to their network positions.
        public static void Apply(Transform target, SeinCharacter local, List<Puppet> remotes)
        {
            GameplayCamera cam = UI.Cameras.Current;
            if (cam == null || local == null || target == null) { Reset(); return; }
            Transform t = cam.Target;
            bool onPuppet = false;
            foreach (Puppet p in remotes) if (p.Sein != null && t == p.Sein.Transform) onPuppet = true;
            bool following = t == local.Transform || t == target || onPuppet;
            if (!following) { Reset(); return; }   // scripted / cutscene target: leave it alone
            if (MustHandBack(cam, local))
            {
                if (t != local.Transform) { cam.Target = local.Transform; HandBacks++; }
                Reset();
                return;
            }

            float baseD = BaseDistance(cam);
            if (baseD <= 0.1f || cam.Camera == null) { if (t != local.Transform) cam.Target = local.Transform; Reset(); return; }
            float halfH = baseD * Mathf.Tan(cam.Camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            CamVec viewHalf = new CamVec(halfH * cam.Camera.aspect, halfH);

            Vector3 me = local.Position;
            CamVec mine = new CamVec(me.x, me.y);
            s_mates.Clear();
            int influencing = 0;
            if (!s_mutateNoMates)
                foreach (Puppet p in remotes)
                {
                    SeinCharacter s = p.Sein;
                    if (s == null || !s.gameObject.activeInHierarchy) continue;
                    CamVec v = new CamVec(s.Position.x, s.Position.y);
                    s_mates.Add(v);
                    if (s_cam.Weight(mine, v) > 0f) influencing++;
                }
            Influencing = influencing;
            s_cam.Step(mine, s_mates.ToArray(), viewHalf, Mathf.Min(0.1f, Time.unscaledDeltaTime));
            target.position = new Vector3(me.x + s_cam.Offset.X, me.y + s_cam.Offset.Y, me.z);
            ZoomExtra = baseD * (s_cam.Zoom - 1f);
            if (t != target)
            {
                // First frame after (re)taking over: the offset starts at zero, so the target sits on the player and
                // the game's own chase smoothing sees no jump.
                cam.Target = target;
                TakeOvers++;
            }
            Active = true;
            Updates++;
        }

        static bool MustHandBack(GameplayCamera cam, SeinCharacter local)
        {
            if (Suspended || !Settings.Enabled) return true;
            if (!local.gameObject.activeInHierarchy || Revive.IsDowned(local)) return true;
            GameController gc = GameController.Instance;
            if (gc == null || gc.GameplaySuspended || gc.InputLocked || gc.IsLoadingGame) return true;
            if (cam.IsSuspended || cam.Motion != GameplayCamera.MotionType.Chase) return true;
            return false;
        }

        // The game's own camera distance without our extra (the offset controller eases toward its goal, so remember
        // the distance seen while we added nothing and only update it when our share is known).
        static float BaseDistance(GameplayCamera cam)
        {
            if (cam.OffsetController == null) return 0f;
            float d = -cam.OffsetController.Offset.z - ZoomExtra - CoopCamera.Extra;
            if (d > 0.1f) s_baseDistance = d;
            return s_baseDistance;
        }
    }
}
