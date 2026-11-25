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
        static uint s_sequence, s_hostGeneration = 1, s_snapshotRevision;
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
        // T-214: pickups a guest collected that this host could not find (scene not loaded, or a copy the save registry
        // does not have). Each is marked collected as soon as it is live here, and the host's own touch gives nothing.
        sealed class Unmarked { public MoonGuid Id, Scene; public bool HasSpot, Registered; public Vector2 Spot; }
        static readonly List<Unmarked> s_unmarked = new List<Unmarked>();
        static float s_unmarkedAt;
        public static int LateMarks, HostRetakesBlocked;
        // A progress snapshot changes everyone's capacity. Only the host's result for this exact request grants a refill.
        static readonly bool s_mutateCellRefillsHost = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_CELL_REFILLS_HOST") == "1";
        static readonly bool s_mutateRewardLoser = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_CELL_REWARD_LOSER") == "1";
        public static int CellRefills;
        public static int UnmarkedPickups { get { return s_unmarked.Count; } }
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
            PickupPlace.MutateGuidOnly = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PICKUP_GUID_ONLY") == "1";
            if (PickupPlace.MutateGuidOnly) Services.Log.Warn("net: MUTATION a pickup's GUID alone is its identity again (ORICOOP_MUTATE_PICKUP_GUID_ONLY, pre-T-214)");
            Services.Net.On(MsgProgress, ReceiveProgress); Services.Net.On(MsgAction, ReceiveAction);
            Services.Net.On(CellResult.Message, ReceiveCellResult);
            Services.Net.On(MsgSkillStatus, delegate(BinaryReader r) {
                bool enabled = ProgressWire.ReadSkillStatus(r);
                if (!Services.Net.IsHost) { SkillPurchaseForwardingEnabled = ProgressWire.ApplySkillStatus(false, enabled, SkillPurchaseForwardingEnabled); Services.Log.Warn("net: skill purchase forwarding enabled=" + enabled); }
            });
            Services.Net.SessionStarted += Reset; Services.Net.SessionEnded += Reset;
            Services.Net.PlayerLeft += delegate(int seat) { s_pending.SeatLeft(seat); };       // T-143
            Services.Net.PlayerJoined += delegate(int seat) { s_pending.SeatJoined(seat); s_inbox.SeatJoined(seat); };
            // SharedProgress's proximity postfix bypasses trigger callbacks. Disable that local-coop
            // shortcut online, where touching a puppet must never collect on behalf of its owner.
            Type t = typeof(SharedProgress).GetNestedType("PickupPatch", BindingFlags.NonPublic);
            if (t != null) StoryHost.Current.PatchPickupShortcut(AccessTools.Method(t, "Postfix"));
        }
        static bool LocalPickupShortcut() { return !Services.Net.Enabled; }
        static void Reset()
        {
            s_pending.Reset(); s_inbox.Reset(); s_ownTree.Clear(); s_unmarked.Clear();
            s_hostGeneration = 1; s_snapshotRevision = 0;
            s_sequence = 0; s_sent = false; s_next = 0; ActionsApplied = ActionsRejected = SnapshotGuestLoad = 0;
            s_loggedMissingTree = false;
            SavedPickupCopies.SceneChanged();
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
        internal static void ObserveAuthority(int host)
        {
            if (s_inbox.ObserveHost(host)) AdvanceHostGeneration();
        }
        static void AdvanceHostGeneration()
        {
            s_hostGeneration++; s_snapshotRevision = 0; s_sent = false;
            s_pending.DiscardQueuedCells();
        }
        public static void Tick()
        {
            // Snapshots (abilities, health/energy, inventory, level) only need both progress states present; they
            // no longer wait for NetPlay.LocalInGameplay(). A relay run left it false after a half-run scene story
            // and the guest never got the host's WallJump=false (CUT reset). The host's pickup-action queue still
            // needs a live local player.
            if (Services.Net.Connected) ObserveAuthority(Authority.NetRoute.HostSeat);
            ReportStall();
            if (!Services.Net.Connected || !Ready) return;
            SavedPickupCopies.HideLoaded();
            EnsureSkillTree(Own);
            if (!s_mutateNoSeatSkills && Services.Net.IsHost && SeatSkillSync.HostRestorePending
                && !SaveSync.HostAdopting && !GameController.Instance.IsLoadingGame)
                RestoreHostSkills();
            if (!Services.Session.Ready) return;
            if (!Services.Net.IsHost)
            {
                if (s_ownTree.Held && s_tree == 0) return;
                if (s_inbox.Snapshot != null) { Apply(s_inbox.Take()); SnapshotGuestLoad = SaveSync.GuestLoads; }
                if (s_inbox.HasClaims && Services.Session.LocalInGameplay) FinishCellClaims();
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
                    catch (Exception e) { SendCellResult(a, false); Services.Log.Warn("net: progress action disabled seq=" + a.Sequence + " " + a.Kind + ": " + e); }
                }
            }
            if (Services.Net.IsHost && s_unmarked.Count > 0 && Time.realtimeSinceStartup >= s_unmarkedAt)
            {
                s_unmarkedAt = Time.realtimeSinceStartup + 1f;
                PickupBase[] active = WorldSync.ActivePickups();
                for (int i = s_unmarked.Count - 1; i >= 0; i--)
                {
                    Unmarked u = s_unmarked[i];
                    if (!WorldSync.MarkCollected(u.Id, u.Scene, u.HasSpot, u.Spot, active, u.Registered)) continue;
                    s_unmarked.RemoveAt(i); LateMarks++;
                    Services.Log.Info("net: pickup " + u.Id + " a partner collected earlier is loaded here now: marked collected");
                }
            }
            if (Time.realtimeSinceStartup < s_next) return;
            s_next = Time.realtimeSinceStartup + 0.1f;
            if (Services.Net.IsHost && (!s_sent || Hash() != s_sentHash)) SendSnapshot();
        }
        public static void SendSnapshot(int seat = 0)
        {
            if (!Services.Net.IsHost || !Services.Net.Connected || !Ready || !Services.Session.Ready) return;
            ObserveAuthority(Authority.NetRoute.HostSeat);
            ProgressSnapshot s = Capture(); s.Generation = s_hostGeneration; s.Revision = ++s_snapshotRevision;
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
            float waited = 0f;
            int waiting = Services.Net.IsHost ? SaveSync.WaitingGuest(out waited) : 0;
            bool stalled = s_inbox.ReceivedAt >= 0 && now - s_inbox.ReceivedAt > 3f
                || ProgressStall.Host(s_unsentAt < 0 ? -1f : now - s_unsentAt, Services.Session.Ready, waiting, waited);
            if (!stalled) { s_stallLogged = false; return; }
            if (s_stallLogged) return;
            s_stallLogged = true;
            GameController gc = GameController.Instance;
            Services.Log.Warn("net: progress sync stalled " + (!Services.Net.IsHost ? "(guest snapshot not applied)"
                : s_unsentAt >= 0 ? "(host change not sent)" : "(a guest's checkpoint load is outstanding)")
                + (waiting != 0 ? " P" + waiting + " has not reported its checkpoint load for " + waited.ToString("F0") + " s" : "")
                + " connected=" + Services.Net.Connected + " saveReady=" + Services.Session.Ready + " progressReady=" + Ready
                + " inGameplay=" + Services.Session.LocalInGameplay + " loading=" + (gc != null && gc.IsLoadingGame)
                + " sein=" + (Characters.Sein != null) + " seinActive=" + (Characters.Sein != null && Characters.Sein.gameObject.activeInHierarchy)
                + " seinIsP2=" + (Characters.Sein != null && StoryHost.Current.P2 != null && Characters.Sein == StoryHost.Current.P2));
        }
        static void ReceiveProgress(BinaryReader r)
        {
            int host = Authority.NetRoute.HostSeat;
            ObserveAuthority(host);
            if (!Services.Net.IsHost && Services.Net.Sender == host)
                s_inbox.Receive(host, Services.Net.Sender, ProgressSnapshot.Read(r), Time.realtimeSinceStartup);
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

        internal static void GuestLoaded() { s_inbox.GuestLoaded(); }

        // T-043: both host and guest roll back to their own recorded tree. T-142's live hold is for unrecorded seats.
        public static void HoldOwnSkillTree(bool joined)
        {
            if (Services.Net.IsHost) return;
            ulong bits = 0;
            SkillRecord record = SaveSync.LoadingCheckpointSkills;
            bool recorded = !s_mutateNoSeatSkills && record != null && record.TryGet(CoopSaves.PlayerId, out bits);
            if (!recorded && s_ownTree.Held) return;
            bool liveReady = !recorded && Ready;
            if (!recorded && !liveReady)
            {
                // T-214: a first join from the title has no Ori yet and, without a record of this player, nothing to
                // hold: the seat starts from the room's save (T-047). That is not a loss (playtest 4 logged it as a
                // warning three times). A seat already in the session would lose its tree here; no load path gets here
                // with one (the hold runs in gameplay, or on the title, which a joined guest only reaches by leaving).
                if (!s_loggedMissingTree)
                {
                    if (joined) Services.Log.Warn("net: own skill tree not held before checkpoint (no record and progress not ready)");
                    else Services.Log.Info("net: new seat, no skill record for this player: its skill tree starts from the room's save");
                }
                s_loggedMissingTree = true;
                return;
            }
            ulong hold;
            if (!SkillRestore.GuestHold(recorded, bits, joined, liveReady ? OwnedBits(Own) : 0, out hold)) return;
            s_ownTree.Hold(hold, recorded);
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

        static void SendAction(ActionKind kind, MoonGuid guid, int amount, int extra, PickupBase at = null)
        {
            ProgressAction a = at != null ? Located(at) : new ProgressAction();
            a.Sequence = ++s_sequence; a.Kind = kind; a.Guid = (guid ?? MoonGuid.Empty).ToByteArray(); a.Amount = amount; a.Extra = extra;
            if (CellResult.IsCell(kind) && !s_mutateCellRefillsHost)
            {
                ObserveAuthority(Authority.NetRoute.HostSeat);
                s_inbox.Register(a.Sequence, kind, amount);
            }
            Services.Net.Send(MsgAction, true, a.Write);
        }
        // T-214: where this pickup is on this PC (scene and spot). Every copy of a prefab carries the same GUID.
        internal static ProgressAction Located(PickupBase p)
        {
            Vector3 at = p.transform.position;
            MoonGuid scene = WorldSync.SceneOf(p);
            return new ProgressAction { Scene = scene.ToByteArray(), Registered = ValidGuid(scene) && ValidGuid(p.MoonGuid) && ReferenceEquals(WorldSync.Find(scene, p.MoonGuid), p), HasSpot = true, X = at.x, Y = at.y };
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
            if (pickup && ValidGuid(guid) && (SavedPickupCopies.Contains(guid, a) || !s_pending.RecordPickup(guid, a.Place)))
            { ActionsRejected++; SendCellResult(a, s_mutateRewardLoser); Services.Log.Info("net: duplicate pickup ignored " + guid); return; }
            s_applying = true;
            try
            {
                if (pickup)
                {
                    if (a.Kind != ActionKind.MarkOnly) ApplyPickup(a);
                    if (ValidGuid(guid)) MarkCollected(a, guid);
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
            SendCellResult(a, true);
        }

        static void SendCellResult(ProgressAction a, bool accepted)
        {
            if (!CellResult.IsCell(a.Kind) || a.SenderSeat == 0 || s_mutateCellRefillsHost) return;
            CellResult result = new CellResult { Sequence = a.Sequence, Kind = a.Kind, Accepted = accepted, Generation = s_hostGeneration, Revision = s_snapshotRevision };
            if (accepted) result.Maximum = a.Kind == ActionKind.MaxHealth ? Own.Mortality.Health.MaxHealth : Own.Energy.Max;
            Services.Net.SendTo(a.SenderSeat, CellResult.Message, true, result.Write);
        }

        static void ReceiveCellResult(BinaryReader r)
        {
            int host = Authority.NetRoute.HostSeat;
            ObserveAuthority(host);
            if (!Services.Net.IsHost && Services.Net.Sender == host)
                s_inbox.ReceiveResult(host, Services.Net.Sender, CellResult.Read(r));
        }

        static void FinishCellClaims()
        {
            SeinCharacter own = Own;
            SeinPickupProcessor collector = PickupCollector(own);
            if (collector == null) return;
            CellClaim claim;
            while ((claim = s_inbox.TakeCell(own.Mortality.Health.MaxHealth, own.Energy.Max)) != null)
            {
                bool health = claim.Kind == ActionKind.MaxHealth;
                CellRefills++;
                if (health) own.Mortality.Health.RestoreAllHealth();
                else
                {
                    if (claim.Maximum == claim.Amount && own.SoulFlame != null) own.SoulFlame.FillSoulFlameBar();
                    own.Energy.RestoreAllEnergy();
                }
                ActionMethod sequence = health ? collector.HeartContainerSequence : collector.EnergyContainerSequence;
                if (sequence == null) continue;
                StorySync.EnterLocalOnly(sequence as ActionSequence);
                try { sequence.Perform(null); }
                finally { StorySync.ExitLocalOnly(); }
            }
        }

        // T-214: mark the copy the guest took (its scene and spot). Not found here: remembered, marked once it is live.
        static void MarkCollected(ProgressAction a, MoonGuid guid)
        {
            if (PickupPlace.MutateGuidOnly) { StoryHost.Current.MarkCollected(guid); return; }
            Unmarked u = new Unmarked { Id = guid, Scene = GameStoryHost.ReadGuid(a.Scene ?? new byte[16]), HasSpot = a.HasSpot, Registered = a.Registered, Spot = new Vector2(a.X, a.Y) };
            bool marked = WorldSync.MarkCollected(u.Id, u.Scene, u.HasSpot, u.Spot, null, a.Registered || SavedPickupCopies.MutateAlias);
            if (a.Kind != ActionKind.MarkOnly && (!a.Registered || !marked)) SavedPickupCopies.Remember(guid, a);
            if (marked) return;
            // Only what gives a reward can be taken twice; orbs of current health / energy are each player's own.
            if (a.Kind == ActionKind.MarkOnly) return;
            s_unmarked.Add(u);
            Services.Log.Info("net: pickup " + guid + " collected by a partner is not loaded here; it is marked when it shows up");
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
            // T-214: the copy at the guest's scene and spot. FindAny took any loaded copy of the prefab.
            PickupBase pickup = !ValidGuid(guid) ? null : PickupPlace.MutateGuidOnly ? StoryHost.Current.FindAny(guid) as PickupBase
                : WorldSync.FindPickup(guid, GameStoryHost.ReadGuid(a.Scene ?? new byte[16]), a.HasSpot, new Vector2(a.X, a.Y));
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
            // T-214: a guest's cell raises the shared maximum here; this host's own current health / energy stay.
            bool cell = c != null && !s_mutateCellRefillsHost && CellResult.IsCell(a.Kind);
            ActionMethod sequence = null;
            SeinCharacter own = Own;
            float health = own.Mortality.Health.Amount, shownMin = own.Mortality.Health.VisualMinAmount,
                shownMax = own.Mortality.Health.VisualMaxAmount, energy = own.Energy.Current;
            try
            {
                if (c == null) throw new InvalidOperationException("local pickup processor is not bound to the local Sein");
                // Keep the native stat, achievements and area-completion bookkeeping. Its pose and message belong to the winner.
                if (cell && a.Kind == ActionKind.MaxHealth) { sequence = c.HeartContainerSequence; c.HeartContainerSequence = null; }
                if (cell && a.Kind == ActionKind.MaxEnergy) { sequence = c.EnergyContainerSequence; c.EnergyContainerSequence = null; }
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
            finally
            {
                if (cell && a.Kind == ActionKind.MaxHealth)
                {
                    c.HeartContainerSequence = sequence; own.Mortality.Health.Amount = health;
                    own.Mortality.Health.VisualMinAmount = shownMin; own.Mortality.Health.VisualMaxAmount = shownMax;
                }
                if (cell && a.Kind == ActionKind.MaxEnergy) { c.EnergyContainerSequence = sequence; own.Energy.Current = energy; }
                s_synthetic = null; if (temporary != null) UnityEngine.Object.Destroy(temporary);
            }
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
                if (Services.Net.IsHost)
                {
                    if (!ValidGuid(guid)) return true;
                    if (PickupPlace.MutateGuidOnly) { s_pending.RecordPickup(guid); return true; }
                    if (s_applying) return true;   // a guest's action collecting the real copy here (ApplyPickup); already in the ledger
                    ProgressAction location = Located(p);
                    if (Kind(p) == ActionKind.MarkOnly) return true;
                    if (!SavedPickupCopies.Contains(guid, location) && s_pending.RecordPickup(guid, location.Place))
                    {
                        if (!location.Registered) SavedPickupCopies.Remember(guid, location);
                        return true;
                    }
                    // T-214: a partner collected this very pickup and its reward was given then; it could not be marked
                    // here at that time. No second reward: it just goes.
                    p.IsCollected = true; p.gameObject.SetActive(false); HostRetakesBlocked++;
                    Services.Log.Info("net: pickup " + guid + " was already collected by a partner: removed here without a second reward");
                    return false;
                }
                if (!Services.Session.Ready) return false;
                if (ValidGuid(guid) && SavedPickupCopies.Contains(guid, Located(p)))
                {
                    p.IsCollected = true; p.gameObject.SetActive(false); return false;
                }
                ActionKind kind = Kind(p);
                FieldInfo amount = p.GetType().GetField("Amount"); int n = amount == null ? 1 : (int)amount.GetValue(p);
                ExpOrbPickup orb = p as ExpOrbPickup;
                SendAction(kind, guid, n, orb == null ? 0 : (int)orb.MessageType, p);
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
        [HarmonyPatch(typeof(RestoreCheckpointController), "RestoreCheckpoint")]
        static class HostCheckpointRestore
        {
            static void Prefix()
            {
                if (Services.Net.Connected && Services.Net.IsHost) AdvanceHostGeneration();
            }
        }
        [HarmonyPatch(typeof(GameController), "RestoreCheckpoint")]
        static class ResetPickupLedger
        {
            static void Postfix() { s_pending.ClearPickups(); s_unmarked.Clear(); SavedPickupCopies.SceneChanged(); s_sent = false; }
        }
    }
}
