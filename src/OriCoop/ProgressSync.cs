using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Only the host mutates shared progress. Actions have session-local sequence numbers;
    // baked pickup GUIDs additionally deduplicate two players touching the same pickup.
    public static class ProgressSync
    {
        const byte MsgProgress = 20, MsgAction = 21, MsgSkillStatus = 22;
        enum ActionKind : byte { Keystone, Experience, SkillPoint, MaxEnergy, MaxHealth, MapStone, MarkOnly,
            DirectExperience, KeysDelta, MapsDelta, DoorEnergyDelta, Skill, Pedestal }

        sealed class State
        {
            public ulong Abilities;
            public int MaxHealth, Keys, Maps, Collected, Experience, Level, Points;
            public float MaxEnergy;
            public void Write(BinaryWriter w)
            {
                w.Write(Abilities); w.Write(MaxHealth); w.Write(MaxEnergy); w.Write(Keys); w.Write(Maps);
                w.Write(Collected); w.Write(Experience); w.Write(Level); w.Write(Points);
            }
            public static State Read(BinaryReader r)
            {
                State s = new State();
                s.Abilities = r.ReadUInt64(); s.MaxHealth = r.ReadInt32(); s.MaxEnergy = r.ReadSingle();
                s.Keys = r.ReadInt32(); s.Maps = r.ReadInt32(); s.Collected = r.ReadInt32();
                s.Experience = r.ReadInt32(); s.Level = r.ReadInt32(); s.Points = r.ReadInt32(); return s;
            }
            public ulong Hash()
            {
                MemoryStream m = new MemoryStream(); BinaryWriter w = new BinaryWriter(m); Write(w); w.Flush();
                ulong h = 14695981039346656037UL;
                foreach (byte b in m.ToArray()) { h ^= b; h = unchecked(h * 1099511628211UL); }
                return h;
            }
        }

        sealed class PendingAction
        {
            public uint Sequence;
            public ActionKind Kind;
            public MoonGuid Guid;
            public int Amount, Extra;
        }

        static readonly HashSet<ulong> s_actions = new HashSet<ulong>();
        static readonly HashSet<MoonGuid> s_collected = new HashSet<MoonGuid>();
        static readonly Queue<PendingAction> s_pending = new Queue<PendingAction>();
        static State s_received;
        static uint s_sequence;
        static ulong s_sentHash;
        static bool s_sent, s_applying;
        static int s_energyDoorDepth;
        static PickupBase s_synthetic;
        static float s_next;
        static int s_loggedCollector;
        static float s_collectorWarningAt;
        public static int ActionsApplied, ActionsRejected;
        public static bool SkillPurchaseForwardingEnabled = true;

        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
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
                Plugin.Log.LogInfo("net: resolved local pickup collector " + processor.GetInstanceID()
                    + " boundTo=" + player.GetInstanceID() + " PickupHandler=" + (player.PickupHandler != null));
            }
            return processor;
        }
        static bool Guest { get { return NetPlay.Connected && !NetPlay.IsHost && !s_applying; } }
        static bool ValidGuid(MoonGuid g) { return !object.ReferenceEquals(g, null) && g != MoonGuid.Empty; }
        static MoonGuid PickupGuid(PickupBase p)
        {
            // Pooled enemy orbs may retain a prefab GUID; it is not a world pickup identity.
            ExpOrbPickup orb = p as ExpOrbPickup;
            return orb != null && orb.MessageType == ExpOrbPickup.ExpOrbMessageType.None ? MoonGuid.Empty : p.MoonGuid;
        }

        public static void Init()
        {
            NetPlay.On(MsgProgress, ReceiveProgress); NetPlay.On(MsgAction, ReceiveAction);
            NetPlay.On(MsgSkillStatus, delegate(BinaryReader r) {
                bool enabled = r.ReadBoolean();
                if (!NetPlay.IsHost) { SkillPurchaseForwardingEnabled = enabled; Plugin.Log.LogWarning("net: skill purchase forwarding enabled=" + enabled); }
            });
            NetPlay.SessionStarted += Reset; NetPlay.SessionEnded += Reset;
            // SharedProgress's proximity postfix bypasses trigger callbacks. Disable that local-coop
            // shortcut online, where touching a puppet must never collect on behalf of its owner.
            Type t = typeof(SharedProgress).GetNestedType("PickupPatch", BindingFlags.NonPublic);
            if (t != null) Plugin.H.Patch(AccessTools.Method(t, "Postfix"),
                new HarmonyMethod(typeof(ProgressSync), "LocalPickupShortcut"), null, null, null, null);
        }
        static bool LocalPickupShortcut() { return !NetPlay.Enabled; }
        static void Reset()
        {
            s_actions.Clear(); s_collected.Clear(); s_pending.Clear(); s_received = null;
            s_sequence = 0; s_sent = false; s_next = 0; ActionsApplied = ActionsRejected = 0;
            s_loggedCollector = 0; s_collectorWarningAt = 0;
            SkillPurchaseForwardingEnabled = true;
        }

        static State Capture()
        {
            SeinCharacter p = Own; State s = new State();
            for (int i = 0; i < 43; i++) if (p.PlayerAbilities.Abilities[i].HasAbility) s.Abilities |= 1UL << i;
            s.MaxHealth = p.Mortality.Health.MaxHealth; s.MaxEnergy = p.Energy.Max;
            s.Keys = p.Inventory.Keystones; s.Maps = p.Inventory.MapStones; s.Collected = p.Inventory.SkillPointsCollected;
            s.Experience = p.Level.Experience; s.Level = p.Level.Current; s.Points = p.Level.SkillPoints; return s;
        }
        public static ulong Hash() { return Ready ? Capture().Hash() : 0; }
        public static void Tick()
        {
            // Snapshots (abilities, health/energy, inventory, level) only need both progress states present; they
            // no longer wait for NetPlay.LocalInGameplay(). A relay run left it false after a half-run scene story
            // and the guest never got the host's WallJump=false (CUT reset). The host's pickup-action queue still
            // needs a live local player.
            ReportStall();
            if (!NetPlay.Connected || !SaveSync.Ready || !Ready) return;
            if (!NetPlay.IsHost && s_received != null) { State s = s_received; s_received = null; s_receivedAt = -1f; Apply(s); }
            if (NetPlay.IsHost && NetPlay.LocalInGameplay())
            {
                while (s_pending.Count > 0)
                {
                    PendingAction pending = s_pending.Peek();
                    if (pending.Kind < ActionKind.MarkOnly && PickupCollector(Own) == null)
                    {
                        if (Time.realtimeSinceStartup >= s_collectorWarningAt)
                        {
                            s_collectorWarningAt = Time.realtimeSinceStartup + 5f;
                            Plugin.Log.LogWarning("net: waiting for bound local pickup collector; queued seq=" + pending.Sequence);
                        }
                        break;
                    }
                    PendingAction a = s_pending.Dequeue();
                    try { ApplyAction(a); }
                    catch (Exception e) { Plugin.Log.LogWarning("net: progress action disabled seq=" + a.Sequence + " " + a.Kind + ": " + e); }
                }
            }
            if (Time.realtimeSinceStartup < s_next) return;
            s_next = Time.realtimeSinceStartup + 0.1f;
            if (NetPlay.IsHost && (!s_sent || Hash() != s_sentHash)) SendSnapshot();
        }
        public static void SendSnapshot(int seat = 0)
        {
            if (!NetPlay.IsHost || !NetPlay.Connected || !Ready || !SaveSync.Ready) return;
            State s = Capture();
            if (seat == 0)
            { NetPlay.Send(MsgProgress, true, s.Write); s_sentHash = s.Hash(); s_sent = true; }
            else NetPlay.SendTo(seat, MsgProgress, true, s.Write);
        }
        static float s_receivedAt = -1f, s_unsentAt = -1f, s_stallCheckAt; static bool s_stallLogged;
        // One line per stall (> 3 s) with every gate, so a stuck sync names its cause in the log.
        static void ReportStall()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_stallCheckAt) return;
            s_stallCheckAt = now + 0.5f;
            bool unsent = NetPlay.IsHost && NetPlay.Connected && Ready && (!s_sent || Hash() != s_sentHash);
            if (!unsent) s_unsentAt = -1f; else if (s_unsentAt < 0) s_unsentAt = now;
            bool stalled = s_receivedAt >= 0 && now - s_receivedAt > 3f || s_unsentAt >= 0 && now - s_unsentAt > 3f;
            if (!stalled) { s_stallLogged = false; return; }
            if (s_stallLogged) return;
            s_stallLogged = true;
            GameController gc = GameController.Instance;
            Plugin.Log.LogWarning("net: progress sync stalled " + (NetPlay.IsHost ? "(host change not sent)" : "(guest snapshot not applied)")
                + " connected=" + NetPlay.Connected + " saveReady=" + SaveSync.Ready + " progressReady=" + Ready
                + " inGameplay=" + NetPlay.LocalInGameplay() + " loading=" + (gc != null && gc.IsLoadingGame)
                + " sein=" + (Characters.Sein != null) + " seinActive=" + (Characters.Sein != null && Characters.Sein.gameObject.activeInHierarchy)
                + " seinIsP2=" + (Characters.Sein != null && P2Context.P2 != null && Characters.Sein == P2Context.P2));
        }
        static void ReceiveProgress(BinaryReader r)
        {
            State s = State.Read(r); if (!NetPlay.IsHost) { s_received = s; if (s_receivedAt < 0) s_receivedAt = Time.realtimeSinceStartup; }
        }
        static void Apply(State s)
        {
            SeinCharacter p = Own; bool changed = false; s_applying = true;
            try
            {
                for (int i = 0; i < 43; i++)
                {
                    bool value = (s.Abilities & (1UL << i)) != 0;
                    if (p.PlayerAbilities.Abilities[i].HasAbility != value) { p.PlayerAbilities.Abilities[i].HasAbility = value; changed = true; }
                }
                p.Mortality.Health.MaxHealth = s.MaxHealth; p.Energy.Max = s.MaxEnergy;
                p.Inventory.Keystones = s.Keys; p.Inventory.MapStones = s.Maps; p.Inventory.SkillPointsCollected = s.Collected;
                p.Level.Experience = s.Experience; p.Level.Current = s.Level; p.Level.SkillPoints = s.Points;
                // Prevent the guest's XP visual interpolation from independently awarding a level.
                p.Level.ExperienceVisualMin = s.Experience; p.Level.ExperienceVisualMax = s.Experience;
                if (changed && p.Prefabs != null) p.Prefabs.EnsureRightPrefabsAreThereForAbilities();
                RefreshSkills();
            }
            finally { s_applying = false; }
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
            NetPlay.Send(MsgAction, true, delegate(BinaryWriter w)
            {
                w.Write(seq); w.Write((byte)kind); w.Write((guid ?? MoonGuid.Empty).ToByteArray()); w.Write(amount); w.Write(extra);
            });
        }
        static void ReceiveAction(BinaryReader r)
        {
            PendingAction a = new PendingAction(); a.Sequence = r.ReadUInt32(); a.Kind = (ActionKind)r.ReadByte();
            a.Guid = new MoonGuid(r); a.Amount = r.ReadInt32(); a.Extra = r.ReadInt32();
            if (NetPlay.IsHost && s_actions.Add(((ulong)NetPlay.Sender << 32) | a.Sequence)) s_pending.Enqueue(a);
        }
        static void ApplyAction(PendingAction a)
        {
            SeinCharacter p = Own;
            bool pickup = a.Kind <= ActionKind.MarkOnly;
            if (pickup && ValidGuid(a.Guid) && !s_collected.Add(a.Guid))
            { ActionsRejected++; Plugin.Log.LogInfo("net: duplicate pickup ignored " + a.Guid); return; }
            s_applying = true;
            try
            {
                if (pickup)
                {
                    if (a.Kind != ActionKind.MarkOnly) ApplyPickup(a);
                    if (ValidGuid(a.Guid)) WorldSync.MarkCollected(a.Guid);
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
                    case ActionKind.Skill: BuySkill((AbilityType)a.Extra); break;
                    case ActionKind.Pedestal:
                        GetAbilityPedestal pedestal = WorldSync.FindAny(a.Guid) as GetAbilityPedestal;
                        AbilityType ability = pedestal != null ? pedestal.Ability : (AbilityType)a.Extra;
                        if (!Enum.IsDefined(typeof(AbilityType), ability)) break;
                        p.PlayerAbilities.SetAbility(ability, true); WorldSync.MarkPedestal(a.Guid); break;
                }
                ActionsApplied++;
                Plugin.Log.LogInfo("net: progress action seq=" + a.Sequence + " " + a.Kind + " amount=" + a.Amount + " keys=" + p.Inventory.Keystones + " xp=" + p.Level.Experience);
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
        static void ApplyPickup(PendingAction a)
        {
            // Real loaded pickups preserve their normal sequences. For an unloaded area / dynamic
            // orb, a detached inactive argument carries the amount through the very same collector.
            PickupBase pickup = ValidGuid(a.Guid) ? WorldSync.FindAny(a.Guid) as PickupBase : null;
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
        static void BuySkill(AbilityType ability)
        {
            if (Own.PlayerAbilities.HasAbility(ability)) return; // a repeated press before the reply is harmless
            SkillTreeManager m = SkillTreeManager.Instance;
            if (!SkillPurchaseForwardingEnabled) return;
            if (m == null)
            {
                DisableSkills("host SkillTreeManager absent (decomp SkillTreeManager.cs:142)"); return;
            }
            SkillItem previous = m.CurrentSkillItem;
            foreach (SkillItem item in m.GetComponentsInChildren<SkillItem>(true))
            {
                if (item.Ability != ability || Own.PlayerAbilities.HasAbility(ability)) continue;
                item.HasSkillItem = false;
                if (!item.CanEarnSkill) return;
                m.CurrentSkillItem = item;
                try { m.OnMenuItemPressed(); }
                finally { m.CurrentSkillItem = previous; }
                return;
            }
            DisableSkills("host skill item unavailable: " + ability + " (decomp SkillTreeManager.cs:142)");
        }
        static void DisableSkills(string reason)
        {
            SkillPurchaseForwardingEnabled = false;
            Plugin.Log.LogWarning("net: SkillPurchaseForwardingEnabled=false: " + reason);
            NetPlay.Send(MsgSkillStatus, true, delegate(BinaryWriter w) { w.Write(false); });
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
                if (!NetPlay.Enabled) return true;
                // Covers direct/proximity callers as well as touch-context callbacks.
                if (Players.OfSein(__instance.Sein) != null) return false;
                if (__instance.Sein != Own || !NetPlay.Connected) return true;
                PickupBase p = (PickupBase)__args[0];
                if (p == s_synthetic) return true;
                if (p.IsCollected) return false;
                MoonGuid guid = PickupGuid(p);
                if (NetPlay.IsHost) { if (ValidGuid(guid)) s_collected.Add(guid); return true; }
                if (!SaveSync.Ready) return false;
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
                if (SaveSync.Ready) SendAction(ActionKind.DirectExperience, MoonGuid.Empty, amount, 0);
                return false;
            }
        }
        [HarmonyPatch(typeof(SeinLevel), "LevelUp")]
        static class GuestLevelPatch
        {
            static bool Prefix(SeinLevel __instance) { return !(Guest && Ready && __instance == Own.Level); }
        }
        [HarmonyPatch(typeof(SkillTreeManager), "OnMenuItemPressed")]
        static class SkillPatch
        {
            static bool Prefix(SkillTreeManager __instance)
            {
                if (!Guest) return true;
                SkillItem item = __instance.CurrentSkillItem;
                if (SaveSync.Ready && SkillPurchaseForwardingEnabled && item != null && !item.HasSkillItem && item.CanEarnSkill)
                    SendAction(ActionKind.Skill, MoonGuid.Empty, 0, (int)item.Ability);
                return false;
            }
        }
        [HarmonyPatch(typeof(GetAbilityPedestal), "ActivatePedestal")]
        static class PedestalPatch
        {
            static bool Prefix(GetAbilityPedestal __instance)
            {
                if (NetPlay.Enabled && P2Context.Inside) return false;
                if (CutsceneGuard.ReplayingPedestal) return true;
                if (CutsceneGuard.Off && Guest)
                {
                    if (SaveSync.Ready && __instance.CurrentState != GetAbilityPedestal.States.Completed)
                        SendAction(ActionKind.Pedestal, __instance.MoonGuid, 0, (int)__instance.Ability);
                    return false;
                }
                if (!Guest) { CutsceneGuard.LocalActivation(__instance); return true; }
                if (SaveSync.Ready && __instance.CurrentState != GetAbilityPedestal.States.Completed)
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
                if (__state == null || !SaveSync.Ready || WorldSync.Applying) return;
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
                if (__state != int.MinValue && SaveSync.Ready && Own.Inventory.MapStones != __state)
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
                if (!float.IsNaN(__state) && SaveSync.Ready && !WorldSync.Applying && __instance.Current != __state)
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
            static bool Prefix() { return !NetPlay.Enabled || !WorldSync.Applying; }
        }
        [HarmonyPatch(typeof(GameController), "ResetInputLocks")]
        static class PreserveLocalInputLocks
        {
            static bool Prefix() { return !NetPlay.Enabled || !WorldSync.Applying; }
        }
        [HarmonyPatch(typeof(GameController), "RestoreCheckpoint")]
        static class ResetPickupLedger
        {
            static void Postfix() { s_collected.Clear(); s_sent = false; }
        }
    }
}
