using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Game;
using UnityEngine;
using HarmonyLib;
using OriCoop.Sweep;

namespace OriCoop
{
    // Opt-in two-PC suite. Message 74 and its resend/ack/exception-safe iterator driver are CUT's.
    public static class NetSceneSweep
    {
        static SeinCharacter Own { get { return NetCutsceneTest.Own; } }
        static float Now { get { return NetCutsceneTest.Now; } }
        static string Side { get { return NetPlay.IsHost ? "host" : "guest"; } }
        static string Env(string key) { return Environment.GetEnvironmentVariable("ORICOOP_SWEEP_" + key); }
        static string s_exception;
        static bool s_observing, s_unexpected, s_running, s_monitor;
        static int s_checks, s_restores, s_loads, s_entryErrors;
        static Vector3 s_placedPosition;
        // SceneManagerScene can still be in ActiveScenes with no live root during streaming.
        // Both native checkpoint loops test HasStartBeenCalled then dereference SceneRoot unchecked.
        static bool CheckpointSceneStarted(SceneManagerScene scene)
        { return scene.HasStartBeenCalled && (!s_running || (scene.SceneRoot != null && scene.MetaData != null)); }
        [HarmonyPatch]
        static class CheckpointSceneGuard
        {
            static bool Prepare() { return TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_NET_ONLY") == "SWEEP"; }
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(RestoreCheckpointController), "RestoreCheckpoint");
                yield return AccessTools.Method(typeof(GameController), "CreateCheckpoint");
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo started = AccessTools.Field(typeof(SceneManagerScene), "HasStartBeenCalled");
                foreach (CodeInstruction code in instructions)
                {
                    if (code.opcode == OpCodes.Ldfld && Equals(code.operand, started))
                    { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(NetSceneSweep), "CheckpointSceneStarted"); }
                    yield return code;
                }
            }
        }
        static bool Grounded
        { get { return Own != null && Own.Active && Own.PlatformBehaviour.PlatformMovement.IsOnGround && Own.Speed.sqrMagnitude < 0.25f; } }
        static void Checkpoint()
        {
            // Local checkpoints on BOTH PCs, including guest: no file writes or alternate serializer.
            GameController.Instance.CreateCheckpoint();
        }
        static bool Storm(TestHarness h) { return h.ErrorCount - s_entryErrors > 20; }
        [HarmonyPatch(typeof(GameController), "Update")]
        static class FrameWatch
        {
            static bool Prepare() { return TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_NET_ONLY") == "SWEEP"; }
            static void Postfix()
            {
                if (!s_running) return;
                if (Own != null) Own.Mortality.DamageReciever.IsImmortal = true;
                if (s_monitor) Observe(s_restores, s_loads);
            }
        }
        static readonly List<string> Failed = new List<string>();
        static void Log(string message, string stack, LogType type)
        {
            if (!s_observing || s_exception != null || (type != LogType.Exception && type != LogType.Error)) return;
            if ((stack != null && stack.Contains("Steamworks.LeaderboardFindResult")) || (message != null && message.StartsWith("<RI.Hid>"))) return;
            s_exception = message + " @ " + (stack ?? "").Split('\n')[0];
        }
        static void BotOff() { NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; }
        static string Diag()
        {
            try { return NetCutsceneTest.Diag(); }
            catch (Exception e) { return "diagnostics unavailable: " + e.Message; }
        }
        static void Observe(int restores, int loads)
        {
            if (Own != null) Own.Mortality.DamageReciever.IsImmortal = true;
            if (Own == null || !Own.Active || Own.Mortality.Health.Amount <= 0 || Revive.IsDowned(Own)
                || TestHarness.RestoreCheckpointCalls != restores || SaveSync.GuestLoads != loads || GameController.Instance.IsLoadingGame) s_unexpected = true;
        }
        static void Check(TestHarness h, bool ok, string name)
        { s_checks++; if (!ok && !Failed.Contains(name)) Failed.Add(name); h.Check(ok, "SWEEP " + name); }
        static SceneRoot Root(Component c)
        {
            for (Transform t = c.transform; t != null; t = t.parent) { SceneRoot r = t.GetComponent<SceneRoot>(); if (r != null) return r; }
            return null;
        }
        static SceneRoot Loaded(string name)
        {
            foreach (SceneRoot r in Resources.FindObjectsOfTypeAll<SceneRoot>())
                if (r != null && r.gameObject.activeInHierarchy && r.MetaData != null && r.MetaData.SceneName == name) return r;
            return null;
        }
        static string Path(Component c)
        {
            string result = c.name;
            for (Transform t = c.transform.parent; t != null; t = t.parent) result = t.name + "/" + result;
            return result;
        }
        static List<SavePedestal> Pedestals(string scene)
        {
            List<SavePedestal> result = new List<SavePedestal>();
            SceneRoot loaded = Loaded(scene);
            if (loaded == null) return result;
            foreach (SavePedestal p in Resources.FindObjectsOfTypeAll<SavePedestal>())
                if (p != null && Root(p) == loaded) result.Add(p);
            result.Sort(delegate(SavePedestal a, SavePedestal b) {
                int c = string.CompareOrdinal(Path(a), Path(b)); if (c != 0) return c;
                c = a.transform.position.x.CompareTo(b.transform.position.x); if (c != 0) return c;
                return a.transform.position.y.CompareTo(b.transform.position.y);
            });
            return result;
        }
        static void Load(RuntimeSceneMetaData scene) { Load(scene, null); }
        // T-102: SceneBounds merges several rectangles, so its centre can fall in a gap outside the scene (L-shaped
        // scenes); with the player there the streaming manager never keeps the scene loaded ("load" in every run and in
        // discovery). Use the largest real boundary rectangle, and the designers' Sein placeholder when it lies inside.
        static Rect Largest(RuntimeSceneMetaData scene)
        {
            Rect best = scene.SceneBounds; float area = -1f;
            foreach (Rect r in scene.SceneBoundaries) if (r.width * r.height > area) { best = r; area = r.width * r.height; }
            return best;
        }
        static Vector3 Anchor(RuntimeSceneMetaData scene)
        {
            Vector2 p = scene.PlaceholderPosition;
            if (p != Vector2.zero && scene.IsInsideSceneBounds(new Vector3(p.x, p.y, 0f))) return new Vector3(p.x, p.y, 0f);
            Vector2 c = Largest(scene).center;
            return new Vector3(c.x, c.y, 0f);
        }
        static void Load(RuntimeSceneMetaData scene, Vector3? position)
        {
            Vector3 a = Anchor(scene);
            if (Own != null) Plugin.Teleport(Own, position ?? new Vector3(a.x + (NetPlay.IsHost ? -1f : 1f), a.y, 0));
            Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(scene.SceneBounds, true, false, false);
        }
        static NetCutsceneTest.Sample Bits(int step, ulong hash, bool found)
        { return new NetCutsceneTest.Sample { Step = step, Plays = unchecked((int)hash), Direction = unchecked((int)(hash >> 32)), Found = found }; }
        static bool Same(NetCutsceneTest.Sample a, NetCutsceneTest.Sample b)
        { return a.Plays == b.Plays && a.Direction == b.Direction; }
        static void Append(string path, string line)
        {
            string dir = System.IO.Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (StreamWriter w = new StreamWriter(path, true)) { w.WriteLine(line); w.Flush(); }
        }
        static void Record(TestHarness h, SweepRecord r)
        {
            string csv = h.ResultPath + ".sweep.csv";
            if (!File.Exists(csv)) Append(csv, SweepRecord.Header);
            Append(csv, r.ToCsv());
            // Dedicated durable artifact survives a process crash. TestHarness also writes its buffered notes at Finish.
            string line = "SWEEP " + r.Index + " " + r.Kind + " " + r.Scene + " " + r.Verdict + " fails=" + r.Fails + " t0=" + r.Start + " t1=" + r.End + " note=" + r.Note.Replace('\r', ' ').Replace('\n', ' ');
            Append(h.ResultPath, line); h.Note(line);
        }
        static IEnumerator Catalog(TestHarness h, List<RuntimeSceneMetaData> metadata, List<SweepScene> catalog, string path)
        {
            // Append-only cache makes discovery restartable too. Snapshot eligibility BEFORE teleporting changes conditions.
            Dictionary<string, SweepRecord> cached = new Dictionary<string, SweepRecord>();
            if (File.Exists(path)) foreach (string line in File.ReadAllLines(path))
            { if (line == SweepRecord.Header || line.Length == 0) continue; try { SweepRecord r = SweepRecord.ParseCsv(line); cached[r.Scene] = r; } catch (FormatException) { } }
            for (int i = 0; i < metadata.Count; i++)
            {
                SweepScene scene = catalog[i]; SweepRecord saved;
                if (cached.TryGetValue(scene.Name, out saved))
                {
                    scene.Dependant = saved.Kind == "dependent"; scene.Loadable = saved.Verdict != "SKIP";
                    scene.Pedestals = int.Parse(saved.Fails, CultureInfo.InvariantCulture);
                    yield return NetCutsceneTest.Barrier(h, 1000000 + i);
                    continue;
                }
                string start = DateTime.UtcNow.ToString("o");
                bool loaded = false;
                if (!scene.Dependant && scene.Loadable)
                {
                    yield return NetCutsceneTest.Settle();
                    if (Grounded && !GameController.Instance.IsLoadingGame) Checkpoint();
                    Load(metadata[i]); float end = Now + 20f;
                    while (Now < end && (Loaded(scene.Name) == null || GameController.Instance.IsLoadingGame))
                    { if (Own != null) Own.Mortality.DamageReciever.IsImmortal = true; yield return null; }
                    loaded = Loaded(scene.Name) != null && !GameController.Instance.IsLoadingGame;
                    scene.Pedestals = loaded ? Pedestals(scene.Name).Count : 0;
                    if (loaded && Grounded) Checkpoint();
                }
                if (!File.Exists(path)) Append(path, SweepRecord.Header);
                Append(path, new SweepRecord { Index = i, Side = Side, Scene = scene.Name, Kind = scene.Dependant ? "dependent" : "scene",
                    Verdict = scene.Loadable ? (loaded ? "PASS" : "INCONCLUSIVE") : "SKIP", Fails = scene.Pedestals.ToString(CultureInfo.InvariantCulture),
                    Start = start, End = DateTime.UtcNow.ToString("o"), Note = loaded ? "" : "discovery not loaded; pedestal count unknown" }.ToCsv());
                h.Note("SWEEP discovery " + i + "/" + metadata.Count + " " + scene.Name + " pedestals=" + scene.Pedestals);
                // Bound startup skew, including when one PC has a cached discovery row and the other does not.
                yield return NetCutsceneTest.Barrier(h, 1000000 + i);
            }
            File.WriteAllText(path + ".complete", metadata.Count.ToString(CultureInfo.InvariantCulture));
        }
        static IEnumerator Recover(TestHarness h, RuntimeSceneMetaData meta, Vector3? position, int step, NetCutsceneTest.Sample result)
        {
            BotOff(); s_monitor = false;
            h.Note("SWEEP RECOVER " + meta.Scene + " step=" + step);
            int errors = h.ErrorCount;
            Load(meta, position);
            float end = Now + 25f;
            while (Now < end && (Loaded(meta.Scene) == null || GameController.Instance.IsLoadingGame || !Grounded)) yield return null;
            result.Found = Loaded(meta.Scene) != null && !GameController.Instance.IsLoadingGame && Grounded && h.ErrorCount - errors <= 20;
            if (result.Found) Checkpoint();
            // Step's final barrier is exception-safe. Publish readiness separately from its failure bit.
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step - 1, Found = result.Found });
            yield return NetCutsceneTest.Barrier(h, step - 1);
            NetCutsceneTest.Sample partner;
            result.Found &= NetCutsceneTest.Remote.TryGetValue(step - 1, out partner) && partner.Found && h.ErrorCount - errors <= 20;
        }
        static IEnumerator Digests(TestHarness h, int step, string name)
        {
            bool match = false;
            for (int i = 0; i < 3; i++)
            {
                int count; ulong digest = WorldSync.Digest(out count);
                NetCutsceneTest.Sample local = Bits(step + i, digest, true), remote;
                NetCutsceneTest.Publish(local); yield return NetCutsceneTest.Barrier(h, step + i);
                match |= NetCutsceneTest.Remote.TryGetValue(step + i, out remote) && Same(local, remote);
                // Always take all three samples, so asymmetric arrival timing cannot put the PCs on different steps.
                if (i < 2) yield return NetCutsceneTest.Wait(1f);
            }
            Check(h, match, name);
        }
        // T-102: reasons that make an entry INCONCLUSIVE instead of a game failure (e.g. "not-loadable-now").
        static string s_inconclusive;
        static bool s_storySeen, s_settledOk;
        static Vector3 s_spot;
        static bool Swimming
        { get { return Own != null && Own.Abilities != null && Own.Abilities.Swimming != null && Own.Abilities.Swimming.IsSwimming; } }
        static bool StoryRunning
        { get { return StorySync.StoryActive || OriCoop.Story.StoryHost.Current.Inside || (UI.Fader != null && UI.Fader.CurrentState == FaderB.State.Timeline); } }
        static int ViewRepairs() { return CutsceneGuard.Repairs - CutsceneGuard.SpiritRepairs; }
        // Ori stands still on dry ground for 0.5 s; a spot inside rock, in water or on a slope that slides does not count.
        static IEnumerator SettleAt(Vector3? position, float timeout)
        {
            s_settledOk = false;
            if (Own == null) yield break;
            if (position.HasValue)
            {
                Plugin.Teleport(Own, position.Value);
                Own.PlatformBehaviour.PlatformMovement.PlaceOnGround(0.5f, 0f);
            }
            float end = Now + timeout, stableSince = -1f; Vector3 last = Own.Position;
            while (Now < end)
            {
                if (Own == null) yield break;
                Own.Mortality.DamageReciever.IsImmortal = true;
                bool ok = Grounded && !Swimming && !GameController.Instance.IsLoadingGame && (Own.Position - last).sqrMagnitude < 0.25f;
                last = Own.Position;
                if (!ok) stableSince = -1f;
                else if (stableSince < 0) stableSince = Now;
                else if (Now - stableSince >= 0.5f) { s_settledOk = true; yield break; }
                yield return null;
            }
        }
        // Host only: the scene centre (where Load dropped Ori), then the save pedestals of the scene, then a grid over the
        // scene dropped onto the floor below each point. Bounded so the waiting guest (60 s barrier) never runs out.
        static IEnumerator HostFindSpot(TestHarness h, SweepEntry entry, RuntimeSceneMetaData meta)
        {
            float deadline = Now + 45f;
            yield return SettleAt(null, 6f);
            if (s_settledOk) { s_spot = Own.Position; h.Note("SWEEP spot centre " + s_spot); yield break; }
            foreach (SavePedestal p in Pedestals(entry.Scene))
            {
                if (Now >= deadline || Storm(h)) yield break;
                yield return SettleAt(p.transform.position + Vector3.left, 3f);
                if (s_settledOk) { s_spot = Own.Position; h.Note("SWEEP spot pedestal " + s_spot); yield break; }
            }
            Rect b = Largest(meta);
            foreach (float[] c in SweepPlan.Candidates(b.xMin, b.yMin, b.width, b.height))
            {
                if (Now >= deadline || Storm(h)) yield break;
                yield return SettleAt(new Vector3(c[0], c[1], 0f), 2.5f);
                if (s_settledOk && Loaded(entry.Scene) != null) { s_spot = Own.Position; h.Note("SWEEP spot grid " + s_spot); yield break; }
            }
            s_settledOk = false;
            h.Note("SWEEP spot none in " + entry.Scene + " bounds=" + b);
        }
        static IEnumerator Entry(TestHarness h, SweepEntry entry, RuntimeSceneMetaData meta, int step, float linger, SweepRecord record)
        {
            yield return NetCutsceneTest.Settle();
            // Do not save an airborne/unsafe origin. Both ends must have a valid current checkpoint before teleport.
            float readyEnd = Now + 20f;
            while (Now < readyEnd && !Storm(h) && (!Grounded || GameController.Instance.IsLoadingGame)) yield return null;
            bool ready = Grounded && !GameController.Instance.IsLoadingGame && !Storm(h);
            if (ready) Checkpoint();
            // T-102: the game never loads a scene whose LoadingCondition is false in this save; that is no failure.
            bool loadable = meta.CanBeLoaded;
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step + 11, Found = ready, Ability = loadable });
            yield return NetCutsceneTest.Barrier(h, step + 11);
            NetCutsceneTest.Sample origin;
            bool haveOrigin = NetCutsceneTest.Remote.TryGetValue(step + 11, out origin);
            if (haveOrigin && (!loadable || !origin.Ability)) { s_inconclusive = "not-loadable-now"; yield break; }
            if (!ready || !haveOrigin || !origin.Found)
            { Check(h, false, Storm(h) ? "exception-storm" : "place-or-settle"); yield break; }
            Load(meta);
            float end = Now + 30f;
            while (Now < end && !Storm(h) && (Loaded(entry.Scene) == null || GameController.Instance.IsLoadingGame))
            { if (Own != null) Own.Mortality.DamageReciever.IsImmortal = true; yield return null; }
            bool loaded = Loaded(entry.Scene) != null && !GameController.Instance.IsLoadingGame && Own != null;
            bool settled = false; Vector3 spot = Vector3.zero;
            if (loaded && entry.Kind == "save")
            {
                List<SavePedestal> ps = Pedestals(entry.Scene);
                if (entry.Pedestal < ps.Count)
                {
                    yield return SettleAt(ps[entry.Pedestal].transform.position + Vector3.right * (NetPlay.IsHost ? -1f : 1f), 10f);
                    settled = s_settledOk;
                }
            }
            else if (loaded && NetPlay.IsHost)
            {
                yield return HostFindSpot(h, entry, meta);
                settled = s_settledOk; spot = s_spot;
            }
            // T-102: the host picks the standing spot and the guest stands next to it, so both ends are in the same
            // place and stream the same neighbour scenes (a centre in rock / water / mid-air used to fail at once).
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step + 12, Found = NetPlay.IsHost ? settled : loaded,
                Plays = SweepPlan.FloatBits(spot.x), Direction = SweepPlan.FloatBits(spot.y) });
            yield return NetCutsceneTest.Barrier(h, step + 12);
            NetCutsceneTest.Sample chosen;
            if (loaded && entry.Kind != "save" && !NetPlay.IsHost && NetCutsceneTest.Remote.TryGetValue(step + 12, out chosen) && chosen.Found)
            {
                Vector3 host = new Vector3(SweepPlan.BitsFloat(chosen.Plays), SweepPlan.BitsFloat(chosen.Direction), 0f);
                foreach (Vector3 offset in new[] { new Vector3(1.5f, 0.5f, 0f), new Vector3(-1.5f, 0.5f, 0f), new Vector3(0f, 1f, 0f) })
                {
                    yield return SettleAt(host + offset, 4f);
                    if (s_settledOk) break;
                }
                settled = s_settledOk;
            }
            loaded = Loaded(entry.Scene) != null && !GameController.Instance.IsLoadingGame;
            if (loaded && settled && !Storm(h)) { Checkpoint(); s_placedPosition = Own.Position; }
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step, Found = loaded && settled && !Storm(h), Ability = loaded });
            int failures = NetCutsceneTest.Failures(h);
            yield return NetCutsceneTest.Barrier(h, step);
            NetCutsceneTest.Sample partner;
            bool foundBoth = settled && NetCutsceneTest.Remote.TryGetValue(step, out partner) && partner.Found;
            if (!loaded || !foundBoth || NetCutsceneTest.Failures(h) != failures)
            {
                record.Verdict = "FAIL";
                Check(h, false, Storm(h) ? "exception-storm" : !loaded ? "load" : !foundBoth ? "place-or-settle" : "barrier-timeout");
                yield break;
            }
            Check(h, true, "loaded-found-both");
            // T-102: the view checks count repairs from here on only, and never the spirit orb: the sweep teleports
            // leave the orb behind, which is not a stuck view.
            int repairs = ViewRepairs();
            int errors = h.ErrorCount;
            int restores = TestHarness.RestoreCheckpointCalls, loads = SaveSync.GuestLoads;
            s_restores = restores; s_loads = loads; s_unexpected = false; s_monitor = true;
            end = Now + linger;
            while (Now < end && !Storm(h))
            {
                Observe(restores, loads);
                if (StoryRunning) s_storySeen = true;
                NetTestHarness.BotLX = ((int)((end - Now) / 1.5f) % 2 == 0) ? 1f : -1f;
                NetTestHarness.BotButtons = ((int)(Now * 5f) % 2 == 0) ? P2Input.BTN_X : 0;
                yield return null;
            }
            BotOff();
            // T-102: a story or camera timeline the spot triggered pauses this end; let it finish before the digest
            // barrier so the two ends compare the world after it, not halfway through on one side only.
            float storyEnd = Now + 30f;
            while (Now < storyEnd && StoryRunning) { s_storySeen = true; yield return null; }
            yield return NetCutsceneTest.Barrier(h, step + 1);
            yield return Digests(h, step + 2, "digest");
            Observe(restores, loads);
            Check(h, Own != null && Own.Active, "own-present");
            bool puppet = P2Context.P2 != null && P2Context.P2 != Own && P2Context.P2.Active && Players.Remotes.Count == 1;
            Check(h, puppet, "partner-present");
            Check(h, NetCutsceneTest.CameraFree() && ViewRepairs() == repairs, "camera-free-no-repair");
            Check(h, !s_unexpected, "no-unexpected-death-reload");
            if (entry.Kind == "save")
            {
                s_monitor = false; // Only this explicit checkpoint round-trip is expected.
                if (NetPlay.IsHost) GameController.Instance.CreateCheckpoint();
                yield return NetCutsceneTest.Barrier(h, step + 5);
                yield return NetCutsceneTest.Wait(3.5f);
                int beforeLoads = SaveSync.GuestLoads;
                GameController.Instance.RestoreCheckpoint();
                yield return NetCutsceneTest.Wait(4f);
                end = Now + 75f;
                while (Now < end && !Storm(h) && (!SaveSync.Ready || Own == null || GameController.Instance.IsLoadingGame || (!NetPlay.IsHost && SaveSync.GuestLoads <= beforeLoads)))
                { if (Own != null) Own.Mortality.DamageReciever.IsImmortal = true; yield return null; }
                Check(h, SaveSync.Ready && Own != null && !GameController.Instance.IsLoadingGame && (NetPlay.IsHost || SaveSync.GuestLoads > beforeLoads), "checkpoint-reload-ready");
                s_restores = TestHarness.RestoreCheckpointCalls; s_loads = SaveSync.GuestLoads; s_monitor = true;
                yield return Digests(h, step + 6, "reload-digest");
                Check(h, NetCutsceneTest.CameraFree() && ViewRepairs() == repairs, "reload-camera-free-no-repair");
                Check(h, Own != null && Own.Active && P2Context.P2 != null && P2Context.P2.Active, "reload-players-present");
            }
            if (Storm(h)) Check(h, false, "exception-storm");
            Check(h, h.ErrorCount == errors, "no-new-exceptions");
            record.Verdict = SweepReport.Classify(true, true, false, s_checks, Failed);
        }
        public static IEnumerator Run(TestHarness h)
        {
            // Register the existing CUT receiver even though its Run phase is intentionally skipped.
            NetPlay.On(74, (Action<BinaryReader>)Delegate.CreateDelegate(typeof(Action<BinaryReader>), typeof(NetCutsceneTest).GetMethod("Receive", BindingFlags.Static | BindingFlags.NonPublic)));
            string directory = Env("DIR") ?? System.IO.Path.GetDirectoryName(h.ResultPath);
            Directory.CreateDirectory(directory);
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true; s_running = true;
            Application.logMessageReceived += Log;
            try
            {
                BotOff(); NetPlay.SetSim(0, 0, 0, 0);
                List<RuntimeSceneMetaData> metadata = new List<RuntimeSceneMetaData>(Core.Scenes.Manager.AllScenes);
                List<SweepScene> catalog = new List<SweepScene>();
                foreach (RuntimeSceneMetaData m in metadata) catalog.Add(new SweepScene(m.Scene, m.DependantScene, m.CanBeLoaded,
                    Game.Checkpoint.SaveGameData.Scenes.ContainsKey(m.SceneMoonGuid), 0));
                yield return Catalog(h, metadata, catalog, System.IO.Path.Combine(directory, Side + ".catalog.csv"));
                List<SweepEntry> plan = SweepPlan.Build(catalog, Env("FROM"), Env("TO"), Env("SHARD"), Env("SCENES"), Env("SKIP"));
                string planPath = System.IO.Path.Combine(directory, Side + ".plan.csv");
                using (StreamWriter w = new StreamWriter(planPath, false))
                {
                    w.WriteLine("index,kind,scene,pedestal");
                    foreach (SweepEntry e in plan) w.WriteLine(e.Index + "," + e.Kind + "," + e.Scene + "," + e.Pedestal);
                }
                string hash = SweepPlan.Hash(plan); h.Note("SWEEP PLAN count=" + plan.Count + " hash=" + hash);
                NetCutsceneTest.Sample local = Bits(999, ulong.Parse(hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture), true), remote;
                int planFailures = NetCutsceneTest.Failures(h);
                NetCutsceneTest.Publish(local); yield return NetCutsceneTest.Barrier(h, 999);
                bool same = NetCutsceneTest.Failures(h) == planFailures && NetCutsceneTest.Remote.TryGetValue(999, out remote) && Same(local, remote);
                h.Check(same, "SWEEP plan hashes equal");
                if (!same) { Append(System.IO.Path.Combine(directory, Side + ".fatal.txt"), "plan-hash-mismatch " + DateTime.UtcNow.ToString("o")); yield break; }
                float linger; if (!float.TryParse(Env("LINGER"), NumberStyles.Float, CultureInfo.InvariantCulture, out linger) || linger < 0 || float.IsNaN(linger) || float.IsInfinity(linger)) linger = 8f;
                int resume; if (!int.TryParse(Env("RESUME"), out resume)) resume = 0;
                RuntimeSceneMetaData lastPassed = plan.Count == 0 ? null : metadata.Find(delegate(RuntimeSceneMetaData m) { return m.Scene == plan[0].Scene; });
                Vector3? lastPassedPosition = null;
                int recoveries = 0, failStreak = 0;
                foreach (SweepEntry e in plan)
                {
                    if (e.Index < resume) continue;
                    s_exception = null; s_checks = 0; Failed.Clear(); s_observing = true; s_monitor = false; s_unexpected = false;
                    s_inconclusive = null; s_storySeen = false; int spiritBefore = CutsceneGuard.SpiritRepairs;
                    SweepRecord record = new SweepRecord { Index = e.Index, Side = Side, Kind = e.Kind, Scene = e.Scene, Start = DateTime.UtcNow.ToString("o") };
                    Record(h, record);
                    RuntimeSceneMetaData meta = metadata.Find(delegate(RuntimeSceneMetaData m) { return m.Scene == e.Scene; });
                    int failures = NetCutsceneTest.Failures(h), errors = h.ErrorCount, step = 1000 + e.Index * 100;
                    s_entryErrors = errors;
                    yield return NetCutsceneTest.Step(h, step + 9, Entry(h, e, meta, step, linger, record));
                    s_monitor = false; s_observing = false; BotOff();
                    if (s_unexpected && !Failed.Contains("no-unexpected-death-reload")) Check(h, false, "no-unexpected-death-reload");
                    if (s_exception == null)
                    {
                        IList list = (IList)typeof(TestHarness).GetField("m_fails", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(h);
                        for (int f = failures; f < list.Count; f++)
                            if (((string)list[f]).Contains("threw ")) { s_exception = (string)list[f]; break; }
                    }
                    if (h.ErrorCount != errors && !Failed.Contains("no-new-exceptions")) Check(h, false, "no-new-exceptions");
                    if (NetCutsceneTest.Failures(h) > failures && Failed.Count == 0) Failed.Add("step-or-partner-failure");
                    if (Storm(h) && !Failed.Contains("exception-storm")) Check(h, false, "exception-storm");
                    record.Verdict = SweepReport.Classify(true, true, false, s_checks, Failed);
                    record.Fails = string.Join(",", Failed.ToArray()); record.End = DateTime.UtcNow.ToString("o");
                    // T-102: an entry the game cannot load in this save is INCONCLUSIVE with its reason, not a failure.
                    if (s_inconclusive != null && Failed.Count == 0) { record.Verdict = "INCONCLUSIVE"; record.Fails = s_inconclusive; }
                    record.Note = (s_exception ?? ("digestMatch=" + WorldSync.LastDigestMatch + " resyncs=" + WorldSync.DigestResyncs + " " + Diag()))
                        + " spiritRepairs=" + (CutsceneGuard.SpiritRepairs - spiritBefore) + (s_storySeen ? " story=seen" : "");
                    Record(h, record);
                    bool recover = Failed.Contains("exception-storm") || Failed.Contains("load") || Failed.Contains("place-or-settle")
                        || Failed.Contains("barrier-timeout") || Failed.Contains("step-or-partner-failure") || s_exception != null;
                    NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step + 10, Found = record.Verdict == "PASS", Ability = recover });
                    yield return NetCutsceneTest.Barrier(h, step + 10);
                    bool havePartner = NetCutsceneTest.Remote.TryGetValue(step + 10, out remote);
                    if (record.Verdict == "PASS" && havePartner && remote.Found)
                    { lastPassed = meta; lastPassedPosition = s_placedPosition; }
                    bool needRecover = recover || !havePartner || remote.Ability;
                    // Both PCs see the same (local, partner) bits here, so the streak stays symmetric. A successful Recover()
                    // does not clear sticky state such as a stuck cameraPuppet (first full sweep: entries 119-222 all failed
                    // placement after "recovering"), so 3 recovery-worthy entries in a row restart the game instead.
                    failStreak = needRecover ? failStreak + 1 : 0;
                    if (failStreak >= 3)
                    {
                        string message = "SWEEP ABORT fail-streak=" + failStreak + " recoveries=" + recoveries;
                        Record(h, new SweepRecord { Index = e.Index, Side = Side, Kind = "abort", Scene = e.Scene, Verdict = "ABORT",
                            Start = DateTime.UtcNow.ToString("o"), End = DateTime.UtcNow.ToString("o"), Note = message });
                        File.WriteAllText(System.IO.Path.Combine(directory, Side + ".abort.txt"), message);
                        break;
                    }
                    if (needRecover)
                    {
                        bool recovered = false;
                        for (int retry = 0; retry < 3 && !recovered; retry++)
                        {
                            recoveries++;
                            NetCutsceneTest.Sample result = new NetCutsceneTest.Sample();
                            int recoveryStep = step + 21 + retry * 3;
                            int recoveryFailures = NetCutsceneTest.Failures(h);
                            yield return NetCutsceneTest.Step(h, recoveryStep, Recover(h, lastPassed, lastPassedPosition, recoveryStep, result));
                            // Exchange the exception-safe step outcome too: neither PC advances on unilateral success.
                            bool ok = result.Found && NetCutsceneTest.Failures(h) == recoveryFailures;
                            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = recoveryStep + 1, Found = ok });
                            yield return NetCutsceneTest.Barrier(h, recoveryStep + 1);
                            recovered = ok && NetCutsceneTest.Remote.TryGetValue(recoveryStep + 1, out remote) && remote.Found;
                            h.Note("SWEEP RECOVER count=" + recoveries + " consecutive=" + (retry + 1) + " ok=" + recovered);
                        }
                        if (!recovered)
                        {
                            string message = "SWEEP ABORT recoveries=" + recoveries + " consecutive-failed=3";
                            Record(h, new SweepRecord { Index = e.Index, Side = Side, Kind = "abort", Scene = e.Scene, Verdict = "ABORT",
                                Start = DateTime.UtcNow.ToString("o"), End = DateTime.UtcNow.ToString("o"), Note = message });
                            File.WriteAllText(System.IO.Path.Combine(directory, Side + ".abort.txt"), message);
                            break;
                        }
                    }
                }
                Append(System.IO.Path.Combine(directory, Side + ".done"), DateTime.UtcNow.ToString("o"));
            }
            finally
            {
                s_running = s_monitor = s_observing = false; Application.logMessageReceived -= Log; BotOff();
                if (Own != null) Own.Mortality.DamageReciever.IsImmortal = immortal;
            }
        }
    }
}
