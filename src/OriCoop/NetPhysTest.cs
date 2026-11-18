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
    public static class NetPhysTest
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
        }

        static SeinCharacter Own { get { return P2Context.P1; } }
        static SeinCharacter Puppet { get { return P2Context.P2; } }
        static string F(float value) { return value.ToString("F2"); }
        static string V(Vector3 value) { return "(" + F(value.x) + "," + F(value.y) + ")"; }
        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) { KeepAlive(); yield return null; }
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
            Sample prior;
            if (!s_partnerSteps.TryGetValue(step, out prior) || sample.Sequence > prior.Sequence) s_partnerSteps[step] = sample;
        }

        static Sample SendSample(int step, PushPullBlock block)
        {
            return SendSample(step, block, true);
        }

        static Sample SendSample(int step, PushPullBlock block, bool reliable, bool grabStarted = false)
        {
            bool loaded = block != null;
            string key = loaded ? PushBlockSync.StateOf(block).Key : "";
            IAuthority authority = AuthoritySource.Current;
            Sample sample = new Sample { Loaded = loaded, Id = loaded ? PushBlockSync.StateOf(block).Id : 0,
                Position = loaded ? block.Position : Vector3.zero, Owner = loaded ? authority.OwnerOf(key) : 0,
                Seat = authority.LocalSeat, Epoch = loaded ? authority.EpochOf(key) : 0, Owns = loaded && authority.Owns(key),
                Kinematic = !loaded || block.GetComponent<Rigidbody>().isKinematic,
                Interacting = loaded && Own != null && Own.Abilities.GrabBlock.IsGrabbing && block.IsGrabbed,
                Sequence = ++s_sampleSequence, GrabStarted = grabStarted };
            s_localSteps[step] = sample;
            NetPlay.Send(MsgBarrier, reliable, w => {
                w.Write(step); w.Write(sample.Loaded); w.Write(sample.Id);
                w.Write(sample.Position.x); w.Write(sample.Position.y); w.Write(sample.Position.z);
                w.Write(sample.Owner); w.Write(sample.Seat); w.Write(sample.Epoch); w.Write(sample.Owns); w.Write(sample.Kinematic);
                w.Write(sample.Interacting); w.Write(sample.Sequence);
                w.Write(sample.GrabStarted);
            });
            return sample;
        }

        static IEnumerator Barrier(TestHarness h, int step, PushPullBlock block, bool grabStarted = false)
        {
            SendSample(step, block, true, grabStarted);
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

        static void Bot(bool push)
        {
            NetTestHarness.BotButtons = 0; NetTestHarness.BotLX = push ? 1f : 0f;
            P2Input.BotRT = push;
        }

        sealed class GrabSetup
        {
            public bool Started;
            public int Attempts;
        }

        static bool PusherReady(PushPullBlock block)
        {
            return Own != null && block != null && Own.PlatformBehaviour.PlatformMovement.IsOnGround
                && Own.Position.x < BlockBounds(block).center.x && Own.Abilities.GrabBlock.IsGrabbing && block.IsGrabbed;
        }

        static IEnumerator GrabBlock(PushPullBlock block, GrabSetup result)
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
                PlaceOwn(bb.min.x - ob.extents.x - 0.02f, bb.min.y + 0.04f, block.Position.z);
                Own.PlatformBehaviour.Visuals.SpriteMirror.FaceLeft = false;
                float approachedAt = Time.fixedTime;
                NetTestHarness.BotLX = 1f;
                while (Time.fixedTime <= approachedAt && Time.realtimeSinceStartup < attemptEnd)
                { KeepAlive(); yield return null; }
                bool pressed = false;
                float grabbedAt = -1f;
                while (Own != null && block != null && Time.realtimeSinceStartup < attemptEnd)
                {
                    KeepAlive();
                    if (pressed && PusherReady(block))
                    {
                        if (grabbedAt < 0f) grabbedAt = Time.realtimeSinceStartup;
                        if (Time.realtimeSinceStartup - grabbedAt >= 0.1f)
                        { result.Started = true; NetTestHarness.BotLX = 0f; yield break; }
                    }
                    else grabbedAt = -1f;
                    if (!pressed && Own.Abilities.GrabBlock.InRange && Own.PlatformBehaviour.PlatformMovement.IsOnGround)
                    { NetTestHarness.BotLX = 0f; P2Input.BotRT = true; pressed = true; }
                    else if (!pressed) NetTestHarness.BotLX = 1f;
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

        static PushPullBlock FindBlock(RuntimeSceneMetaData scene)
        {
            PushPullBlock chosen = null;
            for (int i = 0; i < PushPullBlock.All.Count; i++)
            {
                PushPullBlock block = PushPullBlock.All[i];
                if (!InScene(scene, block.Position)) continue;
                if (chosen == null || PushBlockSync.StateOf(block).Id < PushBlockSync.StateOf(chosen).Id) chosen = block;
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
            float pushEnd = Time.realtimeSinceStartup + 2.5f; bool guestOwned = false;
            while (Time.realtimeSinceStartup < pushEnd)
            {
                KeepAlive();
                if (host) { NetTestHarness.BotLX = 0f; P2Input.BotRT = true; }
                if (AuthoritySource.Current.OwnerOf(PushBlockSync.StateOf(block).Key) == guestSeat) guestOwned = true;
                yield return null;
            }
            Bot(false);
            yield return Wait(1.0f);
            float dx = block.Position.x - start.x;
            Note(h, "PB end", block);
            h.Check(guestOwned, "PB the pushing guest took the stone's lease from the holding host");
            h.Check(dx >= 0.5f, "PB the guest's push moved the held stone >=0.5 right on this PC (dx=" + F(dx) + ")");
            yield return Barrier(h, 98, block);
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
            }
            finally
            {
                NetTestHarness.BotButtons = 0; NetTestHarness.BotLX = 0f; P2Input.BotRT = false; NetPlay.SetSim(0, 0, 0, 0);
            }
        }
    }
}
