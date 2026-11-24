using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    public static partial class ForcedSync
    {
        const byte StateMessage = 83, CountMessage = 84, ForceMessage = 85, FireMessage = 86, ReleaseMessage = 97;
        public static readonly bool Off = Environment.GetEnvironmentVariable("ORICOOP_GATE_OFF") == "1";
        const float PresenceMargin = 0.5f, PromptMargin = 3f, WaitGrace = 2f, PullLead = 0.6f;
        static GateTable s_table = new GateTable();
        sealed class Gate
        {
            public string Key;
            public Component Object;
            public Rect Rect;
            public bool Local, Inside;   // Inside: any of this PC's seats is in the area
            // T-110: presence per seat (the primary and each extra local player of this PC).
            public readonly bool[] SeatIn = new bool[GateSeats.MaxSeat + 1], SeatSent = new bool[GateSeats.MaxSeat + 1];
            public readonly float[] SeatRefresh = new float[GateSeats.MaxSeat + 1];
            public float LeftAt = -1f, CountAt, ResolveAt = -1f;   // ResolveAt: when its trigger was last looked for (T-207)
            public int Count, Total, SentCount = -1, SentTotal = -1;
            public byte Missing, SentMissing;
            public float EmptyUntil, AppliedAt = -1f;
            public bool WaitForActivation, Unloading;
            public GatePillars Pillars;
        }
        sealed class PendingFire
        {
            public Gate Gate;
            public float Requested, At;
            public bool Sent, HasHint, Moved;   // T-212: Hint = the forcing PC's trigger position
            public Vector3 Hint;
        }
        static readonly Dictionary<string, Gate> Gates = new Dictionary<string, Gate>();
        static readonly HashSet<string> DroppedByHost = new HashSet<string>();
        static readonly List<PendingFire> Pending = new List<PendingFire>();
        static readonly Dictionary<Trigger, float> NativeRuns = new Dictionary<Trigger, float>();
        static readonly FieldInfo WasTriggered = AccessTools.Field(typeof(CutsceneTrigger), "m_wasTriggered");
        static bool s_firing, s_buttonMet;
        static float s_pullUntil;
        static Gate s_startAfterDeparture;
        // Test-only mutations (T-196, before the fix): the native fire checks the trigger's button again; only a
        // player whose own trigger attempt passed counts as present.
        static readonly bool MutateFireChecksButton = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_GATE_FIRE_BUTTON") == "1";
        static readonly bool MutateNoAdopt = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_GATE_NO_ADOPT") == "1";
        public static int Adopted, Fizzles, MovedToForce, ForceRearms;
        static GameText s_top, s_bottom, s_leave;
        static GlowRing s_ring;
        public static string LocalGateKey { get; private set; }
        public static int LastCount, LastTotal;
        public static float HoldProgress
        {
            get
            {
                float progress = 0f;
                if (LocalGateKey != null)
                    foreach (LocalWait wait in Waits)
                        if (wait != null && wait.State.Key == LocalGateKey) progress = Mathf.Max(progress, wait.Meter.Progress);
                return progress;
            }
        }
        public static int Fires, Forces, Blocked;
        public static bool PillarsShown { get; private set; }
        public static string TopText = "", BottomText = "", LeaveText = "";
        // Test diagnostics: where the ward and the ring actually are.
        public static string VisualDebug()
        {
            string pillars = "none";
            foreach (Gate gate in Gates.Values) if (gate.Pillars != null) pillars = gate.Pillars.Debug(gate.Rect);
            return "pillars=" + pillars + " ring=" + (s_ring == null || s_ring.Go == null ? "none" : s_ring.Go.activeSelf + "@" + s_ring.Go.transform.position + " layer=" + s_ring.Go.layer);
        }
        static float Now { get { return Time.realtimeSinceStartup; } }
        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        static bool Online { get { return !Off && Services.Net.Enabled && Services.Net.Connected && SaveSync.Ready && !P2Context.Inside; } }
        static bool Active { get { return Online || !Off && !Services.Net.Enabled && P2Context.P2 != null; } }
        static bool IsHost { get { return !Services.Net.Enabled || Services.Net.IsHost; } }
        static int Total { get { return GateSeats.Count(RoomSeats(Players.LocalSeats)); } }
        static void Log(string action, string key, int seat, int n, int total)
        {
            Plugin.Log.LogInfo("gate " + action + " key=" + key + " seat=" + seat + " n=" + n + " N=" + total);
            CoopTrace.Event("story", "gate." + action, "key", key, "seat", seat, "n", n, "N", total);
        }
        public static void Init()
        {
            Services.Net.On(StateMessage, ReceiveState); Services.Net.On(CountMessage, ReceiveCount);
            Services.Net.On(ForceMessage, ReceiveForce); Services.Net.On(FireMessage, ReceiveFire);
            Services.Net.On(ReleaseMessage, ReceiveRelease);
            Services.Net.PlayerLeft += Forget;
            Services.Net.SessionEnded += Clear;
        }
        static void Forget(int seat)
        {
            s_table.Forget(seat);
            if (NetPlay.EndingByChoice || Players.RemoteCount != 0) return;
            int[] locals = Players.LocalSeats;
            foreach (LocalWait wait in Waits)
            {
                Gate gate;
                if (wait == null || wait.State.Key == null || !CanUseGate(wait.Ori)
                    || !Gates.TryGetValue(wait.State.Key, out gate) || !TriggerArmed(gate)) continue;
                bool allHere = true;
                foreach (int local in locals)
                {
                    SeinCharacter ori = Players.OriOf(local);
                    if (CanUseGate(ori) && !gate.SeatIn[local] && !Inside(gate, ori.Position)) { allHere = false; break; }
                }
                if (allHere) { s_startAfterDeparture = gate; break; }
            }
        }
        static void StartAfterDeparture()
        {
            Gate gate = s_startAfterDeparture; s_startAfterDeparture = null;
            if (gate == null || !Services.Net.Enabled || Players.RemoteCount != 0 || !NetPlay.LocalInGameplay()
                || !TriggerArmed(gate) || Active && Gates.ContainsKey(gate.Key)) return;
            s_firing = true;
            try { StartNative(gate); }
            finally { s_firing = false; }
        }
        // T-212 (6): the gate fired, the players died soon after and the checkpoint put the game's trigger back; the
        // fired mark was only cleared when the session ended, so the area ignored presses / walking in for the rest of
        // its 15 s. Called on every checkpoint restore (CutsceneGuard.RestoreRepair).
        // Test-only mutation ORICOOP_MUTATE_GATE_KEEP_FIRED_ON_RESTORE: the marks survive the restore again.
        static readonly bool MutateKeepFired = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_GATE_KEEP_FIRED_ON_RESTORE") == "1";
        public static int RestoreClears;
        internal static void CheckpointRestored()
        {
            if (MutateKeepFired) return;
            foreach (PendingFire fire in Pending) s_table.ClearPresence(fire.Gate.Key);
            Array.Clear(Waits, 0, Waits.Length);
            s_table.ClearFired(); DroppedByHost.Clear(); Pending.Clear(); NativeRuns.Clear(); s_pullUntil = 0f; RestoreClears++;
        }
        // T-207 (3): the host knows what became of a fire (the native trigger took it or refused it; the trigger was
        // never found) and tells everyone when it came to nothing: the fired mark goes at once on every PC.
        // Test-only mutation ORICOOP_MUTATE_GATE_NO_RELEASE: nobody is told; each PC guesses from the story clock again.
        static readonly bool MutateNoRelease = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_GATE_NO_RELEASE") == "1";
        public static int Releases;
        static void ReleaseFire(string key, string why, bool drop = false)
        {
            if (MutateNoRelease && !drop) return;
            ApplyRelease(key, drop);
            if (Online) Services.Net.Send(ReleaseMessage, true, delegate(BinaryWriter w) { GateWire.WriteRelease(w, key, drop); });
            Plugin.Log.LogInfo("gate " + (drop ? "dropped" : "fire released") + " (" + why + ") key=" + key);
            CoopTrace.Event("story", drop ? "gate.dropped" : "gate.released", "key", key, "why", why);
        }
        static void ReceiveRelease(BinaryReader r)
        {
            bool drop; string key = GateWire.ReadRelease(r, out drop);
            if (!Online || Services.Net.IsHost || Services.Net.Sender != Services.Net.HostSeat) return;
            if (drop) DroppedByHost.Add(key);
            ApplyRelease(key, drop);
            Plugin.Log.LogInfo("gate " + (drop ? "dropped" : "fire released") + " by the host key=" + key);
        }
        static void ApplyRelease(string key, bool drop)
        {
            Releases++;
            if (drop) DropGate(key);
            else
            {
                ReleaseLocks(key); s_table.Release(key);
                Gate gate;
                if (Gates.TryGetValue(key, out gate)) gate.AppliedAt = -1f;
            }
        }
        static void DropGate(string key)
        {
            ReleaseLocks(key); s_table.Drop(key);
            Gate gate;
            if (Gates.TryGetValue(key, out gate) && gate.Pillars != null) gate.Pillars.Destroy();
            Gates.Remove(key);
            if (LocalGateKey == key) { Select(null); Hide(); }
        }
        internal static void TestMarkFired(string key) { s_table.MarkFired(key, Now); }
        internal static bool TestRecentlyFired(string key) { return s_table.RecentlyFired(key, Now); }
        static void Clear()
        {
            Array.Clear(Waits, 0, Waits.Length);
            foreach (Gate gate in Gates.Values) if (gate.Pillars != null) gate.Pillars.Destroy();
            Gates.Clear(); DroppedByHost.Clear(); Pending.Clear(); NativeRuns.Clear(); s_table = new GateTable();
            s_pullUntil = 0f; Select(null); Hide(); PillarsShown = false;
        }
        // T-111 net test: the host sends a count for a gate that is not in the scene, through the real message, and
        // a guest reads back what its receiver applied.
        internal static void TestSendCount(string key, int n, int total)
        { Services.Net.Send(CountMessage, true, delegate(BinaryWriter w) { GateWire.WriteCount(w, key, (byte)n, (byte)total); }); }
        internal static int TestCount(string key) { Gate gate; return Gates.TryGetValue(key, out gate) ? gate.Count : -1; }
        internal static void TestRemember(Component trigger) { Remember(trigger); }
        internal static void TestForget(string key) { DropGate(key); }
        static Gate Get(string key)
        {
            Gate gate;
            if (!Gates.TryGetValue(key, out gate)) { gate = new Gate { Key = key, Total = Total }; Gates[key] = gate; }
            return gate;
        }
        static void Attach(Gate gate, Component component)
        {
            if (gate.Object != component) gate.Unloading = false;
            gate.Object = component;
            CutsceneTrigger cutscene = component as CutsceneTrigger;
            gate.Rect = cutscene != null ? Utility.RectFromBounds(cutscene.GetComponent<Collider>().bounds)
                : Utility.RectFromBounds(Utility.BoundsFromTransform(component.transform));
        }
        // T-207 (4): the walk over every trigger in memory ran twice a frame for an area this PC has not loaded (Adopt and
        // the ward drawing). An unresolved gate is looked for at most every GateResolve.Retry seconds.
        // Test-only mutation ORICOOP_MUTATE_GATE_SCAN_EVERY_FRAME: every call walks again.
        static readonly bool MutateScanEveryFrame = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_GATE_SCAN_EVERY_FRAME") == "1";
        public static int Scans;
        static void Resolve(Gate gate)
        {
            if (gate.Object != null) return;
            if (!MutateScanEveryFrame && !GateResolve.Due(Now, gate.ResolveAt)) return;
            gate.ResolveAt = Now; Scans++;
            foreach (Trigger trigger in Resources.FindObjectsOfTypeAll<Trigger>())
                if (trigger != null && StorySync.ComponentKey(trigger) == gate.Key) { Attach(gate, trigger); return; }
            foreach (CutsceneTrigger trigger in Resources.FindObjectsOfTypeAll<CutsceneTrigger>())
                if (trigger != null && StorySync.ComponentKey(trigger) == gate.Key) { Attach(gate, trigger); return; }
        }
        static Rect BoundsWithMargin(Gate gate, float margin)
        {
            Rect rect = gate.Rect; rect.xMin -= margin; rect.xMax += margin; rect.yMin -= margin; rect.yMax += margin;
            return rect;
        }
        static bool Contains(Gate gate, Vector3 position, float margin)
        { return gate.Object != null && !gate.Unloading && BoundsWithMargin(gate, margin).Contains((Vector2)position); }
        static bool Inside(Gate gate, Vector3 position) { return Contains(gate, position, PresenceMargin); }
        static bool CanUseGate(SeinCharacter ori)
        {
            return ori != null && GateSeats.CanUseGate(ori.gameObject.activeInHierarchy, Revive.IsDowned(ori),
                ori.Mortality.Health.Amount, Revive.SpectateActive);
        }
        static bool TriggerEnabled(Gate gate) { return gate.Object != null && !gate.Unloading && ((Behaviour)gate.Object).isActiveAndEnabled; }
        static bool TriggerArmed(Gate gate)
        {
            if (!TriggerEnabled(gate)) return false;
            Trigger trigger = gate.Object as Trigger;
            return trigger != null ? trigger.Active : !(bool)WasTriggered.GetValue(gate.Object);
        }
        static bool TriggerDeactivated(Gate gate)
        {
            if (gate.Object == null) return false;
            Trigger trigger = gate.Object as Trigger;
            return !gate.Object.gameObject.activeSelf || !((Behaviour)gate.Object).enabled || gate.Local && trigger != null && !trigger.Active;
        }
        static bool Waiting(Gate gate) { return gate.Local || gate.Count > 0 || Now < gate.EmptyUntil; }
        static void SetCount(Gate gate, int count, int total, byte missing)
        {
            if (gate.Count > 0 && count == 0) gate.EmptyUntil = Now + WaitGrace;
            gate.Count = count; gate.Total = total; gate.Missing = missing;
        }
        static byte RoomSeats(int[] locals)
        {
            byte mask = 0;
            foreach (int seat in locals) if (CanUseGate(Players.OriOf(seat))) mask |= (byte)(1 << (seat - 1));
            for (int seat = 1; seat <= GateSeats.MaxSeat; seat++)
                if (NetPlay.RemoteInSession(seat) && Revive.DownedAtSeat(seat) == null && !BottomHud.RemoteDownOrDead(seat))
                    mask |= (byte)(1 << (seat - 1));
            return mask;
        }
        static void Remember(Component component)
        {
            SeinCharacter ori = Characters.Current as SeinCharacter;
            if (!CanUseGate(ori)) return;
            int seat = Players.SeatOf(ori);
            string key = StorySync.ComponentKey(component);
            if (seat == 0 || s_table.RecentlyFired(key, Now) || !IsHost && DroppedByHost.Contains(key)) return;
            Gate gate = Get(key); Attach(gate, component);
            if (gate.Unloading) return;
            gate.Local = true;
            LocalPresence(gate, Players.LocalSeats, seat);
        }
        // Reports each of this PC's seats; returns whether any of them is inside.
        static bool LocalPresence(Gate gate, int[] seats, int triggerSeat = 0)
        {
            bool any = false, canLock = TriggerArmed(gate) && !s_firing && Now >= s_pullUntil
                && StorySync.RealStories == 0 && StorySync.TakeoverMaskPublic() == 0;
            foreach (int seat in seats)
            {
                if (seat < 1 || seat > GateSeats.MaxSeat) continue;
                SeinCharacter ori = Players.OriOf(seat);
                bool inside = false;
                if (CanUseGate(ori))
                {
                    LocalWait wait = WaitForSeat(seat, ori);
                    inside = !wait.State.CoolingDown(Now) && (wait.State.Key == gate.Key || seat == triggerSeat || Inside(gate, ori.Position));
                    if (inside && canLock) LockInside(gate, wait);
                }
                if (gate.SeatIn[seat] != inside) { gate.SeatIn[seat] = inside; Log(inside ? "enter" : "leave", gate.Key, seat, gate.Count, Total); }
                Presence(gate, seat, inside);
                any |= inside;
            }
            // A seat this PC no longer has (released extra player): forget it here; the host's entry times out.
            for (int seat = 1; seat <= GateSeats.MaxSeat; seat++)
                if (Array.IndexOf(seats, seat) < 0) gate.SeatIn[seat] = gate.SeatSent[seat] = false;
            if (gate.Inside != any) { gate.Inside = any; gate.LeftAt = any ? -1f : Now; }
            return any;
        }
        // T-196: an area somebody waits at counts every player standing in it, not only those whose own trigger attempt
        // started the wait. Sein's pickup trigger needs a key press: the partner standing next to it was not counted
        // until it pressed too, and the first player read "1/2" with everyone there.
        static bool Adopt(Gate gate)
        {
            if (MutateNoAdopt || gate.Local || gate.Count <= 0 || s_table.RecentlyFired(gate.Key, Now)) return false;
            Resolve(gate);
            if (gate.Object == null) return false;
            foreach (int seat in Players.LocalSeats)
            {
                SeinCharacter ori = Players.OriOf(seat);
                if (!CanUseGate(ori) || !Inside(gate, ori.Position)) continue;
                gate.Local = true; Adopted++;
                Plugin.Log.LogInfo("gate joined by standing in it key=" + gate.Key + " seat=" + seat);
                return true;
            }
            return false;
        }
        // T-196: a fire no story followed (the native trigger refused it) must not leave the area dead for 15 s.
        // The rule is GateTable.Fizzled (unit tests "gate fizzle"); seen live when a fire is refused.
        static void ReleaseFizzled()
        {
            foreach (string key in s_table.FizzledKeys(Now, StorySync.LastStoryActivity))
            {
                Fizzles++;
                ReleaseFire(key, "no story began within " + GateTable.FizzleSeconds.ToString("0") + " s");
                if (MutateNoRelease) s_table.Release(key);
                Plugin.Log.LogInfo("gate fire had no effect (no story began within " + GateTable.FizzleSeconds.ToString("0") + " s); released key=" + key);
                CoopTrace.Event("story", "gate.fizzled", "key", key);
            }
        }
        static void Presence(Gate gate, int seat, bool inside)
        {
            if (inside == gate.SeatSent[seat] && (!inside || Now < gate.SeatRefresh[seat])) return;
            gate.SeatSent[seat] = inside; gate.SeatRefresh[seat] = Now + 0.5f;
            if (IsHost) s_table.SetInside(gate.Key, seat, inside, Now);
            else Services.Net.Send(StateMessage, true, delegate(BinaryWriter w) { GateWire.WriteState(w, gate.Key, seat, inside); });
        }
        static IEnumerable<int> SeatsOf(int device)
        { for (int seat = 1; seat <= GateSeats.MaxSeat; seat++) if (NetPlay.DeviceSeat(seat) == device) yield return seat; }
        static void ReceiveState(BinaryReader r)
        {
            bool inside; int seat; string key = GateWire.ReadState(r, out seat, out inside);
            if (!Online || !Services.Net.IsHost || s_table.RecentlyFired(key, Now)) return;
            if (!GateSeats.MayReport(Services.Net.Sender, seat, NetPlay.DeviceSeat(seat))) return;
            bool before = s_table.IsInside(key, seat, Now);
            s_table.SetInside(key, seat, inside, Now); if (inside) Get(key);
            if (before != inside) Log(inside ? "enter" : "leave", key, seat, s_table.Count(key, Now), Total);
        }
        static void ReceiveCount(BinaryReader r)
        {
            byte n, total, missing; string key = GateWire.ReadCount(r, out n, out total, out missing);
            if (!Online || Services.Net.IsHost || Services.Net.Sender != Services.Net.HostSeat || s_table.RecentlyFired(key, Now)) return;
            DroppedByHost.Remove(key);
            Gate gate = Get(key); SetCount(gate, n, total, missing);
            if (LocalGateKey == key) { LastCount = n; LastTotal = total; }
            Log("count", key, Services.Net.Sender, n, total);
        }
        static void ReceiveForce(BinaryReader r)
        {
            bool has; float x, y, z; string key = GateWire.ReadForce(r, out has, out x, out y, out z);
            if (Online && Services.Net.IsHost) Force(key, Services.Net.Sender, has, new Vector3(x, y, z));
        }
        // at: where the forcing PC's trigger is, for a host that has not loaded that area.
        static void Force(string key, int device, bool hasAt, Vector3 at)
        {
            if (s_table.RecentlyFired(key, Now)) return;
            if (device != Players.LocalSeat && !s_table.AnyInside(key, SeatsOf(device), Now))
            {
                Gate gate;
                if (!Gates.TryGetValue(key, out gate) || !Waiting(gate)) return;
                Resolve(gate);
                if (!DeviceNear(gate, device, hasAt, at)) return;
            }
            if (hasAt && !HintPlausible(device, at))
            {
                HintsRefused++; hasAt = false;
                Plugin.Log.LogInfo("gate force: the position in the request is not where the forcing player stands; ignored key=" + key + " seat=" + device);
            }
            Log("force", key, device, s_table.Count(key, Now), Total); Fire(key, hasAt, at);
        }
        static bool DeviceNear(Gate gate, int device, bool hasAt, Vector3 at)
        {
            if (gate.Object == null) return hasAt && HintPlausible(device, at);
            foreach (int seat in SeatsOf(device))
            {
                Vector3 position;
                if (NetPlay.RemotePos(seat, out position) && Contains(gate, position, PromptMargin)) return true;
            }
            return false;
        }
        // The host only goes to a position near one of the forcing PC's players.
        public static int HintsRefused;
        static bool HintPlausible(int device, Vector3 at)
        {
            if (device == Players.LocalSeat) return true;
            foreach (int seat in SeatsOf(device))
                foreach (Puppet puppet in Players.Remotes)
                    if (puppet.Seat == seat && puppet.Sein != null
                        && GateForce.HintNear(puppet.Sein.Position.x - at.x, puppet.Sein.Position.y - at.y)) return true;
            return false;
        }
        static void Fire(string key) { Fire(key, false, Vector3.zero); }
        static void Fire(string key, bool hasAt, Vector3 at)
        {
            if (s_table.RecentlyFired(key, Now) || s_table.Queued(key)) return;
            Gate gate = Get(key);
            if (gate.WaitForActivation && gate.Object != null && !gate.Object.gameObject.activeInHierarchy) return;
            gate.WaitForActivation = false;
            s_table.MarkFired(key, Now, true); s_table.SetQueued(key, true);
            Pending.Add(new PendingFire { Gate = gate, Requested = Now, HasHint = hasAt, Hint = at });
        }
        // T-212 (2): the host has not loaded the forced trigger's area. Its players go to the forcing player's trigger
        // (the fire would pull them there anyway); the request then waits for the area to load.
        static void MoveToHint(PendingFire pending)
        {
            pending.Moved = true; pending.Requested = Now; MovedToForce++;
            foreach (int seat in Players.LocalSeats)
            {
                SeinCharacter ori = Players.OriOf(seat);
                if (!CanUseGate(ori)) continue;
                Plugin.Teleport(ori, pending.Hint + new Vector3((seat - 1) * 0.7f - 1f, 0.5f, 0));
                if (seat == Players.LocalSeat) CutsceneGuard.SnapSpirit("gate");
            }
            Plugin.Log.LogInfo("gate fire: trigger not loaded here; moved to the forcing player's area key=" + pending.Gate.Key);
            CoopTrace.Event("story", "gate.moved", "key", pending.Gate.Key);
        }
        static void ReceiveFire(BinaryReader r)
        {
            float x, y, z; string key = GateWire.ReadFire(r, out x, out y, out z);
            if (!Online || Services.Net.IsHost || Services.Net.Sender != Services.Net.HostSeat || s_table.RecentlyFired(key, Now)) return;
            DroppedByHost.Remove(key);
            s_table.MarkFired(key, Now); Apply(Get(key), new Vector3(x, y, z));
        }
        static void Apply(Gate gate, Vector3 position)
        {
            Resolve(gate);
            foreach (int seat in Players.LocalSeats)
            {
                SeinCharacter ori = Players.OriOf(seat);
                if (!CanUseGate(ori)) continue;
                LocalWait wait = WaitForSeat(seat, ori);
                if (!wait.JoinStory(gate.SeatIn[seat] || Inside(gate, ori.Position))) continue;
                Plugin.Teleport(ori, position + new Vector3((seat - 1) * 0.7f - 1f, 0.5f, 0));
                if (seat == Players.LocalSeat) CutsceneGuard.SnapSpirit("gate");
            }
            Fires++; Log("fire", gate.Key, Players.LocalSeat, gate.Count, gate.Total);
            gate.AppliedAt = Now; s_pullUntil = gate.AppliedAt + PullLead;
            gate.Local = gate.Inside = false; gate.Count = 0; gate.EmptyUntil = 0f;
            Array.Clear(gate.SeatIn, 0, gate.SeatIn.Length); Array.Clear(gate.SeatSent, 0, gate.SeatSent.Length);
            if (gate.Pillars != null) { gate.Pillars.Destroy(); gate.Pillars = null; }
            if (LocalGateKey == gate.Key) { Select(null); Hide(); }
        }
        static void FirePending()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingFire pending = Pending[i]; Gate gate = pending.Gate;
                Resolve(gate);
                if (gate.Object == null || gate.Unloading)
                {
                    ReleaseLocks(gate.Key);
                    if (pending.HasHint && !pending.Moved) MoveToHint(pending);
                    else if (GateForce.GiveUp(Now - pending.Requested, pending.Moved))
                    {
                        // Nothing was fired: the area takes requests again at once (not after the 15 s of a real fire).
                        Plugin.Log.LogInfo("gate fire: trigger not found key=" + gate.Key + " moved=" + pending.Moved);
                        CoopTrace.Event("story", "gate.notfound", "key", gate.Key, "moved", pending.Moved);
                        s_table.SetQueued(gate.Key, false); s_table.ClearPresence(gate.Key); s_table.Release(gate.Key);
                        ReleaseFire(gate.Key, "trigger not found"); Pending.RemoveAt(i);
                    }
                    continue;
                }
                if (!pending.Sent)
                {
                    if (!TriggerEnabled(gate) && !AlreadyRan(pending))
                    {
                        RejectInactive(gate); s_table.SetQueued(gate.Key, false); Pending.RemoveAt(i);
                        continue;
                    }
                    Vector3 position = gate.Object.transform.position;
                    if (Online) Services.Net.Send(FireMessage, true, delegate(BinaryWriter w) { GateWire.WriteFire(w, gate.Key, position.x, position.y, position.z); });
                    Apply(gate, position); pending.Sent = true; pending.At = s_pullUntil;
                }
                if (Now < pending.At) continue;
                s_firing = true;
                try
                {
                    if (!TriggerEnabled(gate))
                    {
                        if (AlreadyRan(pending))
                        {
                            s_table.ConfirmFired(gate.Key);
                            Plugin.Log.LogInfo("gate completed: native trigger already ran key=" + gate.Key);
                            CoopTrace.Event("story", "gate.completed", "key", gate.Key, "why", "native trigger already ran");
                        }
                        else RejectInactive(gate);
                        continue;
                    }
                    if (!StartNative(gate)) ReleaseFire(gate.Key, "refused by the trigger");
                }
                finally
                {
                    s_firing = false;
                    if (!gate.WaitForActivation) s_table.ClearPresence(gate.Key);
                    s_table.SetQueued(gate.Key, false); Pending.RemoveAt(i);
                }
            }
        }
        static bool StartNative(Gate gate)
        {
            Trigger trigger = gate.Object as Trigger;
            if (trigger == null)
            {
                CutsceneTrigger cutscene = (CutsceneTrigger)gate.Object;
                if ((bool)WasTriggered.GetValue(cutscene)) return true;
                StoryStarted();
                WasTriggered.SetValue(cutscene, true); cutscene.Cutscene.ChangeState(cutscene.State);
                return true;
            }
            bool met;
            s_buttonMet = !MutateFireChecksButton;
            try { met = trigger.Condition == null || trigger.Condition.Validate(null); }
            finally { s_buttonMet = false; }
            ActionWithDuration duration = trigger.ActionToRun as ActionWithDuration;
            if (!met || !trigger.Active || trigger.DontTriggerWhileRunning && duration != null && duration.IsPerforming)
            {
                Plugin.Log.LogInfo("gate fire refused by the trigger key=" + gate.Key + " active=" + trigger.Active
                    + " running=" + (duration != null && duration.IsPerforming) + " condition=" + met);
                CoopTrace.Event("story", "gate.refused", "key", gate.Key, "active", trigger.Active, "condition", met);
                return false;
            }
            StoryStarted();
            trigger.DoTrigger(false);
            return true;
        }
        static bool AlreadyRan(PendingFire pending)
        {
            Trigger trigger = pending.Gate.Object as Trigger;
            if (trigger == null) return (bool)WasTriggered.GetValue(pending.Gate.Object);
            float at;
            return NativeRuns.TryGetValue(trigger, out at) && (trigger.TriggerOnce && !trigger.Active || at >= pending.Requested);
        }
        static void RejectInactive(Gate gate)
        {
            if (!gate.Object.gameObject.activeSelf || !((Behaviour)gate.Object).enabled)
            {
                ReleaseFire(gate.Key, "trigger deactivated", true);
                return;
            }
            gate.WaitForActivation = true;
            gate.Local = true; gate.LeftAt = Now; gate.SentCount = -1;
            Transform inactive = gate.Object.transform;
            while (inactive.gameObject.activeSelf) inactive = inactive.parent;
            Plugin.Log.LogWarning("gate kept waiting: inactive trigger hierarchy at " + inactive.name + " key=" + gate.Key);
            ReleaseFire(gate.Key, "inactive trigger has not run");
        }
        static void Select(string key)
        {
            if (LocalGateKey == key) return;
            LocalGateKey = key;
            if (key == null) { LastCount = LastTotal = 0; return; }
            Gate gate = Gates[key]; LastCount = gate.Count; LastTotal = gate.Total;
        }
        public static void Tick()
        {
            StartAfterDeparture();
            if (!Active)
            {
                if (Gates.Count > 0 || Waits[Players.LocalSeat] != null) Clear();
                return;
            }
            int[] locals = Players.LocalSeats;
            UpdateWaits(locals);
            List<string> gone = new List<string>(), deactivated = null;
            bool host = IsHost;
            byte seats = host ? RoomSeats(locals) : (byte)0;
            int total = GateSeats.Count(seats);
            if (host)
            {
                for (int seat = 1; seat <= GateSeats.MaxSeat; seat++)
                    if (NetPlay.RemoteLoading(seat)) s_table.Forget(seat);
                ReleaseFizzled();
            }
            foreach (Gate gate in Gates.Values)
            {
                if (s_table.RecentlyFired(gate.Key, Now)) continue;
                if (host)
                {
                    Resolve(gate);
                    if (TriggerDeactivated(gate))
                    {
                        if (deactivated == null) deactivated = new List<string>();
                        deactivated.Add(gate.Key);
                        continue;
                    }
                }
                Adopt(gate);
                if (gate.Local)
                {
                    bool inside = LocalPresence(gate, locals);
                    if (!inside && gate.LeftAt < 0f) gate.LeftAt = Now;
                    if (!inside && Now - gate.LeftAt >= WaitGrace) gate.Local = false;
                }
                if (host)
                {
                    byte missing = s_table.Missing(gate.Key, seats, Now);
                    SetCount(gate, total - GateSeats.Count(missing), total, missing);
                    if (Online && total > 0 && (gate.Count != gate.SentCount || gate.Total != gate.SentTotal || gate.Missing != gate.SentMissing || gate.Count > 0 && Now >= gate.CountAt))
                    {
                        gate.SentCount = gate.Count; gate.SentTotal = gate.Total; gate.SentMissing = gate.Missing; gate.CountAt = Now + 1f;
                        Gate send = gate;
                        Services.Net.Send(CountMessage, true, delegate(BinaryWriter w) { GateWire.WriteCount(w, send.Key, (byte)send.Count, (byte)send.Total, send.Missing); });
                        Log("count", gate.Key, Players.LocalSeat, gate.Count, gate.Total);
                    }
                    if (s_table.AllHere(gate.Key, seats, Now)) Fire(gate.Key);
                }
                if (!Waiting(gate)) gone.Add(gate.Key);
            }
            if (deactivated != null) foreach (string key in deactivated) ReleaseFire(key, "trigger deactivated", true);
            if (host) FirePending();
            TickForce(locals);
            Vector3 anchor; int promptSeat;
            Gate local = NearbyGate(locals, out anchor, out promptSeat);
            Select(local == null ? null : local.Key);
            if (local != null)
            {
                LastCount = local.Count; LastTotal = local.Total;
                Show(local, anchor, promptSeat);
            }
            else Hide();
            PillarsShown = false;
            foreach (Gate gate in Gates.Values)
            {
                bool show = !s_table.RecentlyFired(gate.Key, Now) && Waiting(gate);
                if (show)
                {
                    Resolve(gate);
                    if (TriggerEnabled(gate))
                    {
                        if (gate.Pillars == null) gate.Pillars = new GatePillars(gate.Rect, gate.Object.transform.position.z);
                        gate.Pillars.Tick(); PillarsShown = true;
                        continue;
                    }
                }
                if (gate.Pillars != null) { gate.Pillars.Destroy(); gate.Pillars = null; }
            }
            foreach (string key in gone) DropGate(key);
        }
        static Gate NearbyGate(int[] seats, out Vector3 anchor, out int promptSeat)
        {
            anchor = Vector3.zero; promptSeat = 0;
            Gate nearest = null; float best = float.MaxValue;
            foreach (Gate gate in Gates.Values)
            {
                if (!Waiting(gate) || s_table.RecentlyFired(gate.Key, Now)) continue;
                Resolve(gate);
                foreach (int seat in seats)
                {
                    SeinCharacter ori = Players.OriOf(seat);
                    if (!CanUseGate(ori)) continue;
                    LocalWait wait = Waits[seat];
                    bool locked = wait != null && wait.State.Key == gate.Key;
                    if (!locked && !Contains(gate, ori.Position, PromptMargin)) continue;
                    float distance = locked ? -1f : Inside(gate, ori.Position) ? 0f : (gate.Rect.center - (Vector2)ori.Position).sqrMagnitude;
                    if (distance > best || distance == best && gate.Key != LocalGateKey) continue;
                    nearest = gate; anchor = ori.Position; best = distance; promptSeat = seat;
                }
            }
            return nearest;
        }
        static string MissingPlayers(byte missing)
        {
            List<string> names = new List<string>();
            for (int seat = 1; seat <= GateSeats.MaxSeat; seat++)
                if ((missing & (1 << (seat - 1))) != 0) names.Add("P" + seat);
            return string.Join(", ", names.ToArray());
        }
        static void Show(Gate gate, Vector3 point, int seat)
        {
            if (s_bottom == null) s_bottom = new GameText("OriCoop_GateWait");
            BottomText = gate.Missing == 0 ? L10n.F("gate.count", LastCount, LastTotal)
                : L10n.F("gate.wait", MissingPlayers(gate.Missing), LastCount, LastTotal);
            Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null;
            Vector3 view = game != null ? game.WorldToViewportPoint(point + new Vector3(0f, 2f, 0f)) : new Vector3(0.5f, 0.5f, 0f);
            float x = Mathf.Clamp(view.x, 0.31f, 0.69f), y = Mathf.Clamp(view.y, 0.15f, 0.89f);
            if (s_bottom.SetText(BottomText)) s_bottom.PlaceViewport(x, y, 0.03f, 0.6f);
            if (Waits[seat] == null || Waits[seat].State.Key != gate.Key) { HideKeys(); return; }

            if (s_top == null) s_top = new GameText("OriCoop_GateForce");
            if (s_leave == null) s_leave = new GameText("OriCoop_GateLeave");
            if (s_ring == null) s_ring = new GlowRing("OriCoop_GateHold");
            bool primary = seat == Players.LocalSeat;
            bool keyboard = !primary && ExtraUsesKeyboard(seat);
            TopText = L10n.F("gate.force", primary ? ButtonIconUtility.GetButtonString(PlayerInput.Instance.Bash) : keyboard ? "<icon>p</>" : "<icon>i</>");
            LeaveText = L10n.F("gate.leave", primary ? ButtonIconUtility.GetButtonString(PlayerInput.Instance.SoulFlame) : keyboard ? "<icon>H</>" : "<icon>f</>");
            if (s_top.SetText(TopText)) s_top.PlaceViewport(x, y + 0.05f, 0.028f, 0.36f);
            if (s_leave.SetText(LeaveText)) s_leave.PlaceViewport(x, y - 0.05f, 0.028f, 0.36f);
            s_ring.Place(point + new Vector3(3.3f, 2.2f, 0f), 0.08f, HoldProgress, 1f);
        }
        internal static bool WaitPromptOnScreen() { return s_bottom != null && s_bottom.OnScreen(false); }
        internal static bool ForcePromptOnScreen() { return s_top != null && s_top.OnScreen(false) && s_top.OnScreen(true); }
        internal static bool LeavePromptOnScreen() { return s_leave != null && s_leave.OnScreen(false) && s_leave.OnScreen(true); }
        static void HideKeys()
        {
            TopText = LeaveText = "";
            if (s_top != null) s_top.Hide(); if (s_leave != null) s_leave.Hide(); if (s_ring != null) s_ring.Hide();
        }
        static void Hide()
        {
            BottomText = "";
            if (s_bottom != null) s_bottom.Hide();
            HideKeys();
        }
        // Stay triggers call DoTrigger every frame: classify each trigger's action once (reflection walk).
        static readonly Dictionary<int, bool> StoryTriggers = new Dictionary<int, bool>();
        static bool IsStoryTrigger(Trigger trigger)
        {
            bool story; int id = trigger.GetInstanceID();
            if (!StoryTriggers.TryGetValue(id, out story)) StoryTriggers[id] = story = StorySync.IsTakeoverAction(trigger.ActionToRun);
            return story;
        }
        [HarmonyPatch(typeof(Trigger), "ProcessTrigger")]
        static class ObserveNativeTrigger
        {
            static void Postfix(Trigger __instance, ref IEnumerator __result)
            {
                if ((__instance is PlayerCollisionTrigger || __instance is PlayerCollisionStayTrigger) && IsStoryTrigger(__instance))
                    __result = Observe(__instance, __result);
            }
            static IEnumerator Observe(Trigger trigger, IEnumerator action)
            {
                while (action.MoveNext()) yield return action.Current;
                if (trigger == null || trigger.ActionToRun == null || SceneFPSTest.IsRunning()) yield break;
                ActionSequence sequence = trigger.ActionToRun as ActionSequence;
                if (sequence == null || sequence.IsRunning || sequence.Index > 0) NativeRuns[trigger] = Now;
            }
        }
        [HarmonyPatch(typeof(Trigger), "DoTrigger", new Type[] { typeof(bool) })]
        static class AreaTrigger
        {
            static bool Prefix(Trigger __instance, bool shouldCheckCondition)
            {
                if (!Active || s_firing || !(Characters.Current is SeinCharacter) || !(__instance is PlayerCollisionTrigger || __instance is PlayerCollisionStayTrigger)
                    || __instance.ActionToRun == null || !IsStoryTrigger(__instance)) return true;
                ActionWithDuration duration = __instance.ActionToRun as ActionWithDuration;
                if (!__instance.Active || __instance.DontTriggerWhileRunning && duration != null && duration.IsPerforming
                    || shouldCheckCondition && __instance.Condition != null && !__instance.Condition.Validate(null)) return true;
                Remember(__instance); Blocked++; return false;
            }
        }
        // T-196: a trigger with a button condition (Sein's "press to pick up") reaches Remember only while a player
        // presses it, so the gate has already counted the press. The native fire comes 0.6 s later, when nobody may be
        // holding the button any more: the trigger refused and the fire was lost without a word (playtest 4). While the
        // gate fires, the trigger's button counts as pressed; its other conditions are checked as before.
        [HarmonyPatch(typeof(ButtonPressedCondition), "Validate")]
        static class GateButton
        {
            static void Postfix(ref bool __result) { if (s_buttonMet) __result = true; }
        }
        [HarmonyPatch(typeof(CutsceneTrigger), "OnTriggerEnter")]
        static class CutsceneArea
        {
            [HarmonyPriority(Priority.Last)]
            static bool Prefix(CutsceneTrigger __instance, Collider collider)
            {
                if (!Active || s_firing || !(Characters.Current is SeinCharacter) || collider == null || collider.gameObject != Characters.Current.GameObject
                    || (bool)WasTriggered.GetValue(__instance) || !StorySync.IsStoryCutscene(__instance.Cutscene)) return true;
                Remember(__instance); Blocked++; return false;
            }
        }
        // Pillars are drawn on the HUD camera like GlowRing: the game camera does not draw our additive meshes.
        // Each frame every pillar's ground point and top are projected through the game camera.
        sealed class GatePillars
        {
            readonly List<GameObject> objects = new List<GameObject>();
            readonly List<Mesh> meshes = new List<Mesh>();
            readonly List<Vector3> bases = new List<Vector3>();
            readonly List<float> heights = new List<float>();
            readonly Material material;
            public GatePillars(Rect rect, float z)
            {
                Shader shader = Shader.Find("Particles/Additive");
                if (shader == null) shader = Shader.Find("Particles/Alpha Blended");
                material = new Material(shader); material.hideFlags = HideFlags.HideAndDontSave; material.renderQueue = 3000;
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                int count = Mathf.Max(1, Mathf.FloorToInt(rect.width / 1.2f) + 1);
                for (int i = 0; i < count; i++)
                {
                    float x = rect.xMin + i * 1.2f; RaycastHit hit;
                    float y = Physics.Raycast(new Vector3(x, rect.yMax + 0.5f, z), Vector3.down, out hit, rect.height + 3f, -1, QueryTriggerInteraction.Ignore) ? hit.point.y : rect.yMin;
                    bases.Add(new Vector3(x, y, z)); heights.Add(2.4f + 1.2f * (Mathf.Sin(i * 17.17f + rect.xMin) * 0.5f + 0.5f));
                    GameObject go = new GameObject("OriCoop_GatePillar"); go.layer = P2Hud.HudLayer();
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    Mesh mesh = new Mesh(); mesh.hideFlags = HideFlags.HideAndDontSave; mesh.MarkDynamic();
                    mesh.vertices = new Vector3[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0),
                        new Vector3(-0.5f, 0.3f, 0), new Vector3(0.5f, 0.3f, 0), new Vector3(-0.5f, 1f, 0), new Vector3(0.5f, 1f, 0) };
                    mesh.triangles = new int[] { 0, 2, 3, 0, 3, 1, 2, 4, 5, 2, 5, 3 }; mesh.RecalculateBounds();
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.sortingOrder = 31990;
                    objects.Add(go); meshes.Add(mesh);
                }
                Tick();
            }
            public void Tick()
            {
                Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = P2Hud.GuiCamera();
                for (int i = 0; i < meshes.Count; i++)
                {
                    GameObject go = objects[i];
                    if (game == null || gui == null) { go.SetActive(false); continue; }
                    Vector3 vb = game.WorldToViewportPoint(bases[i]), vt = game.WorldToViewportPoint(bases[i] + new Vector3(0, heights[i], 0));
                    if (vb.z < 0f || vb.x < -0.1f || vb.x > 1.1f || vb.y < -0.2f || vb.y > 1.1f) { go.SetActive(false); continue; }
                    if (!go.activeSelf) go.SetActive(true);
                    Vector3 b = gui.ViewportToWorldPoint(new Vector3(vb.x, vb.y, 8f)), t = gui.ViewportToWorldPoint(new Vector3(vt.x, vt.y, 8f));
                    float h = (t - b).magnitude;
                    go.transform.position = b; go.transform.rotation = gui.transform.rotation;
                    go.transform.localScale = new Vector3(h * 0.16f, h, 1f);
                    float alpha = 0.8f + 0.2f * Mathf.Sin(Time.time * 2.2f + i * 0.7f);
                    Color low = new Color(0.65f, 0.85f, 1f, alpha * 0.45f), core = new Color(0.75f, 0.95f, 1f, alpha), clear = new Color(0.75f, 0.95f, 1f, 0);
                    meshes[i].colors = new Color[] { low, low, core, core, clear, clear };
                }
            }
            public string Debug(Rect rect)
            {
                if (objects.Count == 0) return "0";
                GameObject go = objects[0]; Renderer r = go.GetComponent<Renderer>();
                return objects.Count + " rect=" + rect + " first=" + go.transform.position + " layer=" + go.layer
                    + " visible=" + (r != null && r.isVisible) + " cam=" + (UI.Cameras.Current != null ? UI.Cameras.Current.Camera.cullingMask.ToString("X") : "?");
            }
            public void Destroy()
            {
                foreach (GameObject go in objects) UnityEngine.Object.Destroy(go);
                foreach (Mesh mesh in meshes) UnityEngine.Object.Destroy(mesh);
                UnityEngine.Object.Destroy(material);
            }
        }
    }
}
