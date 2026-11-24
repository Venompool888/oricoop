using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using UnityEngine;
using OriCoop.Authority;
using OriCoop.Enemies;

namespace OriCoop
{
    public static partial class EnemySyncTest
    {
        const byte MsgBarrier = 68;
        sealed class Packet
        {
            public string Key; public Vector3 Origin, Position; public float Health;
            public DifficultyMode Difficulty; public bool Ready;
            public int Seat, OwnerSeat; public uint Epoch; public bool Owner, Dead;
            public float Tick, Angle;
            public EnemyTickPosition[] Samples = new EnemyTickPosition[0];
            public string Extra = "";   // T-195: the Swarm family report (SwarmReport)
        }
        static string s_comparisonKey;
        static uint s_comparisonEpoch;
        static readonly List<EnemyTickPosition> s_ownerPositions = new List<EnemyTickPosition>();
        static readonly Dictionary<int, Packet> s_partnerSteps = new Dictionary<int, Packet>();
        static readonly Dictionary<int, Packet> s_localSteps = new Dictionary<int, Packet>();
        static Packet LocalReport(int step) { return s_localSteps[step + s_offset]; }
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
            p.Seat = r.ReadInt32(); p.Owner = r.ReadBoolean(); p.OwnerSeat = r.ReadInt32(); p.Epoch = r.ReadUInt32();
            p.Dead = r.ReadBoolean();
            p.Tick = r.ReadSingle(); int count = r.ReadUInt16();
            if (count > 256) throw new InvalidDataException("E5 owner comparison history too large");
            p.Samples = new EnemyTickPosition[count];
            for (int i = 0; i < count; i++) p.Samples[i] = new EnemyTickPosition(r.ReadSingle(),
                new EnemyVector(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            p.Angle = r.ReadSingle(); p.Extra = r.ReadString();
        }
        static IEnumerator Wait(float seconds) { float end = Now + seconds; while (Now < end) yield return null; }
        static bool FamilyAhead(int step)
        {
            foreach (int received in s_partnerSteps.Keys)
                if (received > step + s_offset && received >= 100 + s_offset && received < 100 + Families.Length * 10 + s_offset) return true;
            return false;
        }
        static IEnumerator Barrier(TestHarness h, int step, string key, bool family = false, float timeout = 30f, string extra = null)
        {
            CaptureOwnerPosition(key);
            EnemySync.Probe p = EnemySync.Inspect(key); Vector3 origin = s_barrierAt.HasValue ? s_barrierAt.Value : p.Origin;
            Packet report = new Packet { Key = key ?? "", Owner = p.Owner, Dead = p.Dead, Position = p.Position,
                Ready = p.Present && (family || p.Mirrored) && p.Known && !p.Dead && ExpectedRole(p),
                Seat = Players.LocalSeat, OwnerSeat = p.OwnerSeat, Epoch = p.Epoch,
                Tick = p.Owner ? NetPlay.LocalTick : p.RenderTick, Angle = p.Angle, Extra = extra ?? "",
                Samples = NetRoute.ServerMode && key == s_comparisonKey && p.Owner && p.Epoch == s_comparisonEpoch
                    ? s_ownerPositions.ToArray() : new EnemyTickPosition[0] };
            s_localSteps[step + s_offset] = report;
            NetPlay.Send(MsgBarrier, true, delegate(BinaryWriter w) {
                w.Write(step + s_offset); w.Write(key ?? "");
                w.Write(origin.x); w.Write(origin.y); w.Write(origin.z);
                w.Write(p.Position.x); w.Write(p.Position.y); w.Write(p.Position.z);
                w.Write(p.Health); w.Write((int)DifficultyController.Instance.Difficulty);
                w.Write(p.Present && (family || p.Mirrored) && p.Known && !p.Dead && ExpectedRole(p));
                w.Write(Players.LocalSeat); w.Write(p.Owner); w.Write(p.OwnerSeat); w.Write(p.Epoch);
                w.Write(p.Dead);
                w.Write(report.Tick); w.Write((ushort)report.Samples.Length);
                foreach (EnemyTickPosition sample in report.Samples)
                { w.Write(sample.Tick); w.Write(sample.Position.X); w.Write(sample.Position.Y); w.Write(sample.Position.Z); }
                w.Write(p.Angle); w.Write(extra ?? "");
            });
            float end = Now + timeout;
            while (NetPlay.Connected && !Has(step) && Now < end && (!family || !FamilyAhead(step)))
            { CaptureOwnerPosition(key); yield return null; }
            h.Check(Has(step), s_tag + " barrier " + step + " reached by both");
        }
        static void CaptureOwnerPosition(string key)
        {
            if (!NetRoute.ServerMode || key != s_comparisonKey) return;
            EnemySync.Probe p = EnemySync.Inspect(key);
            if (!p.Owner || !p.Present) return;
            if (s_comparisonEpoch != p.Epoch) { s_ownerPositions.Clear(); s_comparisonEpoch = p.Epoch; }
            float tick = NetPlay.LocalTick;
            if (s_ownerPositions.Count != 0 && s_ownerPositions[s_ownerPositions.Count - 1].Tick >= tick) return;
            s_ownerPositions.Add(new EnemyTickPosition(tick, new EnemyVector(p.Position.x, p.Position.y, p.Position.z)));
            if (s_ownerPositions.Count > 256) s_ownerPositions.RemoveAt(0);
        }
        static IEnumerator CompareJumper(TestHarness h, Packet chosen, Vector3 final)
        {
            if (!NetRoute.ServerMode)
            {
                yield return Barrier(h, 13, chosen.Key);
                if (Has(13)) h.Check(Agreement(chosen.Key, 13) && Vector3.Distance(final, Part[13].Position) < 5f,
                    s_tag + "a peers agree on owner/epoch and positions differ <5");
                yield break;
            }
            float end = Now + 6f;
            for (int attempt = 0; attempt < 12 && Now < end; attempt++)
            {
                // Unique rounds keep both peers deciding from the same immutable reports.
                int step = 13 + attempt * 10000;
                yield return Barrier(h, step, chosen.Key, false, Mathf.Min(2f, end - Now));
                if (!Has(step)) yield break;
                Packet local = LocalReport(step), remote = Part[step];
                bool agreed = Agreement(chosen.Key, step);
                Packet owner = local.Owner ? local : remote, mirror = local.Owner ? remote : local;
                EnemyVector expected = new EnemyVector();
                bool covered = agreed && EnemyOwnership.TryPosition(owner.Samples, mirror.Tick, out expected);
                float error = float.PositiveInfinity;
                if (covered)
                {
                    error = Vector3.Distance(new Vector3(expected.X, expected.Y, expected.Z), mirror.Position);
                }
                h.Note(s_tag + "a compare round=" + attempt + " owner=" + local.OwnerSeat + " epoch=" + local.Epoch
                    + " peerOwner=" + remote.OwnerSeat + " peerEpoch=" + remote.Epoch + " mirrorTick=" + F(mirror.Tick)
                    + " covered=" + covered + " matchedError=" + F(error));
                if (agreed && local.Ready && remote.Ready && covered && error < 5f)
                {
                    h.Check(true, s_tag + "a peers agree on owner/epoch and positions differ <5 at matched owner tick");
                    yield break;
                }
                if (Now < end) yield return Wait(Mathf.Min(0.15f, end - Now));
            }
            h.Check(false, s_tag + "a owner/epoch agreement and matched positions did not converge within 6s");
        }
        static bool ExpectedRole(EnemySync.Probe p)
        { return EnemyOwnership.ExpectedRole(NetRoute.ServerMode, NetPlay.IsHost, Players.LocalSeat, p.Owner, p.OwnerSeat); }
        static bool OwnerHere(string key)
        { return NetRoute.ServerMode ? EnemySync.Inspect(key).Owner : NetPlay.IsHost; }
        static bool OwnerHere(Packet chosen)
        { return NetRoute.ServerMode ? chosen.OwnerSeat == Players.LocalSeat : NetPlay.IsHost; }
        static bool Agreement(string key, int step)
        {
            Packet local = LocalReport(step), partner = Part[step];
            return local.Key == key && partner.Key == key && EnemyOwnership.PeerAgreement(NetRoute.ServerMode, NetPlay.IsHost,
                local.Seat, local.Owner, local.OwnerSeat, local.Epoch, partner.Seat, partner.Owner, partner.OwnerSeat, partner.Epoch);
        }
        static bool SameGrant(Packet chosen, EnemySync.Probe p)
        { return !NetRoute.ServerMode || (p.OwnerSeat == chosen.OwnerSeat && p.Epoch == chosen.Epoch); }
        static bool BothSameGrant(Packet chosen, int step)
        { return !NetRoute.ServerMode || (LocalReport(step).OwnerSeat == chosen.OwnerSeat && LocalReport(step).Epoch == chosen.Epoch
            && Part[step].OwnerSeat == chosen.OwnerSeat && Part[step].Epoch == chosen.Epoch); }
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
            chosen.Ready = LocalReport(step).Ready && Has(step) && Part[step].Ready && Agreement(chosen.Key, step);
            chosen.OwnerSeat = LocalReport(step).OwnerSeat; chosen.Epoch = LocalReport(step).Epoch;
            h.Check(chosen.Ready, s_tag + (NetRoute.ServerMode
                ? " mirrored enemy present; exactly one owner and matching owner/epoch"
                : " mirrored enemy present on both peers with host authority"));
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
                float side = NetRoute.ServerMode ? (NetPlay.IsHost ? -6f : 6f) : (NetPlay.IsHost ? -19.5f : 1.5f);
                yield return Ready(h, chosen, 11, side);
                if (!chosen.Ready) yield break;
                bool owner = OwnerHere(chosen);
                s_comparisonKey = chosen.Key; s_comparisonEpoch = 0; s_ownerPositions.Clear();
                if (owner && SameGrant(chosen, EnemySync.Inspect(chosen.Key))) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                yield return Barrier(h, 12, chosen.Key);
                if (!Has(12)) yield break;
                bool stable = BothSameGrant(chosen, 12) && Agreement(chosen.Key, 12);
                if (stable) yield return JumperEffects(h, chosen, owner);
                HashSet<string> states = new HashSet<string>(); float travel = 0f;
                Vector3 previous = EnemySync.Inspect(chosen.Key).Position;
                for (int i = 0; i < 60; i++)
                {
                    EnemySync.Probe p = EnemySync.Inspect(chosen.Key); states.Add(p.StateName);
                    CaptureOwnerPosition(chosen.Key);
                    stable &= SameGrant(chosen, p);
                    if (i % 20 == 0) h.Note(s_tag + "a ai " + EnemySync.DebugAi(chosen.Key));
                    if (p.Present) { travel += Vector3.Distance(previous, p.Position); previous = p.Position; }
                    yield return Wait(0.1f);
                }
                bool active = false;
                foreach (string state in states)
                    if (state != null && (state.IndexOf("JumperEnemyChargingState", StringComparison.Ordinal) >= 0
                        || state.IndexOf("JumperEnemyFallState", StringComparison.Ordinal) >= 0)) active = true;
                h.Note(s_tag + "a travel=" + F(travel) + " states=" + string.Join(",", new List<string>(states).ToArray()));
                if (owner) h.Check(stable && active && travel > 1.5f, s_tag + "a owner AI simulates and travels >1.5");
                else h.Check(stable && EnemySync.Inspect(chosen.Key).Mirrored && travel > 1.5f, s_tag + "a mirror jumper travels >1.5");
                Vector3 final = EnemySync.Inspect(chosen.Key).Position;
                yield return CompareJumper(h, chosen, final);
            }
            finally
            {
                s_comparisonKey = null; s_ownerPositions.Clear();
                if (!string.IsNullOrEmpty(chosen.Key)) EnemySync.EndTestEnemy(chosen.Key);
                Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved);
            }
        }
        static IEnumerator JumperEffects(TestHarness h, Packet chosen, bool owner)
        {
            Vector3 at = chosen.Origin;
            if (owner)
            {
                int sent = EnemySync.FxSent;
                EnemySync.TestEmitJumperEffects(chosen.Key, at);
                h.Check(EnemySync.FxSent == sent + 2, s_tag + "a owner sent both native frog effects");
            }
            else
            {
                const string stomp = "jumperEnemyStompEffect", land = "jumperEnemyLandEffect";
                bool stomped = false, landed = false;
                float end = Now + 2f;
                while (Now < end && !(stomped && landed))
                {
                    stomped |= EnemySync.TestHasEffect(stomp, at); landed |= EnemySync.TestHasEffect(land, at);
                    yield return null;
                }
                h.Check(stomped, s_tag + "a mirror spawned " + stomp);
                h.Check(landed, s_tag + "a mirror spawned " + land);
            }
            yield return Barrier(h, 14, chosen.Key);
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
                bool owner = OwnerHere(chosen);
                if (owner && SameGrant(chosen, EnemySync.Inspect(chosen.Key))) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                yield return Wait(0.5f); yield return Barrier(h, 22, chosen.Key);
                if (!Has(22)) yield break;
                bool agreed = BothSameGrant(chosen, 22) && Agreement(chosen.Key, 22);
                h.Check(agreed, s_tag + "b peers agree on one owner/epoch before damage");
                if (!agreed) yield break;
                float baseline = owner ? EnemySync.Inspect(chosen.Key).Health : Part[22].Health;
                if (owner) EnemySync.TestAttack(chosen.Key, 2f, false);
                yield return Wait(1f); float afterHost = EnemySync.Inspect(chosen.Key).Health;
                yield return Barrier(h, 23, chosen.Key);
                if (!owner) EnemySync.TestAttack(chosen.Key, 3f, false);
                yield return Wait(1f); float afterGuest = EnemySync.Inspect(chosen.Key).Health;
                h.Note(s_tag + "b baseline=" + F(baseline) + " ownerDelta=" + F(baseline - afterHost) + " mirrorDelta=" + F(afterHost - afterGuest));
                h.Check(Mathf.Abs(baseline - afterGuest - 5f) < 0.01f, s_tag + "b health dropped exactly 5 on this peer");
                yield return Barrier(h, 24, chosen.Key);
                if (Has(24)) h.Check(BothSameGrant(chosen, 24) && Agreement(chosen.Key, 24),
                    s_tag + "b peers keep one agreed owner/epoch during damage");
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
                    if (OwnerHere(chosen) && SameGrant(chosen, EnemySync.Inspect(chosen.Key))) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                    yield return Wait(0.5f);
                    int deaths = EnemySync.DeathEffects, ownerDeaths = EnemySync.OwnerDeathEffects;
                    bool owner = OwnerHere(chosen);
                    bool hasDeathAction = false;
                    if (owner)
                        foreach (RespawningPlaceholder placeholder in RespawningPlaceholder.All)
                            if (placeholder != null && Vector3.Distance(placeholder.Position, chosen.Origin) < 0.01f
                                && placeholder.CurrentEntity != null && placeholder.CurrentEntity.DamageReciever != null)
                                hasDeathAction = placeholder.CurrentEntity.DamageReciever.DeathAction != null;
                    yield return Barrier(h, step + 3, chosen.Key);
                    if (!Has(step + 3)) yield break;
                    bool agreed = BothSameGrant(chosen, step + 3) && Agreement(chosen.Key, step + 3);
                    h.Check(agreed, s_tag + "c peers agree on one owner/epoch before kill");
                    if (!agreed) yield break;
                    if ((round == 0) == (NetRoute.ServerMode ? owner : NetPlay.IsHost)) EnemySync.TestAttack(chosen.Key, 1000f, false);
                    float end = Now + 1.5f;
                    while (Now < end && (EnemySync.Inspect(chosen.Key).Present
                        || (owner ? EnemySync.OwnerDeathEffects == ownerDeaths : EnemySync.DeathEffects == deaths))) yield return null;
                    h.Check(!EnemySync.Inspect(chosen.Key).Present, s_tag + "c kill " + round + " removed entity");
                    if (!owner) h.Check(EnemySync.DeathEffects == deaths + 1, s_tag + "c kill " + round + " puppet death effect exactly once");
                    else if (!hasDeathAction) h.Note(s_tag + "c owner candidate has no DeathAction");
                    else h.Check(EnemySync.OwnerDeathEffects > ownerDeaths, s_tag + "c kill " + round + " owner death effect");
                    yield return Barrier(h, step + 4, chosen.Key);
                    if (Has(step + 4)) h.Check(Agreement(chosen.Key, step + 4),
                        s_tag + "c peers agree on one owner/epoch after kill");
                    EnemySync.EndTestEnemy(chosen.Key);
                }
            }
            finally
            {
                foreach (string key in rejected) EnemySync.EndTestEnemy(key);
                Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved);
            }
        }
        // T-195: a splitting Swarm (scene of the playtest report). The mirror kills the first one; after every split both
        // PCs must show the same pieces; the two PCs then take turns killing pieces until none is left on either PC.
        // ORICOOP_MUTATE_SWARM_LOCAL=1 (Swarms outside enemy sync, as before the fix) must fail it.
        static readonly string[] SwarmScenes = { "upperGladesSwarmIntroduction" };
        static readonly HashSet<string> s_swarmUsed = new HashSet<string>();
        sealed class PieceSample { public string Key = ""; public bool Alive, Dead, Mirrored; public float Health; public Vector3 Position; }
        static readonly System.Globalization.CultureInfo Plain = System.Globalization.CultureInfo.InvariantCulture;
        // "node:alive:dead:health:x:y:mirrored:key;" for every piece of the family this PC has heard of.
        static string SwarmReport(string root)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            foreach (string key in EnemySync.SwarmPieces(root))
            {
                EnemySync.Probe p = EnemySync.Inspect(key);
                text.Append(p.Node).Append(':').Append(p.Present && !p.Dead ? 1 : 0).Append(':').Append(p.Dead ? 1 : 0).Append(':')
                    .Append((float.IsNaN(p.Health) ? 0f : p.Health).ToString("F1", Plain)).Append(':')
                    .Append(p.Position.x.ToString("F2", Plain)).Append(':').Append(p.Position.y.ToString("F2", Plain)).Append(':')
                    .Append(p.Mirrored ? 1 : 0).Append(':').Append(key).Append(';');
            }
            return text.ToString();
        }
        static Dictionary<int, PieceSample> ParseSwarm(string report)
        {
            Dictionary<int, PieceSample> pieces = new Dictionary<int, PieceSample>();
            foreach (string entry in (report ?? "").Split(';'))
            {
                string[] f = entry.Split(new[] { ':' }, 8); if (f.Length < 8) continue;
                PieceSample p = new PieceSample(); p.Alive = f[1] == "1"; p.Dead = f[2] == "1"; p.Health = float.Parse(f[3], Plain);
                p.Position = new Vector3(float.Parse(f[4], Plain), float.Parse(f[5], Plain), 0f); p.Mirrored = f[6] == "1"; p.Key = f[7];
                pieces[int.Parse(f[0], Plain)] = p;
            }
            return pieces;
        }
        // depth 0 = every level.
        static int SwarmCount(Dictionary<int, PieceSample> pieces, int depth, bool dead)
        {
            int count = 0;
            foreach (KeyValuePair<int, PieceSample> piece in pieces)
                if ((depth == 0 || SwarmFamily.Depth(piece.Key) == depth) && (dead ? piece.Value.Dead : piece.Value.Alive)) count++;
            return count;
        }
        static IEnumerator Swarm(TestHarness h)
        {
            const int step = 70; Packet chosen = new Packet(); Vector3 saved = Own.Position;
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true;
            // The pieces drop experience; an Ori that levels up from it blasts every enemy around it (DamageType.LevelUp).
            // Those deaths sync like any other, but they would kill pieces before the two PCs compare: no blast in here.
            GameObject blast = Own.Level.OnLevelUpGameObject; Own.Level.OnLevelUpGameObject = null;
            try
            {
                string key = "";
                if (NetPlay.IsHost && Core.Scenes.Manager != null)
                    foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                    {
                        if (key != "" || Array.IndexOf(SwarmScenes, scene.Scene) < 0) continue;
                        Vector3 point = scene.PlaceholderPosition;
                        h.Note(s_tag + "s search Swarm scene=" + scene.Scene + " position=" + point);
                        float until = Now + 10f;
                        while (key == "" && Now < until)
                        {
                            EnemySync.LoadTestArea(point); Plugin.Teleport(Own, point);
                            yield return Wait(0.5f);
                            key = EnemySync.FindTestSwarm(point, s_swarmUsed);
                            if (key == "" && s_swarmUsed.Count > 0) key = EnemySync.FindTestSwarm(point, null);   // a later pass: the same family again
                        }
                    }
                yield return Barrier(h, step, key);
                if (!Has(step)) yield break;
                chosen.Key = NetPlay.IsHost ? key : Part[step].Key;
                h.Check(!string.IsNullOrEmpty(chosen.Key), s_tag + "s a Swarm is tracked by enemy sync");
                if (string.IsNullOrEmpty(chosen.Key)) yield break;
                s_swarmUsed.Add(chosen.Key);
                chosen.Origin = NetPlay.IsHost ? EnemySync.Inspect(chosen.Key).Origin : Part[step].Origin;
                float side = NetPlay.IsHost ? -6f : 6f;
                Place(chosen.Origin, side);
                yield return FamilyPresence(chosen);   // its owner brings the first one back if an earlier pass killed it
                yield return Ready(h, chosen, step + 1, side);
                if (!chosen.Ready) yield break;
                string levels = EnemySync.SwarmLevels(chosen.Key); int depths = levels == "" ? 0 : levels.Split(',').Length;
                h.Note(s_tag + "s Swarm splits into " + levels + " " + EnemySync.Describe(chosen.Key));
                h.Check(depths > 0, s_tag + "s the Swarm's pieces are named on this peer (" + levels + ")");
                if (depths == 0) yield break;
                int deaths = EnemySync.DeathEffects, total = 0, vanishing = (1 << (depths + 1)) - 1;   // the last node of the last level
                yield return Barrier(h, step + 2, chosen.Key);
                if (!Has(step + 2)) yield break;
                bool agreed = Agreement(chosen.Key, step + 2);
                h.Check(agreed, s_tag + "s peers agree on one owner/epoch before the kill");
                if (!agreed) yield break;
                // The mirror kills the first one: the split happens on the owner and must show on the mirror too.
                if (!LocalReport(step + 2).Owner) EnemySync.TestAttack(chosen.Key, 1000f, false);
                for (int depth = 1; depth <= depths; depth++)
                {
                    int expected = 1 << depth, barrier = step + 1 + depth * 2; total += expected;
                    float end = Now + 6f;
                    while (Now < end && SwarmCount(ParseSwarm(SwarmReport(chosen.Key)), depth, false) < expected) yield return null;
                    yield return Wait(0.7f);   // thrown pieces land; both PCs then sample at the same barrier
                    yield return Barrier(h, barrier, chosen.Key);
                    if (!Has(barrier)) yield break;
                    yield return Barrier(h, barrier + 1, chosen.Key, false, 30f, SwarmReport(chosen.Key));
                    if (!Has(barrier + 1)) yield break;
                    Dictionary<int, PieceSample> mine = ParseSwarm(LocalReport(barrier + 1).Extra), theirs = ParseSwarm(Part[barrier + 1].Extra);
                    bool same = true, mirrored = true; float apart = 0f, health = 0f;
                    foreach (KeyValuePair<int, PieceSample> piece in mine)
                    {
                        PieceSample other;
                        if (!theirs.TryGetValue(piece.Key, out other)) { same &= !piece.Value.Alive; continue; }
                        same &= piece.Value.Alive == other.Alive && piece.Value.Key == other.Key;
                        if (!piece.Value.Alive || !other.Alive) continue;
                        apart = Mathf.Max(apart, Vector3.Distance(piece.Value.Position, other.Position));
                        health = Mathf.Max(health, Mathf.Abs(piece.Value.Health - other.Health));
                        mirrored &= piece.Value.Mirrored && other.Mirrored;   // from the reports: by now the partner may be killing
                    }
                    foreach (KeyValuePair<int, PieceSample> piece in theirs) if (piece.Value.Alive && !mine.ContainsKey(piece.Key)) same = false;
                    int alive = SwarmCount(mine, depth, false);
                    h.Note(s_tag + "s level " + depth + " alive=" + alive + " partnerAlive=" + SwarmCount(theirs, depth, false) + " apart=" + F(apart)
                        + " owner=" + LocalReport(barrier + 1).Owner + " report=" + LocalReport(barrier + 1).Extra);
                    h.Check(!EnemySync.Inspect(chosen.Key).Present && SwarmCount(mine, 0, false) == alive, s_tag + "s level " + depth + ": the split one is gone on this peer");
                    h.Check(alive == expected, s_tag + "s level " + depth + ": " + expected + " pieces on this peer (" + alive + ")");
                    h.Check(same, s_tag + "s level " + depth + ": both peers show the same pieces");
                    h.Check(mirrored && health < 0.01f && apart < 6f, s_tag + "s level " + depth + ": pieces mirrored, same health, positions differ <6 (" + F(apart) + ")");
                    h.Check(Agreement(chosen.Key, barrier + 1), s_tag + "s level " + depth + ": peers agree on one owner/epoch");
                    // Both PCs kill: the owner the odd nodes, the mirror the even ones.
                    bool odd = LocalReport(barrier + 1).Owner != Part[barrier + 1].Owner ? LocalReport(barrier + 1).Owner : NetPlay.IsHost;
                    // A copy the mirror lost (as when it loads the scene late) comes back from the owner's next state.
                    PieceSample lost;
                    if (depth == 1 && !odd && mine.TryGetValue(SwarmFamily.Child(SwarmFamily.Root, 0), out lost))
                    {
                        int copies = EnemySync.SwarmPiecesMirrored;
                        if (!EnemySync.Inspect(lost.Key).Owner && EnemySync.TestDropPiece(lost.Key))
                        {
                            float back = Now + 3f;
                            while (Now < back && !(EnemySync.SwarmPiecesMirrored > copies && EnemySync.Inspect(lost.Key).Present)) yield return null;
                            h.Check(EnemySync.SwarmPiecesMirrored > copies && EnemySync.Inspect(lost.Key).Present,
                                s_tag + "s a copy lost on the mirror is rebuilt from the owner's next state");
                        }
                        else h.Note(s_tag + "s lost-copy check skipped: this peer owns the piece now");
                    }
                    foreach (KeyValuePair<int, PieceSample> piece in mine)
                    {
                        if (SwarmFamily.Depth(piece.Key) != depth || (piece.Key % 2 == 1) != odd || !EnemySync.Inspect(piece.Value.Key).Present) continue;
                        // The last piece is not killed: its owner loses it without a death (as after a checkpoint restore).
                        if (depth == depths && piece.Key == vanishing && EnemySync.Inspect(piece.Value.Key).Owner && EnemySync.TestDropPiece(piece.Value.Key)) continue;
                        try { EnemySync.TestAttack(piece.Value.Key, 1000f, false); }
                        catch (InvalidOperationException e) { h.Note(s_tag + "s kill skipped: " + e.Message); }
                    }
                }
                float done = Now + 6f;
                while (Now < done)
                {
                    Dictionary<int, PieceSample> now = ParseSwarm(SwarmReport(chosen.Key));
                    if (now.Count >= total && SwarmCount(now, 0, false) == 0) break;
                    yield return null;
                }
                yield return Wait(0.5f);
                yield return Barrier(h, step + 11, chosen.Key);
                if (!Has(step + 11)) yield break;
                yield return Barrier(h, step + 12, chosen.Key, false, 30f, SwarmReport(chosen.Key));
                if (!Has(step + 12)) yield break;
                Dictionary<int, PieceSample> last = ParseSwarm(LocalReport(step + 12).Extra), partner = ParseSwarm(Part[step + 12].Extra);
                h.Note(s_tag + "s end pieces=" + total + " deadHere=" + SwarmCount(last, 0, true) + " deadPartner=" + SwarmCount(partner, 0, true)
                    + " puppetDeathEffects=" + (EnemySync.DeathEffects - deaths) + " split=" + EnemySync.SwarmPiecesSpawned + " copied=" + EnemySync.SwarmPiecesMirrored);
                h.Check(SwarmCount(last, 0, false) == 0 && SwarmCount(partner, 0, false) == 0, s_tag + "s no piece left alive on either peer");
                bool ended = last.Count == total && partner.Count == total;
                foreach (KeyValuePair<int, PieceSample> piece in last)
                { PieceSample other; ended &= partner.TryGetValue(piece.Key, out other) && other.Dead == piece.Value.Dead && (piece.Value.Dead || piece.Key == vanishing); }
                h.Check(ended, s_tag + "s all " + total + " pieces ended the same way on both peers (died: " + SwarmCount(last, 0, true) + ")");
                // The last piece vanished on its owner without dying: gone on the mirror too, and not counted as a kill.
                PieceSample gone, goneThere;
                if (last.TryGetValue(vanishing, out gone) && partner.TryGetValue(vanishing, out goneThere) && !gone.Dead && !goneThere.Dead)
                    h.Check(!gone.Alive && !goneThere.Alive, s_tag + "s a piece that vanished on its owner is removed on the mirror");
                else h.Note(s_tag + "s vanish check skipped: the owner changed, the last piece was killed instead");
                h.Check(!EnemySync.Inspect(chosen.Key).Present, s_tag + "s the first Swarm stays dead on this peer");
            }
            finally
            {
                Own.Mortality.DamageReciever.IsImmortal = immortal; Own.Level.OnLevelUpGameObject = blast; Plugin.Teleport(Own, saved);
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
            "FloatingRockTurretEnemy", "FloatingRockLaserEnemy", "ShootingSpider", "JumpShootShark", "ArmouredRammingEnemy" };
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
                if (p.Present && p.Known && !p.Dead && ExpectedRole(p)) yield break;
                if (OwnerHere(chosen.Key) && p.PlaceholderLoaded && p.Known && p.Owner && !p.Present) EnemySync.EnsureSpawned(chosen.Key);
                if (Now >= loadAt) { EnemySync.LoadTestArea(chosen.Origin); loadAt = Now + 0.25f; }
                yield return null;
            }
        }
        static IEnumerator Family(TestHarness h, string cls, Dictionary<string, Packet> found, int step)
        {
            Packet chosen = new Packet(); Vector3 saved = Vector3.zero; bool immortal = false, held = false, angleHeld = false;
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
                // Only the guest moves (avoiding host checkpoint reloads). Ownership is independent in server mode.
                int deathsAtPlace = EnemySync.DeathEffects; bool suicidal = cls == "DropSlugEnemy" || cls == "KamikazeSootEnemy";
                bool placed = true; if (!NetPlay.IsHost) { EnemySync.LoadTestArea(chosen.Origin); Plugin.Teleport(Own, chosen.Origin + new Vector3(1.5f, 1f, 0)); yield return Wait(1.5f); Vector3 spot; placed = SafeSpot(chosen.Origin, out spot); if (placed) Plugin.Teleport(Own, spot); else h.Note(s_tag + " " + cls + " no safe ground beside it; staying at the spawn point"); }
                if (P2Context.P2 == null) { float back = Now + 15f; while (P2Context.P2 == null && Now < back) yield return null; }
                if (P2Context.P2 == null) { h.Note(s_tag + " " + cls + " skipped: partner puppet missing (checkpoint reload)"); yield break; }
                yield return FamilyPresence(chosen);
                yield return Barrier(h, step + 1, chosen.Key, true);
                if (!Has(step + 1)) yield break;
                EnemySync.Probe p = EnemySync.Inspect(chosen.Key);
                bool agreed = Agreement(chosen.Key, step + 1);
                chosen.OwnerSeat = LocalReport(step + 1).OwnerSeat; chosen.Epoch = LocalReport(step + 1).Epoch;
                bool owner = OwnerHere(chosen);
                bool ready = LocalReport(step + 1).Ready && Part[step + 1].Ready && agreed;
                if (suicidal && (LocalReport(step + 1).Dead || Part[step + 1].Dead))
                {
                    // It already dropped/rolled onto the guest and blew itself up (nobody attacked it): that is the attack. Before
                    // the fix it never noticed the guest (SeeSeinTrigger / AI looked only at P1, 100+ units away).
                    float deathWait = Now + 2f;
                    while (!EnemySync.Inspect(chosen.Key).Dead && Now < deathWait) yield return null;
                    if (owner) h.Check(agreed, s_tag + " " + cls + " agreed owner AI self-destructed beside a player");
                    else h.Check(agreed && EnemySync.DeathEffects > deathsAtPlace, s_tag + " " + cls + " self-destruct death burst played on the mirror");
                    yield return Barrier(h, step + 5, chosen.Key, true); yield break;
                }
                h.Check(ready, s_tag + " " + cls + (NetRoute.ServerMode
                    ? " present on both peers with exactly one agreed owner/epoch" : " present on both peers with host authority"));
                if (!ready) h.Note(s_tag + " " + cls + " presence present=" + p.Present + " known=" + p.Known + " dead=" + p.Dead
                    + " owner=" + p.Owner + " ownerSeat=" + p.OwnerSeat + " epoch=" + p.Epoch
                    + " peerOwnerSeat=" + Part[step + 1].OwnerSeat + " peerEpoch=" + Part[step + 1].Epoch
                    + " loaded=" + p.PlaceholderLoaded + " partnerReady=" + Part[step + 1].Ready + " pos=" + p.Position + " own=" + Own.Position + " " + EnemySync.DebugAi(chosen.Key));
                if (!ready) yield break;
                if (owner && SameGrant(chosen, EnemySync.Inspect(chosen.Key))) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                yield return Barrier(h, step + 2, chosen.Key, true);
                if (!Has(step + 2)) yield break;
                bool rhino = cls == "ArmouredRammingEnemy";
                bool mirrored = Array.IndexOf(Families, cls) < 9 || rhino, mirrorSeen = p.Mirrored, engaged = false, stable = true;
                float travel = 0f, nearest = float.PositiveInfinity;
                Vector3 previous = p.Position; HashSet<string> states = new HashSet<string>();
                for (int i = 0; i < 80; i++)
                {
                    p = EnemySync.Inspect(chosen.Key); engaged |= EnemySync.Engaged(chosen.Key);
                    stable &= SameGrant(chosen, p);
                    if (owner && i % 20 == 0) h.Note(s_tag + " " + cls + " ai " + EnemySync.DebugAi(chosen.Key));
                    states.Add(EnemySync.LiveStateName(chosen.Key));
                    if (p.Present)
                    {
                        mirrorSeen &= p.Mirrored;
                        travel += Vector3.Distance(previous, p.Position); previous = p.Position;
                        nearest = Mathf.Min(nearest, Vector3.Distance(p.Position, owner ? NetPlay.LatestRemotePos : Own.Position));
                    }
                    yield return Wait(0.1f);
                }
                h.Note(s_tag + " " + cls + " engaged=" + engaged + " travel=" + F(travel) + " minGuestDistance=" + F(nearest)
                    + " states=" + string.Join(",", new List<string>(states).ToArray()));
                if (owner) h.Check(stable && engaged, s_tag + " " + cls + " owner AI engages a player");
                else if (mirrored)
                {
                    bool stationary = cls == "MortarWormEnemy" || cls == "FloatingRockTurretEnemy"
                        || cls == "FloatingRockLaserEnemy" || cls == "ShootingSpider";
                    h.Check(stable && mirrorSeen && (travel > 1f || stationary), s_tag + " " + cls + " mirror travels >1 or stationary");
                }
                Vector3 final = p.Position;
                // T-027: a surface crawler (TraceGroundMovement) turns its whole body to the wall or ceiling it walks on.
                // The owner turns it a quarter turn from its current pose (as if onto a wall) and holds it until the
                // step+3 barrier, which carries each peer's body angle; the mirror must show the owner's angle.
                bool crawler = cls == "StarSlugEnemy";
                float crawlerTarget = Mathf.Repeat(EnemySync.Inspect(chosen.Key).Angle + 90f, 360f);
                if (crawler && OwnerHere(chosen.Key)) { EnemySync.TestHoldAngle(chosen.Key, crawlerTarget); angleHeld = true; }
                if (crawler) yield return Wait(1.5f);
                yield return Barrier(h, step + 3, chosen.Key, true);
                if (angleHeld) { EnemySync.TestHoldAngle(chosen.Key, null); angleHeld = false; }
                if (!Has(step + 3)) yield break;
                h.Check(Agreement(chosen.Key, step + 3), s_tag + " " + cls + " peers agree on one owner/epoch after simulation");
                if (!owner && mirrored) h.Check(Vector3.Distance(final, Part[step + 3].Position) < 5f, s_tag + " " + cls + " final peer positions differ <5");
                if (crawler) CrawlerAngle(h, cls, step + 3, crawlerTarget);
                if (rhino)
                {
                    p = EnemySync.Inspect(chosen.Key);
                    h.Check(p.Mirrored && p.ClassName == "ArmouredRammingEnemy", s_tag + " armoured ram is mirrored before armour breaks");
                    uint generation = p.Generation;
                    yield return Barrier(h, step + 6, chosen.Key, true);
                    if (!Has(step + 6)) yield break;
                    if (EnemySync.Inspect(chosen.Key).Owner) EnemySync.TestAttack(chosen.Key, 1f, false, DamageType.Stomp);
                    float changeBy = Now + 3f;
                    while (Now < changeBy && EnemySync.Inspect(chosen.Key).ClassName != "RammingEnemy") yield return null;
                    p = EnemySync.Inspect(chosen.Key);
                    h.Check(p.Present && p.Mirrored && p.ClassName == "RammingEnemy" && p.Generation > generation,
                        s_tag + " broken armour replaced the entity at the same key on this peer");
                    yield return Barrier(h, step + 7, chosen.Key, true);
                    if (!Has(step + 7)) yield break;
                }
                // Suicidal families may have respawned during the sample; kill a live instance on both peers.
                yield return FamilyPresence(chosen);
                p = EnemySync.Inspect(chosen.Key);
                owner = OwnerHere(chosen.Key);
                if (owner && p.Present) EnemySync.PrepareTestEnemy(chosen.Key, 80f);
                int deaths = EnemySync.DeathEffects; bool hasDeathAction = false;
                foreach (RespawningPlaceholder placeholder in RespawningPlaceholder.All)
                    if (placeholder != null && Vector3.Distance(placeholder.Position, chosen.Origin) < 0.01f
                        && placeholder.CurrentEntity != null && placeholder.CurrentEntity.DamageReciever != null)
                        hasDeathAction = placeholder.CurrentEntity.DamageReciever.DeathAction != null;
                yield return Barrier(h, step + 4, chosen.Key, true);
                if (!Has(step + 4) || !LocalReport(step + 4).Ready || !Part[step + 4].Ready) yield break;
                bool killAgreed = Agreement(chosen.Key, step + 4);
                h.Check(killAgreed, s_tag + " " + cls + " peers agree on one owner/epoch before kill");
                if (!killAgreed) yield break;
                owner = NetRoute.ServerMode ? LocalReport(step + 4).Owner : NetPlay.IsHost;
                if (!owner) EnemySync.TestAttack(chosen.Key, 1000f, false);
                float end = Now + 2f;
                while (Now < end && EnemySync.Inspect(chosen.Key).Present) yield return null;
                h.Check(!EnemySync.Inspect(chosen.Key).Present, s_tag + " " + cls + " mirror kill removed entity within 2s");
                if (!owner)
                {
                    if (rhino)
                    {
                        bool explosion = false;
                        foreach (DeadEnemyRagdoll corpse in UnityEngine.Object.FindObjectsOfType<DeadEnemyRagdoll>())
                            if (corpse.name.StartsWith("rammingEnemyDeath", StringComparison.Ordinal)
                                && Vector3.Distance(corpse.transform.position, p.Position) < 8f) explosion = true;
                        h.Check(explosion, s_tag + " ram's native death explosion prefab spawned on the mirror");
                    }
                    if (hasDeathAction) h.Check(EnemySync.DeathEffects == deaths + 1, s_tag + " " + cls + " puppet death effect exactly once");
                    else h.Note(s_tag + " " + cls + " has no DeathAction");
                }
                yield return Barrier(h, step + 5, chosen.Key, true);
            }
            finally
            {
                if (angleHeld) EnemySync.TestHoldAngle(chosen.Key, null);
                if (!string.IsNullOrEmpty(chosen.Key)) EnemySync.EndTestEnemy(chosen.Key);
                if (held && Own != null) { Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved); }
            }
        }
        // T-027 check at the barrier that carried both body angles (see Family).
        static void CrawlerAngle(TestHarness h, string cls, int step, float target)
        {
            Packet mine = LocalReport(step), theirs = Part[step];
            if (mine.Owner && !theirs.Owner) h.Check(Mathf.Abs(Mathf.DeltaAngle(mine.Angle, target)) < 5f, s_tag + " " + cls + " owner holds the turned angle " + F(target) + " (" + F(mine.Angle) + ")");
            else if (!mine.Owner && theirs.Owner) h.Check(Mathf.Abs(Mathf.DeltaAngle(mine.Angle, theirs.Angle)) < 10f,
                s_tag + " " + cls + " mirror body angle matches the owner's (mirror " + F(mine.Angle) + ", owner " + F(theirs.Angle) + ")");
            else h.Note(s_tag + " " + cls + " angle check skipped: owner moved during the hold");
        }
        static IEnumerator EveryFamily(TestHarness h, Dictionary<string, Packet> found)
        {
            for (int i = 0; i < Families.Length; i++) yield return Family(h, Families[i], found, 100 + i * 10);
        }
        public static IEnumerator Run(TestHarness h)
        {
            s_partnerSteps.Clear(); s_localSteps.Clear(); NetPlay.On(MsgBarrier, OnBarrier);
            h.Note(s_tag + " enemy sync: " + (NetPlay.IsHost ? "host" : "guest"));
            if (Own == null || P2Context.P2 == null) { h.Check(false, s_tag + " needs both Oris"); yield break; }
            NetTestHarness.BotLX = 0f; NetTestHarness.BotButtons = 0;
            s_offset = 0; s_tag = "E5";
            // ORICOOP_E5_ONLY=SWARM: only the Swarm scenario (T-195), with and without lag; a shortcut while working on it.
            if (Environment.GetEnvironmentVariable("ORICOOP_E5_ONLY") == "SWARM")
            {
                yield return Swarm(h);
                s_offset = 1000; s_tag = "E5L"; NetPlay.SetSim(100, 0, 0f, 0f);
                try { yield return Wait(1f); yield return Swarm(h); }
                finally { NetPlay.SetSim(0, 0, 0f, 0f); s_offset = 0; s_tag = "E5"; }
                yield break;
            }
            if (Environment.GetEnvironmentVariable("ORICOOP_E5_ONLY") == "LEFT") { yield return Leftovers(h); yield break; }   // T-208: only the leftover checks (E5k)
            yield return Jumper(h); yield return DamageOnce(h); yield return Death(h); yield return Swarm(h); yield return Leftovers(h); yield return Hard(h);
            // E6 (every enemy family, ~5 min with E6L) is opt-in: ORICOOP_NET_E6=1, with ORICOOP_HARNESS_SEC raised (default suite budget 420 s).
            bool e6 = Environment.GetEnvironmentVariable("ORICOOP_NET_E6") == "1";
            Dictionary<string, Packet> families = new Dictionary<string, Packet>(); s_tag = "E6";
            if (e6) { yield return SweepFamilies(h, families); yield return EveryFamily(h, families); }
            // Same scenarios at 200 ms RTT (100 ms each way), the playtest relay's latency.
            s_offset = 1000; s_tag = "E5L"; NetPlay.SetSim(100, 0, 0f, 0f);
            try { yield return Wait(1f); yield return Jumper(h); yield return DamageOnce(h); yield return Death(h); yield return Swarm(h);
                if (e6) { s_tag = "E6L"; yield return EveryFamily(h, families); } }
            finally { NetPlay.SetSim(0, 0, 0f, 0f); s_offset = 0; s_tag = "E5"; }
        }
    }
}
