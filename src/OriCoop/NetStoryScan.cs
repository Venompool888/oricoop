using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Game;
using UnityEngine;

namespace OriCoop
{
    public static class NetStoryScan
    {
        const byte BarrierMessage = 174;
        const int DoneStep = 900;
        static bool s_done, s_ack;
        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        static float Now { get { return Time.realtimeSinceStartup; } }
        static readonly string[] Prefixes = { "sunkenGlades", "upperGlades", "hollowGrove", "moonGrotto", "spiritTree", "ginso", "thornfelt", "westGlades" };
        static readonly string[] Labels = { "A1", "A2", "B1", "B2", "C1", "C2" };
        static readonly MoonGuid[] Targets = {
            new MoonGuid(-1494250536, 1294507390, -1910947653, -548245366),
            new MoonGuid(-1550447088, 1165415630, 1203326368, 1931362044),
            new MoonGuid(-1634915620, 1329113078, 924492718, 946701626),
            new MoonGuid(-445185682, 1196900528, -1375792966, -345924238),
            new MoonGuid(-1930755400, 1341760787, -1205985134, 742195902),
            new MoonGuid(-1290965927, 1294804351, -1762173551, 2013750032)
        };

        static void Receive(BinaryReader r)
        {
            byte kind = r.ReadByte();
            if (r.ReadInt32() != DoneStep) return;
            if (kind == 1) s_ack = true;
            else if (!NetPlay.IsHost)
            {
                s_done = true;
                NetPlay.Send(BarrierMessage, true, delegate(BinaryWriter w) { w.Write((byte)1); w.Write(DoneStep); });
            }
        }

        public static IEnumerator Run(TestHarness h)
        {
            s_done = false; s_ack = false;
            NetPlay.On(BarrierMessage, Receive);
            if (!NetPlay.IsHost)
            {
                float deadline = Now + 900f;
                while (!s_done && Now < deadline) yield return null;
                h.Check(s_done, "SCAN done");
                yield break;
            }
            SeinCharacter own = Own;
            bool immortal = own.Mortality.DamageReciever.IsImmortal;
            bool gravity = own.PlatformBehaviour.Gravity.Active;
            Vector3 speed = own.Speed;
            IEnumerator scan = Scan(h);
            try
            {
                own.Mortality.DamageReciever.IsImmortal = true;
                own.PlatformBehaviour.Gravity.Active = false;
                while (true)
                {
                    own.Speed = Vector3.zero;
                    if (!scan.MoveNext()) break;
                    own.Mortality.DamageReciever.IsImmortal = true;
                    own.PlatformBehaviour.Gravity.Active = false;
                    own.Speed = Vector3.zero;
                    yield return null;
                }
            }
            finally
            {
                try { ((IDisposable)scan).Dispose(); }
                finally
                {
                    own.Mortality.DamageReciever.IsImmortal = immortal;
                    own.PlatformBehaviour.Gravity.Active = gravity;
                    own.Speed = speed;
                }
            }
            float end = Now + 20f, resend = 0f;
            while (!s_ack && Now < end)
            {
                if (Now >= resend)
                {
                    NetPlay.Send(BarrierMessage, true, delegate(BinaryWriter w) { w.Write((byte)0); w.Write(DoneStep); });
                    resend = Now + 1f;
                }
                yield return null;
            }
            h.Check(s_ack, "SCAN barrier 900 acknowledged");
            h.Check(true, "SCAN done");
        }

        static SceneRoot Root(Component component)
        {
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                SceneRoot root = t.GetComponent<SceneRoot>();
                if (root != null) return root;
            }
            return null;
        }

        static string Path(Component component)
        {
            string path = component.name;
            for (Transform t = component.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return Clean(path);
        }

        static string Clean(string value) { return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
        static string Value(object value)
        {
            if (value == null) return "null";
            UnityEngine.Object obj = value as UnityEngine.Object;
            if (!object.ReferenceEquals(obj, null)) return obj == null ? "null" : Clean(obj.name);
            IEnumerable items = value as IEnumerable;
            if (items != null && !(value is string))
            {
                List<string> parts = new List<string>();
                foreach (object item in items) parts.Add(Value(item));
                return "[" + string.Join(",", parts.ToArray()) + "]";
            }
            return Clean(value.ToString());
        }

        static List<ActionMethod> Actions(ActionSequence sequence)
        {
            if (sequence.Actions != null) return sequence.Actions;
            List<ActionMethod> actions = new List<ActionMethod>();
            for (int i = 0; i < sequence.transform.childCount; i++)
                actions.AddRange(sequence.transform.GetChild(i).GetComponents<ActionMethod>());
            actions.Sort(delegate(ActionMethod a, ActionMethod b) { return string.Compare(a.name, b.name, StringComparison.Ordinal); });
            return actions;
        }

        static bool Has(string name, params string[] words)
        {
            foreach (string word in words) if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static bool Cinematic(BaseAnimator animator)
        {
            return animator != null && (animator is GameplayToCinematicAnimator || animator is MasterTimelineSequence
                || animator.GetComponentsInChildren<GameplayToCinematicAnimator>(true).Length > 0
                || animator.GetComponentsInChildren<MasterTimelineSequence>(true).Length > 0);
        }

        static void Walk(ActionMethod action, int depth, HashSet<ActionMethod> visited, HashSet<string> flags, TextWriter dump)
        {
            if (action == null || depth > 6 || !visited.Add(action)) return;
            string name = action.GetType().Name;
            if (Has(name, "Ori") && Has(name, "Fly", "Move", "Return", "Back", "Position")) flags.Add("SPIRIT");
            if (action is SetCameraTargetAction || action is MoveCameraAction || action is MoveCameraHereAction
                || name.StartsWith("Camera", StringComparison.Ordinal)) flags.Add("CAM");
            ShowSeinUIAction ui = action as ShowSeinUIAction;
            if (ui != null && !ui.ShouldShow) flags.Add("HIDEUI");
            if (action is LetterboxAction) flags.Add("LETTERBOX");
            if (action is PauseGameAction) flags.Add("PAUSE");
            if (Has(name, "Lock")) flags.Add("LOCK");
            if (action is ShowStoryTextAction) flags.Add("TEXT");
            if (Has(name, "ShowAbilityMessage", "ShowPages", "InteractiveMessageBox", "ShowMessageBox")) flags.Add("MSGBOX");
            if (Has(name, "Sein") && Has(name, "Move", "Animation", "Teleport")) flags.Add("PLAYER");
            BaseAnimatorAction animation = action as BaseAnimatorAction;
            if (animation != null)
            {
                if (animation.Animators != null) foreach (BaseAnimator animator in animation.Animators)
                    if (Cinematic(animator)) flags.Add("CAM");
                if (animation.Target != null) foreach (BaseAnimator animator in animation.Target.GetComponentsInChildren<BaseAnimator>(true))
                    if (Cinematic(animator)) flags.Add("CAM");
            }
            StringBuilder line = new StringBuilder(new string(' ', depth * 2) + name + " " + Clean(action.gameObject.name));
            bool all = Has(name, "Camera", "Ori", "Sein", "Cinematic", "Timeline", "Animator");
            foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object value = field.GetValue(action);
                if (all || field.Name == "Target" || field.Name == "Position" || field.Name == "Duration"
                    || field.Name == "DurationPlayerCantMove" || field.Name == "SuspendPlayer" || field.Name == "SuspendCamera"
                    || field.Name == "SuspendOri" || field.Name == "ShowLetterbox" || field.Name == "ShowLetterboxes" || field.Name == "ShouldShow")
                    line.Append(" " + field.Name + "=" + Value(value));
                Component component = value as Component;
                GameObject go = value as GameObject;
                if ((component != null && component.GetComponent<InteractiveMessageBox>() != null)
                    || (go != null && go.GetComponentsInChildren<InteractiveMessageBox>(true).Length > 0)) flags.Add("MSGBOX");
            }
            if (dump != null) dump.WriteLine(line.ToString());
            ActionSequence sequence = action as ActionSequence;
            if (sequence != null) foreach (ActionMethod child in Actions(sequence)) Walk(child, depth + 1, visited, flags, dump);
            foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (sequence != null && field.Name == "Actions") continue;
                object value = field.GetValue(action);
                ActionMethod child = value as ActionMethod;
                if (child != null) Walk(child, depth + 1, visited, flags, dump);
                else if (value is IEnumerable && !(value is string))
                    foreach (object item in (IEnumerable)value) Walk(item as ActionMethod, depth + 1, visited, flags, dump);
            }
        }

        static bool References(object value, HashSet<UnityEngine.Object> parents)
        {
            UnityEngine.Object obj = value as UnityEngine.Object;
            return obj != null && parents.Contains(obj);
        }

        static string Triggers(ActionSequence target, MonoBehaviour[] behaviours, TextWriter dump)
        {
            HashSet<UnityEngine.Object> parents = new HashSet<UnityEngine.Object>();
            parents.Add(target);
            for (Transform t = target.transform; t != null; t = t.parent)
            {
                parents.Add(t); parents.Add(t.gameObject);
                if (t != target.transform) foreach (Component parent in t.GetComponents<Component>()) if (parent != null) parents.Add(parent);
            }
            HashSet<string> types = new HashSet<string>();
            SceneRoot root = Root(target);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour == target || Root(behaviour) != root) continue;
                foreach (FieldInfo field in behaviour.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    object value = field.GetValue(behaviour);
                    bool match = References(value, parents);
                    if (!match && value is IEnumerable && !(value is string))
                        foreach (object item in (IEnumerable)value) if (References(item, parents)) { match = true; break; }
                    if (!match) continue;
                    types.Add(behaviour.GetType().Name);
                    if (dump != null) dump.WriteLine("TRIGGER\t" + behaviour.GetType().Name + "\t" + Path(behaviour)
                        + "\t" + behaviour.transform.position + "\t" + field.Name);
                }
            }
            return Joined(types);
        }

        static string Joined(HashSet<string> values)
        {
            List<string> sorted = new List<string>(values); sorted.Sort(StringComparer.Ordinal);
            return string.Join(",", sorted.ToArray());
        }

        static SceneRoot Loaded(RuntimeSceneMetaData meta)
        {
            foreach (SceneManagerScene scene in Core.Scenes.Manager.ActiveScenes)
                if (scene.MetaData.Scene == meta.Scene && scene.SceneRoot != null) return scene.SceneRoot;
            return null;
        }

        static IEnumerator Load(RuntimeSceneMetaData meta)
        {
            Vector2 center = meta.SceneBounds.center;
            Plugin.Teleport(Own, new Vector3(center.x, center.y, 0));
            Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(meta.SceneBounds, true, false, false);
            float end = Now + 20f;   // busy test machine: big scenes need longer than 8 s
            while (Loaded(meta) == null && Now < end) yield return null;
        }

        static IEnumerator Scan(TestHarness h)
        {
            string output = Environment.GetEnvironmentVariable("ORICOOP_SCAN_OUT") ?? @"C:\OriCoop\test\story-scan.txt";
            string directory = System.IO.Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            List<RuntimeSceneMetaData> scenes = new List<RuntimeSceneMetaData>(Core.Scenes.Manager.AllScenes);
            using (StreamWriter writer = new StreamWriter(output, false, new UTF8Encoding(false)))
            {
                List<string> names = new List<string>();
                foreach (RuntimeSceneMetaData meta in scenes) names.Add(meta.Scene);
                writer.WriteLine("ALL SCENES\t" + string.Join(",", names.ToArray()));
                writer.WriteLine("TYPE NOTE\tShowAbilityMessageAction and ShowPagesAction are internal; recognized by name for MSGBOX. No requested types skipped.");
                writer.Flush();
                HashSet<string> found = new HashSet<string>();
                List<RuntimeSceneMetaData> targetScenes = new List<RuntimeSceneMetaData>();
                foreach (RuntimeSceneMetaData meta in scenes)
                    if (meta.Scene == "sunkenGladesSpiritCavernB") targetScenes.Add(meta);
                foreach (RuntimeSceneMetaData meta in scenes)
                    if (!targetScenes.Contains(meta) && (meta.SceneMoonGuid == new MoonGuid(-2061960018, 894748801, 1197361673, 37539976)
                        || meta.SceneMoonGuid == new MoonGuid(-2002218449, 2091178885, 407973721, 907023776))) targetScenes.Add(meta);
                writer.WriteLine("PART A");
                foreach (RuntimeSceneMetaData meta in targetScenes)
                {
                    IEnumerator load = Load(meta);
                    while (load.MoveNext()) yield return null;
                    SceneRoot root = Loaded(meta);
                    int count = 0;
                    if (root == null) writer.WriteLine("TARGET SCENE\t" + meta.Scene + "\tnot loaded");
                    else
                    {
                        MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                        foreach (ActionSequence sequence in Resources.FindObjectsOfTypeAll<ActionSequence>())
                        {
                            if (sequence == null || Root(sequence) != root) continue;
                            for (int i = 0; i < Targets.Length; i++)
                            {
                                if (sequence.MoonGuid != Targets[i]) continue;
                                found.Add(Labels[i]); count++;
                                writer.WriteLine("TARGET\t" + Labels[i] + "\t" + sequence.MoonGuid + "\t" + meta.Scene + "\t" + Path(sequence)
                                    + "\tactive=" + sequence.gameObject.activeInHierarchy + "\tenabled=" + sequence.enabled
                                    + "\t" + sequence.transform.position + "\tstory=" + StorySync.IsStorySequence(sequence));
                                Walk(sequence, 0, new HashSet<ActionMethod>(), new HashSet<string>(), writer);
                                writer.WriteLine("TRIGGER TYPES\t" + Triggers(sequence, behaviours, writer));
                            }
                        }
                    }
                    writer.Flush();
                    h.Note("SCAN targets " + meta.Scene + ": " + count + " targets" + (root == null ? ", not loaded" : ""));
                    yield return null;
                }
                for (int i = 0; i < Labels.Length; i++) if (!found.Contains(Labels[i])) writer.WriteLine("TARGET\t" + Labels[i] + "\t" + Targets[i] + "\tnot found");
                writer.WriteLine("PART B");
                int sceneCount = 0, sequenceCount = 0;
                foreach (RuntimeSceneMetaData meta in scenes)
                {
                    // ORICOOP_SCAN_ALL=1: every scene (T-040 trigger list), not only the early-game prefixes.
                    bool match = Environment.GetEnvironmentVariable("ORICOOP_SCAN_ALL") == "1";
                    foreach (string prefix in Prefixes) if (meta.Scene.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                    if (!match) continue;
                    IEnumerator load = Load(meta);
                    while (load.MoveNext()) yield return null;
                    SceneRoot root = Loaded(meta);
                    writer.WriteLine("SCENE\t" + meta.Scene);
                    if (root == null)
                    {
                        writer.WriteLine("not loaded"); writer.Flush();
                        h.Note("SCAN " + meta.Scene + ": 0 pedestals, 0 sequences, 0 cutscenes, not loaded");
                        continue;
                    }
                    sceneCount++;
                    int pedestals = 0, sequences = 0, cutscenes = 0;
                    MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                    foreach (MonoBehaviour behaviour in behaviours)
                    {
                        if (behaviour == null || Root(behaviour) != root) continue;
                        GetAbilityPedestal pedestal = behaviour as GetAbilityPedestal;
                        if (pedestal != null)
                        {
                            pedestals++;
                            writer.WriteLine("ABILITY\t" + pedestal.Ability + "\t" + Path(pedestal) + "\t" + pedestal.transform.position + "\t" + pedestal.CurrentState);
                        }
                        ActionSequence sequence = behaviour as ActionSequence;
                        if (sequence != null && !object.ReferenceEquals(sequence.MoonGuid, null) && sequence.MoonGuid != MoonGuid.Empty && StorySync.IsStorySequence(sequence))
                        {
                            sequences++; sequenceCount++;
                            HashSet<string> flags = new HashSet<string>();
                            Walk(sequence, 0, new HashSet<ActionMethod>(), flags, null);
                            writer.WriteLine("SEQUENCE\t" + sequence.MoonGuid + "\t" + Path(sequence) + "\t" + sequence.transform.position
                                + "\tactive=" + sequence.gameObject.activeInHierarchy + "\tactions=" + Actions(sequence).Count
                                + "\tFLAGS=" + Joined(flags) + "\tTRIGGERS=" + Triggers(sequence, behaviours, null));
                        }
                        CutsceneController controller = behaviour as CutsceneController;
                        if (controller != null)
                        {
                            bool story = false;
                            List<string> states = new List<string>();
                            foreach (CutsceneState state in controller.GetComponentsInChildren<CutsceneState>(true))
                            {
                                states.Add(state.GetType().Name + ":" + Clean(state.name));
                                if (state is NormalBackwardsCutsceneState || state is MovingCutsceneState || state is PlayAnimationCutsceneState) story = true;
                            }
                            if (story) { cutscenes++; writer.WriteLine("CUTSCENE\t" + Path(controller) + "\t" + string.Join(",", states.ToArray())); }
                        }
                    }
                    writer.Flush();
                    h.Note("SCAN " + meta.Scene + ": " + pedestals + " pedestals, " + sequences + " sequences, " + cutscenes + " cutscenes");
                    yield return null;
                }
                writer.WriteLine("SCAN DONE " + sceneCount + " scenes " + sequenceCount + " sequences");
            }
        }
    }
}
