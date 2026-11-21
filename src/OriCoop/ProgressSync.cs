using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using OriCoop.Saves;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    // Only the host mutates shared progress. Actions have session-local sequence numbers;
    // baked pickup GUIDs additionally deduplicate two players touching the same pickup.
    public static class ProgressSync
    {
        const byte MsgProgress = 20, MsgAction = 21, MsgSkillStatus = 22;
        static readonly ProgressQueue<MoonGuid> s_pending = new ProgressQueue<MoonGuid>();
        static readonly ProgressInbox s_inbox = new ProgressInbox();
        static readonly OwnSkillTree s_ownTree = new OwnSkillTree();
        static uint s_sequence;
        static ulong s_sentHash;
        static bool s_sent, s_applying;
        static int s_energyDoorDepth;
        static PickupBase s_synthetic;
        static float s_next;
        static int s_loggedCollector, s_ignoredActions;
        static float s_ignoredLogAt;
        static float s_collectorWarningAt;
        static readonly bool s_mutateNoSeatSkills = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_SEAT_SKILLS") == "1";
        static bool s_loggedMissingTree;
        public static int ActionsApplied, ActionsRejected;
        internal static int SnapshotGuestLoad;
        public static bool SkillPurchaseForwardingEnabled = true;

        static SeinCharacter Own { get { return StoryHost.Current.P1 != null ? StoryHost.Current.P1 : Characters.Sein; } }
        public static bool Ready
        {
            get
            {
                SeinCharacter p = Own;
                if (p == null || p.PlayerAbilities == null || p.PlayerAbilities.Abilities == null
                    || p.PlayerAbilities.Abilities.Length != 43 || p.Inventory == null || p.Level == null
                    || p.Energy == null || p.Mortality == null || p.Mortality.Health == null) return false;
                foreach (CharacterAbility ability in p.PlayerAbilities.Abilities) if (ability == null) return false;
                return true;
            }
        }
        // The pickup processor is a nested prefab. SetReferenceToSein only assigns the processor's
        // Sein field (decomp SeinPickupProcessor.cs:248); it does not populate Sein.PickupHandler.
        // Validate the binding, so a stale field or a puppet processor can never receive local rewards.
        public static SeinPickupProcessor PickupCollector(SeinCharacter player)
        {
            if (player == null) return null;
            SeinPickupProcessor processor = player.PickupHandler;
            if (processor == null || processor.Sein != player || !processor.transform.IsChildOf(player.transform))
            {
                processor = null;
                foreach (SeinPickupProcessor candidate in player.GetComponentsInChildren<SeinPickupProcessor>(true))
                    if (candidate != null && candidate.Sein == player) { processor = candidate; break; }
            }
            if (processor != null && player == Own && s_loggedCollector != processor.GetInstanceID())
            {
                s_loggedCollector = processor.GetInstanceID();
                Services.Log.Info("net: resolved local pickup collector " + processor.GetInstanceID()
                    + " boundTo=" + player.GetInstanceID() + " PickupHandler=" + (player.PickupHandler != null));
            }
            return processor;
        }
        static bool Guest { get { return Services.Net.Connected && !Services.Net.IsHost && !s_applying; } }
        static bool ValidGuid(MoonGuid g) { return !object.ReferenceEquals(g, null) && g != MoonGuid.Empty; }
        static MoonGuid PickupGuid(PickupBase p)
        {
            // Pooled enemy orbs may retain a prefab GUID; it is not a world pickup identity.
            ExpOrbPickup orb = p as ExpOrbPickup;
            return orb != null && orb.MessageType == ExpOrbPickup.ExpOrbMessageType.None ? MoonGuid.Empty : p.MoonGuid;
        }

        public static void Init()
        {
            StoryHost.Current = new GameStoryHost();
            Services.Net.On(MsgProgress, ReceiveProgress); Services.Net.On(MsgAction, ReceiveAction);
            Services.Net.On(MsgSkillStatus, delegate(BinaryReader r) {
                bool enabled = ProgressWire.ReadSkillStatus(r);
                if (!Services.Net.IsHost) { SkillPurchaseForwardingEnabled = ProgressWire.ApplySkillStatus(false, enabled, SkillPurchaseForwardingEnabled); Services.Log.Warn("net: skill purchase forwarding enabled=" + enabled); }
            });
            Services.Net.SessionStarted += Reset; Services.Net.SessionEnded += Reset;
            Services.Net.PlayerLeft += delegate(int seat) { s_pending.SeatLeft(seat); };       // T-143
            Services.Net.PlayerJoined += delegate(int seat) { s_pending.SeatJoined(seat); };
            // SharedProgress's proximity postfix bypasses trigger callbacks. Disable that local-coop
            // shortcut online, where touching a puppet must never collect on behalf of its owner.
            Type t = typeof(SharedProgress).GetNestedType("PickupPatch", BindingFlags.NonPublic);
            if (t != null) StoryHost.Current.PatchPickupShortcut(AccessTools.Method(t, "Postfix"));
        }
        static bool LocalPickupShortcut() { return !Services.Net.Enabled; }
        static void Reset()
        {
            s_pending.Reset(); s_inbox.Take(); s_ownTree.Clear();
            s_sequence = 0; s_sent = false; s_next = 0; ActionsApplied = ActionsRejected = SnapshotGuestLoad = 0;
            s_loggedMissingTree = false;
            s_loggedCollector = 0; s_collectorWarningAt = 0; s_ignoredActions = 0; s_ignoredLogAt = 0;
            SkillPurchaseForwardingEnabled = true;
        }

        static ProgressSnapshot Capture()
        {
            SeinCharacter p = Own; ProgressSnapshot s = new ProgressSnapshot();
            ulong owned = OwnedBits(p);
            EnsureSkillTree(p);
            // T-047: skill-tree abilities and points are per player; the snapshot carries the shared part only
            // (Story\SkillSplit.cs): abilities without the tree, and `earned` instead of this player's points.
            s.Abilities = SkillSplit.Shared(owned, s_tree);
            s.MaxHealth = p.Mortality.Health.MaxHealth; s.MaxEnergy = p.Energy.Max;
            s.Keys = p.Inventory.Keystones; s.Maps = p.Inventory.MapStones; s.Collected = p.Inventory.SkillPointsCollected;
            s.Experience = p.Level.Experience; s.Level = p.Level.Current;
            s.Points = SkillSplit.Earned(p.Level.SkillPoints, owned, s_tree, s_cost); return s;
        }

        static ulong OwnedBits(SeinCharacter p)
        {
            ulong bits = 0;
            for (int i = 0; i < 43; i++) if (p.PlayerAbilities.Abilities[i].HasAbility) bits |= 1UL << i;
            return bits;
        }

        // Skill tree layout: which PlayerAbilities.Abilities slot each SkillItem buys, and its price. Read once from the
        // game's SkillTreeManager. Unknown (no manager yet) = tree 0, which is the old fully shared behaviour.
        static ulong s_tree;
        static readonly int[] s_cost = new int[64];
        static bool s_treeKnown;
        public static ulong SkillTreeMask { get { return s_tree; } }
        public static ulong OwnTreeBits
        {
            get { if (!Ready) return 0; EnsureSkillTree(Own); return OwnedBits(Own) & s_tree; }
        }

        static void EnsureSkillTree(SeinCharacter p)
        {
            if (s_treeKnown) return;
            SkillTreeManager m = SkillTreeManager.Instance;
            if (m == null || DifficultyController.Instance == null) return;
            ulong tree = 0;
            foreach (SkillItem item in m.GetComponentsInChildren<SkillItem>(true))
            {
                int i = AbilitySlot(p.PlayerAbilities, item.Ability);
                if (i < 0) continue;
                tree |= 1UL << i;
                s_cost[i] = item.ActualRequiredSkillPoints;
            }
            if (tree == 0) return;
            s_tree = tree; s_treeKnown = true;
            Services.Log.Info("net: skill tree: " + CountBits(tree) + " per-player abilities (mask " + tree.ToString("X") + ")");
        }

        static int CountBits(ulong v) { int n = 0; for (; v != 0; v &= v - 1) n++; return n; }

        // Abilities[] is not in AbilityType order: find the slot whose flag HasAbility(type) reads (flip and restore one
        // plain field at a time; SetAbility is patched elsewhere, so it is not used here).
        static int AbilitySlot(PlayerAbilities a, AbilityType type)
        {
            bool before = a.HasAbility(type);
            for (int i = 0; i < a.Abilities.Length; i++)
            {
                CharacterAbility c = a.Abilities[i];
                if (c == null) continue;
                c.HasAbility = !c.HasAbility;
                bool moved = a.HasAbility(type) != before;
                c.HasAbility = !c.HasAbility;
                if (moved) return i;
            }
            return -1;
        }
        public static ulong Hash() { return Ready ? Capture().Hash() : 0; }
        public static void Tick()
        {
            // Snapshots (abilities, health/energy, inventory, level) only need both progress states present; they
            // no longer wait for NetPlay.LocalInGameplay(). A relay run left it false after a half-run scene story
            // and the guest never got the host's WallJump=false (CUT reset). The host's pickup-action queue still
            // needs a live local player.
            ReportStall();
            if (!Services.Net.Connected || !Ready) return;
            EnsureSkillTree(Own);
            if (!s_mutateNoSeatSkills && Services.Net.IsHost && SeatSkillSync.HostRestorePending
                && !SaveSync.HostAdopting && !GameController.Instance.IsLoadingGame)
                RestoreHostSkills();
            if (!Services.Session.Ready) return;
            if (!Services.Net.IsHost)
            {
                if (s_ownTree.Held && s_tree == 0) return;
                if (s_inbox.Snapshot != null) { Apply(s_inbox.Take()); SnapshotGuestLoad = SaveSync.GuestLoads; }
                if (!s_ownTree.Held) SeatSkillSync.Report();
            }
            if (Services.Net.IsHost && Services.Session.LocalInGameplay)
            {
                while (s_pending.Count > 0)
                {
                    ProgressAction pending = s_pending.Peek();
                    if (pending.Kind < ActionKind.MarkOnly && PickupCollector(Own) == null)
                    {
                        if (Time.realtimeSinceStartup >= s_collectorWarningAt)
                        {
                            s_collectorWarningAt = Time.realtimeSinceStartup + 5f;
                            Services.Log.Warn("net: waiting for bound local pickup collector; queued seq=" + pending.Sequence);
                        }
                        break;
                    }
                    ProgressAction a = s_pending.Dequeue();
                    try { ApplyAction(a); }
                    catch (Exception e) { Services.Log.Warn("net: progress action disabled seq=" + a.Sequence + " " + a.Kind + ": " + e); }
                }
            }
            if (Time.realtimeSinceStartup < s_next) return;
            s_next = Time.realtimeSinceStartup + 0.1f;
            if (Services.Net.IsHost && (!s_sent || Hash() != s_sentHash)) SendSnapshot();
        }
        public static void SendSnapshot(int seat = 0)
        {
            if (!Services.Net.IsHost || !Services.Net.Connected || !Ready || !Services.Session.Ready) return;
            ProgressSnapshot s = Capture();
            if (seat == 0)
            { Services.Net.Send(MsgProgress, true, s.Write); s_sentHash = s.Hash(); s_sent = true; }
            else Services.Net.SendTo(seat, MsgProgress, true, s.Write);
        }
        static float s_unsentAt = -1f, s_stallCheckAt; static bool s_stallLogged;
        // One line per stall (> 3 s) with every gate, so a stuck sync names its cause in the log.
        static void ReportStall()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_stallCheckAt) return;
            s_stallCheckAt = now + 0.5f;
            bool unsent = Services.Net.IsHost && Services.Net.Connected && Ready && (!s_sent || Hash() != s_sentHash);
            if (!unsent) s_unsentAt = -1f; else if (s_unsentAt < 0) s_unsentAt = now;
            bool stalled = s_inbox.ReceivedAt >= 0 && now - s_inbox.ReceivedAt > 3f || s_unsentAt >= 0 && now - s_unsentAt > 3f;
            if (!stalled) { s_stallLogged = false; return; }
            if (s_stallLogged) return;
            s_stallLogged = true;
            GameController gc = GameController.Instance;
            Services.Log.Warn("net: progress sync stalled " + (Services.Net.IsHost ? "(host change not sent)" : "(guest snapshot not applied)")
                + " connected=" + Services.Net.Connected + " saveReady=" + Services.Session.Ready + " progressReady=" + Ready
                + " inGameplay=" + Services.Session.LocalInGameplay + " loading=" + (gc != null && gc.IsLoadingGame)
                + " sein=" + (Characters.Sein != null) + " seinActive=" + (Characters.Sein != null && Characters.Sein.gameObject.activeInHierarchy)
                + " seinIsP2=" + (Characters.Sein != null && StoryHost.Current.P2 != null && Characters.Sein == StoryHost.Current.P2));
        }
        static void ReceiveProgress(BinaryReader r)
        {
            ProgressSnapshot s = ProgressSnapshot.Read(r); s_inbox.Receive(Services.Net.IsHost, s, Time.realtimeSinceStartup);
        }
        static void Apply(ProgressSnapshot s)
        {
            SeinCharacter p = Own; s_applying = true;
            try
            {
                EnsureSkillTree(p);
                // Keep this seat's tree (T-043: the recorded tree held before a checkpoint load, see HoldOwnSkillTree).
                ulong merged = SkillSplit.Merge(s_ownTree.Local(OwnedBits(p)), s.Abilities, s_tree);
                bool changed = SetOwnedBits(p, merged);
                p.Mortality.Health.MaxHealth = s.MaxHealth; p.Energy.Max = s.MaxEnergy;
                p.Inventory.Keystones = s.Keys; p.Inventory.MapStones = s.Maps; p.Inventory.SkillPointsCollected = s.Collected;
                p.Level.Experience = s.Experience; p.Level.Current = s.Level;
                p.Level.SkillPoints = SkillSplit.Points(s.Points, merged, s_tree, s_cost);
                // Prevent the guest's XP visual interpolation from independently awarding a level.
                p.Level.ExperienceVisualMin = s.Experience; p.Level.ExperienceVisualMax = s.Experience;
                if (changed && p.Prefabs != null) p.Prefabs.EnsureRightPrefabsAreThereForAbilities();
                RefreshSkills();
            }
            finally { s_applying = false; }
        }
        static bool SetOwnedBits(SeinCharacter p, ulong bits)
        {
            bool changed = false;
            for (int i = 0; i < 43; i++)
            {
                bool value = (bits & (1UL << i)) != 0;
                if (p.PlayerAbilities.Abilities[i].HasAbility != value) { p.PlayerAbilities.Abilities[i].HasAbility = value; changed = true; }
            }
            return changed;
        }

        internal static void GuestLoaded() { s_inbox.Take(); }

        // T-043: both host and guest roll back to their own recorded tree. T-142's live hold is for unrecorded seats.
        public static void HoldOwnSkillTree(bool joined)
        {
            if (Services.Net.IsHost || s_ownTree.Held) return;
            ulong bits = 0;
            SkillRecord record = SeatSkillSync.ForLoadingCheckpoint();
            bool recorded = !s_mutateNoSeatSkills && record != null && record.TryGet(CoopSaves.PlayerId, out bits);
            bool liveReady = Ready;
            if (!recorded && !liveReady)
            {
                if (!s_loggedMissingTree) Services.Log.Warn("net: own skill tree not held before checkpoint (no record and progress not ready)");
                s_loggedMissingTree = true;
                return;
            }
            ulong hold;
            if (!SkillRestore.GuestHold(recorded, bits, joined, liveReady ? OwnedBits(Own) : 0, out hold)) return;
            s_ownTree.Hold(hold);
            if (recorded) SeatSkillSync.Restores++;
            Services.Log.Info("net: holding " + (recorded ? "recorded" : "live") + " skill tree across checkpoint (abilities " + hold.ToString("X") + ")");
        }

        internal static void RestoreHostSkills()
        {
            if (s_mutateNoSeatSkills || !Ready) return;
            SeinCharacter p = Own;
            EnsureSkillTree(p);
            if (s_tree == 0) return;
            SeatSkillSync.HostRestorePending = false;
            SkillRecord record = SeatSkillSync.Current;
            ulong recorded;
            if (record == null || !record.TryGet(CoopSaves.PlayerId, out recorded)) return;
            ulong owned; int points;
            SkillRestore.Restore(p.Level.SkillPoints, OwnedBits(p), recorded, s_tree, s_cost, out owned, out points);
            bool changed = SetOwnedBits(p, owned);
            p.Level.SkillPoints = points;
            if (changed && p.Prefabs != null) p.Prefabs.EnsureRightPrefabsAreThereForAbilities();
            RefreshSkills();
            SeatSkillSync.Restores++;
            Services.Log.Info("net: restored host checkpoint skill tree (abilities " + recorded.ToString("X") + ", points " + points + ")");
        }
        static void RefreshSkills()
        {
            SkillTreeManager m = SkillTreeManager.Instance;
            if (m == null) return;
            foreach (SkillItem item in m.GetComponentsInChildren<SkillItem>(true))
            {
                item.HasSkillItem = Own.PlayerAbilities.HasAbility(item.Ability);
                if (item.gameObject.activeInHierarchy) item.UpdateItem();
            }
            if (m.gameObject.activeInHierarchy && m.NavigationManager != null && m.NavigationManager.CurrentMenuItem != null) m.UpdateRequirementsText();
        }

        static void SendAction(ActionKind kind, MoonGuid guid, int amount, int extra)
        {
            uint seq = ++s_sequence;
            Services.Net.Send(MsgAction, true, delegate(BinaryWriter w) {
                ProgressAction a = new ProgressAction { Sequence = seq, Kind = kind,
                    Guid = (guid ?? MoonGuid.Empty).ToByteArray(), Amount = amount, Extra = extra };
                a.Write(w);
            });
        }
        static void ReceiveAction(BinaryReader r)
        {
            ProgressAction a = ProgressAction.Read(r);
            if (!Services.Net.IsHost || s_pending.Enqueue(Services.Net.Sender, a)) return;
            // T-143: a duplicate (or an action from a seat that has left) is dropped; say so, at most every 5 s.
            s_ignoredActions++;
            float now = Time.realtimeSinceStartup;
            if (now < s_ignoredLogAt) return;
            s_ignoredLogAt = now + 5f;
            Services.Log.Info("net: progress action ignored (duplicate or seat gone) P" + Services.Net.Sender + " seq=" + a.Sequence
                + " " + a.Kind + " (" + s_ignoredActions + " ignored so far)");
        }
        static void ApplyAction(ProgressAction a)
        {
            SeinCharacter p = Own; MoonGuid guid = GameStoryHost.ReadGuid(a.Guid);
            bool pickup = a.Kind <= ActionKind.MarkOnly;
            if (pickup && ValidGuid(guid) && !s_pending.RecordPickup(guid))
            { ActionsRejected++; Services.Log.Info("net: duplicate pickup ignored " + guid); return; }
            s_applying = true;
            try
            {
                if (pickup)
                {
                    if (a.Kind != ActionKind.MarkOnly) ApplyPickup(a);
                    if (ValidGuid(guid)) StoryHost.Current.MarkCollected(guid);
                }
                else switch (a.Kind)
                {
                    case ActionKind.DirectExperience: p.Level.GainExperience(a.Amount); break;
                    case ActionKind.KeysDelta:
                        if (a.Amount < 0) p.Inventory.SpendKeystones(-a.Amount); else p.Inventory.CollectKeystones(a.Amount); break;
                    case ActionKind.MapsDelta:
                        if (a.Amount < 0) p.Inventory.SpendMapstone(-a.Amount); else p.Inventory.CollectMapstone(a.Amount); break;
                    case ActionKind.DoorEnergyDelta:
                        float energy = a.Amount / 1000f;
                        if (energy < 0) p.Energy.Spend(-energy); else p.Energy.Gain(energy); break;
                    case ActionKind.Skill: Services.Log.Info("net: skill purchase action ignored (each player buys in their own tree)"); break;
                    case ActionKind.Pedestal:
                        GetAbilityPedestal pedestal = StoryHost.Current.FindAny(guid) as GetAbilityPedestal;
                        AbilityType ability = pedestal != null ? pedestal.Ability : (AbilityType)a.Extra;
                        if (!Enum.IsDefined(typeof(AbilityType), ability)) break;
                        p.PlayerAbilities.SetAbility(ability, true); StoryHost.Current.MarkPedestal(guid); break;
                }
                ActionsApplied++;
                Services.Log.Info("net: progress action seq=" + a.Sequence + " " + a.Kind + " amount=" + a.Amount + " keys=" + p.Inventory.Keystones + " xp=" + p.Level.Experience);
            }
            finally { s_applying = false; }
            SendSnapshot();
        }

        static Type PickupType(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Keystone: return typeof(KeystonePickup);
                case ActionKind.Experience: return typeof(ExpOrbPickup);
                case ActionKind.SkillPoint: return typeof(SkillPointPickup);
                case ActionKind.MaxEnergy: return typeof(MaxEnergyContainerPickup);
                case ActionKind.MaxHealth: return typeof(MaxHealthContainerPickup);
                default: return typeof(MapStonePickup);
            }
        }
        static void ApplyPickup(ProgressAction a)
        {
            // Real loaded pickups preserve their normal sequences. For an unloaded area / dynamic
            // orb, a detached inactive argument carries the amount through the very same collector.
            MoonGuid guid = GameStoryHost.ReadGuid(a.Guid);
            PickupBase pickup = ValidGuid(guid) ? StoryHost.Current.FindAny(guid) as PickupBase : null;
            GameObject temporary = null;
            if (pickup == null || pickup.IsCollected || pickup.GetType() != PickupType(a.Kind))
            {
                temporary = new GameObject("OriCoop remote pickup argument"); temporary.SetActive(false);
                pickup = (PickupBase)temporary.AddComponent(PickupType(a.Kind)); s_synthetic = pickup;
                pickup.OnValidate();
                FieldInfo amount = pickup.GetType().GetField("Amount"); if (amount != null) amount.SetValue(pickup, a.Amount);
                ExpOrbPickup orb = pickup as ExpOrbPickup;
                if (orb != null) orb.MessageType = (ExpOrbPickup.ExpOrbMessageType)a.Extra;
            }
            SeinPickupProcessor c = PickupCollector(Own);
            try
            {
                if (c == null) throw new InvalidOperationException("local pickup processor is not bound to the local Sein");
                switch (a.Kind)
                {
                    case ActionKind.Keystone: c.OnCollectKeystonePickup((KeystonePickup)pickup); break;
                    case ActionKind.Experience: c.OnCollectExpOrbPickup((ExpOrbPickup)pickup); break;
                    case ActionKind.SkillPoint: c.OnCollectSkillPointPickup((SkillPointPickup)pickup); break;
                    case ActionKind.MaxEnergy: c.OnCollectMaxEnergyContainerPickup((MaxEnergyContainerPickup)pickup); break;
                    case ActionKind.MaxHealth: c.OnCollectMaxHealthContainerPickup((MaxHealthContainerPickup)pickup); break;
                    case ActionKind.MapStone: c.OnCollectMapStonePickup((MapStonePickup)pickup); break;
                }
            }
            finally { s_synthetic = null; if (temporary != null) UnityEngine.Object.Destroy(temporary); }
        }
        static ActionKind Kind(PickupBase p)
        {
            if (p is KeystonePickup) return ActionKind.Keystone;
            if (p is ExpOrbPickup) return ActionKind.Experience;
            if (p is SkillPointPickup) return ActionKind.SkillPoint;
            if (p is MaxEnergyContainerPickup) return ActionKind.MaxEnergy;
            if (p is MaxHealthContainerPickup) return ActionKind.MaxHealth;
            if (p is MapStonePickup) return ActionKind.MapStone;
            return ActionKind.MarkOnly;
        }
        [HarmonyPatch]
        static class CollectorPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (MethodInfo m in typeof(IPickupCollector).GetMethods()) yield return AccessTools.Method(typeof(SeinPickupProcessor), m.Name);
            }
            static bool Prefix(SeinPickupProcessor __instance, object[] __args)
            {
                if (!Services.Net.Enabled) return true;
                // Covers direct/proximity callers as well as touch-context callbacks.
                if (StoryHost.Current.IsPuppet(__instance.Sein)) return false;
                if (__instance.Sein != Own || !Services.Net.Connected) return true;
                PickupBase p = (PickupBase)__args[0];
                if (p == s_synthetic) return true;
                if (p.IsCollected) return false;
                MoonGuid guid = PickupGuid(p);
                if (Services.Net.IsHost) { if (ValidGuid(guid)) s_pending.RecordPickup(guid); return true; }
                if (!Services.Session.Ready) return false;
                ActionKind kind = Kind(p);
                FieldInfo amount = p.GetType().GetField("Amount"); int n = amount == null ? 1 : (int)amount.GetValue(p);
                ExpOrbPickup orb = p as ExpOrbPickup;
                SendAction(kind, guid, n, orb == null ? 0 : (int)orb.MessageType);
                if (kind == ActionKind.MarkOnly) return true; // current health/energy belong to the collecting player
                p.Collected(); return false;
            }
        }
        [HarmonyPatch(typeof(PickupBase), "Collected")]
        static class SyntheticCollectedPatch
        {
            static bool Prefix(PickupBase __instance)
            { if (__instance != s_synthetic) return true; __instance.IsCollected = true; return false; }
        }
        [HarmonyPatch(typeof(SeinLevel), "GainExperience")]
        static class DirectExperiencePatch
        {
            static bool Prefix(SeinLevel __instance, int amount)
            {
                if (!Guest || !Ready || __instance != Own.Level) return true;
                if (Services.Session.Ready) SendAction(ActionKind.DirectExperience, MoonGuid.Empty, amount, 0);
                return false;
            }
        }
        [HarmonyPatch(typeof(SeinLevel), "LevelUp")]
        static class GuestLevelPatch
        {
            static bool Prefix(SeinLevel __instance) { return !(Guest && Ready && __instance == Own.Level); }
        }
        // T-047: no purchase forwarding. Every player buys with their own points through the game's code, and only
        // the buyer sees the flash and hears the sound (#9 / T-028). A purchase is a save point online too (T-043, SaveSync).
        [HarmonyPatch(typeof(GetAbilityPedestal), "ActivatePedestal")]
        static class PedestalPatch
        {
            static bool Prefix(GetAbilityPedestal __instance)
            {
                if (Services.Net.Enabled && StoryHost.Current.Inside) return false;
                if (CutsceneGuard.ReplayingPedestal) return true;
                if (CutsceneGuard.Off && Guest)
                {
                    if (Services.Session.Ready && __instance.CurrentState != GetAbilityPedestal.States.Completed)
                        SendAction(ActionKind.Pedestal, __instance.MoonGuid, 0, (int)__instance.Ability);
                    return false;
                }
                if (!Guest) { CutsceneGuard.LocalActivation(__instance); return true; }
                if (Services.Session.Ready && __instance.CurrentState != GetAbilityPedestal.States.Completed)
                {
                    CutsceneGuard.LocalActivation(__instance);
                    SendAction(ActionKind.Pedestal, __instance.MoonGuid, 0, (int)__instance.Ability);
                    return true;
                }
                return false;
            }
        }
        // Door insertion is inline in FixedUpdate. Preserve its animation and slot transaction
        // locally and forward exact resource deltas; world blobs carry the resulting door state.
        [HarmonyPatch]
        static class InventoryDeltaPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SeinInventory), "SpendKeystones");
                yield return AccessTools.Method(typeof(SeinInventory), "CollectKeystones");
                yield return AccessTools.Method(typeof(SeinInventory), "SpendMapstone");
                yield return AccessTools.Method(typeof(SeinInventory), "CollectMapstone");
            }
            static void Prefix(SeinInventory __instance, out int[] __state)
            { __state = Guest && Ready && __instance == Own.Inventory ? new int[] { __instance.Keystones, __instance.MapStones } : null; }
            static void Postfix(SeinInventory __instance, int[] __state)
            {
                if (__state == null || !Services.Session.Ready || StoryHost.Current.WorldApplying) return;
                int keys = __instance.Keystones - __state[0], maps = __instance.MapStones - __state[1];
                if (keys != 0) SendAction(ActionKind.KeysDelta, MoonGuid.Empty, keys, 0);
                if (maps != 0) SendAction(ActionKind.MapsDelta, MoonGuid.Empty, maps, 0);
            }
        }
        [HarmonyPatch(typeof(MapStone), "FixedUpdate")]
        static class MapSpendPatch
        {
            static void Prefix(out int __state) { __state = Guest && Ready ? Own.Inventory.MapStones : int.MinValue; }
            static void Postfix(int __state)
            {
                if (__state != int.MinValue && Services.Session.Ready && Own.Inventory.MapStones != __state)
                    SendAction(ActionKind.MapsDelta, MoonGuid.Empty, Own.Inventory.MapStones - __state, 0);
            }
        }
        [HarmonyPatch]
        static class EnergyDoorScope
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(EnergyDoor), "FixedUpdate");
                yield return AccessTools.Method(typeof(EnergyDoor), "RestoreOrbs");
            }
            static void Prefix() { s_energyDoorDepth++; }
            static Exception Finalizer(Exception __exception) { s_energyDoorDepth--; return __exception; }
        }
        [HarmonyPatch]
        static class EnergyDoorDeltaPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SeinEnergy), "Spend");
                yield return AccessTools.Method(typeof(SeinEnergy), "Gain");
            }
            static void Prefix(SeinEnergy __instance, out float __state)
            { __state = Guest && Ready && s_energyDoorDepth > 0 && __instance == Own.Energy ? __instance.Current : float.NaN; }
            static void Postfix(SeinEnergy __instance, float __state)
            {
                if (!float.IsNaN(__state) && Services.Session.Ready && !StoryHost.Current.WorldApplying && __instance.Current != __state)
                    SendAction(ActionKind.DoorEnergyDelta, MoonGuid.Empty, Mathf.RoundToInt((__instance.Current - __state) * 1000f), 0);
            }
        }
        [HarmonyPatch]
        static class NoRemoteDoorRefund
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DoorWithSlots), "RestoreOrbs");
                yield return AccessTools.Method(typeof(EnergyDoor), "RestoreOrbs");
            }
            static bool Prefix() { return !Services.Net.Enabled || !StoryHost.Current.WorldApplying; }
        }
        [HarmonyPatch(typeof(GameController), "ResetInputLocks")]
        static class PreserveLocalInputLocks
        {
            static bool Prefix() { return !Services.Net.Enabled || !StoryHost.Current.WorldApplying; }
        }
        [HarmonyPatch(typeof(GameController), "RestoreCheckpoint")]
        static class ResetPickupLedger
        {
            static void Postfix() { s_pending.ClearPickups(); s_sent = false; }
        }
    }
}
