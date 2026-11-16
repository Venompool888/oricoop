using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game;
using UnityEngine;

namespace OriCoop
{
    public static class NetStorySweep
    {
        sealed class Entry
        {
            public string Scene, Suffix;
            public bool Host;
            public AbilityType Ability;
            public Entry(string scene, string suffix, bool host)
            { Scene = scene; Suffix = suffix; Host = host; }
            public Entry(string scene, AbilityType ability, bool host)
            { Scene = scene; Ability = ability; Host = host; }
        }
        static SeinCharacter Own { get { return NetCutsceneTest.Own; } }
        static float Now { get { return NetCutsceneTest.Now; } }
        static readonly Entry[] Entries = {
            new Entry("sunkenGladesSpiritCavernWalljumpB", "*storySetups/storyTextWithTrigger/action", false),
            new Entry("sunkenGladesSpiritCavernSaveRoomB", "*objectiveSetup/objectiveSetupTrigger/objectiveSetupAction", true),
            new Entry("upperGladesSwarmIntroduction", "*hintzone/*activateSequence", false),
            new Entry("upperGladesBelowSpiritTree", "*objectiveSetup/objectiveSetupTrigger/objectiveSetupAction", false),
            new Entry("upperGladesHollowTreeSplitB", "*kuroAct2/*action", true),
            new Entry("moonGrottoRopeBridge", "*gumoBridgeSetup/group/action", true),
            new Entry("thornfeltSwampB", "*gumoMortarsSetup/action", false),
            new Entry("sunkenGladesRunaway", "*objectiveSetup/objectiveSetupTrigger/objectiveSetupAction", true),
            new Entry("upperGladesBelowSpiritTree", AbilityType.ChargeFlame, false),
            new Entry("moonGrottoDoubleJump", AbilityType.DoubleJump, true)
        };

        static SceneRoot Root(Component component)
        {
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                SceneRoot root = t.GetComponent<SceneRoot>();
                if (root != null) return root;
            }
            return null;
        }
        static Component Find(Entry entry)
        {
            if (entry.Suffix == null)
            {
                foreach (GetAbilityPedestal pedestal in Resources.FindObjectsOfTypeAll<GetAbilityPedestal>())
                {
                    if (pedestal == null || pedestal.Ability != entry.Ability) continue;
                    SceneRoot root = Root(pedestal);
                    if (root != null && root.MetaData != null && root.MetaData.SceneName == entry.Scene) return pedestal;
                }
            }
            else
            {
                string pattern = Regex.Escape(entry.Suffix).Replace("\\*", ".*") + "$";
                foreach (ActionSequence sequence in Resources.FindObjectsOfTypeAll<ActionSequence>())
                {
                    if (sequence == null) continue;
                    SceneRoot root = Root(sequence);
                    if (root == null || root.MetaData == null || root.MetaData.SceneName != entry.Scene) continue;
                    string path = sequence.transform.name;
                    for (Transform t = sequence.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                    if (Regex.IsMatch(path, pattern)) return sequence;
                }
            }
            return null;
        }
        static void Load(string name)
        {
            foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
            {
                if (scene.Scene != name) continue;
                Vector2 center = scene.SceneBounds.center;
                Plugin.Teleport(Own, new Vector3(center.x, center.y, 0));
                Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(scene.SceneBounds, true, false, false);
                break;
            }
        }
        static bool PartnerFound(int step)
        {
            NetCutsceneTest.Sample sample;
            return NetCutsceneTest.Remote.TryGetValue(step, out sample) && sample.Found;
        }
        static bool Free()
        {
            return (StorySync.TakeoverMaskPublic() & 191) == 0
                && !Letterbox.ShowLetterboxes && !StorySync.StoryActive;
        }
        static void Jump()
        {
            NetTestHarness.BotButtons = NetCutsceneTest.OpenBoxes() > 0 && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0;
        }
        static IEnumerator Restored(TestHarness h, string label)
        {
            yield return NetCutsceneTest.Wait(2f);
            h.Check(NetCutsceneTest.SpiritFollows(), label + ": spirit follows own player");
            h.Check(NetCutsceneTest.HudRestored(), label + ": HUD restored");
            h.Check(NetCutsceneTest.CameraFree(), label + ": camera not locked");
        }
        static void ActivateAncestors(ActionSequence sequence)
        {
            List<GameObject> ancestors = new List<GameObject>();
            if (sequence == null) return;   // destroyed meanwhile (e.g. a checkpoint reload unloaded the scene)
            for (Transform t = sequence.transform; t != null; t = t.parent)
            {
                ancestors.Add(t.gameObject);
                if (t.GetComponent<SceneRoot>() != null) break;
            }
            for (int i = ancestors.Count - 1; i >= 0; i--)
                if (!ancestors[i].activeSelf) ancestors[i].SetActive(true);
        }
        static IEnumerator Story(TestHarness h, Entry entry, ActionSequence sequence, string label, int step)
        {
            int sent = StorySync.StartsSent, mirrored = StorySync.StartsMirrored;
            bool hidden = StorySync.Watching, freeAgain = false;
            yield return NetCutsceneTest.Barrier(h, step - 1);
            if (NetPlay.IsHost == entry.Host)
            {
                ActivateAncestors(sequence);
                if (sequence != null) sequence.Perform(null);
            }
            float end = Now + 60f, freeSince = -1f;
            while (Now < end)
            {
                hidden |= StorySync.Watching;
                Jump();
                if (!Free()) freeSince = -1f;
                else if (freeSince < 0) freeSince = Now;
                if (freeSince >= 0 && Now - freeSince >= 3f) { freeAgain = true; break; }
                yield return null;
            }
            NetTestHarness.BotButtons = 0;
            NetCutsceneTest.Publish(step, false);
            yield return NetCutsceneTest.Barrier(h, step);
            if (NetPlay.IsHost == entry.Host) h.Check(StorySync.StartsSent > sent, label + ": story sent");
            else
            {
                h.Check(StorySync.StartsMirrored > mirrored, label + ": partner mirrored");
                h.Check(!hidden, label + ": never hidden");
            }
            h.Check(freeAgain, label + ": free again");
            yield return Restored(h, label);
        }
        static IEnumerator Pedestal(TestHarness h, Entry entry, GetAbilityPedestal pedestal, string label, int step)
        {
            bool original = Own.PlayerAbilities.HasAbility(entry.Ability);
            try
            {
                pedestal.CurrentState = GetAbilityPedestal.States.OutOfRange;
                if (NetPlay.IsHost) Own.PlayerAbilities.SetAbility(entry.Ability, false);
                float end = Now + 20f;
                while (Own.PlayerAbilities.HasAbility(entry.Ability) && Now < end) yield return null;
                NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step, Ability = Own.PlayerAbilities.HasAbility(entry.Ability) });
                yield return NetCutsceneTest.Barrier(h, step);
                NetCutsceneTest.Sample partner;
                h.Check(!Own.PlayerAbilities.HasAbility(entry.Ability) && NetCutsceneTest.Remote.TryGetValue(step, out partner)
                    && !partner.Ability, label + ": reset ability false on both");
                Plugin.Teleport(Own, pedestal.transform.position + Vector3.right * (NetPlay.IsHost ? -0.5f : 0.5f));
                end = Now + 5f;
                while (!Own.PlatformBehaviour.PlatformMovement.IsOnGround && Now < end) yield return null;
                h.Check(Own.PlatformBehaviour.PlatformMovement.IsOnGround && Vector3.Distance(Own.Position, pedestal.transform.position) <= 1f,
                    label + ": beside pedestal on ground");
                pedestal.CurrentState = GetAbilityPedestal.States.OutOfRange;
                int baseline = CutsceneGuard.LocalPedestalActivations;
                yield return NetCutsceneTest.Barrier(h, step + 1);
                if (NetPlay.IsHost == entry.Host) pedestal.ActivatePedestal();
                bool freeAgain = false;
                end = Now + 60f; float freeSince = -1f;
                while (Now < end)
                {
                    bool played = CutsceneGuard.LocalPedestalActivations == baseline + 1 && Own.PlayerAbilities.HasAbility(entry.Ability);
                    Jump();
                    if (!played || !Free()) freeSince = -1f;
                    else if (freeSince < 0) freeSince = Now;
                    if (freeSince >= 0 && Now - freeSince >= 3f) { freeAgain = true; break; }
                    yield return null;
                }
                NetTestHarness.BotButtons = 0;
                NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step + 2,
                    Plays = CutsceneGuard.LocalPedestalActivations - baseline, Ability = Own.PlayerAbilities.HasAbility(entry.Ability) });
                yield return NetCutsceneTest.Barrier(h, step + 2);
                bool received = NetCutsceneTest.Remote.TryGetValue(step + 2, out partner);
                h.Note(label + " pedestal: native=" + (CutsceneGuard.LocalPedestalActivations - baseline) + " saveReady=" + SaveSync.Ready
                    + " state=" + pedestal.CurrentState + " sent=" + CutsceneGuard.PedestalPlaysSent + " received=" + CutsceneGuard.PedestalPlaysReceived
                    + " played=" + CutsceneGuard.PedestalPlaysPlayed + " ability=" + Own.PlayerAbilities.HasAbility(entry.Ability));
                h.Check(CutsceneGuard.LocalPedestalActivations == baseline + 1 && received && partner.Plays == 1,
                    label + ": cutscene played on both");
                h.Check(Own.PlayerAbilities.HasAbility(entry.Ability) && received && partner.Ability,
                    label + ": " + entry.Ability + " true on both");
                h.Check(freeAgain, label + ": free again");
                yield return Restored(h, label);
            }
            finally
            {
                NetTestHarness.BotButtons = 0;
                if (NetPlay.IsHost) Own.PlayerAbilities.SetAbility(entry.Ability, original);
            }
        }
        static IEnumerator SweepEntry(TestHarness h, Entry entry, string label, int step)
        {
            int failures = NetCutsceneTest.Failures(h);
            try
            {
                yield return NetCutsceneTest.Settle();
                Load(entry.Scene);
                Component target = null;
                float end = Now + 40f;
                while (Now < end)
                {
                    target = Find(entry);
                    if (target != null && !GameController.Instance.IsLoadingGame) break;
                    yield return null;
                }
                if (target != null)
                {
                    Plugin.Teleport(Own, target.transform.position + Vector3.right * (NetPlay.IsHost ? -1f : 1f));
                    end = Now + 5f;
                    while (!Own.PlatformBehaviour.PlatformMovement.IsOnGround && Now < end) yield return null;
                }
                NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = step, Found = target != null });
                yield return NetCutsceneTest.Barrier(h, step);
                bool found = target != null && PartnerFound(step);
                h.Check(found, label + ": found");
                if (!found) yield break;
                if (entry.Suffix == null) yield return Pedestal(h, entry, (GetAbilityPedestal)target, label, step + 2);
                else yield return Story(h, entry, (ActionSequence)target, label, step + 2);
            }
            finally
            {
                NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0;
                if (NetCutsceneTest.Failures(h) > failures) h.Note(label + " state: " + NetCutsceneTest.Diag());
            }
        }
        public static IEnumerator Run(TestHarness h)
        {
            NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0;
            NetPlay.SetSim(0, 0, 0, 0);
            // Teleporting into hazard scenes killed players; each death reloads the guest (SaveSync checkpoint
            // transfer) and the next entry then searched a half-loaded world. Stay alive during the sweep.
            bool immortal = NetCutsceneTest.Own.Mortality.DamageReciever.IsImmortal;
            NetCutsceneTest.Own.Mortality.DamageReciever.IsImmortal = true;
            yield return NetCutsceneTest.Barrier(h, 200);
            int passed = 0, failed = 0;
            // ORICOOP_SWEEP_ONLY = comma list of entry numbers (1-based) to run, e.g. "4,9"; default all.
            string only = Environment.GetEnvironmentVariable("ORICOOP_SWEEP_ONLY");
            for (int i = 0; i < Entries.Length; i++)
            {
                if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(','), (i + 1).ToString()) < 0) continue;
                Entry entry = Entries[i];
                string shortName = entry.Scene.Replace("sunkenGlades", "").Replace("upperGlades", "").Replace("moonGrotto", "");
                string label = "SW" + (i + 1) + " " + shortName;
                int failures = NetCutsceneTest.Failures(h), step = 210 + i * 10;
                yield return NetCutsceneTest.Step(h, step + 9, SweepEntry(h, entry, label, step));
                if (NetCutsceneTest.Failures(h) == failures) passed++;
                else
                {
                    failed++;
                    h.Note(label + " state: " + NetCutsceneTest.Diag());
                }
            }
            h.Note("SW sweep: " + passed + " entries passed / " + failed + " failed");
            if (NetCutsceneTest.Own != null) NetCutsceneTest.Own.Mortality.DamageReciever.IsImmortal = immortal;
        }
    }
}
