using System;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Shared-screen rules used by most same-screen co-op platformers:
    // 1. Camera frames both players: it follows the midpoint (the game's own chase smoothing does the easing)
    //    and zooms out smoothly as they separate, up to a cap.
    // 2. Screen edges are walls for the players: once the two are as far apart as the most zoomed-out view
    //    allows, neither can move further outward (one at the left edge and one at the right edge are both
    //    stuck until one walks back). Moving inward is always allowed.
    // 3. Nobody is snapped back. A player who ends up off-screen vertically (fell, got launched) for a while
    //    glides back to the other player; only huge jumps (doors/teleports, a scene cut anyway) are instant.
    public static class CoopCamera
    {
        public static bool DynamicZoom = true;
        public static SeinCharacter FocusOverride;   // set while one Ori is downed
        public static float MaxZoomOut = 1.6f;   // max camera distance = game's distance * this
        public static float EdgeMargin = 2.5f;   // world units kept between a player and the screen edge
        public static float RegroupAfter = 2f;   // seconds off-screen before gliding back

        static float s_extra, s_extraVel;        // extra camera distance we add (smoothed)
        static Vector3 s_last1, s_last2;
        static bool s_haveLast;
        static float s_offscreenT;
        static float s_regroupT = -1f;
        static Vector3 s_regroupFrom;
        public static int EdgeBlocks;            // test counters
        public static int Regroups;
        public static int OffsetUpdates;
        public static float Extra { get { return s_extra; } }

        public static void Reset() { s_extra = 0f; s_extraVel = 0f; s_haveLast = false; s_offscreenT = 0f; s_regroupT = -1f; }

        // Current visible size at the gameplay plane (z = 0), and the size at maximum zoom-out.
        public static bool ViewSize(out Vector2 now, out Vector2 atMax)
        {
            now = atMax = Vector2.zero;
            GameplayCamera cam = UI.Cameras.Current;
            if (cam == null || cam.Camera == null || cam.OffsetController == null) return false;
            float d = -cam.OffsetController.Offset.z;
            if (d <= 0.1f) return false;
            float h = 2f * d * Mathf.Tan(cam.Camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            now = new Vector2(h * cam.Camera.aspect, h);
            float baseD = Mathf.Max(0.1f, d - s_extra);
            atMax = now * (baseD * MaxZoomOut / d);
            return true;
        }

        public static bool Regrouping { get { return s_regroupT >= 0f; } }

        // Called once per frame after physics (Runner.LateUpdate), only while both players exist.
        public static void Apply(SeinCharacter p1, SeinCharacter p2, bool limitsOn)
        {
            Vector2 now, atMax;
            if (!ViewSize(out now, out atMax)) { s_haveLast = false; return; }

            if (FocusOverride != null) UpdateZoom(FocusOverride.Position, FocusOverride.Position, now); // one Ori is down: frame the living one
            else UpdateZoom(p1.Position, p2.Position, now);

            if (Regrouping) { StepRegroup(p1, p2); s_haveLast = false; return; }
            if (!limitsOn || p1.gameObject == null || !p1.gameObject.activeInHierarchy || !p2.gameObject.activeInHierarchy
                || GameController.Instance == null || GameController.Instance.GameplaySuspendedForUI || GameController.Instance.InputLocked)
            { s_haveLast = false; return; }

            float maxX = Mathf.Max(4f, atMax.x - 2f * EdgeMargin);
            float maxY = Mathf.Max(4f, atMax.y - 2f * EdgeMargin);
            Vector3 a = p1.Position, b = p2.Position;

            // Huge jump in one frame (door, area teleport, checkpoint): a cut, not a walk. Bring P2 over at once.
            if (Mathf.Abs(a.x - b.x) > maxX * 3f || Mathf.Abs(a.y - b.y) > maxY * 3f)
            {
                Plugin.Teleport(p2, a);
                s_haveLast = false;
                return;
            }

            if (s_haveLast)
            {
                ClampAxis(p1, p2, 0, maxX, ref a, ref b);
            }

            // Vertical: no wall (falling into a wall of air looks broken in Ori); off-screen too long -> glide back.
            if (Mathf.Abs(a.y - b.y) > maxY) s_offscreenT += Time.unscaledDeltaTime; else s_offscreenT = 0f;
            if (s_offscreenT > RegroupAfter) StartRegroup(p2);

            s_last1 = p1.Position; s_last2 = p2.Position; s_haveLast = true;
        }

        // If this frame's movement pushed the pair further apart than allowed, undo only the outward part of the
        // movement of whoever moved outward, and stop their outward speed. Nobody is dragged by the other.
        static void ClampAxis(SeinCharacter p1, SeinCharacter p2, int axis, float max, ref Vector3 a, ref Vector3 b)
        {
            float sep = b[axis] - a[axis];
            float over = Mathf.Abs(sep) - max;
            if (over <= 0f) return;
            float dir = Mathf.Sign(sep);                       // +1: P2 is on the + side of P1
            float out1 = Mathf.Max(0f, -(a[axis] - s_last1[axis]) * dir); // P1 moving away from P2
            float out2 = Mathf.Max(0f, (b[axis] - s_last2[axis]) * dir);  // P2 moving away from P1
            float total = out1 + out2;
            if (total <= 0.0001f) return;                       // separation came from something else; leave it
            float fix = Mathf.Min(over, total);
            float fix1 = fix * out1 / total, fix2 = fix * out2 / total;
            if (fix1 > 0f)
            {
                a[axis] += fix1 * dir;
                p1.Position = a;
                Vector3 s = p1.Speed; if (s[axis] * -dir > 0f) { s[axis] = 0f; p1.Speed = s; }
            }
            if (fix2 > 0f)
            {
                b[axis] -= fix2 * dir;
                p2.Position = b;
                Vector3 s = p2.Speed; if (s[axis] * dir > 0f) { s[axis] = 0f; p2.Speed = s; }
            }
            EdgeBlocks++;
        }

        static void UpdateZoom(Vector3 a, Vector3 b, Vector2 now)
        {
            GameplayCamera cam = UI.Cameras.Current;
            float target = 0f;
            if (DynamicZoom && !Regrouping)
            {
                float d = -cam.OffsetController.Offset.z;
                float baseD = Mathf.Max(0.1f, d - s_extra);
                // Distance needed so both fit with a comfort band on each side: start opening up once a player
                // enters the outer ~18% of the normal view, well before the hard edge (EdgeMargin) is reached.
                float perUnitX = Mathf.Max(0.01f, now.x / d), perUnitY = Mathf.Max(0.01f, now.y / d);
                float bandX = Mathf.Max(EdgeMargin, 0.18f * perUnitX * baseD), bandY = Mathf.Max(EdgeMargin, 0.18f * perUnitY * baseD);
                float needX = (Mathf.Abs(a.x - b.x) + 2f * bandX) / perUnitX;
                float needY = (Mathf.Abs(a.y - b.y) + 2f * bandY) / perUnitY;
                target = Mathf.Clamp(Mathf.Max(needX, needY) - baseD, 0f, baseD * (MaxZoomOut - 1f));
            }
            // Ease out faster than in, so the camera opens up before anyone hits the edge and settles back slowly.
            float smooth = target > s_extra ? 0.35f : 0.9f;
            s_extra = Mathf.SmoothDamp(s_extra, target, ref s_extraVel, smooth, Mathf.Infinity, Mathf.Max(0.0001f, Time.unscaledDeltaTime));
        }

        static void StartRegroup(SeinCharacter p2)
        {
            s_regroupT = 0f;
            s_regroupFrom = p2.Position;
            s_offscreenT = 0f;
            Regroups++;
            VisualInvariants.Grace(2.5f);
            Plugin.Log.LogInfo("P2 off-screen too long; gliding back to P1");
        }

        // Glide P2 to P1 over ~0.7 s along a small arc (no snap); the camera follows the moving midpoint.
        static void StepRegroup(SeinCharacter p1, SeinCharacter p2)
        {
            s_regroupT += Time.unscaledDeltaTime / 0.7f;
            float t = Mathf.Clamp01(s_regroupT);
            float e = t * t * (3f - 2f * t);
            Vector3 to = p1.Position + new Vector3(0f, 0.5f, 0f);
            Vector3 pos = Vector3.Lerp(s_regroupFrom, to, e) + new Vector3(0f, Mathf.Sin(t * Mathf.PI) * 2f, 0f);
            p2.Position = pos;
            p2.Speed = Vector3.zero;
            if (t >= 1f) { s_regroupT = -1f; Plugin.Teleport(p2, p1.Position); }
        }

        // Add our extra distance through the game's own offset pipeline (it lerps the result itself).
        [HarmonyPatch(typeof(CameraOffsetController), "UpdateOffset")]
        static class ZoomPatch
        {
            static void Prefix(CameraOffsetController __instance, out Vector3 __state)
            {
                __state = __instance.AdditiveDefaultOffset;
                OffsetUpdates++;
                if (P2Context.P2 != null && s_extra > 0.001f) __instance.AdditiveDefaultOffset += new Vector3(0f, 0f, s_extra);
            }
            static void Postfix(CameraOffsetController __instance, Vector3 __state)
            {
                __instance.AdditiveDefaultOffset = __state;
            }
        }
    }
}
