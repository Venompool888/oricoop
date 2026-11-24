using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game;
using UnityEngine;

namespace OriCoop
{
    // T-205 investigation tool, not a test (src\run-titlejoin.ps1 -Prologue probe): two real clients play the whole
    // prologue; the host walks its playable parts with test input. Twice a second each game writes what drives the
    // prologue right now to titlejoin-prologue-probe-<role>.txt: the master timelines (time, scenes), running
    // timelines and action sequences, cutscene states, the controlled character, the collision triggers in reach.
    // docs\shared-prologue-design.md (段落表) was written from its output.
    public static partial class NativeModeMenu
    {
        partial class TitleJoinTest
        {
            // ORICOOP_TITLEJOIN_PROBE_JOIN=<seconds> (run-titlejoin -ProbeJoin): the host starts alone and the guest
            // connects once the host's story is that many seconds in: how long does a late copy take to get there?
            // With -FastForward the host reaches that point sooner (its cinematic parts only).
            static float ProbeJoin
            {
                get { float s; return float.TryParse(Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_PROBE_JOIN") ?? "", out s) ? s : 0f; }
            }

            // The way through each playable part, for the probe's driver: right, except where the story walks left
            // (the bridge at night, the walk past the ghosts, the last steps to Naru).
            const string LastScene = "spiritTreeCutscene";   // the weak Ori walks to the Spirit Tree
            static int ProbeDirection(string scene)
            { return scene == "swallowsNestBridgeNight" || scene == "swallowsNestAfterSein" || scene == "swallowsNestCaveNaruComa" ? -1 : 1; }

            static string PPath(Transform t)
            {
                List<string> parts = new List<string>();
                for (int depth = 0; t != null && depth < 7; depth++, t = t.parent) parts.Add(t.name);
                parts.Reverse();
                return string.Join("/", parts.ToArray());
            }

            static string PActions(ActionSequence s)
            {
                if (s == null) return "-";
                List<string> parts = new List<string>();
                if (s.Actions != null)
                    foreach (ActionMethod a in s.Actions) parts.Add(a == null ? "null" : a.GetType().Name + ":" + a.name);
                return PPath(s.transform) + " [" + string.Join(", ", parts.ToArray()) + "]";
            }

            static string PXY(Vector3 p) { return p.x.ToString("F1") + "," + p.y.ToString("F1"); }

            IEnumerator PrologueProbe(float limit, bool drive)
            {
                string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_out)), "titlejoin-prologue-probe-" + m_role + ".txt");
                HashSet<int> seen = new HashSet<int>();
                float t0 = Time.realtimeSinceStartup, end = t0 + limit, nextDump = 0f, evalAt = 0f, lastX = float.NaN, jumpAt = 0f, backUntil = 0f;
                int dir = 1, holds = 0, backOffs = 0; bool sawPrologue = false, ffOn = false, ready = false; string lastScene = "";
                long lastSceneAt = -1;   // the host's story step when it entered the prologue's last scene
                StringBuilder sb = new StringBuilder();
                // ORICOOP_TITLEJOIN_FF=<n>: both games run n physics steps per frame (how fast can a copy be fast-forwarded?).
                int ff; int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_FF") ?? "", out ff);
                int steps0 = SharedPrologue.TestSteps;
                try { File.WriteAllText(path, "probe " + m_role + " start utc " + Num(Utc()) + " ff " + ff + "\n"); } catch (Exception) { }
                while (Time.realtimeSinceStartup < end)
                {
                    float real = Time.realtimeSinceStartup;
                    // The driver's clock is game time (physics steps), so it presses at the same story pace when fast-forwarded.
                    float now = SharedPrologue.TestSteps * Time.fixedDeltaTime;
                    ICharacter cur = Characters.Current;
                    bool locked = GameController.Instance != null && GameController.Instance.InputLocked;
                    bool playable = cur != null && !locked;
                    // Where the late guest joins. -ProbeJoin s: the host's story is s seconds in. The deep suite: the host
                    // is 45 s of story into the LAST scene, wherever its story clock stands (its driver may have needed
                    // more or less time for the playable parts on the way).
                    bool deep = PrologueMode == "deep";
                    if (deep && lastSceneAt < 0 && SceneNow == LastScene) lastSceneAt = SharedPrologue.HostStep;
                    bool joinPoint = deep ? lastSceneAt >= 0 && SharedPrologue.HostStep >= lastSceneAt + 45 * 60 : ProbeJoin > 0f && SharedPrologue.HostStep >= ProbeJoin * 60f;
                    bool lateJoin = deep || ProbeJoin > 0f;
                    if (ff > 0)
                    {
                        // With a late join only the host is fast-forwarded, only where nothing is controlled (the driver
                        // walks at the game's own speed) and only up to the join point: it gets the host there sooner.
                        bool wanted = !lateJoin || drive && !playable && !joinPoint;
                        if (wanted)
                        {
                            // n steps per frame (the frame-time cap applies before the scale); nothing runs while a scene
                            // still loads, or the story would reach a scene change before its scene is there.
                            bool loading = Core.Scenes.Manager != null && Core.Scenes.Manager.IsLoadingScenes;
                            Time.maximumDeltaTime = 1f / 60f;
                            Time.timeScale = loading ? 0.02f : ff;
                            if (loading) holds++;
                        }
                        else if (ffOn) { Time.timeScale = 1f; Time.maximumDeltaTime = 1f / 30f; }
                        ffOn = wanted;
                    }
                    if (drive && lateJoin && !ready && joinPoint)
                    {
                        ready = true; Put("probe.ready", Utc());
                        sb.Append("READY for the late guest at story step " + SharedPrologue.HostStep + " (scene " + SceneNow + "), " + (real - t0).ToString("F1") + " s after the start\n");
                        Note("host: ready for the late guest: story " + (SharedPrologue.HostStep / 60f).ToString("F0") + " s, scene " + SceneNow + ", " + SharedPrologue.EventsLogged + " player points so far");
                    }
                    if (InPrologueState) sawPrologue = true;
                    if (sawPrologue && !InPrologueState) { sb.Append("END left the Prologue state at " + (real - t0).ToString("F1") + "\n"); break; }
                    if (!drive && Peer("probe.done") != null) break;
                    if (drive)
                    {
                        // Always on: some gates are "press a button" with no character on screen.
                        SharedPrologue.TestInput = true;
                        // Held 0.45 s of every 0.6 s: each press starts a jump as soon as the character stands, and holding
                        // it gives the full height (a short tap is a low hop: Naru with Ori on her back then cannot get out
                        // of the gap before the river, and the host stood there for minutes).
                        if (now >= jumpAt) jumpAt = now + 0.6f;
                        SharedPrologue.TestJump = jumpAt - now > 0.15f;
                        if (playable)
                        {
                            float x = cur.Position.x;
                            if (float.IsNaN(lastX)) { lastX = x; evalAt = now + (SceneNow == "spiritTreeCutscene" ? 5f : 3f); }
                            if (SceneNow == "spiritTreeCutscene")
                            {
                                // The weak Ori: its states want right, left or a button in turn; turn around when nothing moves.
                                if (now >= evalAt)
                                {
                                    if (Math.Abs(x - lastX) < 0.5f) { dir = -dir; sb.Append("DRIVE no progress at x " + x.ToString("F1") + ": direction now " + dir + "\n"); }
                                    lastX = x; evalAt = now + 5f;
                                }
                            }
                            else
                            {
                                // Every other playable part has one way through. No progress for 3 s: step back for a
                                // moment (a different length each time) and come again with a run-up.
                                dir = ProbeDirection(SceneNow);
                                if (now >= evalAt)
                                {
                                    if (Math.Abs(x - lastX) < 0.5f)
                                    {
                                        backOffs++; backUntil = now + 0.8f + 0.5f * (backOffs % 3);
                                        sb.Append("DRIVE no progress at x " + x.ToString("F1") + ": stepping back for " + (backUntil - now).ToString("F1") + " s\n");
                                    }
                                    lastX = x; evalAt = Math.Max(now, backUntil) + 3f;
                                }
                                if (now < backUntil) dir = -dir;
                            }
                            SharedPrologue.TestHorizontal = dir;
                        }
                        else { lastX = float.NaN; dir = 1; backUntil = 0f; SharedPrologue.TestHorizontal = 0f; }
                    }
                    if (real >= nextDump)
                    {
                        nextDump = real + 0.5f;
                        if (ff > 0) sb.Append("FF frames holding for a scene load so far: " + holds + ", frame " + Time.frameCount + "\n");
                        try { ProbeDump(sb, seen, real - t0, ref lastScene); File.AppendAllText(path, sb.ToString()); }
                        catch (Exception e) { try { File.AppendAllText(path, "probe error: " + e + "\n"); } catch (Exception) { } }
                        sb.Length = 0;
                    }
                    yield return null;
                }
                SharedPrologue.TestInput = false; SharedPrologue.TestHorizontal = 0f; SharedPrologue.TestJump = false;
                if (ff > 0) { Time.timeScale = 1f; Time.maximumDeltaTime = 1f / 30f; }
                sb.Append("probe " + m_role + " steps " + (SharedPrologue.TestSteps - steps0) + " = " + ((SharedPrologue.TestSteps - steps0) * Time.fixedDeltaTime).ToString("F1") + " s of game time\n");
                sb.Append("probe " + m_role + " over after " + (Time.realtimeSinceStartup - t0).ToString("F1") + " s, state " + GameStateMachine.Instance.CurrentState + ", scene " + SceneNow + "\n");
                try { File.AppendAllText(path, sb.ToString()); } catch (Exception) { }
                Check(sawPrologue, m_role + ": probe ran (" + (Time.realtimeSinceStartup - t0).ToString("F0") + " s), written to " + Path.GetFileName(path));
                if (drive) Put("probe.done", Utc());
            }

            void ProbeDump(StringBuilder sb, HashSet<int> seen, float t, ref string lastScene)
            {
                ICharacter cur = Characters.Current;
                string who = cur == null ? "none" : object.ReferenceEquals(cur, Characters.Naru) ? "Naru" : object.ReferenceEquals(cur, Characters.BabySein) ? "BabySein"
                    : object.ReferenceEquals(cur, Characters.Sein) ? "Sein" : cur.GetType().Name;
                sb.Append("T " + t.ToString("F1") + " utc " + Num(Utc()) + " state " + GameStateMachine.Instance.CurrentState + " scene " + SceneNow
                    + " cur " + who + (cur == null ? "" : "@" + PXY(cur.Position)) + " kind " + (CharacterFactory.Instance == null ? "?" : CharacterFactory.Instance.Current.ToString()) + " lock " + (GameController.Instance != null && GameController.Instance.InputLocked)
                    + " ts " + Time.timeScale.ToString("F2") + " steps " + SharedPrologue.TestSteps + " frozen " + GameController.FreezeFixedUpdate
                    + (m_role == "host" ? " story " + SharedPrologue.HostStep + " points " + SharedPrologue.EventsLogged
                        : " story " + SharedPrologue.LocalStep + " lag " + SharedPrologue.LastLag + " rate " + SharedPrologue.RateNow + " done " + SharedPrologue.EventsApplied
                            + "/" + SharedPrologue.EventsKnown + " leftout " + SharedPrologue.EventsSkipped + " catchups " + SharedPrologue.CatchUps
                            + " last " + SharedPrologue.LastCatchUpSeconds.ToString("F1") + "s") + "\n");
                if (SceneNow != lastScene)
                {
                    lastScene = SceneNow;
                    List<string> roots = new List<string>();
                    foreach (SceneRoot r in UnityEngine.Object.FindObjectsOfType<SceneRoot>()) roots.Add(r.name);
                    sb.Append("  SCENES " + string.Join(", ", roots.ToArray()) + "\n");
                }
                foreach (MasterTimelineSequence m in UnityEngine.Object.FindObjectsOfType<MasterTimelineSequence>())
                {
                    if (seen.Add(m.GetInstanceID()))
                    {
                        sb.Append("  NEW MASTER " + PPath(m.transform) + " duration " + m.Duration.ToString("F2") + " offset " + m.TimeOffset.ToString("F2") + " speed " + m.Speed.ToString("F2")
                            + " playAtStart " + m.PlayAtStart + " beforeFinishOffset " + m.BeforeFinishActionTimeOffset.ToString("F2") + "\n");
                        foreach (MasterTimelineSequence.SceneSettings s in m.Scenes)
                            sb.Append("    scene " + (s.SceneMetaData == null ? "null" : s.SceneMetaData.name) + " start " + s.StartTime.ToString("F2") + " duration " + s.Duration.ToString("F2")
                                + " crossfade " + s.CrossfadeDuration.ToString("F2") + " preload " + s.PreloadTime.ToString("F2") + "\n");
                        sb.Append("    onFinish " + PActions(m.OnFinish) + "\n    beforeFinish " + PActions(m.BeforeFinishAction) + "\n");
                        if (m.TimelineSequence != null) ProbeEntries(sb, m.TimelineSequence, "    ");
                    }
                    AnimatorDriver d = m.AnimatorDriver;
                    sb.Append("  MASTER " + m.name + " playing " + d.IsPlaying + " t " + d.CurrentTime.ToString("F2") + "/" + m.Duration.ToString("F2") + " suspended " + m.IsSuspended + "\n");
                }
                foreach (TimelineSequence s in UnityEngine.Object.FindObjectsOfType<TimelineSequence>())
                {
                    bool label = s.GetComponent<TimelineSequenceLabel>() != null;
                    if (seen.Add(s.GetInstanceID()) && label)
                    {
                        sb.Append("  NEW SCENE TIMELINE " + PPath(s.transform) + " duration " + s.Duration.ToString("F2") + " entries " + s.Entries.Count + "\n");
                        ProbeEntries(sb, s, "    ");
                    }
                    AnimatorDriver d = s.AnimatorDriver;
                    if (!d.IsPlaying) continue;
                    if (!label && seen.Add(-s.GetInstanceID()))
                    {
                        sb.Append("  NEW TIMELINE " + PPath(s.transform) + " duration " + s.Duration.ToString("F2") + " entries " + s.Entries.Count + " loop " + s.Loop + "\n");
                        ProbeEntries(sb, s, "    ");
                    }
                    sb.Append("  TIMELINE " + PPath(s.transform) + " t " + d.CurrentTime.ToString("F2") + "/" + s.Duration.ToString("F2") + "\n");
                }
                foreach (OnSceneStartRunAction o in UnityEngine.Object.FindObjectsOfType<OnSceneStartRunAction>())
                {
                    if (!seen.Add(o.GetInstanceID())) continue;
                    sb.Append("  NEW SCENESTART " + PPath(o.transform) + " once " + o.TriggerOnce + " condition " + (o.Condition == null ? "none" : o.Condition.GetType().Name + ":" + o.Condition.name)
                        + " action " + (o.ActionToRun == null ? "null" : o.ActionToRun.GetType().Name + ":" + PPath(o.ActionToRun.transform)) + "\n");
                }
                foreach (CutsceneController c in UnityEngine.Object.FindObjectsOfType<CutsceneController>())
                    sb.Append("  CUTSCENE " + PPath(c.transform) + " state " + (c.CurrentState == null ? "null" : c.CurrentState.GetType().Name + ":" + c.CurrentState.name)
                        + " t " + c.CurrentStateTime.ToString("F1") + "\n");
                foreach (ActionSequence a in UnityEngine.Object.FindObjectsOfType<ActionSequence>())
                {
                    if (!a.IsRunning) continue;
                    if (seen.Add(a.GetInstanceID()))
                    {
                        List<string> comps = new List<string>();
                        foreach (Component c in a.GetComponents<Component>()) if (c != null && !(c is Transform) && !object.ReferenceEquals(c, a)) comps.Add(c.GetType().Name);
                        if (a.transform.parent != null)
                            foreach (Component c in a.transform.parent.GetComponents<Component>()) if (c != null && !(c is Transform)) comps.Add("^" + c.GetType().Name);
                        sb.Append("  NEW ACTIONS " + PPath(a.transform) + " (" + string.Join(", ", comps.ToArray()) + ")\n");
                        if (a.Actions != null)
                            for (int n = 0; n < a.Actions.Count; n++)
                            {
                                ActionMethod m = a.Actions[n];
                                sb.Append("    " + n + " " + (m == null ? "null" : m.GetType().Name + ": " + m.name + ProbeActionDetail(m)) + "\n");
                            }
                    }
                    ActionMethod at = a.Actions != null && a.Index >= 0 && a.Index < a.Actions.Count ? a.Actions[a.Index] : null;
                    sb.Append("  ACTIONS " + PPath(a.transform) + " at " + a.Index + "/" + (a.Actions == null ? 0 : a.Actions.Count)
                        + " " + (at == null ? "null" : at.GetType().Name + ":" + at.name) + "\n");
                }
                foreach (TimedActionSequence a in UnityEngine.Object.FindObjectsOfType<TimedActionSequence>())
                    if (a.IsSequenceRunning) sb.Append("  TIMED " + PPath(a.transform) + " t " + a.SequenceCurrentTime.ToString("F2") + "\n");
                foreach (Trigger g in UnityEngine.Object.FindObjectsOfType<Trigger>())
                {
                    if (!(g is PlayerCollisionTrigger || g is PlayerCollisionStayTrigger || g is DirectionalPlayerCollisionTrigger || g is TriggerByString)) continue;
                    if (!seen.Add(g.GetInstanceID())) continue;
                    Bounds b = Utility.BoundsFromTransform(g.transform);
                    TriggerByString byString = g as TriggerByString;
                    sb.Append("  NEW TRIGGER " + g.GetType().Name + " " + PPath(g.transform) + " at " + PXY(b.center) + " size " + PXY(b.size) + " active " + g.Active
                        + " once " + g.TriggerOnce + (byString != null && byString.Data != null ? " string '" + byString.Data.String + "' on " + byString.Data.TriggerEvent : "")
                        + " action " + (g.ActionToRun == null ? "null" : g.ActionToRun.GetType().Name + ":" + PPath(g.ActionToRun.transform)) + "\n");
                }
            }

            static string ProbeActionDetail(ActionMethod m)
            {
                try
                {
                    InstantiateCharacterAction ic = m as InstantiateCharacterAction;
                    if (ic != null) return " -> " + ic.Character + " at " + (ic.Position == null ? "?" : PXY(ic.Position.position));
                    CrossFadeSceneAction cf = m as CrossFadeSceneAction;
                    if (cf != null) return " -> " + (cf.SceneMetaData == null ? "?" : cf.SceneMetaData.name) + " in " + cf.Duration.ToString("F1") + " s";
                    BaseAnimatorAction ba = m as BaseAnimatorAction;
                    if (ba != null) return " -> " + ba.Command + " " + (ba.Target == null ? "?" : PPath(ba.Target.transform));
                    WaitAction wa = m as WaitAction;
                    if (wa != null) return " -> " + wa.Duration.ToString("F2") + " s" + (wa.LastActionFinished ? ", last action done" : "")
                        + (wa.Condition == null ? "" : ", condition " + wa.Condition.GetType().Name + ":" + wa.Condition.name);
                    ActionSequence seq = m as ActionSequence;
                    if (seq != null) return " -> " + PActions(seq);
                }
                catch (Exception e) { return " (detail failed: " + e.Message + ")"; }
                return "";
            }

            static void ProbeEntries(StringBuilder sb, TimelineSequence s, string indent)
            {
                Dictionary<string, int> kinds = new Dictionary<string, int>();
                foreach (TimelineSequence.SequenceEntry e in s.Entries)
                {
                    if (e.Animator == null) continue;
                    string kind = e.Animator.GetType().Name; int n;
                    kinds[kind] = kinds.TryGetValue(kind, out n) ? n + 1 : 1;
                    // The entries with lasting effects (actions, sounds, input locks, other timelines) are listed one by one.
                    if (e.Animator is TimelineActionTrigger || e.Animator is SoundAnimator || e.Animator is InputLockAnimator || e.Animator is TimelineSequence
                        || e.Animator is InstantiateAnimator || e.Animator is GameplayToCinematicAnimator)
                    {
                        TimelineActionTrigger trigger = e.Animator as TimelineActionTrigger;
                        float duration = 0f;
                        try { duration = e.Animator.Duration; } catch (Exception) { }
                        sb.Append(indent + "entry " + kind + " " + e.Animator.name + " start " + e.StartTime.ToString("F2") + " speed " + e.Speed.ToString("F2") + " duration " + duration.ToString("F2")
                            + (trigger != null && trigger.Action != null ? " action " + trigger.Action.GetType().Name + ":" + trigger.Action.name
                                + (trigger.Action is ActionSequence ? " " + PActions((ActionSequence)trigger.Action) : "") : "") + "\n");
                    }
                }
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, int> k in kinds) parts.Add(k.Key + " x" + k.Value);
                sb.Append(indent + "entry kinds: " + string.Join(", ", parts.ToArray()) + "\n");
            }
        }
    }
}
