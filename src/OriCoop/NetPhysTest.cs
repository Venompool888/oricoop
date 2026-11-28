// Add after NetTestHarness.cs:1136: yield return Safely(h, "P", NetPhysTest.Run(h));
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using OriCoop.Authority;

namespace OriCoop
{
    // Online pushable-stone phase P. Both PCs observe their own game; numbered barriers use message 72.
    public static partial class NetPhysTest
    {
        const byte MsgBarrier = 72;
        static readonly Dictionary<int, Sample> s_localSteps = new Dictionary<int, Sample>();
        static readonly Dictionary<int, Sample> s_partnerSteps = new Dictionary<int, Sample>();
        static uint s_sampleSequence;
        sealed class Sample
        {
            public bool Loaded;
            public int Id;
            public Vector3 Position;
            public int Owner, Seat;
            public uint Epoch;
            public bool Owns, Kinematic, Interacting, GrabStarted;
            public uint Sequence;
            public float Extra;   // one scenario-defined number (PH: share of frames on ground)
        }

        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Game.Characters.Sein; } }
        static SeinCharacter Puppet { get { return P2Context.P2; } }
        static string F(float value) { return value.ToString("F2"); }
        static string V(Vector3 value) { return "(" + F(value.x) + "," + F(value.y) + ")"; }
        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) { KeepAlive(); yield return null; }
        }

        // #424: waits until the stone lies still on this PC for `still` real seconds, at most `limit` seconds; waited[0] =
        // how long it took, waited[1] = 1 when it came to rest. Still means what the pacer counts as still, with margin:
        // within half of PositionEpsilon / RotationEpsilon of where it lay when the quiet began, and, where this PC
        // simulates it, under half of SpeedEpsilon / AngularEpsilon. 2026-11-28 round 3: the first version compared one
        // frame with the next (0.005), and at 140-170 fps a stone creeping at 0.2-0.4 a second stays under that every
        // frame, so it was called still while it was not (the window then saw max speed 0.16-0.39).
        static IEnumerator WaitAtRest(PushPullBlock block, float still, float limit, float[] waited)
        {
            Rigidbody body = block.GetComponent<Rigidbody>();
            float began = Time.realtimeSinceStartup, quietSince = began;
            Vector3 anchor = block.Position; float anchorRot = block.transform.eulerAngles.z;
            waited[1] = 0f;
            while (Time.realtimeSinceStartup - began < limit)
            {
                KeepAlive(); yield return null;
                Vector3 now = block.Position; float rot = block.transform.eulerAngles.z;
                bool fast = body != null && !body.isKinematic && (Mathf.Abs(body.velocity.x) > StoneSendPacer.SpeedEpsilon * 0.5f
                    || Mathf.Abs(body.velocity.y) > StoneSendPacer.SpeedEpsilon * 0.5f || Mathf.Abs(body.angularVelocity.z) > StoneSendPacer.AngularEpsilon * 0.5f);
                if (fast || Mathf.Max(Mathf.Abs(now.x - anchor.x), Mathf.Abs(now.y - anchor.y)) > StoneSendPacer.PositionEpsilon * 0.5f
                    || Mathf.Abs(Mathf.DeltaAngle(anchorRot, rot)) > StoneSendPacer.RotationEpsilon * 0.5f)
                { quietSince = Time.realtimeSinceStartup; anchor = now; anchorRot = rot; }
                if (Time.realtimeSinceStartup - quietSince >= still) { waited[1] = 1f; break; }
            }
            waited[0] = Time.realtimeSinceStartup - began;
        }

        // T-226: a push whose distance is checked lasts this much game (physics) time, capped in real time. A busy machine
        // runs fewer physics steps per real second (Unity drops game time when a frame takes too long), so a push timed
        // in real seconds moved the stone less and missed the distance threshold (PB, PF). Returns the real seconds used.
        static IEnumerator GameWait(float seconds, float[] realUsed)
        {
            float began = Time.realtimeSinceStartup, until = Time.fixedTime + seconds, cap = began + seconds * 3f + 2f;
            while (Time.fixedTime < until && Time.realtimeSinceStartup < cap) { KeepAlive(); yield return null; }
            realUsed[0] = Time.realtimeSinceStartup - began;
        }

        static void OnBarrier(BinaryReader r)
        {
            int step = r.ReadInt32();
            Sample sample = new Sample();
            sample.Loaded = r.ReadBoolean(); sample.Id = r.ReadInt32();
            sample.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            sample.Owner = r.ReadInt32(); sample.Seat = r.ReadInt32(); sample.Epoch = r.ReadUInt32(); sample.Owns = r.ReadBoolean();
            sample.Kinematic = r.ReadBoolean(); sample.Interacting = r.ReadBoolean(); sample.Sequence = r.ReadUInt32();
            sample.GrabStarted = r.ReadBoolean();
            sample.Extra = r.ReadSingle();
            Sample prior;
            if (!s_partnerSteps.TryGetValue(step, out prior) || sample.Sequence > prior.Sequence) s_partnerSteps[step] = sample;
        }

        static Sample SendSample(int step, PushPullBlock block)
        {
            return SendSample(step, block, true);
        }

        static Sample SendSample(int step, PushPullBlock block, bool reliable, bool grabStarted = false, float extra = 0f)
        {
            bool loaded = block != null;
            string key = loaded ? PushBlockSync.StateOf(block).Key : "";
            IAuthority authority = AuthoritySource.Current;
            Sample sample = new Sample { Loaded = loaded, Id = loaded ? PushBlockSync.StateOf(block).Id : 0,
                Position = loaded ? block.Position : Vector3.zero, Owner = loaded ? authority.OwnerOf(key) : 0,
                Seat = authority.LocalSeat, Epoch = loaded ? authority.EpochOf(key) : 0, Owns = loaded && authority.Owns(key),
                Kinematic = !loaded || block.GetComponent<Rigidbody>().isKinematic,
                Interacting = loaded && Own != null && Own.Abilities.GrabBlock.IsGrabbing && block.IsGrabbed,
                Sequence = ++s_sampleSequence, GrabStarted = grabStarted, Extra = extra };
            s_localSteps[step] = sample;
            NetPlay.Send(MsgBarrier, reliable, w => {
                w.Write(step); w.Write(sample.Loaded); w.Write(sample.Id);
                w.Write(sample.Position.x); w.Write(sample.Position.y); w.Write(sample.Position.z);
                w.Write(sample.Owner); w.Write(sample.Seat); w.Write(sample.Epoch); w.Write(sample.Owns); w.Write(sample.Kinematic);
                w.Write(sample.Interacting); w.Write(sample.Sequence);
                w.Write(sample.GrabStarted);
                w.Write(sample.Extra);
            });
            return sample;
        }

        static IEnumerator Barrier(TestHarness h, int step, PushPullBlock block, bool grabStarted = false, float extra = 0f)
        {
            SendSample(step, block, true, grabStarted, extra);
            float end = Time.realtimeSinceStartup + 30f;
            while (NetPlay.Connected && !s_partnerSteps.ContainsKey(step) && Time.realtimeSinceStartup < end)
            { KeepAlive(); yield return null; }
            h.Check(s_partnerSteps.ContainsKey(step), "P barrier " + step + " reached by both");
        }

        static void CheckOwner(TestHarness h, int step, string phase)
        {
            Sample partner, mine;
            if (!s_localSteps.TryGetValue(step, out mine)) { h.Check(false, phase + " local ownership sample exists"); return; }
            s_partnerSteps.TryGetValue(step, out partner);
            CheckOwner(h, mine, partner, phase);
        }

        static bool OwnersAgree(Sample mine, Sample partner)
        {
            return mine != null && partner != null && mine.Owner != 0 && partner.Owner == mine.Owner && partner.Epoch == mine.Epoch
                && (mine.Owns ? 1 : 0) + (partner.Owns ? 1 : 0) == 1
                && (mine.Kinematic ? 0 : 1) + (partner.Kinematic ? 0 : 1) == 1
                && mine.Kinematic == !mine.Owns && partner.Kinematic == !partner.Owns
                && mine.Owns == (mine.Seat == mine.Owner) && partner.Owns == (partner.Seat == mine.Owner);
        }

        static void CheckOwner(TestHarness h, Sample mine, Sample partner, string phase)
        {
            h.Check(OwnersAgree(mine, partner),
                phase + " exactly one lease owner and simulating body; both PCs agree on OwnerOf and epoch");
        }

        static void Bot(bool push, int dir = 1)
        {
            NetTestHarness.BotButtons = 0; NetTestHarness.BotLX = push ? dir : 0f;
            P2Input.BotRT = push;
        }

        sealed class GrabSetup
        {
            public bool Started;
            public int Attempts;
        }

        // dir: +1 = at the left face, pushing right (the default); -1 = at the right face, pushing left. helper: a second
        // grabber does not own the stone, so the stone's own grab flag (set by its simulating owner) is not asked for.
        static bool PusherReady(PushPullBlock block, int dir = 1, bool helper = false)
        {
            return Own != null && block != null && Own.PlatformBehaviour.PlatformMovement.IsOnGround
                && (dir > 0 ? Own.Position.x < BlockBounds(block).center.x : Own.Position.x > BlockBounds(block).center.x)
                && Own.Abilities.GrabBlock.IsGrabbing && (helper || block.IsGrabbed);
        }

        static IEnumerator GrabBlock(PushPullBlock block, GrabSetup result, int dir = 1, bool helper = false)
        {
            float end = Time.realtimeSinceStartup + 3f;
            for (int attempt = 1; attempt <= 3 && Time.realtimeSinceStartup < end; attempt++)
            {
                result.Attempts = attempt;
                float attemptEnd = Mathf.Min(end, Time.realtimeSinceStartup + 1f);
                Bot(false);
                float releasedAt = Time.fixedTime;
                while (Time.fixedTime <= releasedAt && Time.realtimeSinceStartup < attemptEnd)
                { KeepAlive(); yield return null; }
                if (Own == null || block == null || Time.realtimeSinceStartup >= attemptEnd) continue;
                Bounds bb = BlockBounds(block), ob = Own.PlatformBehaviour.PlatformMovement.CapsuleCollider.bounds;
                PlaceOwn(dir > 0 ? bb.min.x - ob.extents.x - 0.02f : bb.max.x + ob.extents.x + 0.02f, bb.min.y + 0.04f, block.Position.z);
                Own.PlatformBehaviour.Visuals.SpriteMirror.FaceLeft = dir < 0;
                float approachedAt = Time.fixedTime;
                NetTestHarness.BotLX = dir;
                while (Time.fixedTime <= approachedAt && Time.realtimeSinceStartup < attemptEnd)
                { KeepAlive(); yield return null; }
                bool pressed = false;
                float grabbedAt = -1f;
                while (Own != null && block != null && Time.realtimeSinceStartup < attemptEnd)
                {
                    KeepAlive();
                    if (pressed && PusherReady(block, dir, helper))
                    {
                        if (grabbedAt < 0f) grabbedAt = Time.realtimeSinceStartup;
                        if (Time.realtimeSinceStartup - grabbedAt >= 0.1f)
                        { result.Started = true; NetTestHarness.BotLX = 0f; yield break; }
                    }
                    else grabbedAt = -1f;
                    if (!pressed && Own.Abilities.GrabBlock.InRange && Own.PlatformBehaviour.PlatformMovement.IsOnGround)
                    { NetTestHarness.BotLX = 0f; P2Input.BotRT = true; pressed = true; }
                    else if (!pressed) NetTestHarness.BotLX = dir;
                    yield return null;
                }
            }
            Bot(false);
        }

        static bool InScene(RuntimeSceneMetaData scene, Vector3 position)
        {
            foreach (Rect boundary in scene.SceneBoundaries)
                if (boundary.Contains(new Vector2(position.x, position.y))) return true;
            return false;
        }

        static PushPullBlock FindBlock(RuntimeSceneMetaData scene, bool largest = false)
        {
            PushPullBlock chosen = null;
            for (int i = 0; i < PushPullBlock.All.Count; i++)
            {
                PushPullBlock block = PushPullBlock.All[i];
                if (!InScene(scene, block.Position)) continue;
                if (chosen == null || (largest ? PushBlockSync.StateOf(block).Id > PushBlockSync.StateOf(chosen).Id
                    : PushBlockSync.StateOf(block).Id < PushBlockSync.StateOf(chosen).Id)) chosen = block;
            }
            return chosen;
        }

        static Bounds BlockBounds(PushPullBlock block)
        {
            Bounds bounds = new Bounds(); bool any = false;
            foreach (Collider collider in block.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
                if (!any) { bounds = collider.bounds; any = true; }
                else bounds.Encapsulate(collider.bounds);
            }
            return bounds;
        }

        static Vector3 s_lastTarget;
        static void PlaceOwn(float centerX, float feetY, float z)
        {
            s_lastTarget = new Vector3(centerX, feetY + 0.55f, z);
            // Absolute target: Ori.Position is ~0.53 above the feet (measured standing on the stone). Collider bounds can
            // lag the transform right after a reset / push-out, which once put the Ori 3 units off, inside the wall.
            Plugin.Teleport(Own, new Vector3(centerX, feetY + 0.55f, z) - Vector3.up * 0.5f);
            SessionRules.PositionChanged();
        }

        static float Penetration(Bounds block, Bounds ori)
        {
            return Mathf.Max(0f, Mathf.Min(Mathf.Min(ori.max.x - block.min.x, block.max.x - ori.min.x),
                Mathf.Min(ori.max.y - block.min.y, block.max.y - ori.min.y)));
        }

        // Depth inside the stone's real shape (rounded MeshCollider), measured the same way as PushBlockSync's push-out.
        static float ShapeDepth(PushPullBlock block)
        {
            Vector3 move; string dir;
            return PushBlockSync.Depth(block, Own, out move, out dir);
        }

        // Slug hits knock the fixture airborne even if health is restored. Stone/crush damage still applies.
        static void KeepAlive()
        {
            WatchTick();
            if (Own == null || Revive.IsDowned(Own)) return;
            Own.Mortality.DamageReciever.MakeInvincibleToEnemies(0.25f);
            if (Own.Mortality.Health.Amount < Own.Mortality.Health.MaxHealth) Own.Mortality.Health.RestoreAllHealth();
        }

        static void Observe(PushPullBlock block, bool top, ref float contactAt, ref float maxPenetration,
            ref float minFeet, ref bool contacted)
        {
            KeepAlive();
            Bounds bb = BlockBounds(block), ob = Own.PlatformBehaviour.PlatformMovement.CapsuleCollider.bounds;
            float depth = ShapeDepth(block), now = Time.realtimeSinceStartup;
            if (depth > 0f)
            {
                if (contactAt < 0f) contactAt = now;
                if (top || now - contactAt >= 0.1f) maxPenetration = Mathf.Max(maxPenetration, depth);
            }
            else contactAt = -1f;
            float surface = PushBlockSync.SurfaceY(block, ob.center.x);
            if (top && !float.IsNaN(surface)) minFeet = Mathf.Min(minFeet, ob.min.y - surface);
            if (!top && ob.max.y > bb.min.y && ob.min.y < bb.max.y && ob.min.x <= bb.max.x + 0.15f && ob.max.x >= bb.min.x)
                contacted = true;
        }

        static void Note(TestHarness h, string phase, PushPullBlock block)
        {
            if (Own == null || block == null) { h.Note(phase + " (own Ori or stone missing)"); return; }
            PushBlockSync.St state = PushBlockSync.StateOf(block);
            h.Note(phase + " block=" + V(block.Position) + " rz=" + F(block.transform.eulerAngles.z) + " grounded=" + Own.PlatformBehaviour.PlatformMovement.IsOnGround + " hp=" + Own.Mortality.Health.Amount.ToString("F0") + " Ori=" + V(Own.Position) + " puppet=" + (Puppet == null ? "none" : V(Puppet.Position))
                + " id=" + state.Id + " owner=" + state.OwnerSeat + " epoch=" + state.Epoch
                + " localOwner=" + PushBlockSync.IsLocalOwner(block)
                + " ignoresOwn=" + PushBlockSync.Ignores(block, Own.PlatformBehaviour.PlatformMovement)
                + " PushOuts=" + PushBlockSync.PushOuts + " PuppetPushOuts=" + PushBlockSync.PuppetPushOuts
                + " StatesSent=" + PushBlockSync.StatesSent + " StatesApplied=" + PushBlockSync.StatesApplied
                + " Denied=" + PushBlockSync.Denied + " PuppetCallsIgnored=" + PushBlockSync.PuppetCallsIgnored
                + " Snaps=" + PushBlockSync.Snaps + " locked=" + SessionRules.LocalLocked + " watching=" + StorySync.Watching + " respawning=" + SessionRules.Respawning);
        }

        // PB (T-023): both players use the same stone. The host grabs it and only holds it (standing still); then the guest
        // grabs the same face and pushes right. Holding must not lock the stone: the guest takes the lease and the stone
        // moves on both PCs. (Before: the holder's lease stayed "active" every frame, so the guest's push did nothing.)
        static IEnumerator BothInteract(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host, int guestSeat)
        {
            NetPlay.SetSim(0, 0, 0, 0);
            Bot(false);
            PlaceOwn(resting.x + (host ? -2.45f : 2.8f), resting.y + 0.5f, block.Position.z);
            yield return Wait(0.5f);
            yield return Barrier(h, 95, block);
            if (PushBlockSync.IsLocalOwner(block))
            {
                Rigidbody body = block.GetComponent<Rigidbody>();
                body.position = resting; block.transform.position = resting; body.rotation = restingRot; block.transform.rotation = restingRot;
                body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.Sleep();
            }
            yield return Wait(1.2f);
            float settleEnd = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < settleEnd && (block.Position - resting).magnitude > 0.05f) { KeepAlive(); yield return null; }
            GrabSetup holdSetup = new GrabSetup();
            if (host) yield return GrabBlock(block, holdSetup);   // the host keeps BotRT pressed and LX at 0: holding only
            yield return Barrier(h, 96, block, holdSetup.Started);
            if (host) h.Check(holdSetup.Started, "PB host grabbed the stone and holds it");
            Vector3 start = block.Position;
            GrabSetup pushSetup = new GrabSetup();
            if (!host) yield return GrabBlock(block, pushSetup);
            yield return Barrier(h, 97, block, pushSetup.Started);
            if (!host) h.Check(pushSetup.Started, "PB guest grabbed the same stone while the host holds it");
            if (!host) Bot(true);
            // 2.5 s of game time (T-226: was 2.5 real seconds, too little push on a busy machine), counted from the moment this
            // PC sees the guest holding the stone's lease: under load the lease handover itself took part of a fixed window
            // and left dx=0.48 against 0.5 (2026-11-25). A holder that keeps the stone locked (the T-023 bug) never hands the
            // lease over, so the window still ends at the cap with dx near 0. Capped at 12 real s from the start of the push.
            float pushBegan = Time.realtimeSinceStartup, pushUntil = float.MaxValue, pushCap = pushBegan + 12f, ownedAt = -1f; bool guestOwned = false;
            while (Time.fixedTime < pushUntil && Time.realtimeSinceStartup < pushCap)
            {
                KeepAlive();
                if (host) { NetTestHarness.BotLX = 0f; P2Input.BotRT = true; }
                if (!guestOwned && AuthoritySource.Current.OwnerOf(PushBlockSync.StateOf(block).Key) == guestSeat)
                { guestOwned = true; ownedAt = Time.realtimeSinceStartup - pushBegan; pushUntil = Time.fixedTime + 2.5f; }
                yield return null;
            }
            h.Note("PB push window: lease to the guest after " + (guestOwned ? F(ownedAt) : "never") + " real s, then 2.50 game s; "
                + F(Time.realtimeSinceStartup - pushBegan) + " real s in all");
            Bot(false);
            yield return Wait(1.0f);
            float dx = block.Position.x - start.x;
            Note(h, "PB end", block);
            h.Check(guestOwned, "PB the pushing guest took the stone's lease from the holding host");
            h.Check(dx >= 0.5f, "PB the guest's push moved the held stone >=0.5 right on this PC (dx=" + F(dx) + ")");
            yield return Barrier(h, 98, block);
        }

        // PC (T-049): the stone moves faster when both players push it the same way. First the host pushes alone for 2 s,
        // then (stone reset) both grab the same face and push right for 2 s. Each PC measures the stone's travel itself;
        // together must reach >=1.2x the peak speed (the rule is 1.5x on the speed cap, which the Oris need ~0.5 s of the
        // stone's ~1.4 of travel to reach; before T-049 the second push did nothing: about 1x). The stone has a stop about
        // 1.4 to the right, so the metric is the peak speed over 0.2 s windows (snaps > 1.0 per window are ignored).
        static IEnumerator ResetStone(TestHarness h, int step, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host)
        {
            Bot(false);
            PlaceOwn(resting.x + (host ? -2.45f : 2.8f), resting.y + 0.5f, block.Position.z);
            yield return Wait(0.5f);
            yield return Barrier(h, step, block);
            if (PushBlockSync.IsLocalOwner(block))
            {
                Rigidbody body = block.GetComponent<Rigidbody>();
                body.position = resting; block.transform.position = resting; body.rotation = restingRot; block.transform.rotation = restingRot;
                body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.Sleep();
            }
            yield return Wait(1.2f);
            float settleEnd = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < settleEnd && (block.Position - resting).magnitude > 0.05f) { KeepAlive(); yield return null; }
        }

        static IEnumerator PushTogether(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host)
        {
            NetPlay.SetSim(0, 0, 0, 0);
            float[] travel = new float[2], ratio = new float[2]; int[] boosted = new int[2];
            for (int round = 0; round < 2; round++)
            {
                int boosted0 = PushBlockSync.CoPushBoosted;
                bool together = round == 1; int step = 100 + round * 10;
                yield return ResetStone(h, step, block, resting, restingRot, host);
                GrabSetup setup = new GrabSetup();
                if (host || together) yield return GrabBlock(block, setup);
                yield return Barrier(h, step + 1, block, setup.Started);
                bool pushing = host || together;
                if (pushing) h.Check(setup.Started, "PC " + (together ? "together" : "alone") + ": grabbed the stone");
                Vector3 start = block.Position;
                if (pushing) Bot(true);
                float pushStart = Time.realtimeSinceStartup, end = pushStart + 2f, peak = 0f;
                float winAt = pushStart, winX = block.Position.x;
                while (Time.realtimeSinceStartup < end)
                {
                    KeepAlive();
                    ratio[round] = Mathf.Max(ratio[round], block.PushableSpeedRatio());
                    if (Time.realtimeSinceStartup - winAt >= 0.2f)
                    {
                        float dx = block.Position.x - winX;
                        if (dx > 0f && dx < 1.0f) peak = Mathf.Max(peak, dx / (Time.realtimeSinceStartup - winAt));
                        winAt = Time.realtimeSinceStartup; winX = block.Position.x;
                    }
                    yield return null;
                }
                Bot(false);
                yield return Wait(1.0f);
                travel[round] = peak; boosted[round] = PushBlockSync.CoPushBoosted - boosted0;
                Note(h, "PC " + (together ? "together" : "alone") + " end", block);
                yield return Barrier(h, step + 2, block);
            }
            h.Note("PC peak stone speed: alone=" + F(travel[0]) + " together=" + F(travel[1]));
            h.Check(travel[0] >= 0.5f, "PC pushing alone moves the stone (peak " + F(travel[0]) + "/s)");
            // The game's own push speed ratio is what drives both Oris and the stone; it is the oracle (1.0 alone, 1.5 together,
            // on both PCs). The measured peak speed is noisy (the Oris need ~0.5 s to reach their cap within the stone's ~1.4
            // of travel, a follower's copy is interpolated), so it is only noted.
            h.Check(Mathf.Abs(ratio[0] - 1f) < 0.01f && Mathf.Abs(ratio[1] - 1.5f) < 0.01f,
                "PC push speed ratio is 1.0 pushing alone and 1.5 with both pushing (alone " + F(ratio[0]) + ", together " + F(ratio[1]) + ")");
            // Only noted (T-083): the stone hits its stop after ~1.4, before both Oris reach their capped speed, so the
            // peak failed on plain main (631dcc9) although the ratio above passed. The ratio and the engaged count below
            // are the checks.
            if (host) h.Note("PC two pushers' peak stone speed vs one pusher's: " + F(travel[1] / Mathf.Max(0.01f, travel[0])) + "x (alone " + F(travel[0]) + "/s, together " + F(travel[1]) + "/s)");
            h.Check(boosted[0] == 0 && boosted[1] >= 1, "PC the push speed-up engaged on this PC only while both pushed (alone " + boosted[0] + ", together " + boosted[1] + ")");
        }

        // ---- co-op stone scenarios from playtest 4 (T-197, T-198), on the scene's second stone (largest id). In the test
        // save it stands at the right end of a long flat floor (about 18 units free to its left), so pushers stand at
        // its right face and push left. The host pushes first, the guest is the partner. 60 ms are added on each side
        // (120 ms RTT, as in the playtest). ORICOOP_P_ONLY=COOP runs only these.
        const int CoopDir = -1;
        const float CoopIdle = 16.5f;   // players who only watch wait this far left of the stone's start, beyond its travel
        static string Pct(float share) { return Mathf.RoundToInt(share * 100f) + "%"; }
        static readonly System.Reflection.FieldInfo s_groundPlatform = HarmonyLib.AccessTools.Field(typeof(MovingPlatformsController), "m_oldGroundPlatform");

        static bool OnTop(PushPullBlock block)
        {
            if (Own == null || block == null || !Own.PlatformBehaviour.PlatformMovement.IsOnGround) return false;
            Bounds ob = Own.PlatformBehaviour.PlatformMovement.CapsuleCollider.bounds;
            float surface = PushBlockSync.SurfaceY(block, ob.center.x);
            return !float.IsNaN(surface) && Mathf.Abs(ob.min.y - surface) <= 0.25f;
        }

        // Stone watch: KeepAlive runs in every frame of every wait of this phase, so it also records how the watched
        // stone moves on this PC. Pushes go CoopDir, so a step the other way is a step back.
        static PushPullBlock s_watch;
        static float s_watchX, s_watchBack, s_watchStep;
        static int s_watchSnaps;
        static void WatchStart(PushPullBlock block)
        {
            s_watch = block; s_watchX = block.Position.x; s_watchBack = s_watchStep = 0f; s_watchSnaps = PushBlockSync.Snaps;
        }
        static void WatchTick()
        {
            if (s_watch == null) return;
            float x = s_watch.Position.x, step = x - s_watchX;
            s_watchBack = Mathf.Max(s_watchBack, step * -CoopDir);
            s_watchStep = Mathf.Max(s_watchStep, Mathf.Abs(step));
            s_watchX = x;
        }
        static int WatchSnaps { get { return PushBlockSync.Snaps - s_watchSnaps; } }

        static void CoopIdlePlace(PushPullBlock block, Vector3 resting, bool host)
        {
            Bounds bb = BlockBounds(block);
            PlaceOwn(resting.x - CoopIdle + (host ? 0f : 1.2f), bb.min.y + 0.5f, block.Position.z);
        }

        // Both wait far to the left; the stone's owner (or each PC, while nobody owns it) puts it back at its start.
        static IEnumerator CoopReset(TestHarness h, int step, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host)
        {
            Bot(false);
            s_watch = null;
            CoopIdlePlace(block, resting, host);
            yield return Wait(0.6f);
            yield return Barrier(h, step, block);
            if (PushBlockSync.IsLocalOwner(block) || AuthoritySource.Current.OwnerOf(PushBlockSync.StateOf(block).Key) == 0)
            {
                Rigidbody body = block.GetComponent<Rigidbody>();
                body.position = resting; block.transform.position = resting; body.rotation = restingRot; block.transform.rotation = restingRot;
                body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            }
            yield return Wait(1.2f);
            float settleEnd = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < settleEnd && (block.Position - resting).magnitude > 0.1f) { KeepAlive(); yield return null; }
        }

        static bool Agree(PushPullBlock block, Sample partner, out string detail)
        {
            float dx = Mathf.Abs(block.Position.x - partner.Position.x), dy = Mathf.Abs(block.Position.y - partner.Position.y);
            detail = "dx=" + F(dx) + ", dy=" + F(dy);
            return partner.Loaded && dx < 0.3f && dy < 0.3f;
        }

        // PH (T-198): the guest stands on the stone, first at rest, then while the host pushes it. Oracle: the guest's
        // own game (is its Ori on ground?); the host's copy of the guest must agree. Before the fix the host saw the
        // partner in the air (falling pose) all the time, because puppets do not collide with stones.
        // Moving part: a partner that rides along on its own PC (on ground 75% of the time or more) must be on ground
        // here as well. When the stone slides out from under it on its own PC (seen when the rider's PC owned the
        // stone just before the push: it drops to the floor), the two PCs are compared instead: the copy must be on
        // ground about as often as the partner itself.
        static IEnumerator Rider(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host)
        {
            yield return CoopReset(h, 210, block, resting, restingRot, host);
            bool onTop = false;
            for (int attempt = 0; !host && attempt < 2 && !onTop; attempt++)
            {
                Bounds bb = BlockBounds(block);
                PlaceOwn(bb.center.x, bb.max.y + 0.4f, block.Position.z);
                yield return Wait(1.0f);
                onTop = OnTop(block);
            }
            yield return Barrier(h, 211, block, onTop);
            Sample partner;
            if (!s_partnerSteps.TryGetValue(211, out partner)) yield break;
            if (!(host ? partner.GrabStarted : onTop))
            {
                h.Check(false, "PH setup: the guest stands on top of the stone (Ori=" + (Own == null ? "none" : V(Own.Position)) + ")");
                yield break;
            }
            for (int part = 0; part < 2; part++)
            {
                bool riding = part == 1; int step = 212 + part * 2;
                GrabSetup setup = new GrabSetup();
                if (riding && host) yield return GrabBlock(block, setup, CoopDir);
                if (riding)
                {
                    yield return Barrier(h, 220, block, setup.Started);
                    if (!s_partnerSteps.TryGetValue(220, out partner)) yield break;
                    if (!(host ? setup.Started : partner.GrabStarted))
                    {
                        Bot(false);
                        h.Check(false, "PH setup: the host grabbed the stone under the rider");
                        yield break;
                    }
                    if (host) Bot(true, CoopDir);
                }
                Vector3 start = block.Position;
                int frames = 0, grounded = 0, onStone = 0;
                SeinCharacter rider = host ? Puppet : Own;
                float end = Time.realtimeSinceStartup + (riding ? 2f : 1.5f), offset0 = rider == null ? 0f : rider.Position.x - block.Position.x;
                while (Time.realtimeSinceStartup < end)
                {
                    KeepAlive();
                    if (rider != null)
                    {
                        frames++;
                        if (rider.PlatformBehaviour.PlatformMovement.IsOnGround) grounded++;
                        Transform under = s_groundPlatform.GetValue(rider.PlatformBehaviour.PlatformMovement.MovingPlatforms) as Transform;
                        if (under != null && under.IsChildOf(block.transform)) onStone++;
                    }
                    yield return null;
                }
                Bot(false);
                float share = frames == 0 ? 0f : (float)grounded / frames, travel = (block.Position.x - start.x) * CoopDir;
                if (rider != null) h.Note("PH " + (riding ? "moving" : "resting") + ": rider - stone x " + F(offset0) + " -> " + F(rider.Position.x - block.Position.x)
                    + ", stone moved " + F(block.Position.x - start.x) + ", rider=" + V(rider.Position) + " stone=" + V(block.Position)
                    + ", the stone was the rider's ground platform in " + Pct(frames == 0 ? 0f : (float)onStone / frames) + " of frames");
                yield return Wait(0.5f);
                yield return Barrier(h, step, block, false, share);
                if (!s_partnerSteps.TryGetValue(step, out partner)) yield break;
                if (!riding)
                {
                    if (!host) h.Check(share >= 0.9f, "PH the guest's own Ori is on ground standing on the resting stone (" + Pct(share) + " of frames)");
                    else if (partner.Extra < 0.9f) h.Check(false, "PH cannot judge: the guest itself was not standing on the resting stone (on ground " + Pct(partner.Extra) + " of frames on its own PC)");
                    else h.Check(share >= 0.9f, "PH the partner standing on the resting stone is on ground on this PC too (" + Pct(share)
                        + " of frames here, " + Pct(partner.Extra) + " on its own PC; stands reported " + PushBlockSync.PuppetStands + ")");
                }
                else if (host)
                {
                    h.Check(travel >= 1f, "PH the host's push moved the stone under the partner (" + F(travel) + ")");
                    if (partner.Extra < 0.3f) h.Note("PH cannot judge the moving part: the guest itself was on ground only " + Pct(partner.Extra) + " of frames");
                    else if (partner.Extra >= 0.75f) h.Check(share >= 0.75f, "PH the partner riding the stone the host pushes is on ground on this PC too ("
                        + Pct(share) + " of frames here, " + Pct(partner.Extra) + " on its own PC)");
                    else h.Check(Mathf.Abs(share - partner.Extra) <= 0.25f, "PH while the stone moves under the partner, its copy here is on ground about as often as the partner itself ("
                        + Pct(share) + " of frames here, " + Pct(partner.Extra) + " on its own PC)");
                }
            }
            Note(h, "PH end", block);
        }

        // PE (T-197): both players hold the same face of one stone and push it the same way. The host starts; the
        // guest joins while the stone moves. Both must keep their hold for the whole push (each PC judges its own Ori;
        // the partner's copy must show the same), the x1.5 co-push speed must stay on, and the stone must never jump
        // back. Before the fix the second pusher fell out of the push state within half a second (the grab ray stopped
        // at the partner's capsule; the hold was dropped when the lease was refused; the takeover prediction made the
        // stone run ahead and jump back).
        static IEnumerator PushSameStone(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host, bool pauseStates = false)
        {
            int step = pauseStates ? 270 : 230;
            yield return CoopReset(h, step, block, resting, restingRot, host);
            GrabSetup first = new GrabSetup();
            if (host) { yield return GrabBlock(block, first, CoopDir); if (first.Started) Bot(true, CoopDir); }
            yield return Barrier(h, step + 1, block, first.Started);
            Sample partner;
            if (!s_partnerSteps.TryGetValue(step + 1, out partner)) yield break;
            if (!(host ? first.Started : partner.GrabStarted))
            {
                Bot(false);
                h.Check(false, "PE setup: the host grabbed the stone and pushes it");
                yield break;
            }
            WatchStart(block);
            yield return Wait(0.5f);   // the host owns the stone and it moves
            GrabSetup second = new GrabSetup();
            if (!host) { yield return GrabBlock(block, second, CoopDir, true); if (second.Started) Bot(true, CoopDir); }
            yield return Barrier(h, step + 2, block, second.Started);
            if (!s_partnerSteps.TryGetValue(step + 2, out partner)) yield break;
            if (!(host ? partner.GrabStarted : second.Started))
            {
                Bot(false); s_watch = null;
                h.Check(false, "PE setup: the guest grabbed the stone the host is pushing (try " + second.Attempts + ")");
                yield return Barrier(h, step + 3, block);
                yield break;
            }
            if (pauseStates)
            {
                yield return OwnerStateGap(h, block, host);
                yield break;
            }
            // The push stays on the first 9 units of the floor (further left it rises, and an Ori that leaves the ground
            // for a frame lets go, as in the plain game).
            int frames = 0, own = 0, puppet = 0, boosted = 0;
            float began = Time.realtimeSinceStartup, end = began + 1.6f, gameBegan = Time.fixedTime;
            int lastOwner = PushBlockSync.StateOf(block).OwnerSeat; bool lastOwn = true, lastPuppet = true; string events = "";
            while (Time.realtimeSinceStartup < end && (block.Position.x - resting.x) * CoopDir < 9f)
            {
                KeepAlive();
                frames++;
                bool ownHolds = Own != null && Own.Abilities.GrabBlock.IsGrabbing, puppetHolds = Puppet != null && Puppet.Abilities.GrabBlock.IsGrabbing;
                int owner = PushBlockSync.StateOf(block).OwnerSeat;
                if (ownHolds) own++;
                if (puppetHolds) puppet++;
                if (block.PushableSpeedRatio() > 1.49f) boosted++;
                if ((ownHolds != lastOwn || puppetHolds != lastPuppet || owner != lastOwner) && events.Length < 600)
                    events += " t=" + F(Time.realtimeSinceStartup - began) + ":own=" + (ownHolds ? 1 : 0) + ",copy=" + (puppetHolds ? 1 : 0) + ",owner=P" + owner
                        + ",grounded=" + (Own.PlatformBehaviour.PlatformMovement.IsOnGround ? 1 : 0) + ",gap=" + F(Mathf.Abs(Own.Position.x - block.Position.x))
                        + ",travel=" + F((block.Position.x - resting.x) * CoopDir);
                lastOwn = ownHolds; lastPuppet = puppetHolds; lastOwner = owner;
                yield return null;
            }
            Bot(false);
            // T-226: how loaded this PC was during the push (a busy machine shows few frames and less game time per real s).
            h.Note("PE push window: " + frames + " frames, " + F(Time.fixedTime - gameBegan) + " game s in " + F(Time.realtimeSinceStartup - began) + " real s");
            h.Note("PE holds during the push (own Ori, partner copy, lease owner):" + (events.Length == 0 ? " no change" : events));
            float ownShare = frames == 0 ? 0f : (float)own / frames, puppetShare = frames == 0 ? 0f : (float)puppet / frames,
                boostShare = frames == 0 ? 0f : (float)boosted / frames, travel = (block.Position.x - resting.x) * CoopDir;
            yield return Wait(1.0f);
            float back = s_watchBack; int snaps = WatchSnaps; s_watch = null;
            yield return Barrier(h, 233, block, false, ownShare);
            if (!s_partnerSteps.TryGetValue(233, out partner)) yield break;
            string who = host ? "first pusher (host)" : "second pusher (guest)";
            h.Check(ownShare >= 0.9f, "PE the " + who + " keeps hold of the stone while both push it (" + Pct(ownShare) + " of the push)");
            if (partner.Extra < 0.9f) h.Check(false, "PE cannot judge the partner's pose: it did not keep hold on its own PC (" + Pct(partner.Extra) + ")");
            else h.Check(puppetShare >= 0.75f, "PE the partner is shown holding the stone here (" + Pct(puppetShare) + " of frames, " + Pct(partner.Extra) + " on its own PC)");
            h.Check(boostShare >= 0.6f, "PE both pushing the same way keeps the x1.5 speed on (" + Pct(boostShare) + " of frames)");
            h.Check(back <= 1f && snaps == 0, "PE the stone never jumps back on this PC (largest step back " + F(back) + ", snaps " + snaps + ")");
            string detail;
            h.Check(Agree(block, partner, out detail), "PE the stone ends in the same place on both PCs (" + detail + ")");
            if (host) h.Check(travel >= 2f, "PE the stone moved (" + F(travel) + ")");
            h.Note("PE: partner copies took hold again " + PushBlockSync.PuppetRegrabs + " times on this PC; owner P" + PushBlockSync.StateOf(block).OwnerSeat
                + ", own Ori grabbing " + (Own != null && Own.Abilities.GrabBlock.IsGrabbing) + " at the end");
            Note(h, "PE end", block);
        }

        static string s_gapStone;
        static int s_gapPackets;

        [HarmonyLib.HarmonyPatch(typeof(DynamicLease), "Receive")]
        static class PauseGuestStoneStates
        {
            static bool Prepare() { return TestHarness.Enabled; }
            static bool Prefix(StateUpdate state)
            {
                if (state.Key != s_gapStone || s_gapStone == null) return true;
                s_gapPackets++;
                return false;
            }
        }

        // Pause delivery to the helper only. The arbiter must still hear the owner's active states.
        static IEnumerator OwnerStateGap(TestHarness h, PushPullBlock block, bool host)
        {
            PushBlockSync.St stone = PushBlockSync.StateOf(block);
            DynamicLease lease = stone.Lease;
            uint epoch = lease.Epoch;
            if (host) h.Check(lease.Owns && lease.Active, "PI the first pusher owns the moving stone");
            else
            {
                bool heldDuringGap = false, predicted = false;
                float lastPacket = lease.ReceivedAt;
                s_gapStone = stone.Key;
                s_gapPackets = 0;
                try
                {
                    float until = Time.realtimeSinceStartup + 0.7f;
                    while (Time.realtimeSinceStartup < until)
                    {
                        KeepAlive();
                        float now = Time.realtimeSinceStartup;
                        if (lease.Epoch == epoch && lease.StateEpoch == epoch && !lease.Owns && lease.RemoteActive
                            && now < stone.InteractionUntil && now - lastPacket > 0.3f)
                        {
                            heldDuringGap = true;
                            predicted |= stone.Simulation.Predicting || !block.GetComponent<Rigidbody>().isKinematic;
                        }
                        yield return null;
                    }
                }
                finally { s_gapStone = null; }
                h.Check(heldDuringGap && s_gapPackets > 0, "PI the guest kept pushing during the owner's state gap");
                h.Check(!predicted, "PI the helper follows throughout the gap without starting a local simulation");
                float resumeBy = Time.realtimeSinceStartup + 3f;
                while (lease.ReceivedAt <= lastPacket && Time.realtimeSinceStartup < resumeBy)
                { KeepAlive(); yield return null; }
                h.Check(lease.Epoch == epoch && lease.ReceivedAt > lastPacket, "PI fresh owner states resume on the same stone");
            }
            yield return Barrier(h, 273, block);
            Bot(false);
            yield return Wait(0.8f);
            yield return Barrier(h, 274, block);
            Sample partner; string detail;
            if (s_partnerSteps.TryGetValue(274, out partner))
                h.Check(Agree(block, partner, out detail), "PI the stone settles at the same position on both PCs (" + detail + ")");
            s_watch = null;
        }

        // PF (T-197): the lease goes host -> guest -> host, each pushing the stone on alone. A PC that follows the new
        // owner still has the state that owner published two leases ago: before the fix the stone jumped back there
        // (3 units or more here, 31 in the playtest) and returned when the owner's first new state arrived. Then the
        // owner's stones are reset the way a partner's rejoin does: its stone must stay where it is (playtest: both
        // PCs saw it jump from -246 to -279).
        static IEnumerator Handover(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host)
        {
            yield return CoopReset(h, 240, block, resting, restingRot, host);
            Sample partner; string detail;
            for (int k = 0; k < 3; k++)
            {
                bool pusher = (k % 2 == 0) == host; int step = 241 + k * 3;
                CoopIdlePlace(block, resting, host);
                yield return Wait(0.4f);
                yield return Barrier(h, step, block);
                WatchStart(block);
                Vector3 start = block.Position;
                GrabSetup setup = new GrabSetup();
                if (pusher) { yield return GrabBlock(block, setup, CoopDir); if (setup.Started) Bot(true, CoopDir); }
                yield return Barrier(h, step + 1, block, setup.Started);
                if (!s_partnerSteps.TryGetValue(step + 1, out partner)) yield break;
                if (!(pusher ? setup.Started : partner.GrabStarted))
                {
                    Bot(false); s_watch = null;
                    h.Check(false, "PF setup: push " + (k + 1) + " grabbed the stone");
                    yield break;
                }
                float[] used = { 0f };
                yield return GameWait(1.5f, used);   // T-226: game time, not 1.5 real seconds
                Bot(false);
                if (pusher) h.Note("PF push " + (k + 1) + " window: 1.50 game s in " + F(used[0]) + " real s");
                yield return Wait(1.0f);
                float back = s_watchBack, travel = (block.Position.x - start.x) * CoopDir; int snaps = WatchSnaps; s_watch = null;
                yield return Barrier(h, step + 2, block);
                if (!s_partnerSteps.TryGetValue(step + 2, out partner)) yield break;
                string whose = pusher ? "own push" : "the partner's push";
                h.Check(back <= 1f && snaps == 0, "PF push " + (k + 1) + " (" + whose + "): the stone never jumps back on this PC (largest step back " + F(back) + ", snaps " + snaps + ")");
                h.Check(Agree(block, partner, out detail), "PF after push " + (k + 1) + " the stone is in the same place on both PCs (" + detail + ")");
                if (pusher) h.Check(travel >= 1.5f, "PF push " + (k + 1) + " moved the stone (" + F(travel) + ")");
                if (pusher && travel < 1.5f) Note(h, "PF push " + (k + 1) + " short", block);   // #424 round 3: push 2 once moved 0.00
            }
            // #424: the stone lies still after the pushes. Its owner sends a heartbeat (2 a second, after a 0.5 s burst
            // when it stops), not 20 states a second; the partner still hears it and both PCs agree where it lies.
            // 2026-11-28: 1 s after the last push the stone was still sliding on a busy test PC (max speed 0.9 in the
            // window), so the window starts once it has lain still here for longer than the pacer's settle burst.
            float[] rest = new float[2];
            yield return WaitAtRest(block, StoneSendPacer.Settle + 0.2f, 8f, rest);
            h.Note("PF stone came to rest after " + F(rest[0]) + " s" + (rest[1] > 0f ? "" : " (still moving at the limit)"));
            yield return Barrier(h, 252, block);
            PushBlockSync.St idleState = PushBlockSync.StateOf(block);
            DynamicLease idleLease = DynamicLease.For(idleState.Key);
            bool idleOwner = PushBlockSync.IsLocalOwner(block);
            int idleSentBefore = idleState.Sent; float idleHeardBefore = idleLease.ReceivedAt;
            idleState.Pacer.ResetStats();
            Vector3 idleFrom = block.Position;
            yield return Wait(2.0f);
            int idleSent = idleState.Sent - idleSentBefore;
            string idleWhy = idleState.Pacer.Why() + "; drift " + F((block.Position - idleFrom).magnitude) + ", fps " + F(1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f))
                + ", interacting " + (Time.realtimeSinceStartup < idleState.InteractionUntil);
            if (idleOwner) h.Note("PF stone at rest: " + idleSent + " states in 2 s (" + idleWhy + ")");
            bool idleOwnerAfter = PushBlockSync.IsLocalOwner(block);
            yield return Barrier(h, 253, block);
            if (s_partnerSteps.TryGetValue(253, out partner))
            {
                if (idleOwner && idleOwnerAfter && rest[1] == 0f)
                    h.Check(false, "PF setup: the stone came to rest on its owner's PC within 8 s (" + idleWhy + ")");
                else if (idleOwner && idleOwnerAfter)
                    h.Check(idleSent <= 8, "PF the owner of a stone at rest sends a heartbeat, not 20 states a second (" + idleSent + " states in 2 s; " + idleWhy + ")");
                else if (!idleOwner && !idleOwnerAfter && idleLease.Owner != 0)
                    h.Check(idleLease.ReceivedAt > idleHeardBefore, "PF the partner still hears the owner of a stone at rest (heartbeat)");
                h.Check(Agree(block, partner, out detail), "PF the stone at rest is in the same place on both PCs (" + detail + ")");
            }
            yield return Barrier(h, 250, block);
            WatchStart(block);
            if (host) PushBlockSync.TestSessionReset();
            yield return Wait(1.5f);
            float moved = s_watchStep; int jumped = WatchSnaps; s_watch = null;
            yield return Barrier(h, 251, block);
            if (!s_partnerSteps.TryGetValue(251, out partner)) yield break;
            h.Check(moved <= 1f && jumped == 0, "PF a session reset on the stone's owner leaves the stone where it is on this PC (largest step " + F(moved) + ", snaps " + jumped + ")");
            h.Check(Agree(block, partner, out detail), "PF after the session reset the stone is in the same place on both PCs (" + detail + ")");
            Note(h, "PF end", block);
        }

        // PG (T-197): the host goes down while it pushes the stone. A downed Ori is suspended, and the game's grab
        // state only updates on a running Ori, so the stone stayed grabbed (slippery material, no release friction)
        // and slid away on a slope. The downed player must let go, on both PCs. The guest then revives the host.
        static IEnumerator DownedHolder(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion restingRot, bool host)
        {
            yield return CoopReset(h, 260, block, resting, restingRot, host);
            GrabSetup hold = new GrabSetup();
            if (host) { yield return GrabBlock(block, hold, CoopDir); if (hold.Started) Bot(true, CoopDir); }
            yield return Barrier(h, 261, block, hold.Started);
            Sample partner;
            if (!s_partnerSteps.TryGetValue(261, out partner)) yield break;
            if (!(host ? hold.Started : partner.GrabStarted))
            {
                Bot(false);
                h.Check(false, "PG setup: the host grabbed the stone and pushes it");
                yield break;
            }
            // No KeepAlive from here: its invincibility has to run out before the hit, and a downed Ori needs none.
            float until = Time.realtimeSinceStartup + 0.7f;
            while (Time.realtimeSinceStartup < until) yield return null;
            if (host) Own.Mortality.DamageReciever.OnRecieveDamage(new Damage(1000f, Vector2.zero, Own.Position, DamageType.Spikes, null));
            until = Time.realtimeSinceStartup + 1.2f;
            while (Time.realtimeSinceStartup < until) yield return null;   // the bot keeps the grab key and the stick held, as the player did
            if (host)
            {
                PlatformMovement pm = Own.PlatformBehaviour.PlatformMovement;
                h.Check(Revive.IsDowned(Own), "PG setup: the host went down while pushing the stone");
                h.Check(!Own.Abilities.GrabBlock.IsGrabbing && !block.IsGrabbed && !PushBlockSync.Ignores(block, pm),
                    "PG a downed player lets go of the stone (still grabbing=" + Own.Abilities.GrabBlock.IsGrabbing + ", stone grabbed=" + block.IsGrabbed
                    + ", stone ignores its Ori=" + PushBlockSync.Ignores(block, pm) + ", suspended releases " + PushBlockSync.SuspendedReleases + ")");
            }
            else
            {
                h.Check(Revive.DownedOf(2) != null, "PG setup: the host is shown downed on the guest's PC");
                bool holds = Puppet != null && (Puppet.Abilities.GrabBlock.IsGrabbing || PushBlockSync.Ignores(block, Puppet.PlatformBehaviour.PlatformMovement));
                h.Check(!holds, "PG the downed partner's copy lets go of the stone on this PC too");
            }
            Bot(false);
            yield return Barrier(h, 262, block);
            // Clean-up, so the phases after this one have two players: the guest revives the host (hold Spirit Flame at
            // the skull). If that does not work here, the host gets itself up (hold Y); which way worked is only noted.
            for (int attempt = 0; !host && attempt < 3 && Revive.DownedOf(2) != null; attempt++)
            {
                Revive.Downed d = Revive.DownedOf(2);
                Plugin.Teleport(Own, d.Marker + new Vector3(attempt == 1 ? -0.8f : 0.8f, 0f, 0f));
                SessionRules.PositionChanged();
                yield return Wait(0.8f);
                NetTestHarness.BotButtons = P2Input.BTN_X; NetTestHarness.BotLX = 0f;
                TestClock.Window reviving = new TestClock.Window(3f);
                while (Revive.DownedOf(2) != null && reviving.Open) yield return null;
                NetTestHarness.BotButtons = 0;
                h.Note("PG guest revive try " + (attempt + 1) + ": skull=" + V(d.Marker) + " Ori=" + V(Own.Position) + " partner copy=" + (Puppet == null ? "none" : V(Puppet.Position))
                    + " progress=" + d.Progress.ToString("F2") + " partner still down=" + (Revive.DownedOf(2) != null));
            }
            if (host)
            {
                until = Time.realtimeSinceStartup + 16f;
                while (Revive.IsDowned(Own) && Time.realtimeSinceStartup < until) yield return null;
                bool byGuest = !Revive.IsDowned(Own);
                if (!byGuest)
                {
                    Revive.TestHoldSelf = true;
                    TestClock.Window self = new TestClock.Window(6f);
                    while (Revive.IsDowned(Own) && self.Open) yield return null;
                    Revive.TestHoldSelf = false;
                }
                yield return Wait(1.5f);
                h.Note("PG host up again: " + (byGuest ? "revived by the guest" : "by holding Y itself"));
                h.Check(!Revive.IsDowned(Own), "PG the host is up again after the scenario (both play on)");
            }
            yield return Barrier(h, 263, block);
            Note(h, "PG end", block);
        }

        static IEnumerator Coop(TestHarness h, RuntimeSceneMetaData scene, bool host)
        {
            Bot(false);
            PushPullBlock block = FindBlock(scene, true);
            yield return Barrier(h, 200, block);
            Sample partner;
            if (!s_partnerSteps.TryGetValue(200, out partner)) yield break;
            bool same = block != null && partner.Loaded && partner.Id == PushBlockSync.StateOf(block).Id;
            h.Check(same, "PH both PCs chose the same second stone (largest id)");
            if (!same) yield break;
            NetPlay.SetSim(60, 0, 0, 0);
            Vector3 resting = block.Position; Quaternion restingRot = block.transform.rotation;
            h.Note("--- co-op stone scenarios on " + PushBlockSync.StateOf(block).Name + " at " + V(resting));
            // ORICOOP_P_COOP=PE,PH,... picks the scenarios and their order (default: PE first, on the untouched stone, as
            // two players meet it in the game; then PH, PF, PG).
            string order = Environment.GetEnvironmentVariable("ORICOOP_P_COOP");
            if (string.IsNullOrEmpty(order)) order = "PE,PH,PF,PG";
            try
            {
                foreach (string scenario in order.Split(','))
                {
                    if (scenario == "PH") yield return Rider(h, block, resting, restingRot, host);
                    else if (scenario == "PE") yield return PushSameStone(h, block, resting, restingRot, host);
                    else if (scenario == "PI") yield return PushSameStone(h, block, resting, restingRot, host, true);
                    else if (scenario == "PF") yield return Handover(h, block, resting, restingRot, host);
                    else if (scenario == "PK")
                    {
                        yield return SoloRejoin(h, scene, block, resting, host);
                        // The guest's native checkpoint restore replaced its original stone instance.
                        block = FindBlock(scene, true);
                    }
                    else if (scenario == "PG") yield return DownedHolder(h, block, resting, restingRot, host);
                    else if (scenario == "PR") yield return PushReports(h, block, resting, restingRot, host);
                    else if (scenario == "PJ")
                    {
                        yield return DepartedHolder(h, block, resting, restingRot, host);
                        if (block == null) block = FindBlock(scene, true);
                    }
                    else h.Check(false, "Unknown P co-op scenario '" + scenario + "'");
                }
                yield return CoopReset(h, 290, block, resting, restingRot, host);
            }
            finally
            {
                s_watch = null; Bot(false); NetPlay.SetSim(0, 0, 0, 0);
            }
        }

        public static IEnumerator Run(TestHarness h)
        {
            s_localSteps.Clear(); s_partnerSteps.Clear(); NetPlay.On(MsgBarrier, OnBarrier);
            bool host = NetPlay.IsHost;
            h.Note("--- phase P: online pushable stones (" + (host ? "host" : "guest") + ")");
            try
            {
                Bot(false);
                if (Environment.GetEnvironmentVariable("ORICOOP_PHYS_OFF") == "1")
                {
                    PushBlockSync.Enabled = false;
                    h.Note("P mutation: ORICOOP_PHYS_OFF=1, PushBlockSync.Enabled=false");
                }
                if (Own == null || Puppet == null) { h.Check(false, "P needs both Oris"); yield break; }
                RuntimeSceneMetaData scene = Core.Scenes.Manager.GetSceneInformation("sunkenGladesSpiritCavernsPushBlockIntroduction");
                h.Check(scene != null, "P push-block tutorial scene found");
                if (scene == null) yield break;
                Plugin.Teleport(Own, new Vector3(scene.PlaceholderPosition.x, scene.PlaceholderPosition.y, Own.Position.z));
                SessionRules.PositionChanged();
                PushPullBlock block = null;
                float deadline = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    KeepAlive();
                    block = FindBlock(scene);
                    // Remote-owned blocks are deliberately kinematic; test the game's boundary condition instead.
                    if (block != null && Core.Scenes.Manager.IsInsideActiveSceneBoundary(block.Position + Vector3.down * 2f)) break;
                    block = null;
                    yield return null;
                }
                h.Check(block != null, "P deterministic block loaded inside active scene boundaries within 20s");
                yield return Barrier(h, 1, block);
                Sample partner;
                if (!s_partnerSteps.TryGetValue(1, out partner)) yield break;
                h.Check(partner.Loaded && block != null && partner.Id == PushBlockSync.StateOf(block).Id, "P both PCs chose the same smallest block id");
                if (block == null || !partner.Loaded || partner.Id != PushBlockSync.StateOf(block).Id) yield break;
                yield return Wait(1f);
                if (AuthoritySource.Current.OwnerOf(PushBlockSync.StateOf(block).Key) == 0)
                    h.Check(!block.GetComponent<Rigidbody>().isKinematic, "P unowned stone keeps native simulation before interaction");
                Vector3 resting = block.Position; Quaternion restingRot = block.transform.rotation;
                int guestSeat = host ? Players.First.Seat : Players.LocalSeat;
                if (Environment.GetEnvironmentVariable("ORICOOP_P_ONLY") == "COOP")
                {
                    h.Note("P: ORICOOP_P_ONLY=COOP, only the co-op stone scenarios");
                    yield return Coop(h, scene, host);
                    yield break;
                }

                int previousPusher = 0; uint previousEpoch = 0;
                for (int run = 1; run <= 7; run++)
                {
                    // runs 4/5 repeat P1/P3 with 100 ms added on each side (200 ms RTT, the playtest's relay).
                    int round = run <= 3 ? run : run == 4 || run == 6 ? 1 : 3; bool lag = run >= 4;
                    string phase = "P" + round + (lag ? "L" : "");
                    int step = run * 10;
                    bool pusher = round == 3 ? !host : host;
                    NetPlay.SetSim(run >= 6 ? (pusher ? 200 : 0) : lag ? 100 : 0, 0, 0, 0);
                    if (run >= 6) phase += "200oneway";
                    bool top = round != 2;
                    Bot(false);
                    KeepAlive();
                    // Both players step clear (host onto the free floor left of the rest spot, guest right of it) before the
                    // stone is put back, so the reset never drops it onto someone (PD tests that case on purpose).
                    PlaceOwn(resting.x + (host ? -2.45f : 2.8f), resting.y + 0.5f, block.Position.z);   // free floor: host left (wall at ~-282), guest right
                    yield return Wait(0.5f);
                    yield return Barrier(h, step, block);
                    if (PushBlockSync.IsLocalOwner(block))
                    {
                        Rigidbody body = block.GetComponent<Rigidbody>();
                        body.position = resting; block.transform.position = resting; body.rotation = restingRot; block.transform.rotation = restingRot;
                        body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
                        body.Sleep();   // the authored pose (9.6 deg on a slope) only holds while asleep, as after scene load
                    }
                    yield return Wait(1.2f);
                    yield return Barrier(h, step + 1, block);
                    // Under lag the follower's copy is still gliding back to the reset spot: place only once it is there.
                    float settleEnd = Time.realtimeSinceStartup + 3f;
                    while (Time.realtimeSinceStartup < settleEnd && (block.Position - resting).magnitude > 0.05f)
                    { KeepAlive(); yield return null; }
                    h.Note(phase + " stone settled at the reset spot (off=" + F((block.Position - resting).magnitude) + ")");
                    Bounds bb = BlockBounds(block), ob = Own.PlatformBehaviour.PlatformMovement.CapsuleCollider.bounds;
                    GrabSetup setup = new GrabSetup();
                    if (pusher) yield return GrabBlock(block, setup);
                    yield return Barrier(h, step + 7, block, setup.Started);
                    Sample setupPartner;
                    if (!s_partnerSteps.TryGetValue(step + 7, out setupPartner)) yield break;
                    bool grabStarted = pusher ? setup.Started : setupPartner.GrabStarted;
                    if (!grabStarted)
                    {
                        Bot(false);
                        h.Check(false, phase + " grab never started within 3 s (max 3 Glide tries)");
                        previousPusher = 0; previousEpoch = 0;
                        yield return Barrier(h, step + 4, block);
                        continue;
                    }
                    if (pusher) h.Check(setup.Started, phase + " Glide press enters native grab path (try " + setup.Attempts + ")");
                    yield return Wait(Mathf.Max(1.5f, NetPlay.RttMs * 0.001f + 0.5f));
                    yield return Barrier(h, step + 5, block);
                    CheckOwner(h, step + 5, phase + " held before rider lands");
                    bb = BlockBounds(block);
                    if (!pusher && top) PlaceOwn(bb.center.x + 0.4f, bb.max.y + 0.5f, block.Position.z);   // drop onto the rounded top (its rest tilt slopes down to the left)
                    else if (!pusher) PlaceOwn(bb.max.x + ob.extents.x + 0.3f, bb.max.y + 0.5f, block.Position.z);   // drop onto the floor right of it
                    yield return Wait(1.0f);
                    // The rounded top can shed a dropped rider (seen twice in full-suite runs: landed 2 units off, beside it):
                    // place once more before judging the setup.
                    if (!pusher && top && (float.IsNaN(PushBlockSync.SurfaceY(block, Own.Position.x)) || !Own.PlatformBehaviour.PlatformMovement.IsOnGround))
                    {
                        h.Note(phase + " rider slid off the top while landing, placing again (Ori=" + V(Own.Position) + ")");
                        bb = BlockBounds(block);
                        PlaceOwn(bb.center.x + 0.4f, bb.max.y + 0.5f, block.Position.z);
                        yield return Wait(1.0f);
                    }
                    GrabSetup pushSetup = new GrabSetup();
                    if (pusher)
                    {
                        if (PusherReady(block)) pushSetup.Started = true;
                        else
                        {
                            h.Note(phase + " pusher displaced during setup; replacing and settling before the push");
                            yield return GrabBlock(block, pushSetup);
                        }
                    }
                    yield return Barrier(h, step + 8, block, pushSetup.Started);
                    if (!s_partnerSteps.TryGetValue(step + 8, out setupPartner)) yield break;
                    if (!(pusher ? pushSetup.Started : setupPartner.GrabStarted))
                    {
                        Bot(false);
                        h.Check(false, phase + " pusher setup failed: not grounded and grabbing beside the left face within 3 s (max 3 tries)");
                        previousPusher = 0; previousEpoch = 0;
                        yield return Barrier(h, step + 4, block);
                        continue;
                    }
                    if (!pusher) h.Check(Own.PlatformBehaviour.PlatformMovement.IsOnGround, phase + " " + (top ? "stander landed on the stone" : "stander landed beside the stone")
                        + " (Ori=" + V(Own.Position) + " target=" + V(s_lastTarget) + " surface=" + F(PushBlockSync.SurfaceY(block, Own.Position.x)) + ")");
                    if (pusher) h.Check(pushSetup.Started, phase + " pusher grounded beside the left face");
                    Note(h, phase + " start", block);
                    float contactAt = -1f, maxPenetration = 0f, minFeet = float.PositiveInfinity;
                    bool contacted = false;
                    IEnumerator ready = Barrier(h, step + 2, block);
                    while (ready.MoveNext())
                    {
                        if (!pusher) Observe(block, top, ref contactAt, ref maxPenetration, ref minFeet, ref contacted);
                        yield return ready.Current;
                    }
                    Vector3 start = block.Position;
                    float began = Time.realtimeSinceStartup;
                    int pushingSeat = round == 3 ? guestSeat : (host ? Players.LocalSeat : Players.First.Seat);
                    float ownerWait = Mathf.Max(0.5f, NetPlay.RttMs * 0.001f + 0.5f);
                    bool grabbed = false, guestOwned = false;
                    float notedAt = 0f, sampleAt = 0f; Sample pushing = null, pushingPartner = null;
                    if (pusher) Bot(true);
                    while (Time.realtimeSinceStartup - began < 4.5f)   // 2 s push, then the stone rolls back and settles
                    {
                        float elapsed = Time.realtimeSinceStartup - began;
                        KeepAlive();
                        if (elapsed >= notedAt + 0.5f) { notedAt += 0.5f; Note(h, phase + " t=" + F(notedAt), block); }
                        if (elapsed <= 2f + ownerWait && Time.realtimeSinceStartup >= sampleAt)
                        {
                            sampleAt = Time.realtimeSinceStartup + 0.1f;
                            Sample current = SendSample(step + 6, block, false), ownerPartner;
                            if (s_partnerSteps.TryGetValue(step + 6, out ownerPartner) && OwnersAgree(current, ownerPartner)
                                && current.Owner == pushingSeat && (pusher ? current.Interacting : ownerPartner.Interacting))
                            { pushing = current; pushingPartner = ownerPartner; }
                        }
                        if (pusher && elapsed >= 2f) Bot(false);
                        if (pusher && Own.Abilities.GrabBlock.IsGrabbing && block.IsGrabbed) grabbed = true;
                        if (round == 3 && elapsed <= 1f && PushBlockSync.StateOf(block).OwnerSeat == guestSeat) guestOwned = true;
                        if (!pusher) Observe(block, top, ref contactAt, ref maxPenetration, ref minFeet, ref contacted);
                        yield return null;
                    }
                    Bot(false);
                    if (pusher) h.Check(grabbed, phase + " pusher grabbed the stone using Glide + LX");
                    if (pusher) h.Check(!block.IsGrabbed, phase + " release clears native grab state after ownership or prediction");
                    if (round == 3) h.Check(guestOwned,
                        phase + " guest became owner on this PC within 1s (seat=" + guestSeat + ")");
                    h.Check(block.Position.x - start.x >= 0.5f, phase + " block moved >=0.5 right on this PC (dx=" + F(block.Position.x - start.x) + ")");
                    Vector3 final = block.Position;
                    IEnumerator finished = Barrier(h, step + 3, block);
                    while (finished.MoveNext())
                    {
                        if (!pusher) Observe(block, top, ref contactAt, ref maxPenetration, ref minFeet, ref contacted);
                        yield return finished.Current;
                    }
                    CheckOwner(h, step + 3, phase + " settled");
                    CheckOwner(h, pushing, pushingPartner, phase + " pushing");
                    h.Check(pushing != null && pushing.Owner == pushingSeat, phase + " pushing player holds the lease during interaction");
                    if (previousPusher != 0 && previousPusher != pushingSeat)
                        h.Check(pushing != null && pushing.Owner == pushingSeat && pushing.Epoch > previousEpoch,
                            phase + " lease handed over when the other player pushed after the first stopped");
                    previousPusher = pushingSeat; previousEpoch = pushing == null ? 0 : pushing.Epoch;
                    // A rider on a stone rolling back can be in the air at any one instant: give it up to 1 s to land.
                    float landBy = Time.realtimeSinceStartup + 1f;
                    while (!pusher && !Own.PlatformBehaviour.PlatformMovement.IsOnGround && Time.realtimeSinceStartup < landBy)
                    {
                        Observe(block, top, ref contactAt, ref maxPenetration, ref minFeet, ref contacted);
                        yield return null;
                    }
                    if (!pusher)
                    {
                        bb = BlockBounds(block); ob = Own.PlatformBehaviour.PlatformMovement.CapsuleCollider.bounds;
                        h.Check(maxPenetration <= 0.15f, phase + " own capsule never penetrates >0.15" + (top ? "" : " after 0.1s contact grace")
                            + " (max=" + F(maxPenetration) + ")");
                        if (top)
                        {
                            float surface = PushBlockSync.SurfaceY(block, ob.center.x);
                            h.Check(minFeet >= -0.15f, phase + " feet stay above the stone's surface-0.15 while over it (min=" + F(minFeet) + ")");
                            // The tutorial stone climbs a slope and rolls back after release; a rider may step off onto the
                            // terrain (game physics). What must hold: standing (on it or beside it), never inside it.
                            h.Check(Own.PlatformBehaviour.PlatformMovement.IsOnGround && ShapeDepth(block) <= 0.15f,
                                phase + " own Ori grounded and not inside the stone after the push (onTop=" + (!float.IsNaN(surface) && Mathf.Abs(ob.min.y - surface) <= 0.25f)
                                + " feet-surface=" + F(ob.min.y - surface) + ")");
                        }
                        else
                        {
                            h.Check(contacted, "P2 stone reached own Ori on its right side");
                            h.Check(ShapeDepth(block) <= 0.15f, "P2 own Ori is not inside the stone at the end");
                        }
                    }
                    if (s_partnerSteps.TryGetValue(step + 3, out partner))
                    {
                        float dx = Mathf.Abs(final.x - partner.Position.x), dy = Mathf.Abs(final.y - partner.Position.y);
                        h.Check(partner.Loaded && partner.Id == PushBlockSync.StateOf(block).Id && dx < 0.3f && dy < 0.3f,
                            "P4 after " + phase + " block positions agree (dx=" + F(dx) + ", dy=" + F(dy) + ")");
                    }
                    Note(h, phase + " end", block);
                    yield return Barrier(h, step + 4, block);   // both finished their checks before anyone moves on / quits
                }

                // PD: a stone dropped onto a player. The owner holds the stone 2.5 above its rest spot, the other player
                // stands on the floor there, then the owner lets go. The victim must never stay inside it.
                NetPlay.SetSim(0, 0, 0, 0);
                bool owner = PushBlockSync.IsLocalOwner(block);
                Rigidbody rb = block.GetComponent<Rigidbody>();
                Vector3 lifted = resting + Vector3.up * 2.5f;
                yield return Barrier(h, 90, block);
                float holdEnd = Time.realtimeSinceStartup + 2.5f;
                bool placed = false, replaced = false; float placedAt = 0f;
                while (Time.realtimeSinceStartup < holdEnd)
                {
                    KeepAlive();
                    if (owner) { rb.position = lifted; block.transform.position = lifted; rb.rotation = restingRot; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                    else if ((!placed || (placedAt > 0f && Time.realtimeSinceStartup - placedAt > 0.8f && !Own.PlatformBehaviour.PlatformMovement.IsOnGround && !replaced))
                        && (block.Position - lifted).magnitude < 0.3f)
                    {
                        // Under load the first drop onto the floor sometimes did not land (full-suite flake): place once more.
                        if (placed) replaced = true;
                        placed = true; placedAt = Time.realtimeSinceStartup;
                        Bounds rest = BlockBounds(block); rest.center -= Vector3.up * 2.5f;
                        PlaceOwn(resting.x + 0.3f, rest.min.y + 0.6f, block.Position.z);
                    }
                    yield return null;
                }
                if (!owner) h.Check(placed && Own.PlatformBehaviour.PlatformMovement.IsOnGround, "PD victim standing under the held stone (Ori=" + V(Own.Position) + ")");
                Note(h, "PD drop", block);
                yield return Barrier(h, 91, block);
                float dropAt = Time.realtimeSinceStartup, dContact = -1f, dMax = 0f, dMinFeet = float.PositiveInfinity;
                bool dTouched = false;
                while (Time.realtimeSinceStartup - dropAt < 3f)
                {
                    if (Own == null || block == null) { h.Check(false, "PD own Ori or the stone disappeared during the drop"); yield break; }
                    KeepAlive();
                    if (!owner) Observe(block, false, ref dContact, ref dMax, ref dMinFeet, ref dTouched);
                    yield return null;
                }
                Note(h, "PD end", block);
                if (!owner)
                {
                    h.Check(dMax <= 0.15f, "PD dropped stone never keeps the victim inside >0.15 after 0.1 s (max=" + F(dMax) + ")");
                    h.Check(Own.PlatformBehaviour.PlatformMovement.IsOnGround && ShapeDepth(block) <= 0.15f, "PD victim grounded and not inside the stone at the end (Ori="
                        + V(Own.Position) + " block=" + V(block.Position) + ")");
                }
                yield return Barrier(h, 92, block);
                yield return BothInteract(h, block, resting, restingRot, host, guestSeat);
                yield return PushTogether(h, block, resting, restingRot, host);
                yield return Coop(h, scene, host);
            }
            finally
            {
                NetTestHarness.BotButtons = 0; NetTestHarness.BotLX = 0f; P2Input.BotRT = false; NetPlay.SetSim(0, 0, 0, 0);
            }
        }
    }
}
