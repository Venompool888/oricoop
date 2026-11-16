using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using UnityEngine;

namespace OriCoop
{
    public static class EnemySyncTest
    {
        const byte MsgBarrier = 68;
        sealed class Packet
        {
            public string Key; public Vector3 Origin, Position; public float Health;
            public DifficultyMode Difficulty; public bool Ready;
        }
        static readonly Dictionary<int, Packet> s_partnerSteps = new Dictionary<int, Packet>();
        // Each pass (no lag / 200 ms RTT) uses its own barrier step range so a fast peer can't be confused with the last pass.
        static int s_offset; static string s_tag = "E5";
        static bool Has(int step) { return s_partnerSteps.ContainsKey(step + s_offset); }
        sealed class Steps { public Packet this[int step] { get { return s_partnerSteps[step + s_offset]; } } }
        static readonly Steps Part = new Steps();
        static SeinCharacter Own { get { return P2Context.P1; } }
        static float Now { get { return Time.realtimeSinceStartup; } }
        static string F(float f) { return f.ToString("F2"); }
        static void OnBarrier(BinaryReader r)
        {
            int step = r.ReadInt32(); Packet p = new Packet(); p.Key = r.ReadString();
            p.Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            p.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            p.Health = r.ReadSingle(); p.Difficulty = (DifficultyMode)r.ReadInt32(); p.Ready = r.ReadBoolean(); s_partnerSteps[step] = p;
        }
        static IEnumerator Wait(float seconds) { float end = Now + seconds; while (Now < end) yield return null; }
        static bool FamilyAhead(int step)
        {
            foreach (int received in s_partnerSteps.Keys)
                if (received > step + s_offset && received >= 100 + s_offset && received < 240 + s_offset) return true;
            return false;
        }
        static IEnumerator Barrier(TestHarness h, int step, string key, bool family = false)
        {
            EnemySync.Probe p = EnemySync.Inspect(key); Vector3 origin = s_barrierAt.HasValue ? s_barrierAt.Value : p.Origin;
            NetPlay.Send(MsgBarrier, true, delegate(BinaryWriter w) {
                w.Write(step + s_offset); w.Write(key ?? "");
                w.Write(origin.x); w.Write(origin.y); w.Write(origin.z);
                w.Write(p.Position.x); w.Write(p.Position.y); w.Write(p.Position.z);
                w.Write(p.Health); w.Write((int)DifficultyController.Instance.Difficulty);
                w.Write(p.Present && (family || p.Mirrored) && p.Known && !p.Dead && p.Owner == NetPlay.IsHost);
            });
            float end = Now + 30f;
            while (NetPlay.Connected && !Has(step) && Now < end && (!family || !FamilyAhead(step))) yield return null;
            h.Check(Has(step), s_tag + " barrier " + step + " reached by both");
        }
        static string Find(Vector3 near, string className, HashSet<string> rejected)
        {
            if (className != "")
            {
                // The push-block intro frog sits beside an automatic checkpoint: moving there reloads the guest mid-test.
                HashSet<string> skip = rejected == null ? new HashSet<string>() : new HashSet<string>(rejected);
                for (int i = 0; i < 6; i++)
                {
                    string k = EnemySync.FindTestEnemyOfClass(near, className, skip);
                    if (k == "" || EnemySync.Inspect(k).SceneName.IndexOf("PushBlock", StringComparison.Ordinal) < 0) return k;
                    skip.Add(k);
                }
                return "";
            }
            // Stable walkers first; DropSlug/Kamikaze types blow themselves up and can't hold a damage baseline.
            foreach (string c in new[] { "StarSlugEnemy", "AcidSlugEnemy", "SpitterEnemy", "RammingEnemy" })
            { string k = EnemySync.FindTestEnemyOfClass(near, c, rejected); if (k != "") return k; }
            return "";
        }
        static IEnumerator Choose(TestHarness h, Packet chosen, string className, HashSet<string> rejected, int step)
        {
            string key = ""; Vector3 saved = Own.Position;
            if (NetPlay.IsHost)
            {
                key = Find(saved, className, rejected);
                if (key == "" && Core.Scenes.Manager != null)
                {
                    List<RuntimeSceneMetaData> areas = new List<RuntimeSceneMetaData>();
                    foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                        if (!scene.DependantScene && scene.CanBeLoaded && scene.SceneBoundaries.Count > 0
                            && Game.Checkpoint.SaveGameData.Scenes.ContainsKey(scene.SceneMoonGuid)
                            && scene.Scene.IndexOf("boss", StringComparison.OrdinalIgnoreCase) < 0
                            && scene.Scene.IndexOf("escape", StringComparison.OrdinalIgnoreCase) < 0) areas.Add(scene);
                    areas.Sort(delegate(RuntimeSceneMetaData a, RuntimeSceneMetaData b) {
                        return Vector3.Distance(a.PlaceholderPosition, saved).CompareTo(Vector3.Distance(b.PlaceholderPosition, saved)); });
                    for (int i = 0; i < areas.Count && i < 4 && key == ""; i++)
                    {
                        Vector3 point = areas[i].PlaceholderPosition;
                        h.Note(s_tag + " search " + className + " scene=" + areas[i].Scene + " position=" + point);
                        EnemySync.LoadTestArea(point); Plugin.Teleport(Own, point);
                        yield return Wait(2f); key = Find(point, className, rejected);
                    }
                }
            }
            yield return Barrier(h, step, key);
            if (!Has(step)) yield break;
            chosen.Key = NetPlay.IsHost ? key : Part[step].Key;
            chosen.Origin = NetPlay.IsHost ? EnemySync.Inspect(chosen.Key).Origin : Part[step].Origin;
        }
        // Far placement (|side| > 5) must land on ground near the enemy's height: try both sides.
        static void Place(Vector3 origin, float side)
        {
            if (Mathf.Abs(side) > 5f && !Ground(origin, side) && Ground(origin, -side)) side = -side;
            PlaceAt(origin, side);
        }
        static bool Ground(Vector3 origin, float side)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(origin + new Vector3(side, 3f, 0), Vector3.down, 7f, Own.Controller.RayTestLayerMask))
                if (hit.collider != null && !hit.collider.isTrigger && hit.normal.y >= 0.6f && hit.collider.GetComponentInParent<Entity>() == null
                    && Mathf.Abs(hit.point.y - origin.y) < 3f) return true;
            return false;
        }
        static void PlaceAt(Vector3 origin, float side)
        {
            EnemySync.LoadTestArea(origin); Vector3 position = origin + new Vector3(side, 1f, 0);
            RaycastHit[] hits = Physics.RaycastAll(origin + new Vector3(side, 2f, 0), Vector3.down, 12f, Own.Controller.RayTestLayerMask);
            float highest = float.NegativeInfinity;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || hit.collider.isTrigger || hit.normal.y < 0.6f
                    || hit.collider.GetComponentInParent<Entity>() != null || hit.collider.GetComponentInParent<SeinCharacter>() != null
                    || hit.point.y <= highest || hit.point.y < origin.y - 8f) continue;
                highest = hit.point.y;
                position = hit.point + (Own.Position - Own.PlatformBehaviour.PlatformMovement.FeetPosition) + Vector3.up * 0.1f;
            }
            Plugin.Teleport(Own, position);
        }
        static IEnumerator Ready(TestHarness h, Packet chosen, int step, float side)
        {
            Place(chosen.Origin, side); float end = Now + 8f, loadAt = Now;
            while (!EnemySync.Inspect(chosen.Key).Present && Now < end)
            {
                if (Now >= loadAt) { EnemySync.LoadTestArea(chosen.Origin); loadAt = Now + 0.25f; }
                yield return null;
            }
            yield return Wait(1f); yield return Barrier(h, step, chosen.Key);
            EnemySync.Probe p = EnemySync.Inspect(chosen.Key);
            chosen.Ready = p.Present && p.Mirrored && p.Known && !p.Dead && p.Owner == NetPlay.IsHost
                && Has(step) && Part[step].Ready;
            h.Check(chosen.Ready, s_tag + " mirrored enemy present on both peers with host authority");
        }
        static IEnumerator Jumper(TestHarness h)
        {
            Packet chosen = new Packet(); Vector3 saved = Own.Position;
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true;
            try
            {
                yield return Choose(h, chosen, "JumperEnemy", null, 10);
                if (string.IsNullOrEmpty(chosen.Key)) { h.Note(s_tag + "a skipped: no JumperEnemy found in loaded/saved test areas"); yield break; }
                chosen.Origin = NetPlay.IsHost ? EnemySync.Inspect(chosen.Key).Position : Part[10].Position;
                yield return Ready(h, chosen, 11, NetPlay.IsHost ? -19.5f : 1.5f);
                if (!chosen.Ready) yield break;
                if (NetPlay.IsHost) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                yield return Barrier(h, 12, chosen.Key);
                HashSet<string> states = new HashSet<string>(); float travel = 0f;
                Vector3 previous = EnemySync.Inspect(chosen.Key).Position;
                for (int i = 0; i < 60; i++)
                {
                    EnemySync.Probe p = EnemySync.Inspect(chosen.Key); states.Add(p.StateName);
                    if (i % 20 == 0) h.Note(s_tag + "a ai " + EnemySync.DebugAi(chosen.Key));
                    if (p.Present) { travel += Vector3.Distance(previous, p.Position); previous = p.Position; }
                    yield return Wait(0.1f);
                }
                bool active = false;
                foreach (string state in states)
                    if (state != null && (state.IndexOf("JumperEnemyChargingState", StringComparison.Ordinal) >= 0
                        || state.IndexOf("JumperEnemyFallState", StringComparison.Ordinal) >= 0)) active = true;
                h.Note(s_tag + "a travel=" + F(travel) + " states=" + string.Join(",", new List<string>(states).ToArray()));
                if (NetPlay.IsHost) h.Check(active && travel > 1.5f, s_tag + "a owner targets nearby guest and travels >1.5");
                else h.Check(EnemySync.Inspect(chosen.Key).Mirrored && travel > 1.5f, s_tag + "a guest jumper mirrored and travels >1.5");
                Vector3 final = EnemySync.Inspect(chosen.Key).Position;
                yield return Barrier(h, 13, chosen.Key);
                if (Has(13)) h.Check(Vector3.Distance(final, Part[13].Position) < 5f, s_tag + "a final peer positions differ <5 (frog may be mid-jump)");
            }
            finally
            {
                if (!string.IsNullOrEmpty(chosen.Key)) EnemySync.EndTestEnemy(chosen.Key);
                Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved);
            }
        }
        static IEnumerator DamageOnce(TestHarness h)
        {
            Packet chosen = new Packet(); Vector3 saved = Own.Position;
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true;
            try
            {
                yield return Choose(h, chosen, "", null, 20);
                h.Check(!string.IsNullOrEmpty(chosen.Key), s_tag + "b mirrored candidate available");
                if (string.IsNullOrEmpty(chosen.Key)) yield break;
                yield return Ready(h, chosen, 21, NetPlay.IsHost ? -10f : 10f);
                if (!chosen.Ready) yield break;
                if (NetPlay.IsHost) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                yield return Wait(0.5f); yield return Barrier(h, 22, chosen.Key);
                float baseline = NetPlay.IsHost ? EnemySync.Inspect(chosen.Key).Health : Part[22].Health;
                if (NetPlay.IsHost) EnemySync.TestAttack(chosen.Key, 2f, false);
                yield return Wait(1f); float afterHost = EnemySync.Inspect(chosen.Key).Health;
                yield return Barrier(h, 23, chosen.Key);
                if (!NetPlay.IsHost) EnemySync.TestAttack(chosen.Key, 3f, false);
                yield return Wait(1f); float afterGuest = EnemySync.Inspect(chosen.Key).Health;
                h.Note(s_tag + "b baseline=" + F(baseline) + " hostDelta=" + F(baseline - afterHost) + " guestDelta=" + F(afterHost - afterGuest));
                h.Check(Mathf.Abs(baseline - afterGuest - 5f) < 0.01f, s_tag + "b health dropped exactly 5 on this peer");
                yield return Barrier(h, 24, chosen.Key);
            }
            finally
            {
                if (!string.IsNullOrEmpty(chosen.Key)) EnemySync.EndTestEnemy(chosen.Key);
                Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved);
            }
        }
        static IEnumerator Death(TestHarness h)
        {
            Vector3 saved = Own.Position; bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true;
            HashSet<string> rejected = new HashSet<string>(); string first = ""; Packet chosen = new Packet();
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    int step = 30 + round * 10;
                    yield return Choose(h, chosen, "", rejected, step);
                    if (string.IsNullOrEmpty(chosen.Key) && first != "")
                    {
                        h.Note(s_tag + "c reusing sole candidate after respawn");
                        yield return Choose(h, chosen, "", null, step + 1);
                    }
                    h.Check(!string.IsNullOrEmpty(chosen.Key), s_tag + "c kill candidate " + round + " available");
                    if (string.IsNullOrEmpty(chosen.Key)) yield break;
                    if (first == "") first = chosen.Key;
                    rejected.Add(chosen.Key);
                    yield return Ready(h, chosen, step + 2, NetPlay.IsHost ? -10f : 10f);
                    if (!chosen.Ready) yield break;
                    if (NetPlay.IsHost) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                    yield return Wait(0.5f);
                    int deaths = EnemySync.DeathEffects, ownerDeaths = EnemySync.OwnerDeathEffects;
                    bool owner = EnemySync.Inspect(chosen.Key).Owner;
                    bool hasDeathAction = false;
                    if (owner)
                        foreach (RespawningPlaceholder placeholder in RespawningPlaceholder.All)
                            if (placeholder != null && Vector3.Distance(placeholder.Position, chosen.Origin) < 0.01f
                                && placeholder.CurrentEntity != null && placeholder.CurrentEntity.DamageReciever != null)
                                hasDeathAction = placeholder.CurrentEntity.DamageReciever.DeathAction != null;
                    yield return Barrier(h, step + 3, chosen.Key);
                    if ((round == 0) == NetPlay.IsHost) EnemySync.TestAttack(chosen.Key, 1000f, false);
                    float end = Now + 1.5f;
                    while (Now < end && (EnemySync.Inspect(chosen.Key).Present
                        || (owner ? EnemySync.OwnerDeathEffects == ownerDeaths : EnemySync.DeathEffects == deaths))) yield return null;
                    h.Check(!EnemySync.Inspect(chosen.Key).Present, s_tag + "c kill " + round + " removed entity");
                    if (!owner) h.Check(EnemySync.DeathEffects == deaths + 1, s_tag + "c kill " + round + " puppet death effect exactly once");
                    else if (!hasDeathAction) h.Note(s_tag + "c owner candidate has no DeathAction");
                    else h.Check(EnemySync.OwnerDeathEffects > ownerDeaths, s_tag + "c kill " + round + " owner death effect");
                    yield return Barrier(h, step + 4, chosen.Key);
                    EnemySync.EndTestEnemy(chosen.Key);
                }
            }
            finally
            {
                foreach (string key in rejected) EnemySync.EndTestEnemy(key);
                Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved);
            }
        }
        static IEnumerator Hard(TestHarness h)
        {
            DifficultyMode old = DifficultyController.Instance.Difficulty;
            yield return Barrier(h, 60, "");
            if (!Has(60)) yield break;
            if (old == DifficultyMode.OneLife || Part[60].Difficulty == DifficultyMode.OneLife)
            { h.Note(s_tag + "d skipped on OneLife"); yield break; }
            Vector3 saved = Own.Position; bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            try
            {
                if (NetPlay.IsHost) DifficultyController.Instance.Difficulty = DifficultyMode.Hard;
                yield return Barrier(h, 61, ""); yield return Wait(1.5f);
                h.Check(DifficultyController.Instance.Difficulty == DifficultyMode.Hard, s_tag + "d Hard difficulty on this peer");
                Own.Mortality.DamageReciever.IsImmortal = false;
                Own.Mortality.Health.RestoreAllHealth(); Own.Mortality.DamageReciever.ResetInviciblity();
                float before = Own.Mortality.Health.Amount;
                Own.Mortality.DamageReciever.OnRecieveDamage(new Damage(4f, Vector2.zero, Own.Position, DamageType.Enemy, null));
                float lost = before - Own.Mortality.Health.Amount;
                h.Check(Mathf.Abs(lost - 8f) < 0.01f, s_tag + "d Enemy damage 4 loses 8 health (lost=" + F(lost) + ")");
                yield return Barrier(h, 62, "");
            }
            finally
            {
                Own.Mortality.Health.RestoreAllHealth(); Own.Mortality.DamageReciever.IsImmortal = immortal;
                if (NetPlay.IsHost) DifficultyController.Instance.Difficulty = old;
                Plugin.Teleport(Own, saved);
            }
        }
        static readonly string[] Families = { "StarSlugEnemy", "AcidSlugEnemy", "DropSlugEnemy", "JumperEnemy",
            "SpitterEnemy", "RammingEnemy", "KamikazeSootEnemy", "DashOwlEnemy", "FishEnemy", "MortarWormEnemy",
            "FloatingRockTurretEnemy", "FloatingRockLaserEnemy", "ShootingSpider", "JumpShootShark" };
        static void CollectFamilies(Dictionary<string, Packet> found) { CollectFamilies(found, null); }
        static void CollectFamilies(Dictionary<string, Packet> found, object at)
        {
            string[] loaded;
            try { loaded = at == null ? EnemySync.LoadedEnemies() : EnemySync.LoadedEnemiesAt((Vector3)at); } catch (Exception e) { Plugin.Log.LogWarning("E6 LoadedEnemies: " + e); return; }
            foreach (string entry in loaded)
            {
                try
                {
                    string[] fields = entry.Split('|');
                    EnemySync.Probe p = EnemySync.Inspect(fields[0]);
                    if ((p.SceneName ?? "").IndexOf("PushBlock", StringComparison.OrdinalIgnoreCase) >= 0 || found.ContainsKey(fields[1])) continue;
                    found.Add(fields[1], new Packet { Key = fields[0], Origin = p.Origin });
                }
                catch (Exception e) { Plugin.Log.LogWarning("E6 collect " + entry + ": " + e); }
            }
        }
        // The guest visits each nearby saved area; the host streams the scenes around the guest (EnemySync.StreamPartner)
        // and lists their enemies. The host never teleports here: moving P1 through areas can hit automatic checkpoints,
        // which reload the guest mid-test.
        // Safe guest teleport for E6: only onto solid, dry ground near `origin` (scene and flier origins can be over a pit
        // or poisoned water; falling out of the world makes the guest restore a checkpoint mid-test).
        static bool SafeSpot(Vector3 origin, out Vector3 spot)
        {
            float best = float.PositiveInfinity; spot = origin;
            foreach (float dx in new[] { 0f, 1.5f, -1.5f, 3f, -3f, 5f, -5f, 8f, -8f })
                foreach (float dy in new[] { 4f, 12f })
                    foreach (RaycastHit hit in Physics.RaycastAll(origin + new Vector3(dx, dy, 0), Vector3.down, 40f, Own.Controller.RayTestLayerMask))
                    {
                        if (hit.collider == null || hit.collider.isTrigger || hit.normal.y < 0.6f || hit.collider.GetComponentInParent<Entity>() != null
                            || hit.collider.GetComponentInParent<SeinCharacter>() != null || WaterZone.PositionInWater(hit.point + Vector3.up)) continue;
                        float d = Vector3.Distance(hit.point, origin); if (d >= best) continue;
                        best = d; spot = hit.point + (Own.Position - Own.PlatformBehaviour.PlatformMovement.FeetPosition) + Vector3.up * 0.1f;
                    }
            return best < float.PositiveInfinity;
        }        static Vector3? s_barrierAt;
        // Families come from the enemies this session already tracked in the areas E3/E5 visit (safe ground, no
        // automatic checkpoints, no poisoned water). A world sweep that teleports a player through other areas of this
        // early save kept triggering checkpoint reloads, so families outside these areas are reported as not covered.
        static IEnumerator SweepFamilies(TestHarness h, Dictionary<string, Packet> found)
        {
            if (!NetPlay.IsHost) yield break;
            string[] known;
            try { known = EnemySync.KnownEnemies(); } catch (Exception e) { Plugin.Log.LogWarning("E6 KnownEnemies: " + e); yield break; }
            foreach (string entry in known)
            {
                string[] fields = entry.Split('|'); EnemySync.Probe p = EnemySync.Inspect(fields[0]);
                if (found.ContainsKey(fields[1]) || (p.SceneName ?? "").IndexOf("PushBlock", StringComparison.OrdinalIgnoreCase) >= 0
                    || (p.SceneName ?? "").IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                found.Add(fields[1], new Packet { Key = fields[0], Origin = p.Origin });
            }
            foreach (string cls in Families) h.Note(s_tag + " families " + cls + (found.ContainsKey(cls) ? " found " + found[cls].Key : " not in the test areas"));
        }        static IEnumerator FamilyPresence(Packet chosen)
        {
            float end = Now + 8f, loadAt = Now;
            while (Now < end)
            {
                EnemySync.Probe p = EnemySync.Inspect(chosen.Key);
                if (p.Present && p.Known && !p.Dead && p.Owner == NetPlay.IsHost) yield break;
                if (NetPlay.IsHost && p.PlaceholderLoaded && p.Known && p.Owner && !p.Present) EnemySync.EnsureSpawned(chosen.Key);
                if (Now >= loadAt) { EnemySync.LoadTestArea(chosen.Origin); loadAt = Now + 0.25f; }
                yield return null;
            }
        }
        static IEnumerator Family(TestHarness h, string cls, Dictionary<string, Packet> found, int step)
        {
            Packet chosen = new Packet(); Vector3 saved = Vector3.zero; bool immortal = false, held = false;
            try
            {
                if (Own == null || P2Context.P2 == null) { float back = Now + 20f; while ((P2Context.P1 == null || P2Context.P2 == null) && Now < back) yield return null; }
                if (Own == null || P2Context.P2 == null) { h.Note(s_tag + " " + cls + " skipped: a player is missing (checkpoint reload)"); yield break; }
                saved = Own.Position; immortal = Own.Mortality.DamageReciever.IsImmortal; Own.Mortality.DamageReciever.IsImmortal = true; held = true;
                Packet candidate; string key = NetPlay.IsHost && found.TryGetValue(cls, out candidate) ? candidate.Key : "";
                yield return Barrier(h, step, key, true);
                if (!Has(step)) yield break;
                chosen.Key = NetPlay.IsHost ? key : Part[step].Key;
                if (chosen.Key == "") { h.Note(s_tag + " " + cls + " skipped: not in the test save's areas"); yield break; }
                if (cls == "FishEnemy") { h.Note(s_tag + " " + cls + " skipped: only engages a player in the water, which is poisoned in the test save"); yield break; }
                chosen.Origin = NetPlay.IsHost ? found[cls].Origin : Part[step].Origin;
                // Only the guest moves next to the enemy; the host stays where it is (far away) and keeps ownership by streaming the guest's scenes.
                int deathsAtPlace = EnemySync.DeathEffects; bool suicidal = cls == "DropSlugEnemy" || cls == "KamikazeSootEnemy";
                bool placed = true; if (!NetPlay.IsHost) { EnemySync.LoadTestArea(chosen.Origin); Plugin.Teleport(Own, chosen.Origin + new Vector3(1.5f, 1f, 0)); yield return Wait(1.5f); Vector3 spot; placed = SafeSpot(chosen.Origin, out spot); if (placed) Plugin.Teleport(Own, spot); else h.Note(s_tag + " " + cls + " no safe ground beside it; staying at the spawn point"); }
                if (P2Context.P2 == null) { float back = Now + 15f; while (P2Context.P2 == null && Now < back) yield return null; }
                if (P2Context.P2 == null) { h.Note(s_tag + " " + cls + " skipped: partner puppet missing (checkpoint reload)"); yield break; }
                yield return FamilyPresence(chosen);
                yield return Barrier(h, step + 1, chosen.Key, true);
                if (!Has(step + 1)) yield break;
                EnemySync.Probe p = EnemySync.Inspect(chosen.Key);
                bool ready = p.Present && p.Known && !p.Dead && p.Owner == NetPlay.IsHost && Part[step + 1].Ready;
                if (suicidal && p.Dead)
                {
                    // It already dropped/rolled onto the guest and blew itself up (nobody attacked it): that is the attack. Before
                    // the fix it never noticed the guest (SeeSeinTrigger / AI looked only at P1, 100+ units away).
                    if (NetPlay.IsHost) h.Check(true, s_tag + " " + cls + " owner AI engages the guest (self-destructed beside it)");
                    else h.Check(EnemySync.DeathEffects > deathsAtPlace, s_tag + " " + cls + " self-destruct death burst played on the guest");
                    yield return Barrier(h, step + 5, chosen.Key, true); yield break;
                }
                h.Check(ready, s_tag + " " + cls + " present on both peers with host authority");
                if (!ready) h.Note(s_tag + " " + cls + " presence present=" + p.Present + " known=" + p.Known + " dead=" + p.Dead + " owner=" + p.Owner + " loaded=" + p.PlaceholderLoaded + " partnerReady=" + Part[step + 1].Ready + " pos=" + p.Position + " own=" + Own.Position + " " + EnemySync.DebugAi(chosen.Key));
                if (!ready) yield break;
                if (NetPlay.IsHost) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                yield return Barrier(h, step + 2, chosen.Key, true);
                if (!Has(step + 2)) yield break;
                bool mirrored = Array.IndexOf(Families, cls) < 9, mirrorSeen = p.Mirrored, engaged = false;
                float travel = 0f, nearest = float.PositiveInfinity;
                Vector3 previous = p.Position; HashSet<string> states = new HashSet<string>();
                for (int i = 0; i < 80; i++)
                {
                    p = EnemySync.Inspect(chosen.Key); engaged |= EnemySync.Engaged(chosen.Key);
                    if (NetPlay.IsHost && i % 20 == 0) h.Note(s_tag + " " + cls + " ai " + EnemySync.DebugAi(chosen.Key));
                    states.Add(EnemySync.LiveStateName(chosen.Key));
                    if (p.Present)
                    {
                        mirrorSeen &= p.Mirrored;
                        travel += Vector3.Distance(previous, p.Position); previous = p.Position;
                        nearest = Mathf.Min(nearest, Vector3.Distance(p.Position, NetPlay.IsHost ? NetPlay.LatestRemotePos : Own.Position));
                    }
                    yield return Wait(0.1f);
                }
                h.Note(s_tag + " " + cls + " engaged=" + engaged + " travel=" + F(travel) + " minGuestDistance=" + F(nearest)
                    + " states=" + string.Join(",", new List<string>(states).ToArray()));
                if (NetPlay.IsHost) h.Check(engaged, s_tag + " " + cls + " owner AI engages the guest");
                else if (mirrored)
                {
                    bool stationary = cls == "MortarWormEnemy" || cls == "FloatingRockTurretEnemy"
                        || cls == "FloatingRockLaserEnemy" || cls == "ShootingSpider";
                    h.Check(mirrorSeen && (travel > 1f || stationary), s_tag + " " + cls + " guest mirrored and travels >1 or stationary");
                }
                Vector3 final = p.Position;
                yield return Barrier(h, step + 3, chosen.Key, true);
                if (!Has(step + 3)) yield break;
                if (!NetPlay.IsHost && mirrored) h.Check(Vector3.Distance(final, Part[step + 3].Position) < 5f, s_tag + " " + cls + " final peer positions differ <5");
                // Suicidal families may have respawned during the sample; kill a live instance on both peers.
                yield return FamilyPresence(chosen);
                p = EnemySync.Inspect(chosen.Key);
                if (NetPlay.IsHost && p.Present) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                int deaths = EnemySync.DeathEffects; bool hasDeathAction = false;
                foreach (RespawningPlaceholder placeholder in RespawningPlaceholder.All)
                    if (placeholder != null && Vector3.Distance(placeholder.Position, chosen.Origin) < 0.01f
                        && placeholder.CurrentEntity != null && placeholder.CurrentEntity.DamageReciever != null)
                        hasDeathAction = placeholder.CurrentEntity.DamageReciever.DeathAction != null;
                yield return Barrier(h, step + 4, chosen.Key, true);
                if (!Has(step + 4) || !Part[step + 4].Ready || !p.Present) yield break;
                if (!NetPlay.IsHost) EnemySync.TestAttack(chosen.Key, 1000f, false);
                float end = Now + 2f;
                while (Now < end && EnemySync.Inspect(chosen.Key).Present) yield return null;
                h.Check(!EnemySync.Inspect(chosen.Key).Present, s_tag + " " + cls + " guest kill removed entity within 2s");
                if (!NetPlay.IsHost)
                {
                    if (hasDeathAction) h.Check(EnemySync.DeathEffects == deaths + 1, s_tag + " " + cls + " puppet death effect exactly once");
                    else h.Note(s_tag + " " + cls + " has no DeathAction");
                }
                yield return Barrier(h, step + 5, chosen.Key, true);
            }
            finally
            {
                if (!string.IsNullOrEmpty(chosen.Key)) EnemySync.EndTestEnemy(chosen.Key);
                if (held && Own != null) { Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved); }
            }
        }
        static IEnumerator EveryFamily(TestHarness h, Dictionary<string, Packet> found)
        {
            for (int i = 0; i < Families.Length; i++) yield return Family(h, Families[i], found, 100 + i * 10);
        }
        public static IEnumerator Run(TestHarness h)
        {
            s_partnerSteps.Clear(); NetPlay.On(MsgBarrier, OnBarrier);
            h.Note(s_tag + " enemy sync: " + (NetPlay.IsHost ? "host" : "guest"));
            if (Own == null || P2Context.P2 == null) { h.Check(false, s_tag + " needs both Oris"); yield break; }
            NetTestHarness.BotLX = 0f; NetTestHarness.BotButtons = 0;
            s_offset = 0; s_tag = "E5";
            yield return Jumper(h); yield return DamageOnce(h); yield return Death(h); yield return Hard(h);
            // E6 (every enemy family, ~5 min with E6L) is opt-in: ORICOOP_NET_E6=1, with ORICOOP_HARNESS_SEC raised (default suite budget 420 s).
            bool e6 = Environment.GetEnvironmentVariable("ORICOOP_NET_E6") == "1";
            Dictionary<string, Packet> families = new Dictionary<string, Packet>(); s_tag = "E6";
            if (e6) { yield return SweepFamilies(h, families); yield return EveryFamily(h, families); }
            // Same scenarios at 200 ms RTT (100 ms each way), the playtest relay's latency.
            s_offset = 1000; s_tag = "E5L"; NetPlay.SetSim(100, 0, 0f, 0f);
            try { yield return Wait(1f); yield return Jumper(h); yield return DamageOnce(h); yield return Death(h);
                if (e6) { s_tag = "E6L"; yield return EveryFamily(h, families); } }
            finally { NetPlay.SetSim(0, 0, 0f, 0f); s_offset = 0; s_tag = "E5"; }
        }
    }
}