using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Net test phase "CAM" (T-058, GitHub #33; rule in docs\multiplayer-model.md §3.2). Two clients, run after WO.
    // The guest stands still; the host passes across it from 30 units right to 30 units left (kinematic path, so the
    // terrain doesn't matter), then waits far left. On the guest:
    //   - before: host 30 away (outside the 20-unit radius) -> camera target on the guest (offset ~0)
    //   - host approaching on the right, inside the radius -> camera target pulled right (toward the host)
    //   - host past it on the left -> camera target pulled left although the guest did not move
    //   - host gone (30 left) -> offset back to ~0, and no frame-to-frame jump anywhere
    // Mutation: ORICOOP_MUTATE_CAM_NO_MATES=1 (teammates ignored) must fail the pull checks.
    // Synchronised by NetStoryTest.PlaceAtStart's barrier (steps 66/67): guest is placed 1.5 right of the host.
    public static class NetCameraTest
    {
        const float Far = 30f, Speed = 8f;
        static SeinCharacter Own { get { return P2Context.P1; } }
        static SeinCharacter Partner { get { return P2Context.P2; } }
        static string F(float f) { return f.ToString("F2"); }
        static float Now { get { return Time.realtimeSinceStartup; } }

        public static IEnumerator Run(TestHarness h, Vector3 phaseAStart)
        {
            bool host = NetPlay.IsHost;
            h.Note("--- phase CAM: online influence camera (" + (host ? "host walks across" : "guest stands still") + ")");
            yield return NetStoryTest.PlaceAtStart(h, phaseAStart, 66);
            if (Own == null || Partner == null) { h.Check(false, "CAM needs both Oris"); yield break; }
            NetTestHarness.BotLX = 0f; NetTestHarness.BotButtons = 0;
            if (host) yield return HostWalk(h);
            else yield return GuestWatch(h);
            // Part 2 (hotfix): a camera left parked in Move motion by a cut-off sequence is repaired and the influence
            // camera takes over again. The host only waits out the same window.
            if (host) { float until = Now + ParkWindow; while (Now < until) yield return null; }
            else yield return GuestParkedCamera(h);
        }

        static IEnumerator HostWalk(TestHarness h)
        {
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true;
            Vector3 guest = Partner.Position, back = Own.Position;
            float y = guest.y;
            try
            {
                float start = Now;
                while (Now - start < 12f)
                {
                    float t = Now - start;
                    // 0-2 s far right, 2-9.5 s walk to far left at 8 u/s, then stay far left.
                    float x = t < 2f ? Far : Mathf.Max(-Far, Far - (t - 2f) * Speed);
                    Own.Position = new Vector3(guest.x + x, y, Own.Position.z);
                    Own.Speed = Vector3.zero;
                    yield return null;
                }
                h.Check(Mathf.Abs(Own.Position.x - (guest.x - Far)) < 0.5f, "CAM host: path finished 30 left of the guest (" + F(Own.Position.x - guest.x) + ")");
            }
            finally
            {
                Own.Mortality.DamageReciever.IsImmortal = immortal;
                Plugin.Teleport(Own, back - Vector3.up * 0.5f);
            }
        }

        const float ParkWindow = 14f;
        // Mutation: ORICOOP_MUTATE_CAM_NO_UNPARK=1 skips CutsceneGuard's Move repair, so this check must fail.
        static IEnumerator GuestParkedCamera(TestHarness h)
        {
            float start = Now;
            GameplayCamera cam = UI.Cameras.Current;
            if (cam == null || Own == null) { h.Check(false, "CAM guest: camera present for the parked-camera check"); yield break; }
            int repairsBefore = CutsceneGuard.CameraMoveRepairs;
            cam.MoveToTarget(Own.Position + new Vector3(40f, 0f, 0f), 0.3f, true);   // what a stomp post / door cinematic does
            bool parked = false, repaired = false, back = false; float repairedAt = 0f;
            while (Now - start < ParkWindow - 0.5f)
            {
                yield return null;
                cam = UI.Cameras.Current; if (cam == null || Own == null) break;
                if (cam.Motion == GameplayCamera.MotionType.Move) parked = true;
                if (!repaired && parked && cam.Motion == GameplayCamera.MotionType.Chase) { repaired = true; repairedAt = Now - start; }
                if (repaired && OnlineCamera.Active && cam.Target != Own.Transform) { back = true; break; }
            }
            h.Note("CAM guest parked camera: parked=" + parked + " repaired=" + repaired + " after " + F(repairedAt) + " s, repairs="
                + (CutsceneGuard.CameraMoveRepairs - repairsBefore) + " influence camera back=" + back);
            h.Check(parked && repaired && CutsceneGuard.CameraMoveRepairs > repairsBefore, "CAM guest: a camera left parked in Move is sent back to Chase (after " + F(repairedAt) + " s)");
            h.Check(back, "CAM guest: the influence camera takes over again after the repair");
            while (Now - start < ParkWindow) yield return null;
        }

        static IEnumerator GuestWatch(TestHarness h)
        {
            Vector3 home = Own.Position;
            float start = Now;
            float baseline = 0f, maxRight = 0f, minLeft = 0f, maxStep = 0f, maxRate = 0f, maxDrift = 0f, endOffset = float.NaN, maxZoom = 1f;
            int baseSamples = 0, controlled = 0, frames = 0, influencedFrames = 0;
            bool haveLast = false; float lastRel = 0f;
            while (Now - start < 12f)
            {
                float t = Now - start;
                yield return null;   // the target was placed in the last LateUpdate; the guest stands still
                if (Own == null || Partner == null) break;
                var cam = UI.Cameras.Current;
                bool ours = cam != null && cam.Target != null && cam.Target != Own.Transform && OnlineCamera.Active;
                float rel = cam != null && cam.Target != null ? cam.Target.position.x - Own.Position.x : 0f;   // target relative to the guest
                float dx = Partner.Position.x - Own.Position.x;
                frames++; if (ours) controlled++;
                if (OnlineCamera.Influencing > 0) influencedFrames++;
                maxDrift = Mathf.Max(maxDrift, Mathf.Abs(Own.Position.x - home.x));
                maxZoom = Mathf.Max(maxZoom, OnlineCamera.Zoom);
                if (t >= 1.5f && t < 2f && dx > 25f) { baseline = Mathf.Max(baseline, Mathf.Abs(rel)); baseSamples++; }
                if (dx > 2f && dx < 12f) maxRight = Mathf.Max(maxRight, rel);
                if (dx < -2f && dx > -12f) minLeft = Mathf.Min(minLeft, rel);
                if (haveLast) { float d = Mathf.Abs(rel - lastRel); maxStep = Mathf.Max(maxStep, d); if (Time.unscaledDeltaTime > 0.001f) maxRate = Mathf.Max(maxRate, d / Time.unscaledDeltaTime); }
                lastRel = rel; haveLast = true;
                if (t > 11.5f) endOffset = Mathf.Abs(rel);
            }
            h.Note("CAM guest: baseline=" + F(baseline) + " (" + baseSamples + " samples) right=" + F(maxRight) + " left=" + F(minLeft)
                + " end=" + F(endOffset) + " maxStep=" + F(maxStep) + " maxRate=" + F(maxRate) + " drift=" + F(maxDrift) + " zoom<=" + F(maxZoom)
                + " controlled=" + controlled + "/" + frames + " influenced=" + influencedFrames + " takeOvers=" + OnlineCamera.TakeOvers);
            h.Check(maxDrift < 1f, "CAM guest: the guest stood still (drift " + F(maxDrift) + ")");
            h.Check(controlled > frames * 0.8f, "CAM guest: the influence camera drives the guest's camera (" + controlled + "/" + frames + " frames)");
            h.Check(baseSamples > 0 && baseline < 0.3f, "CAM guest: host 30 away (outside the radius) -> camera on the guest (offset " + F(baseline) + ")");
            h.Check(maxRight > 1f, "CAM guest: host approaching on the right pulls the camera right (" + F(maxRight) + ")");
            h.Check(minLeft < -1f, "CAM guest: host walking on to the left extends the view left though the guest stood still (" + F(minLeft) + ")");
            h.Check(!float.IsNaN(endOffset) && endOffset < 0.3f, "CAM guest: host gone -> camera back on the guest (offset " + F(endOffset) + ")");
            h.Check(maxStep < 0.6f, "CAM guest: no camera jump (largest frame step " + F(maxStep) + ", fastest " + F(maxRate) + " u/s)");
            h.Check(maxZoom <= OnlineCamera.Settings.MaxZoom + 0.001f, "CAM guest: zoom-out within the cap (" + F(maxZoom) + ")");
        }
    }
}
