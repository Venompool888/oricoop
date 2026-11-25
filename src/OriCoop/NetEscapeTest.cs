using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using UnityEngine;

using OriCoop.Rules;

namespace OriCoop
{
    // T-169 escape rules in the real Ginso escape (its own rising water, its own end trigger). Expected behaviour comes
    // from the ticket (docs\tickets\T-169.md, owner's #15 wording), not from the code:
    //   2P phase ESC (run-nettest.ps1, after CUT):
    //     ESC0 both in ginsoTreeWaterRisingBtm with the escape's water switched on: the escape is running on both PCs.
    //     ESC1 the guest dies in the rising water -> downed; nobody's escape restarts (no checkpoint reload anywhere).
    //     ESC2 the guest mashes Y -> back beside the host (the only teammate) with full health; the ring took >= 0.5 s.
    //     ESC4 both die in the water -> the escape restarts (one checkpoint reload on both PCs).
    //     ESC3 the host is downed in the water, the guest reaches the escape's end trigger -> the host is revived and
    //        brought to the end too; the game's own end ran on both PCs (WaterPurified).
    //   3P (net-3p suites, Run3P): P3 dies in the water and mashes -> comes back beside P1 (alive, in the escape, far
    //     away), never beside P2 (alive and nearer, but outside the escape); nothing restarts.
    // Planted bugs: ORICOOP_MUTATE_ESCAPE_NO_TEAM (ESC3 fails), ORICOOP_MUTATE_ESCAPE_MASH_NEAREST (3P fails).
    public static class NetEscapeTest
    {
        const byte MsgBarrier = 93;   // docs/net-msg-ids.md
        const string Btm = "ginsoTreeWaterRisingBtm", Mid = "ginsoTreeWaterRisingMid", End = "ginsoTreeWaterRisingEnd",
            Heart = "ginsoTreeResurrection", Background = "ginsoTreeWaterRisingBackground", EndTrigger = "ginsoTreeWaterRisingEnd/*exit/trigger";
        // Spots where an Ori stands in each scene (the in-game probe's placements, T-169 ticket).
        static readonly Vector3 BtmSpot = new Vector3(522.5f, 641f, 0f), MidSpot = new Vector3(522.5f, 753f, 0f), HeartSpot = new Vector3(521.4f, 560.5f, 0f);

        sealed class Report { public float A, B, C; public int I; }
        static readonly Dictionary<int, Dictionary<int, Report>> s_reports = new Dictionary<int, Dictionary<int, Report>>();
        static RisingWater s_water;
        static WorldEvents s_event; static int s_eventBefore = int.MinValue; static int s_reactivated;
        static float s_waterY;
        static bool s_registered;

        static SeinCharacter Own { get { return P2Context.P1 ?? Characters.Sein; } }
        static int Seat { get { return Players.LocalSeat; } }
        static int Clients { get { int n; return int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_NET_CLIENTS"), out n) ? n : 2; } }
        static string F(float f) { return f.ToString("F2"); }
        static string Role { get { return NetPlay.IsHost ? "host" : "guest P" + Seat; } }

        static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            NetPlay.On(MsgBarrier, OnBarrier);
            NetPlay.Forward(MsgBarrier);
        }

        static void OnBarrier(BinaryReader r)
        {
            int step = r.ReadInt32(), seat = r.ReadInt32();
            Report p = new Report { A = r.ReadSingle(), B = r.ReadSingle(), C = r.ReadSingle(), I = r.ReadInt32() };
            Store(step, seat, p);
        }

        static void Store(int step, int seat, Report p)
        {
            Dictionary<int, Report> round;
            if (!s_reports.TryGetValue(step, out round)) s_reports[step] = round = new Dictionary<int, Report>();
            round[seat] = p;
        }

        static Report Of(int step, int seat)
        {
            Dictionary<int, Report> round; Report p;
            return s_reports.TryGetValue(step, out round) && round.TryGetValue(seat, out p) ? p : null;
        }

        static IEnumerator Barrier(TestHarness h, int step, Vector3 v = default(Vector3), int i = 0)
        {
            Report mine = new Report { A = v.x, B = v.y, C = v.z, I = i };
            Store(step, Seat, mine);
            int seat = Seat;
            NetPlay.Send(MsgBarrier, true, w => { w.Write(step); w.Write(seat); w.Write(mine.A); w.Write(mine.B); w.Write(mine.C); w.Write(mine.I); });
            float end = Time.realtimeSinceStartup + 45f;
            while (NetPlay.Connected && Time.realtimeSinceStartup < end)
            {
                Dictionary<int, Report> round;
                if (s_reports.TryGetValue(step, out round) && round.Count >= Clients) break;
                HoldWater();
                yield return null;
            }
            Dictionary<int, Report> got;
            h.Check(s_reports.TryGetValue(step, out got) && got.Count >= Clients, "ESC barrier " + step + " reached by all " + Clients);
        }

        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) { HoldWater(); yield return null; }
        }

        static void Place(Vector3 p)
        {
            if (Own == null) return;
            Plugin.Teleport(Own, new Vector3(p.x, p.y, Own.Position.z));
            SessionRules.PositionChanged();
        }

        static bool Immortal { set { if (Own != null) Own.Mortality.DamageReciever.IsImmortal = value; } }

        // Teleport into a scene and wait until it (and the escape's water scene) is loaded.
        static IEnumerator Load(TestHarness h, string scene, Vector3 at)
        {
            RuntimeSceneMetaData meta = Core.Scenes.Manager.GetSceneInformation(scene);
            if (meta == null) { h.Check(false, "ESC scene " + scene + " exists"); yield break; }
            Place(at);
            Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(meta.SceneBounds, true, false, false);
            float start = Time.realtimeSinceStartup, lastPlace = start;
            while (Time.realtimeSinceStartup < start + 25f && !(SceneLoaded(scene) && Water() != null))
            {
                if (Time.realtimeSinceStartup - lastPlace > 5f) { lastPlace = Time.realtimeSinceStartup; Place(at); Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(meta.SceneBounds, true, false, false); }
                yield return null;
            }
            yield return Wait(1f);
            Place(at);
            yield return Wait(0.5f);
        }

        static bool SceneLoaded(string scene)
        {
            foreach (SceneRoot root in UnityEngine.Object.FindObjectsOfType<SceneRoot>())
                if (root != null && root.MetaData != null && root.MetaData.SceneName == scene) return true;
            return false;
        }

        // The escape's own rising water (inactive until the escape's story switches it on).
        static RisingWater Water()
        {
            foreach (RisingWater w in Resources.FindObjectsOfTypeAll<RisingWater>())
                if (w != null && w.transform.root != null && w.transform.root.name == Background && w.gameObject.scene.IsValid()) return w;
            return null;
        }

        // Switch the real water on (as its story does) and hold it still at y, so the test decides who drowns when.
        static void StartWater(TestHarness h, float y)
        {
            s_water = Water();
            if (s_water == null) { h.Note("ESC " + Role + ": no escape water in this PC's loaded scenes"); return; }
            // The escape's story switches the water on through the Ginso world event (the water scene's own
            // *triggerWaterSequence sets it, T-169 probe); set that event the same way, then make sure it is on.
            SetWorldEventAction start = null;
            foreach (SetWorldEventAction a in Resources.FindObjectsOfTypeAll<SetWorldEventAction>())
                if (a != null && a.transform.root != null && a.transform.root.name == Background && a.gameObject.scene.IsValid() && a.transform.parent != null
                    && a.transform.parent.parent != null && a.transform.parent.parent.name == "*triggerWaterSequence") start = a;
            if (start != null && start.WorldEvents != null)
            {
                if (s_eventBefore == int.MinValue) s_eventBefore = Game.World.Events.Find(start.WorldEvents).Value;
                s_event = start.WorldEvents;
                start.Perform(null);
                h.Note("ESC " + Role + ": Ginso world event " + start.WorldEvents.name + " -> " + start.State + " (was " + s_eventBefore + ")");
            }
            else h.Note("ESC " + Role + ": Ginso water-sequence world event action not found");
            for (Transform t = s_water.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            s_water.enabled = true;
            object instance = HarmonyLib.AccessTools.Field(typeof(RisingWater), "m_instance").GetValue(null);
            h.Note("ESC " + Role + ": water on: available=" + RisingWater.Available + " instance is it=" + ReferenceEquals(instance, s_water)
                + " active=" + s_water.gameObject.activeInHierarchy + " enabled=" + s_water.enabled + " at " + s_water.transform.position);
            AnimationCurve flat = AnimationCurve.Linear(0f, 0f, 100f, 0f);
            s_water.SpeedOverDistance = flat; s_water.EasySpeedOverDistance = flat;
            s_water.AcceleratedSpeed = 0f; s_water.Speed = 0f;
            s_waterY = y;
            HoldWater(true);
            foreach (Collider c in s_water.GetComponentsInChildren<Collider>(true))
                h.Note("ESC " + Role + ": water collider " + c.name + " bounds " + c.bounds + " trigger=" + c.isTrigger);
        }

        static void HoldWater(bool force = false)
        {
            if (s_water == null) return;
            if (!s_water.gameObject.activeInHierarchy)
            {
                s_reactivated++;
                for (Transform t = s_water.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            }
            s_water.Speed = 0f; s_water.IsSuspended = false;   // the escape's own triggers set a speed when an Ori passes
            if (NetPlay.IsHost || force)   // a guest's water follows the host's height (WorldObjSync)
                s_water.transform.position = new Vector3(s_water.transform.position.x, s_waterY, s_water.transform.position.z);
        }

        static void StopWater()
        {
            if (s_water != null) s_water.gameObject.SetActive(false);
            s_water = null;
            if (s_event != null && s_eventBefore != int.MinValue) Game.World.Events.Find(s_event).Value = s_eventBefore;
            s_event = null; s_eventBefore = int.MinValue;
        }

        // Into the water, well below its surface.
        static IEnumerator Drown(TestHarness h, string label, bool lastStanding = false)
        {
            Immortal = false;
            Own.Mortality.Health.RestoreAllHealth();
            float end = Time.realtimeSinceStartup + 6f;
            int restores = TestHarness.RestoreCheckpointCalls;
            while (Own != null && !Revive.IsDowned(Own) && TestHarness.RestoreCheckpointCalls == restores && Time.realtimeSinceStartup < end)
            {
                if (Own.Position.y > s_waterY - 4f) Place(new Vector3(522.5f, s_waterY - 8f, 0f));
                HoldWater();
                yield return null;
            }
            if (lastStanding) { h.Check(TestHarness.RestoreCheckpointCalls > restores || Revive.IsDowned(Own), label + ": died in the rising water"); yield break; }
            h.Check(Own != null && Revive.IsDowned(Own), label + ": died in the rising water and is downed, not dead (at " + (Own == null ? "?" : Own.Position.ToString()) + ", water y " + F(s_waterY) + ")");
        }

        static IEnumerator WaitFor(Func<bool> done, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < end) { HoldWater(); yield return null; }
        }

        static float s_mashTook;
        static IEnumerator MashBackRoutine(TestHarness h, string label)
        {
            s_mashTook = -1f;
            float start = Time.realtimeSinceStartup;
            Revive.TestMashPresses = 1;   // one press every frame: far faster than a player, so the 0.5 s minimum shows
            try
            {
                while (Revive.IsDowned(Own) && Time.realtimeSinceStartup < start + 8f) { HoldWater(); yield return null; }
            }
            finally { Revive.TestMashPresses = 0; }
            if (!Revive.IsDowned(Own)) s_mashTook = Time.realtimeSinceStartup - start;
            h.Check(!Revive.IsDowned(Own), label + ": mashing Y brings the downed Ori back (" + F(s_mashTook) + " s, hint '" + Revive.LastMashHint + "')");
        }

        static SeinCharacter SeinOf(int seat)
        {
            if (seat == Seat) return Own;
            foreach (Puppet p in Players.Remotes) if (p.Seat == seat) return p.Sein;
            return null;
        }

        static Vector3 PositionOf(int seat)
        {
            SeinCharacter s = SeinOf(seat);
            return s == null ? new Vector3(float.NaN, float.NaN, 0f) : s.Position;
        }

        static float Dist(Vector3 a, Vector3 b) { return new Vector2(a.x - b.x, a.y - b.y).magnitude; }

        static Trigger FindEndTrigger()
        {
            foreach (PlayerCollisionTrigger t in Resources.FindObjectsOfTypeAll<PlayerCollisionTrigger>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                string path = t.transform.name;
                for (Transform p = t.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
                if (path == EndTrigger) return t;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------
        public static IEnumerator Run(TestHarness h)
        {
            Register(); s_reports.Clear();
            bool host = NetPlay.IsHost;
            h.Note("--- phase ESC: escape rules in the Ginso water escape (" + Role + ")");
            if (DifficultyController.Instance.Difficulty == DifficultyMode.OneLife) { h.Note("ESC skipped on One Life difficulty"); yield break; }
            if (Own == null || Players.Remotes.Count == 0) { h.Check(false, "ESC needs both Oris"); yield break; }
            bool immortal0 = Own.Mortality.DamageReciever.IsImmortal, purified0 = Sein.World.Events.WaterPurified;
            int restores0 = TestHarness.RestoreCheckpointCalls;
            try
            {
                // ESC0 ---------------------------------------------------------------------------------------------
                Immortal = true;
                yield return Load(h, Btm, BtmSpot + new Vector3(host ? 0f : 3f, 0f, 0f));
                yield return Barrier(h, 1);
                StartWater(h, 630f);
                yield return Barrier(h, 2);
                yield return Wait(1f);
                h.Check(s_water != null && Revive.EscapeAt(Own.Position) != null && Revive.EscapeAt(Own.Position).Id == EscapeRules.Ginso,
                    "ESC0 " + Role + ": the Ginso escape is running here (water on, inside its scenes; at " + Own.Position + "; " + Revive.EscapeDebug(Own.Position) + "; test re-activations " + s_reactivated + ")");
                Own.Mortality.Health.RestoreAllHealth();
                yield return Barrier(h, 3);

                // ESC1 ---------------------------------------------------------------------------------------------
                if (!host) yield return Drown(h, "ESC1 guest");
                yield return Barrier(h, 4);
                yield return Wait(1f);
                h.Check(TestHarness.RestoreCheckpointCalls == restores0, "ESC1 " + Role + ": one death in the water does not restart the escape (reloads "
                    + (TestHarness.RestoreCheckpointCalls - restores0) + ")");
                if (host)
                {
                    h.Check(Revive.IsDowned(SeinOf(2)), "ESC1 host: sees the guest downed");
                    h.Check(!Revive.IsDowned(Own), "ESC1 host: still alive and playing");
                    Place(BtmSpot + new Vector3(-4f, 0f, 0f));   // somewhere else than where the guest went down
                    yield return Wait(0.5f);
                }
                yield return Barrier(h, 5, Own.Position);

                // ESC2 ---------------------------------------------------------------------------------------------
                if (!host)
                {
                    yield return MashBackRoutine(h, "ESC2 guest");
                    Vector3 hostAt = new Vector3(Of(5, 1).A, Of(5, 1).B, 0f);
                    h.Check(s_mashTook >= EscapeRules.MashMinSeconds - 0.05f, "ESC2 guest: the ring takes at least 0.5 s however fast Y is mashed (" + F(s_mashTook) + " s)");
                    h.Check(Revive.LastMashTeammate == 1 && Dist(Own.Position, hostAt) < 3f, "ESC2 guest: came back beside the host, the only teammate (P"
                        + Revive.LastMashTeammate + ", " + F(Dist(Own.Position, hostAt)) + " from the host)");
                    h.Check(Own.Mortality.Health.Amount >= Own.Mortality.Health.MaxHealth - 0.01f, "ESC2 guest: full health (" + F(Own.Mortality.Health.Amount) + ")");
                    Immortal = true;
                }
                yield return Barrier(h, 6);
                if (host)
                {
                    yield return WaitFor(() => !Revive.IsDowned(SeinOf(2)), 3f);
                    h.Check(!Revive.IsDowned(SeinOf(2)) && Dist(PositionOf(2), Own.Position) < 4f, "ESC2 host: the guest is back beside me ("
                        + F(Dist(PositionOf(2), Own.Position)) + ")");
                }
                h.Check(TestHarness.RestoreCheckpointCalls == restores0, "ESC2 " + Role + ": still no escape restart");

                // ESC4: everyone dies -> restart ------------------------------------------------------------------
                yield return Barrier(h, 7);
                int restores1 = TestHarness.RestoreCheckpointCalls;
                if (!host) yield return Drown(h, "ESC4 guest");
                yield return Barrier(h, 8);
                if (host) yield return Drown(h, "ESC4 host (last one standing)", true);
                float endR = Time.realtimeSinceStartup + 30f;
                while (TestHarness.RestoreCheckpointCalls == restores1 && Time.realtimeSinceStartup < endR) yield return null;
                s_water = null;   // the reload rebuilt the scenes
                h.Check(TestHarness.RestoreCheckpointCalls == restores1 + 1, "ESC4 " + Role + ": everyone dead in the escape -> it restarts (reloads "
                    + (TestHarness.RestoreCheckpointCalls - restores1) + ")");
                endR = Time.realtimeSinceStartup + 30f;
                while ((Characters.Sein == null || !Characters.Sein.gameObject.activeInHierarchy || GameController.Instance.InputLocked) && Time.realtimeSinceStartup < endR) yield return null;
                yield return Wait(3f);
                h.Check(NetPlay.NotInGameplayReason() == null, "ESC4 " + Role + ": back in gameplay after the restart (#431; "
                    + (NetPlay.NotInGameplayReason() ?? "yes") + "; " + CoopMenu.GameplayDetail() + ")");
                h.Check(!Revive.AnyDowned, "ESC4 " + Role + ": nobody downed after the restart");
                yield return Barrier(h, 9);

                // ESC3: one reaches the end -> the team is through ------------------------------------------------
                Immortal = true;
                int restores2 = TestHarness.RestoreCheckpointCalls;
                int sent0 = Revive.EscapeFinishesSent, received0 = Revive.EscapeFinishesReceived;
                yield return Load(h, Btm, BtmSpot + new Vector3(host ? 0f : 3f, 0f, 0f));
                StartWater(h, 630f);
                yield return Barrier(h, 10);
                if (host) yield return Drown(h, "ESC3 host");
                yield return Barrier(h, 11);
                if (!host)
                {
                    yield return Load(h, End, new Vector3(527.6f, 940f, 0f));
                    Trigger end = FindEndTrigger();
                    h.Check(end != null && end.Active, "ESC3 guest: the escape's end trigger is loaded and unused here");
                    if (end != null)
                    {
                        Rect r = (Rect)HarmonyLib.AccessTools.Field(typeof(PlayerCollisionTrigger), "m_bounds").GetValue(end);
                        Place(new Vector3(r.center.x, r.center.y, 0f));
                    }
                    yield return WaitFor(() => Revive.EscapeFinishesSent > sent0, 6f);
                    h.Check(Revive.EscapeFinishesSent == sent0 + 1, "ESC3 guest: reaching the end tells the team once (sent " + (Revive.EscapeFinishesSent - sent0) + ")");
                }
                yield return Barrier(h, 12);
                if (host)
                {
                    yield return WaitFor(() => Own != null && !Revive.IsDowned(Own), 12f);
                    yield return Wait(3f);   // the game's end places the Ori (and loads its scenes) on both PCs
                    h.Check(Revive.EscapeFinishesReceived == received0 + 1, "ESC3 host: heard the guest reach the end (" + (Revive.EscapeFinishesReceived - received0) + ")");
                    h.Check(!Revive.IsDowned(Own), "ESC3 host: downed in the water, revived because the guest got through");
                    h.Check(Own.Mortality.Health.Amount >= Own.Mortality.Health.MaxHealth - 0.01f, "ESC3 host: full health after being brought (" + F(Own.Mortality.Health.Amount) + ")");
                }
                yield return WaitFor(() => Sein.World.Events.WaterPurified, 6f);
                h.Check(Sein.World.Events.WaterPurified, "ESC3 " + Role + ": the escape's end ran on this PC (WaterPurified)");
                h.Check(TestHarness.RestoreCheckpointCalls == restores2, "ESC3 " + Role + ": getting through does not restart anything");
                // #431: the escape's end is kuroMoment's MasterTimelineSequence. It hides the Ori and was still playing 20 s
                // later (story active) on both PCs; the phase then moved everyone away and both Oris stayed inactive, so F8
                // refused to open in the G checks. A player sits through it or skips it: the test skips it the game's own
                // way, only when the skip registered is that sequence's, so its OnFinish runs and hands the Ori back.
                // The skip registers when the sequence gets there, later on the PC that did not reach the end trigger (the
                // host's controller held no skip at all 5 s after WaterPurified, PR #432 round 3): skip as soon as it shows.
                float skipFrom = Time.realtimeSinceStartup, skipUntil = skipFrom + 30f;
                string endSkip = null;
                while (Time.realtimeSinceStartup < skipUntil && !(NetPlay.NotInGameplayReason() == null && !StorySync.StoryActive))
                {
                    if (endSkip == null && (endSkip = EndSkip()) != null)
                    {
                        h.Note("ESC3 " + Role + ": escape end skip after " + F(Time.realtimeSinceStartup - skipFrom) + " s: " + endSkip);
                        SkipCutsceneController.Instance.SkipCutscene();
                    }
                    yield return null;
                }
                if (endSkip == null) h.Note("ESC3 " + Role + ": no skip of the escape's end in " + F(Time.realtimeSinceStartup - skipFrom) + " s (" + SkipDebug() + ")");
                h.Check(NetPlay.NotInGameplayReason() == null, "ESC3 " + Role + ": back in gameplay after the escape's end ("
                    + (NetPlay.NotInGameplayReason() ?? "yes") + "; " + CoopMenu.GameplayDetail() + ", story " + StorySync.StoryActive + ")");
                if (!host) yield return Wait(3f);
                yield return Barrier(h, 13, Own.Position);
                if (host)
                {
                    Vector3 guestAt = new Vector3(Of(13, 2).A, Of(13, 2).B, 0f);
                    h.Check(Dist(Own.Position, guestAt) < 8f, "ESC3 host: brought to the end with the guest (" + F(Dist(Own.Position, guestAt))
                        + " apart, host " + Own.Position + ", guest " + guestAt + ", fallbacks " + Revive.EscapeFallbacks + ")");
                }
                yield return Wait(1f);
            }
            finally
            {
                StopWater();
                Revive.TestMashPresses = 0;
                if (Own != null) Own.Mortality.DamageReciever.IsImmortal = immortal0;
                Sein.World.Events.WaterPurified = purified0;   // later phases run in the test save's world
            }
        }

        // The skip SkipCutsceneController holds, when it belongs to the escape end's MasterTimelineSequence (kuroMoment):
        // "field -> sequence in root", else null. Read by reflection (the field is the game's), tolerant of its shape.
        static string EndSkip()
        {
            SkipCutsceneController c = SkipCutsceneController.Instance;
            if (c == null || !c.SkippingAvailable) return null;
            foreach (FieldInfo f in typeof(SkipCutsceneController).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(c); } catch (Exception) { continue; }
                List<object> items = new List<object>();
                if (v is IList) foreach (object o in (IList)v) items.Add(o); else items.Add(v);
                foreach (object item in items)
                {
                    Delegate d = item as Delegate;
                    Component comp = (d != null ? d.Target : item) as Component;
                    if (comp == null) continue;
                    try
                    {
                        MasterTimelineSequence seq = comp as MasterTimelineSequence;
                        if (seq == null) seq = comp.GetComponentInParent<MasterTimelineSequence>();
                        if (seq == null || seq.transform.root.name.IndexOf("kuroMoment", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        return f.Name + " -> " + seq.name + " in " + seq.transform.root.name;
                    }
                    catch (Exception) { }   // a destroyed object left in the field
                }
            }
            return null;
        }
        static string SkipDebug()
        {
            SkipCutsceneController c = SkipCutsceneController.Instance;
            if (c == null) return "no skip controller";
            string text = "available=" + c.SkippingAvailable;
            foreach (FieldInfo f in typeof(SkipCutsceneController).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(c); } catch (Exception) { continue; }
                if (v == null) continue;
                Delegate d = v as Delegate;
                Component comp = (d != null ? d.Target : v) as Component;
                string what;
                try { what = comp != null ? comp.GetType().Name + " " + comp.name + " in " + comp.transform.root.name : v.GetType().Name; }
                catch (Exception) { what = "destroyed " + v.GetType().Name; }
                text += ", " + f.Name + "=" + what;
            }
            return text;
        }

        // Stories that some PC starts in or near the escape (arriving, the water event, the heart room) make StorySync
        // pull teammates more than 12 units away to them. Wait for them to end, take the spots again, and repeat
        // until everyone stays on their own spot. A downed Ori stays where it is.
        static IEnumerator Settle(TestHarness h, int step0, string scene, Vector3 spot)
        {
            for (int round = 0; round < 5; round++)
            {
                yield return Wait(2f);
                yield return WaitFor(() => !StorySync.StoryActive, 15f);
                bool here = Revive.IsDowned(Own) || Dist(Own.Position, spot) < 3f;
                if (!here) yield return Load(h, scene, spot);
                yield return Barrier(h, step0 + round, Own.Position, here ? 1 : 0);
                bool all = true;
                for (int seat = 1; seat <= 3; seat++) { Report r = Of(step0 + round, seat); all &= r != null && r.I == 1; }
                if (all) yield break;
                h.Note("3P ESC " + Role + ": settle round " + round + ": not everyone stayed on their spot (I am " + (here ? "" : "not ") + "on mine)");
            }
        }

        // The heart room below the escape has its own stories (seeing the heart); a player standing there would start
        // one and StorySync would pull the others to it. The 3P setup only needs a place outside the escape: switch the
        // room's story triggers off while it runs.
        static readonly List<GameObject> s_heartTriggers = new List<GameObject>();
        static void HeartStories(bool on)
        {
            if (!on)
            {
                foreach (Trigger t in Resources.FindObjectsOfTypeAll<Trigger>())
                {
                    if (t == null || !t.gameObject.scene.IsValid() || !t.gameObject.activeSelf || t.transform.root == null || t.transform.root.name != Heart) continue;
                    bool heart = false;
                    for (Transform p = t.transform; p != null; p = p.parent) if (p.name == "*heartResurrection") heart = true;
                    if (heart) { t.gameObject.SetActive(false); s_heartTriggers.Add(t.gameObject); }
                }
            }
            else
            {
                foreach (GameObject g in s_heartTriggers) if (g != null) g.SetActive(true);
                s_heartTriggers.Clear();
            }
        }

        // 3P (ThreeClients): random teammate = only the eligible one.
        public static IEnumerator Run3P(TestHarness h)
        {
            Register(); s_reports.Clear();
            h.Note("--- 3P ESC: mash back beside a teammate in the escape (" + Role + ")");
            if (DifficultyController.Instance.Difficulty == DifficultyMode.OneLife) { h.Note("3P ESC skipped on One Life difficulty"); yield break; }
            bool immortal0 = Own.Mortality.DamageReciever.IsImmortal;
            int restores0 = TestHarness.RestoreCheckpointCalls;
            try
            {
                Immortal = true;
                // P1 high in the escape (Mid), P2 just outside it (the heart room below), P3 at the escape's bottom.
                Vector3 spot = Seat == 1 ? MidSpot : Seat == 2 ? HeartSpot : BtmSpot;
                string scene = Seat == 1 ? Mid : Seat == 2 ? Heart : Btm;
                if (Seat == 2) { yield return Load(h, Heart, HeartSpot + new Vector3(0f, 30f, 0f)); HeartStories(false); h.Note("3P ESC P2: heart room story triggers off: " + s_heartTriggers.Count); }
                yield return Load(h, scene, spot);
                yield return Barrier(h, 30);
                StartWater(h, 545f);
                // Arriving in the escape and switching its water on start stories on some PC, and StorySync pulls
                // teammates more than 12 units away to them: let them finish and take the spots again until all three
                // stay where they belong.
                yield return Settle(h, 40, scene, spot);
                yield return Barrier(h, 31, Own.Position);
                h.Note("3P ESC " + Role + ": at " + Own.Position + " (in the escape's scenes: " + (Revive.EscapeDebug(Own.Position).Contains("inside=True")) + ")");
                yield return Wait(1f);
                if (Seat == 3)
                {
                    h.Check(Revive.EscapeAt(Own.Position) != null, "3P ESC P3: the Ginso escape is running here");
                    Vector3 p1 = new Vector3(Of(31, 1).A, Of(31, 1).B, 0f), p2 = new Vector3(Of(31, 2).A, Of(31, 2).B, 0f);
                    h.Note("3P ESC P3: P1 " + F(Dist(p1, Own.Position)) + " away in the escape, P2 " + F(Dist(p2, Own.Position)) + " away outside it");
                    h.Check(Dist(p2, Own.Position) < Dist(p1, Own.Position), "3P ESC P3: setup: P2 (outside) is nearer than P1 (inside)");
                    yield return Drown(h, "3P ESC P3");
                }
                yield return Barrier(h, 33);
                yield return Settle(h, 50, scene, spot);
                h.Check(TestHarness.RestoreCheckpointCalls == restores0, "3P ESC " + Role + ": one death in the water restarts nothing");
                if (Seat != 3) h.Check(Revive.IsDowned(SeinOf(3)), "3P ESC " + Role + ": sees P3 downed");
                yield return Barrier(h, 34, Own.Position);
                if (Seat == 3)
                {
                    yield return MashBackRoutine(h, "3P ESC P3");
                    // P1 as this screen shows it (its puppet may still be catching up with P1's last move): wait for both to settle.
                    yield return WaitFor(() => Dist(Own.Position, PositionOf(1)) < 3f, 3f);
                    Vector3 p1 = PositionOf(1);
                    h.Check(Revive.LastMashTeammate == 1 && Dist(Own.Position, p1) < 3f, "3P ESC P3: came back beside P1, the only teammate alive in the escape, not P2 outside it (P"
                        + Revive.LastMashTeammate + ", " + F(Dist(Own.Position, p1)) + " from P1)");
                    Immortal = true;
                }
                yield return Barrier(h, 35);
                if (Seat == 1)
                {
                    yield return WaitFor(() => !Revive.IsDowned(SeinOf(3)) && Dist(PositionOf(3), Own.Position) < 4f, 3f);
                    h.Check(!Revive.IsDowned(SeinOf(3)) && Dist(PositionOf(3), Own.Position) < 4f, "3P ESC P1: P3 is back beside me (" + F(Dist(PositionOf(3), Own.Position)) + ")");
                }
                if (Seat == 2)
                {
                    // P1's and P3's puppets on this screen settle within a moment (both just moved / landed).
                    yield return WaitFor(() => !Revive.IsDowned(SeinOf(3)) && Dist(PositionOf(3), PositionOf(1)) < 4f, 4f);
                    Vector3 p1 = PositionOf(1), p3 = PositionOf(3), me = Own.Position;
                    h.Check(!Revive.IsDowned(SeinOf(3)) && Dist(p3, p1) < 4f, "3P ESC P2: P3 is back beside P1 on this screen too (P1 " + p1 + ", P3 " + p3 + ", me " + me + ")");
                    h.Note("3P ESC P2: P3 is " + F(Dist(p3, me)) + " from me (I was " + F(Dist(new Vector3(Of(34, 2).A, Of(34, 2).B, 0f), new Vector3(Of(34, 1).A, Of(34, 1).B, 0f))) + " from P1 at the mash)");
                }
                h.Check(TestHarness.RestoreCheckpointCalls == restores0, "3P ESC " + Role + ": still nothing restarted");
                yield return Barrier(h, 36);
            }
            finally
            {
                StopWater();
                HeartStories(true);
                Revive.TestMashPresses = 0;
                if (Own != null) Own.Mortality.DamageReciever.IsImmortal = immortal0;
            }
        }
    }
}
