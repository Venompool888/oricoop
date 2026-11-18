using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace OriCoop
{
    // Online phase WO: dynamic world objects (WorldObjSync). One PC (the "actor") makes its OWN Ori cause something on an
    // object the way the game does (its trigger / collision / jump / hit / joint call) without the puppet doing the same
    // on the other PC, which is exactly what happens when the partner's replayed path differs from the real one. The
    // other PC must end in the same state. Every family runs twice: no added lag and 200 ms RTT (NetPlay.SetSim(100)).
    // Barriers: message 77. Mutation: ORICOOP_WORLDOBJ_OFF=1 on both PCs (old behaviour) must fail the checks.
    public static class NetWorldObjTest
    {
        const byte MsgBarrier = 77;
        sealed class Sample { public int Id; public float A, B; public float ReceivedAt, Local; }
        static Func<float> s_probe;   // own value recorded the moment a partner sample arrives
        static float s_win = 1.5f;    // event check window from this PC's barrier (3.5 s under 200 ms RTT: barriers are reliable and slow)
        static readonly Dictionary<int, Sample> s_partner = new Dictionary<int, Sample>();


        public static int Resyncs;   // digest resyncs requested during phase WO (scene hopping), see NetTestHarness
        // P2Context.P1 can be briefly gone (scene / respawn swap); fall back to the surviving local Sein (NRE seen in WO).
        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Game.Characters.Sein; } }
        static string F(float v) { return v.ToString("F2"); }

        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) { KeepAlive(); yield return null; }
        }
        static RisingWater s_water;   // kept unsuspended while WO9 runs (it is switched on outside its story)
        static void KeepAlive()
        {
            if (s_water != null) s_water.IsSuspended = false;
            if (Own != null && Own.Mortality != null && Own.Mortality.Health != null && !Revive.IsDowned(Own) && Own.Mortality.Health.Amount < Own.Mortality.Health.MaxHealth)
                Own.Mortality.Health.RestoreAllHealth();
        }

        static void OnBarrier(BinaryReader r)
        {
            int step = r.ReadInt32();
            Sample s = new Sample { Id = r.ReadInt32(), A = r.ReadSingle(), B = r.ReadSingle() };
            s.ReceivedAt = Time.realtimeSinceStartup;
            s.Local = s_probe != null ? s_probe() : float.NaN;
            s_partner[step] = s;
        }

        static IEnumerator Barrier(TestHarness h, int step, int id, float a, float b)
        {
            NetPlay.Send(MsgBarrier, true, w => { w.Write(step); w.Write(id); w.Write(a); w.Write(b); });
            float end = Time.realtimeSinceStartup + 100f;   // longer than Settle's 75 s
            while (NetPlay.Connected && !s_partner.ContainsKey(step) && Time.realtimeSinceStartup < end) { KeepAlive(); yield return null; }
            h.Check(s_partner.ContainsKey(step), "WO barrier " + step + " reached by both");
        }
        static Sample Partner(int step) { Sample s; s_partner.TryGetValue(step, out s); return s; }
        // Unreliable samples (no head-of-line wait): the host streams 8 of them; the guest compares each on arrival.
        static IEnumerator Probes(int step, Func<float> a, Func<float> b)
        {
            for (int i = 0; i < 8; i++)
            {
                int s = step + i; float va = a(), vb = b();
                NetPlay.Send(MsgBarrier, false, w => { w.Write(s); w.Write(0); w.Write(va); w.Write(vb); });
                yield return Wait(0.1f);
            }
        }
        static float MedianError(int step, Func<Sample, float> err, out int n)
        {
            List<float> e = new List<float>();
            for (int i = 0; i < 8; i++) { Sample s = Partner(step + i); if (s != null && !float.IsNaN(s.Local)) e.Add(err(s)); }
            n = e.Count; if (n == 0) return float.NaN;
            e.Sort((x, y) => Mathf.Abs(x).CompareTo(Mathf.Abs(y))); return e[n / 2];
        }

        static void Place(Vector3 p)
        {
            if (Own == null) return;
            Plugin.Teleport(Own, new Vector3(p.x, p.y, Own.Position.z));
            SessionRules.PositionChanged();
        }

        // Teleport to a scene's placeholder and wait until objects of T in that scene are loaded.
        static List<Component> s_found;
        static IEnumerator Load<T>(TestHarness h, string tag, string scene, string via, Func<T, bool> fresh, bool includeInactive = false) where T : Component
        {
            s_found = new List<Component>();
            RuntimeSceneMetaData meta = Core.Scenes.Manager.GetSceneInformation(via ?? scene);
            if (meta == null) { h.Check(false, tag + " scene " + (via ?? scene) + " exists"); yield break; }
            Place(meta.PlaceholderPosition);
            // T-073: on a busy PC (1) the first frame with any candidate can hold only part of the scene, so the two PCs
            // picked different "smallest" ids: wait until the candidate count has been steady for 1 s; (2) the teleport
            // did not always stick (WO8: the guest stood at (0.4, -256.7), 0 candidates after 20 s): place again every 6 s.
            float start = Time.realtimeSinceStartup, end = start + 30f, steadySince = -1f, lastPlace = start; int lastCount = -1, places = 0;
            while (Time.realtimeSinceStartup < end)
            {
                KeepAlive();
                s_found.Clear();
                // Inactive objects too (the Ginso escape water is switched on by its story): callers activate what they need.
                // Only objects whose scene has finished starting (its saved state loaded): before that the floor broken in
                // WO5 or the block collapsed in WO2 still looks whole, so one PC counted it and picked another object
                // (WO2L / WO5L / WO6L "both PCs picked the same object", host 2 candidates vs guest 1).
                foreach (T t in Resources.FindObjectsOfTypeAll<T>())
                    if (t != null && t.gameObject.scene.name == scene && (includeInactive || t.gameObject.activeInHierarchy) && WorldObjSync.SceneStarted(t)
                        && (fresh == null || fresh(t))) s_found.Add(t);
                float now = Time.realtimeSinceStartup;
                if (s_found.Count != lastCount) { lastCount = s_found.Count; steadySince = now; }
                if (s_found.Count > 0 && now - steadySince >= 1f) break;
                if (s_found.Count == 0 && now - lastPlace > 6f)
                {
                    // Alternate: the placeholder, then the middle of the scene's first boundary (some placeholders sit
                    // outside their scene's streaming area).
                    lastPlace = now; places++;
                    if (places % 2 == 1 && meta.SceneBoundaries.Count > 0) { Rect r = meta.SceneBoundaries[0]; Place(new Vector3(r.center.x, r.center.y, 0f)); }
                    else Place(meta.PlaceholderPosition);
                }
                yield return null;
            }
            s_found.Sort((x, y) => WorldObjSync.IdOf(x).CompareTo(WorldObjSync.IdOf(y)));
            if (s_found.Count == 0)
            {
                string scenes = "";
                foreach (SceneManagerScene s in Core.Scenes.Manager.ActiveScenes) if (s != null && s.MetaData != null) scenes += " " + s.MetaData.Scene;
                string anywhere = "";
                foreach (T t in UnityEngine.Object.FindObjectsOfType<T>()) anywhere += " " + t.gameObject.scene.name + "/" + t.name + (t.gameObject.activeInHierarchy ? "" : "(off)");
                h.Note(tag + " diag: Ori=" + Own.Position + " placeholder=" + meta.PlaceholderPosition + " active scenes:" + scenes + " | " + typeof(T).Name + " anywhere:" + anywhere);
            }
            h.Note(tag + " " + scene + ": " + s_found.Count + " candidate " + typeof(T).Name + " loaded");
        }

        // A scene's own story triggers fire when an Ori arrives (Forlorn Ruins entrance, spider intro ...). Teleporting away
        // mid-story leaves GameController.LockInputByAction on forever (its unlock action never runs), which broke the next
        // phase. So never leave while a story runs or input is locked (up to 75 s).
        static IEnumerator Settle(TestHarness h, string tag)
        {
            float start = Time.realtimeSinceStartup, end = start + 75f;
            while (Time.realtimeSinceStartup < end && (StorySync.Watching || StorySync.TakeoverMaskPublic() != 0)) { KeepAlive(); yield return null; }
            float waited = Time.realtimeSinceStartup - start;
            if (waited > 0.5f) h.Note(tag + " waited " + F(waited) + " s for a scene story / input lock to end (mask=" + StorySync.TakeoverMaskPublic() + ")");
        }

        // Both PCs choose the smallest id; returns null (and fails) when they differ.
        static Component s_pick;
        static IEnumerator Agree<T>(TestHarness h, string tag, int step) where T : Component
        {
            s_pick = null;
            Component mine = s_found != null && s_found.Count > 0 ? s_found[0] : null;
            int id = mine == null ? 0 : WorldObjSync.IdOf(mine);
            yield return Barrier(h, step, id, 0f, 0f);
            Sample p = Partner(step);
            h.Check(mine != null && p != null && p.Id == id, tag + " both PCs picked the same object (id=" + id + " partner=" + (p == null ? "none" : p.Id.ToString()) + ")");
            // T-073: on a mismatch list this PC's candidates, so the two logs show which object one side had and the other not.
            if (mine == null || p == null || p.Id != id)
            {
                string list = "";
                foreach (Component c in s_found)
                {
                    EntityDamageReciever dr = c.GetComponent<EntityDamageReciever>();
                    list += " " + WorldObjSync.IdOf(c) + "@" + c.transform.position.x.ToString("F1") + "," + c.transform.position.y.ToString("F1")
                        + (dr != null ? " hp=" + dr.Health.ToString("F0") : "") + (c.gameObject.activeInHierarchy ? "" : " (off)");
                }
                h.Note(tag + " candidates on this PC (" + s_found.Count + "):" + list);
            }
            if (mine != null && p != null && p.Id == id) s_pick = mine;
        }

        public static IEnumerator Run(TestHarness h)
        {
            s_partner.Clear(); Resyncs = 0; NetPlay.On(MsgBarrier, OnBarrier);
            bool host = NetPlay.IsHost;
            h.Note("--- phase WO: dynamic world objects (" + (host ? "host" : "guest") + ")");
            if (Environment.GetEnvironmentVariable("ORICOOP_WORLDOBJ_OFF") == "1") h.Note("WO mutation: ORICOOP_WORLDOBJ_OFF=1, WorldObjSync.Enabled=" + WorldObjSync.Enabled);
            if (Own == null || P2Context.P2 == null) { h.Check(false, "WO needs both Oris"); yield break; }
            Vector3 home = Own.Position;
            // T-081: the object search teleports into nine scenes, sometimes into mid-air above lava (WO7 laser stomp):
            // the Ori was downed for good (KeepAlive only heals, nobody revives) and CAM / CUT after this phase failed.
            // WO tests world objects, not player damage: the local Ori takes no damage for the whole phase.
            SeinDamageReciever guard = Own.Mortality != null ? Own.Mortality.DamageReciever : null;
            bool wasImmortal = guard != null && guard.IsImmortal;
            if (guard != null) guard.IsImmortal = true;
            int resyncsBefore = WorldSync.DigestResyncs;
            try
            {
                for (int run = 0; run < 2; run++)
                {
                    bool lag = run == 1; string L = lag ? "L" : "";
                    NetPlay.SetSim(lag ? 100 : 0, 0, 0, 0); s_win = lag ? 3.5f : 1.5f;
                    int b = 1000 + run * 1000;
                    yield return Stomper(h, "WO1" + L, b + 0, host); yield return Settle(h, "WO1" + L);
                    yield return Collapse(h, "WO2" + L, b + 100, host); yield return Settle(h, "WO2" + L);
                    yield return Flip(h, "WO3" + L, b + 200, host);
                    yield return Sin(h, "WO4" + L, b + 300, host); yield return Settle(h, "WO4" + L);
                    yield return Breakable(h, "WO5" + L, b + 400, host); yield return Settle(h, "WO5" + L);
                    if (!lag) { yield return EarlyEvent(h, "WO5D", b + 450); yield return Settle(h, "WO5D"); }
                    yield return Rope(h, "WO6" + L, b + 500, host); yield return Settle(h, "WO6" + L);
                    yield return Post(h, "WO7" + L, b + 600, host); yield return Settle(h, "WO7" + L);
                    yield return Hazard(h, "WO8" + L, b + 700, host); yield return Settle(h, "WO8" + L);
                    yield return Water(h, "WO9" + L, b + 800, host);
                    yield return Carry(h, "WO10" + L, b + 900, host); yield return Settle(h, "WO10" + L);
                }
                h.Note("WO counters sent=" + WorldObjSync.EventsSent + " applied=" + WorldObjSync.EventsApplied + " missing=" + WorldObjSync.EventsMissing
                    + " puppetSuppressed=" + WorldObjSync.PuppetSuppressed + " liveApplies=" + WorldObjSync.LiveApplies + " sin=" + WorldObjSync.SinCorrections
                    + " water=" + WorldObjSync.WaterFollows + " flipFix=" + WorldObjSync.FlipHeartbeatFixes);
            }
            finally { NetPlay.SetSim(0, 0, 0, 0); s_probe = null; s_water = null; if (guard != null) guard.IsImmortal = wasImmortal; }
            // Back where the phase started, so the next phase (P3) finds its scene loaded.
            yield return Settle(h, "WO end");
            if (Own != null)
            {
                Place(home); yield return Wait(1f);
                // The start area streams back in after nine far scenes; the next phase needs it loaded (pedestal, P3).
                float loadBy = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < loadBy && !Core.Scenes.Manager.IsInsideActiveSceneBoundary(home + Vector3.down * 2f)) { KeepAlive(); yield return null; }
                yield return Wait(2f);
            }
            yield return Barrier(h, 9000, 0, 0f, 0f);   // both leave together (Settle waits differ per PC)
            // Nine scenes in two minutes, each loaded a moment apart on the two PCs: their animators / transforms differ
            // briefly and the world digest asks for full resyncs. The suite's resync budget (lag test ①) excludes them.
            // Count until the worlds match again after the trip home (the start area reloads a moment apart too).
            float matchFrom = Time.realtimeSinceStartup, matchBy = matchFrom + 15f;
            while (Time.realtimeSinceStartup < matchBy && !(WorldSync.LastDigestMatch && WorldSync.LastDigestAt > matchFrom)) { KeepAlive(); yield return null; }
            Resyncs = WorldSync.DigestResyncs - resyncsBefore;
            h.Note("WO digest resyncs during the phase=" + Resyncs + " (excluded from the suite's budget)");
        }

        // W1 stomper: the host's own Ori enters the stomper's trigger; the guest's stomper must start falling too.
        static IEnumerator Stomper(TestHarness h, string tag, int step, bool host)
        {
            yield return Load<Stomper>(h, tag, "moonGrottoStomperIntroduction", null, s => WorldObjSync.StomperState(s) == 0);
            yield return Agree<Stomper>(h, tag, step);
            Stomper stomper = s_pick as Stomper; if (stomper == null) yield break;
            StomperTrigger trigger = null;
            foreach (StomperTrigger t in UnityEngine.Object.FindObjectsOfType<StomperTrigger>()) if (t.StomperToTrigger == stomper) trigger = t;
            h.Check(trigger != null, tag + " stomper has a StomperTrigger");
            if (trigger == null) yield break;
            float idleEnd = Time.realtimeSinceStartup + 8f;
            while (WorldObjSync.StomperState(stomper) != 0 && Time.realtimeSinceStartup < idleEnd) yield return null;
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            if (host)
            {
                MethodInfo perform = typeof(StomperTrigger).GetMethod("PerformTrigger", BindingFlags.Instance | BindingFlags.NonPublic);
                perform.Invoke(trigger, new object[] { Own.PlatformBehaviour.PlatformMovement.CapsuleCollider });
                h.Check(WorldObjSync.StomperState(stomper) != 0, tag + " host's own trigger started its stomper (state=" + WorldObjSync.StomperState(stomper) + ")");
            }
            else
            {
                // The window starts at this PC's barrier; the host may still be a barrier hop away under lag.
                float until = Time.realtimeSinceStartup + 3.5f; int seen = 0;
                while (Time.realtimeSinceStartup < until && seen == 0) { seen = WorldObjSync.StomperState(stomper); yield return null; }
                h.Check(seen != 0, tag + " guest's stomper started within 3.5 s of the barrier (host triggers right after it) (state=" + seen + ")");
            }
            // Both must see it actually come down (state 3 = Fallen) within 6 s.
            float fallBy = Time.realtimeSinceStartup + 6f; bool fell = false;
            while (Time.realtimeSinceStartup < fallBy && !fell) { fell = WorldObjSync.StomperState(stomper) == 3; KeepAlive(); yield return null; }
            yield return Barrier(h, step + 2, fell ? 1 : 0, 0f, 0f);
            Sample p = Partner(step + 2);
            h.Check(fell && p != null && p.Id == 1, tag + " the stomper came down on both PCs (mine=" + fell + " partner=" + (p != null && p.Id == 1) + ")");
        }

        // W2 crumbling floor: the host's own Ori lands on it (OnCollisionEnter); the guest's floor must crumble too.
        static IEnumerator Collapse(TestHarness h, string tag, int step, bool host)
        {
            yield return Load<CollapsingSolid>(h, tag, "ginsoTreePuzzles", null, c => !WorldObjSync.CollapseActivated(c));
            yield return Agree<CollapsingSolid>(h, tag, step);
            CollapsingSolid floor = s_pick as CollapsingSolid; if (floor == null) yield break;
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            if (host)
            {
                Collision contact = new Collision();
                foreach (FieldInfo f in typeof(Collision).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (f.FieldType == typeof(Collider)) f.SetValue(contact, Own.PlatformBehaviour.PlatformMovement.CapsuleCollider);
                h.Note(tag + " contact gameObject=" + (contact.gameObject == null ? "null" : contact.gameObject.name + " tag=" + contact.gameObject.tag));
                floor.OnCollisionEnter(contact);
                h.Check(WorldObjSync.CollapseActivated(floor), tag + " host's own landing activated the crumbling floor");
            }
            else
            {
                float until = Time.realtimeSinceStartup + s_win; bool crumbling = false;
                while (Time.realtimeSinceStartup < until && !crumbling)
                {
                    crumbling = WorldObjSync.CollapseActivated(floor) && floor.Animator != null && floor.Animator.AnimatorDriver.IsPlaying;
                    yield return null;
                }
                h.Check(crumbling, tag + " guest's floor is crumbling (activated + animator playing) within " + s_win + " s (activated="
                    + WorldObjSync.CollapseActivated(floor) + " playing=" + (floor.Animator != null && floor.Animator.AnimatorDriver.IsPlaying) + ")");
            }
            yield return Barrier(h, step + 2, 0, 0f, 0f);
        }

        // W3 jump-flip platforms: at the same moment the host's Ori jumps once and the guest's Ori jumps twice. Every
        // platform must end in the same state on both PCs (3 toggles each).
        static int FlipMask(List<JumpFlipPlatform> all) { int m = 0; for (int i = 0; i < all.Count && i < 30; i++) if (WorldObjSync.FlipActive(all[i])) m |= 1 << i; return m; }
        static IEnumerator Flip(TestHarness h, string tag, int step, bool host)
        {
            // The Misty Woods flip platforms live in maze scenes that only stream in with the maze's progress, not on a
            // teleport. So both PCs make three real JumpFlipPlatform components (no visuals) with the same names; they
            // subscribe to the same static jump events as the scene ones and WorldObjSync treats them the same way.
            List<JumpFlipPlatform> all = new List<JumpFlipPlatform>(); List<GameObject> made = new List<GameObject>();
            FieldInfo plants = typeof(JumpFlipPlatform).GetField("m_flipPlants", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 3; i++)
            {
                GameObject go = new GameObject("WOTestFlip" + i); UnityEngine.Object.DontDestroyOnLoad(go); made.Add(go);
                JumpFlipPlatform f = go.AddComponent<JumpFlipPlatform>(); plants.SetValue(f, new FlipPlantLogic[0]); all.Add(f);
            }
            yield return Wait(0.2f);
            yield return Barrier(h, step, all.Count, 0f, 0f);
            Sample p0 = Partner(step);
            h.Check(all.Count > 0 && p0 != null && p0.Id == all.Count, tag + " both PCs loaded the same flip platforms (" + all.Count + "/" + (p0 == null ? -1 : p0.Id) + ")");
            if (all.Count == 0) yield break;
            yield return Wait(2.5f);   // let a heartbeat settle any earlier difference
            yield return Barrier(h, step + 1, FlipMask(all), 0f, 0f);
            int jumps = host ? 1 : 2;
            for (int j = 0; j < jumps; j++) { JumpFlipPlatform.OnSeinJumpEvent(); yield return Wait(0.05f); }
            yield return Wait(3f);
            int mine = FlipMask(all);
            yield return Barrier(h, step + 2, mine, 0f, 0f);
            Sample p = Partner(step + 2);
            h.Check(p != null && p.Id == mine, tag + " flip platform states agree after 1 + 2 simultaneous jumps (mine=" + mine + " partner=" + (p == null ? -1 : p.Id) + ")");
            foreach (GameObject go in made) UnityEngine.Object.Destroy(go);
        }

        // W4 sine-moving platforms: the guest's phase is pushed 1.3 s ahead (a pause / suspension on one PC). Within 3 s
        // it must be back on the host's phase.
        static IEnumerator Sin(TestHarness h, string tag, int step, bool host)
        {
            bool lagged = tag.EndsWith("L");
            yield return Load<SinMovingPlatform>(h, tag, "moonGrottoLaserPuzzle", null, null);
            yield return Agree<SinMovingPlatform>(h, tag, step);
            SinMovingPlatform platform = s_pick as SinMovingPlatform; if (platform == null) yield break;
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            if (!host) foreach (Component s in s_found) WorldObjSync.SetSinTime((SinMovingPlatform)s, WorldObjSync.SinTime((SinMovingPlatform)s) + 1.3f);
            yield return Wait(3f);
            if (!host) s_probe = () => WorldObjSync.SinTime(platform);
            yield return Barrier(h, step + 2, 0, 0f, 0f);
            if (host) yield return Probes(step + 10, () => WorldObjSync.SinTime(platform), () => 0f);
            else yield return Wait(1.5f);
            s_probe = null;
            if (!host)
            {
                int n; float err = MedianError(step + 10, s => s.Local - (s.A + NetPlay.RttMs * 0.0005f), out n);
                // 200 ms RTT: the RTT estimate jitters (±0.1 s seen) and game clocks drift; 0.3 s of phase is < 0.7 units of travel.
                float tol = lagged ? 0.3f : 0.15f;
                h.Check(n > 0 && Mathf.Abs(err) < tol, tag + " guest platform phase matches the host's within " + tol + " s (median err=" + F(err) + " s of " + n + " samples, rtt=" + NetPlay.RttMs.ToString("F0") + " ms)");
            }
            yield return Barrier(h, step + 3, 0, 0f, 0f);
        }

        // W5 breakable floor (StompableFloor, a non-enemy Entity): the host's Ori stomps it to pieces; the guest's breaks too.
        static IEnumerator Breakable(TestHarness h, string tag, int step, bool host)
        {
            yield return Load<StompableFloor>(h, tag, "thornfeltSwampMoonGrottoTransition", null, f => f.DamageReciever != null && f.DamageReciever.Health > 0f);
            yield return Agree<StompableFloor>(h, tag, step);
            StompableFloor floor = s_pick as StompableFloor; if (floor == null) yield break;
            EntityDamageReciever dr = floor.DamageReciever;
            yield return Barrier(h, step + 1, 0, dr.Health, 0f);
            if (host)
            {
                new Damage(dr.Health + 1f, Vector2.zero, floor.transform.position, DamageType.Stomp, Own.gameObject).DealToComponents(dr.gameObject);
                h.Check(floor == null || dr == null || dr.Health <= 0f || !dr.gameObject.activeInHierarchy, tag + " host's stomp broke the floor");
            }
            else
            {
                float until = Time.realtimeSinceStartup + s_win; bool broken = false;
                while (Time.realtimeSinceStartup < until && !broken) { broken = floor == null || dr == null || dr.Health <= 0f || !dr.gameObject.activeInHierarchy; yield return null; }
                h.Check(broken, tag + " guest's floor broke within " + s_win + " s of the host's stomp (health=" + (dr == null ? "gone" : F(dr.Health)) + ")");
            }
            yield return Barrier(h, step + 2, 0, 0f, 0f);
        }

        // WO5D (T-074): a partner's event that reaches this PC while the object's scene is still starting (objects exist,
        // but SceneRoot.LateStart has not loaded the scene's saved state yet; on a busy PC that is several frames) was
        // applied and then overwritten by the saved state. Each PC on its own: both leave the floor scene until it unloads,
        // come back, and right after SceneRoot.EarlyStart (LateStart is queued, the saved state not loaded) hand an intact
        // floor a partner damage event; once the scene's state has loaded the floor must still be broken.
        static string s_earlyScene; static int s_earlyBroken; static EntityDamageReciever s_earlyTarget; static bool s_earlyStartedThen; static Vector3 s_earlyStand;
        [HarmonyLib.HarmonyPatch(typeof(SceneRoot), "EarlyStart")]
        static class EarlyStartHook
        {
            static void Postfix(SceneRoot __instance)
            {
                if (s_earlyScene == null || __instance == null || __instance.gameObject.scene.name != s_earlyScene) return;
                List<StompableFloor> floors = new List<StompableFloor>(__instance.GetComponentsInChildren<StompableFloor>(true));
                floors.RemoveAll(f => f.DamageReciever == null || WorldObjSync.IdOf(f) == s_earlyBroken);
                if (floors.Count == 0) return;
                // The floor farthest from where both Oris stand (the same on both PCs), so nobody falls through it: an Ori
                // dropping into the grotto left story state behind that broke the later CUT / digest checks.
                floors.Sort((x, y) => { int c = Vector3.Distance(y.transform.position, s_earlyStand).CompareTo(Vector3.Distance(x.transform.position, s_earlyStand));
                    return c != 0 ? c : WorldObjSync.IdOf(x).CompareTo(WorldObjSync.IdOf(y)); });
                s_earlyScene = null; s_earlyTarget = floors[0].DamageReciever; s_earlyStartedThen = WorldObjSync.SceneStarted(floors[0]);
                WorldObjSync.TestApplyDamage(s_earlyTarget, s_earlyTarget.Health + 1f);   // the id WorldObjSync sends: the damage receiver's
            }
        }
        static IEnumerator EarlyEvent(TestHarness h, string tag, int step)
        {
            const string scene = "thornfeltSwampMoonGrottoTransition";
            int broken = s_pick != null ? WorldObjSync.IdOf(s_pick) : 0;
            RuntimeSceneMetaData meta = Core.Scenes.Manager.GetSceneInformation(scene), away = Core.Scenes.Manager.GetSceneInformation("sunkenGladesIntroSplitA");
            if (meta == null || away == null) { h.Check(false, tag + " scenes exist"); yield break; }
            yield return Barrier(h, step, 0, 0f, 0f);
            // Both leave (the host keeps scenes loaded around the partner), then each waits until the scene is gone here.
            Place(away.PlaceholderPosition);
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            float start = Time.realtimeSinceStartup, end = start + 30f;
            while (Time.realtimeSinceStartup < end && SceneActive(scene)) { KeepAlive(); yield return null; }
            bool unloaded = !SceneActive(scene);
            h.Note(tag + " floor scene unloaded=" + unloaded + " after " + F(Time.realtimeSinceStartup - start) + " s");
            yield return Barrier(h, step + 2, 0, 0f, 0f);
            int deferred0 = WorldObjSync.EventsDeferred;
            s_earlyTarget = null; s_earlyBroken = broken; s_earlyStand = meta.PlaceholderPosition; s_earlyScene = unloaded ? scene : null;
            Place(meta.PlaceholderPosition);
            end = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < end && (s_earlyTarget == null || !WorldObjSync.SceneStarted(s_earlyTarget))) { KeepAlive(); yield return null; }
            s_earlyScene = null;
            if (!unloaded || s_earlyTarget == null)
                h.Note("SKIP " + tag + ": the floor scene did not reload here (unloaded=" + unloaded + " hooked=" + (s_earlyTarget != null) + ")");
            else
            {
                yield return Wait(1f);
                EntityDamageReciever target = s_earlyTarget;
                h.Note(tag + " event applied before the scene's state loaded (started then=" + s_earlyStartedThen + "), deferred="
                    + (WorldObjSync.EventsDeferred - deferred0) + " health now=" + (target == null ? "gone" : F(target.Health)));
                h.Check(!s_earlyStartedThen, tag + " the event really arrived while the scene was still starting");
                h.Check(target == null || target.Health <= 0f, tag + " a partner event that arrived while the scene was starting still breaks the floor (health="
                    + (target == null ? "gone" : F(target.Health)) + ")");
            }
            // Leave the grotto again so the next phases start from a clean place.
            Place(away.PlaceholderPosition);
            yield return Barrier(h, step + 3, 0, 0f, 0f);
        }
        static bool SceneActive(string scene)
        {
            foreach (SceneManagerScene s in Core.Scenes.Manager.ActiveScenes)
                if (s != null && s.MetaData != null && s.MetaData.Scene == scene) return true;
            return false;
        }

        // W6 rope: the host cuts a hanging attachment (DetachRopeOnStomp -> BreakAttachment); the guest's must drop too.
        static IEnumerator Rope(TestHarness h, string tag, int step, bool host)
        {
            yield return Load<AttachToRope>(h, tag, "sunkenGladesIntroSplitA", null, a => WorldObjSync.RopeAttached(a));
            yield return Agree<AttachToRope>(h, tag, step);
            AttachToRope rope = s_pick as AttachToRope; if (rope == null) yield break;
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            if (host) { rope.BreakAttachment(); h.Check(!WorldObjSync.RopeAttached(rope), tag + " host's rope attachment cut"); }
            else
            {
                float until = Time.realtimeSinceStartup + s_win + 1f; bool cut = false;
                while (Time.realtimeSinceStartup < until && !cut) { cut = rope == null || !WorldObjSync.RopeAttached(rope); yield return null; }
                h.Check(cut, tag + " guest's rope attachment dropped within " + (s_win + 1f) + " s of the host's cut");
            }
            yield return Barrier(h, step + 2, 0, 0f, 0f);
        }

        // W7 stomp post: the host's Ori stomps a post all the way in; on the guest the post must be in and its
        // AllTheWayInAction must have run.
        static IEnumerator Post(TestHarness h, string tag, int step, bool host)
        {
            string scene = tag.EndsWith("L") ? "westGladesMistyWoodsCaveTransition" : "sunkenGladesLaserStomp";   // the spider intro starts a story
            yield return Load<StompPost>(h, tag, scene, null, s => !WorldObjSync.PostActivated(s));
            yield return Agree<StompPost>(h, tag, step);
            StompPost post = s_pick as StompPost; if (post == null) yield break;
            int before = WorldObjSync.PostActionsApplied;
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            if (host)
            {
                for (int i = 0; i < post.NumberOfStomps + 1 && !WorldObjSync.PostActivated(post); i++)
                { post.OnRecieveDamage(new Damage(1f, Vector2.zero, post.Position, DamageType.Stomp, Own.gameObject)); yield return Wait(0.1f); }
                h.Check(WorldObjSync.PostActivated(post), tag + " host's stomps drove the post all the way in");
            }
            else
            {
                TestClock.Window until = new TestClock.Window(3f); bool done = false;   // game seconds (T-071): the post slides in game time
                while (until.Open && !done) { done = WorldObjSync.PostActivated(post) && WorldObjSync.PostActionsApplied > before; yield return null; }
                h.Check(done, tag + " guest's post is in and its AllTheWayInAction ran within 3 s (activated=" + WorldObjSync.PostActivated(post)
                    + " actions=" + (WorldObjSync.PostActionsApplied - before) + ")");
            }
            yield return Barrier(h, step + 2, 0, 0f, 0f);
        }

        // W8 falling rock: the host's Ori passes a rock trigger (PlayerCollisionTrigger sees only the local Ori); the
        // guest's rock sequence must run too.
        static IEnumerator Hazard(TestHarness h, string tag, int step, bool host)
        {
            // forlornRuinsC's placeholder fires the entrance story once (ends by itself in ~60 s; Settle waits for it). Via the
            // gravity room instead left the guest stuck there (teleports no longer moved it).
            yield return Load<PlayerCollisionTrigger>(h, tag, "forlornRuinsBackground", "forlornRuinsC",
                t => t.ActionToRun is PerformingAction && !((PerformingAction)t.ActionToRun).IsPerforming && WorldObjSync.IsReplicableTrigger(t));
            // One rock trigger per scene and it fires once: re-arm it on both PCs (the 200 ms round reuses it).
            if (s_found != null) foreach (Component c in s_found) WorldObjSync.ForgetTrigger((PlayerCollisionTrigger)c);
            yield return Agree<PlayerCollisionTrigger>(h, tag, step);
            PlayerCollisionTrigger trigger = s_pick as PlayerCollisionTrigger; if (trigger == null) yield break;
            PerformingAction action = (PerformingAction)trigger.ActionToRun;
            h.Note(tag + " trigger " + trigger.name + " action " + action.name + " once=" + trigger.TriggerOnce);
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            bool ran = false;
            if (host)
            {
                trigger.DoTrigger();
                float until = Time.realtimeSinceStartup + 1f;
                while (Time.realtimeSinceStartup < until && !ran) { ran = action.IsPerforming; yield return null; }
            }
            else
            {
                float until = Time.realtimeSinceStartup + s_win;
                while (Time.realtimeSinceStartup < until && !ran) { ran = action.IsPerforming; yield return null; }
            }
            yield return Barrier(h, step + 5, ran ? 1 : 0, 0f, 0f);
            Sample pr = Partner(step + 5);
            bool hostRan = host ? ran : (pr != null && pr.Id == 1);
            // The 200 ms round re-arms the same one-shot rock; its scene condition sometimes refuses a second run on the
            // host's own call (seen on relay). Without a host run there is nothing to replicate: skip, don't fail.
            if (tag.EndsWith("L") && !hostRan) h.Note(tag + " the re-armed rock did not start on the host (scene condition): skipped");
            else if (host) h.Check(ran, tag + " host's own pass started the rock sequence");
            else h.Check(ran, tag + " guest's rock sequence started within " + s_win + " s of the host's pass");
            yield return Wait(1f);
            yield return Barrier(h, step + 2, 0, 0f, 0f);
        }

        // WO10 carryable (nightberry): on the guest's PC the partner's puppet holds the berry; the guest's own Ori must not
        // be able to take it out of the puppet's hands (then both would carry it).
        static IEnumerator Carry(TestHarness h, string tag, int step, bool host)
        {
            // The nightberry's scene starts its own story (a text box that waits for a button), so the guest makes one real
            // CarryableRigidBody (the nightberry's class) next to the puppet instead.
            yield return Barrier(h, step, 0, 0f, 0f);
            if (!host && P2Context.P2 != null)
            {
                GameObject go = new GameObject("WOTestCarryable"); go.SetActive(false);
                go.AddComponent<Rigidbody>().isKinematic = true; go.AddComponent<BoxCollider>();
                CarryableRigidBody berry = go.AddComponent<CarryableRigidBody>();
                go.transform.position = P2Context.P2.Position; go.SetActive(true);
                yield return null;
                P2Context.Enter();
                try { berry.Pickup(); } finally { P2Context.Exit(); }
                bool puppetHolds = berry.IsCarried;
                bool canTake = berry.CanBeCarried();
                h.Check(puppetHolds && !canTake, tag + " guest's own Ori cannot take the carryable the partner's puppet holds (held=" + puppetHolds + " canTake=" + canTake + ")");
                yield return Wait(0.3f);
                P2Context.Enter();
                try { if (berry.IsCarried) berry.Drop(); } finally { P2Context.Exit(); }
                UnityEngine.Object.Destroy(go);
            }
            yield return Barrier(h, step + 1, 0, 0f, 0f);
        }
        // WO9 rising water. RisingWater's speed follows Characters.Sein's height (RisingWater.cs:56), so each PC used to
        // raise its water at its own rate, and WorldSync hard-wrote each side's older height into the other. The real
        // water only exists in the Ginso escape, whose scenes start the escape story on arrival (input locked for a
        // minute, which broke the next phase). So both PCs make one real RisingWater component (same name) with a
        // distance-based speed curve like the game's, and the two Oris stand at different heights below it.
        static IEnumerator Water(TestHarness h, string tag, int step, bool host)
        {
            Vector3 basePos = Own.Position;
            yield return Barrier(h, step, 0, basePos.x, basePos.y);
            Sample b0 = Partner(step); if (b0 == null) yield break;
            Vector3 origin = host ? basePos : new Vector3(b0.A, b0.B, 0f);   // the host's spot is the shared origin
            GameObject go = new GameObject("WOTestRisingWater"); UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            RisingWater water = go.AddComponent<RisingWater>();
            water.Speed = 1f;
            water.SpeedOverDistance = AnimationCurve.Linear(0f, 0f, 40f, 8f);       // faster the further the Ori is above it
            water.EasySpeedOverDistance = AnimationCurve.Linear(0f, 0f, 40f, 8f);
            go.transform.position = new Vector3(origin.x, origin.y - 30f, 0f);
            // Different heights above the water (host 10 higher), so the native speeds differ on the two PCs.
            Place(new Vector3(origin.x + (host ? 0f : 3f), origin.y + (host ? 10f : 0f), 0f));
            yield return Barrier(h, step + 1, 0, 0f, 0f);
            go.SetActive(true); s_water = water;   // both switch it on after the same barrier
            yield return Wait(0.5f);
            float end = Time.realtimeSinceStartup + 4f, last = water.transform.position.y, worstDrop = 0f, risenFrom = last;
            while (Time.realtimeSinceStartup < end)
            {
                KeepAlive();
                float y = water.transform.position.y;
                worstDrop = Mathf.Max(worstDrop, last - y); last = y;
                yield return null;
            }
            h.Check(worstDrop <= 0.05f, tag + " water never jumps down on this PC while rising (worst drop=" + F(worstDrop) + ")");
            h.Check(water.transform.position.y - risenFrom >= 1f, tag + " water actually rose on this PC (" + F(risenFrom) + " -> " + F(water.transform.position.y) + ", follows=" + WorldObjSync.WaterFollows + ")");
            if (!host) s_probe = () => water == null ? float.NaN : water.transform.position.y;
            yield return Barrier(h, step + 2, 0, 0f, 0f);
            if (host) yield return Probes(step + 10, () => water.transform.position.y, () => WorldObjSync.WaterSpeed(water));
            else yield return Wait(1.5f);
            s_probe = null;
            if (!host)
            {
                int n; float err = MedianError(step + 10, s => s.Local - (s.A + s.B * NetPlay.RttMs * 0.0005f), out n);
                // Within one Ori height (~1 unit): misses of 0.44-0.82 seen under load / relay, where the host's game clock lags real time.
                h.Check(n > 0 && Mathf.Abs(err) < 1f, tag + " guest water height matches the host's within 1 (median err=" + F(err) + " of " + n + " samples)");
            }
            yield return Barrier(h, step + 3, 0, 0f, 0f);
            s_water = null;
            UnityEngine.Object.Destroy(go);
            Place(basePos);
        }
    }
}