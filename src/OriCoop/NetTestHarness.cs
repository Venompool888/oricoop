using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game;
using HarmonyLib;
using OriCoop.Net;
using OriCoop.Authority;
using OriCoop.Enemies;
using UnityEngine;

namespace OriCoop
{
    // Only the local Ori is driven by this bot. The partner always uses NetPlay's received input.
    public static partial class NetTestHarness
    {
        public static bool BotActive;
        public static int BotButtons;
        public static float BotLX;
        static readonly InputState s_tmp = new InputState();
        static StreamWriter s_track;
        static TestHarness s_harness;
        static bool s_tracking;
        static uint s_ownWritten, s_remoteWritten;
        static PhaseSample s_sample;
        const byte MsgThreeTest = 98;
        static readonly HashSet<int> s_readySeats = new HashSet<int>();
        static readonly Dictionary<int, Dictionary<int, ThreePacket>> s_threePackets = new Dictionary<int, Dictionary<int, ThreePacket>>();
        static double s_goAt, s_pickupAt, s_collectedAt;
        static bool s_threeEnded, s_leaveReleased, s_done;
        internal static bool HostLeftInServerMode;   // T-111: the 3P server-mode check made P1 leave (CoopSaveTest skips phase L)
        static float s_threeRise, s_threeTravel, s_threeError;
        static long s_threeFrames;
        static int ClientCount
        {
            get { int count; return int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_NET_CLIENTS"), out count) ? count : 2; }
        }
        static double Clock()
        { return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency; }
        static void ReadTestSignal(int seat, string signal)
        {
            if (NetPlay.IsHost && signal == "ready") s_readySeats.Add(seat);
            if (NetPlay.IsHost && signal == "locals-finished") localFinishAcks.Add(seat);
            if (NetPlay.IsHost && FourTesting && signal == "4P done") s_fourDoneSeats.Add(seat);
            if (seat == 1 && signal == "leave") s_leaveReleased = true;
            if (seat == 1 && signal == "done") s_done = true;
            double at;
            if (seat == 1 && signal.StartsWith("go:", StringComparison.Ordinal)
                && double.TryParse(signal.Substring(3), NumberStyles.Float, CultureInfo.InvariantCulture, out at)) s_goAt = at;
            if (seat == 1 && signal.StartsWith("pickup:", StringComparison.Ordinal)
                && double.TryParse(signal.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out at)) s_pickupAt = at;
        }
        static void ThreeSessionEnded() { s_threeEnded = true; }

        static IEnumerator StartBarrier(TestHarness h)
        {
            float deadline = Time.realtimeSinceStartup + (ClientCount == 3 || ClientCount == 4 ? 120f : 30f);
            if (NetPlay.IsHost)
            {
                while (s_readySeats.Count < ClientCount - 1 && Time.realtimeSinceStartup < deadline) yield return null;
                h.Check(s_readySeats.Count == ClientCount - 1, (ClientCount == 4 ? "4P " : "net ") + "all guests ready before go");
                if (s_readySeats.Count != ClientCount - 1) yield break;
                s_goAt = Clock() + 1.0;
                NetPlay.SendTestSignal("go:" + s_goAt.ToString("R", CultureInfo.InvariantCulture));
            }
            else
            {
                NetPlay.SendTestSignal("ready");
                while (s_goAt == 0 && Time.realtimeSinceStartup < deadline) yield return null;
                h.Check(s_goAt > 0, (ClientCount == 4 ? "4P " : "net ") + "start signal from host received");
            }
            while (s_goAt > 0 && Clock() < s_goAt) yield return null;
        }

        sealed class ThreePacket
        {
            public int Step, Seat, PuppetCount, SeatMask, Keys, Loads, WorldCount, Applied, Rejected;
            public bool Unique, Joined, Connected, Ended;
            public float Distance, Rise, Travel, Error;
            public long Frames;
            public double CollectedAt;
            public ulong Digest;
            public int DownMask, Skulls, Forwarded, Glows, GlowEffects, RevivedMessages, RevivesSent, Wipes, Restores, MenuMask, StoryCalls, StoryLeaves, StorySent, StoryMirrored, EnemyOwner;
            public bool Ready, Alive, Paused, BannerP3, EnemyLoaded;
            public float EnemyHealth;
            public Vector3 Position, EnemyOrigin;
            public string EnemyKey = "";
            public void Write(BinaryWriter w)
            {
                w.Write(Step); w.Write(Seat); w.Write(PuppetCount); w.Write(SeatMask); w.Write(Keys); w.Write(Loads);
                w.Write(WorldCount); w.Write(Applied); w.Write(Rejected); w.Write(Unique); w.Write(Joined);
                w.Write(Connected); w.Write(Ended); w.Write(Distance); w.Write(Rise); w.Write(Travel); w.Write(Error);
                w.Write(Frames); w.Write(CollectedAt); w.Write(Digest);
                if (ClientCount == 4)
                {
                    w.Write(DownMask); w.Write(Skulls); w.Write(Forwarded); w.Write(Glows); w.Write(GlowEffects);
                    w.Write(RevivedMessages); w.Write(RevivesSent);
                    w.Write(Wipes); w.Write(Restores); w.Write(MenuMask); w.Write(StoryCalls); w.Write(StoryLeaves);
                    w.Write(StorySent); w.Write(StoryMirrored); w.Write(EnemyOwner); w.Write(Ready); w.Write(Alive);
                    w.Write(Paused); w.Write(BannerP3); w.Write(EnemyLoaded); w.Write(EnemyHealth);
                    w.Write(Position.x); w.Write(Position.y); w.Write(Position.z);
                    w.Write(EnemyOrigin.x); w.Write(EnemyOrigin.y); w.Write(EnemyOrigin.z); w.Write(EnemyKey);
                }
            }
            public static ThreePacket Read(BinaryReader r)
            {
                ThreePacket p = new ThreePacket();
                p.Step = r.ReadInt32(); p.Seat = r.ReadInt32(); p.PuppetCount = r.ReadInt32(); p.SeatMask = r.ReadInt32();
                p.Keys = r.ReadInt32(); p.Loads = r.ReadInt32(); p.WorldCount = r.ReadInt32();
                p.Applied = r.ReadInt32(); p.Rejected = r.ReadInt32(); p.Unique = r.ReadBoolean(); p.Joined = r.ReadBoolean();
                p.Connected = r.ReadBoolean(); p.Ended = r.ReadBoolean(); p.Distance = r.ReadSingle();
                p.Rise = r.ReadSingle(); p.Travel = r.ReadSingle(); p.Error = r.ReadSingle();
                p.Frames = r.ReadInt64(); p.CollectedAt = r.ReadDouble(); p.Digest = r.ReadUInt64();
                if (ClientCount == 4)
                {
                    p.DownMask = r.ReadInt32(); p.Skulls = r.ReadInt32(); p.Forwarded = r.ReadInt32(); p.Glows = r.ReadInt32(); p.GlowEffects = r.ReadInt32();
                    p.RevivedMessages = r.ReadInt32(); p.RevivesSent = r.ReadInt32();
                    p.Wipes = r.ReadInt32(); p.Restores = r.ReadInt32(); p.MenuMask = r.ReadInt32(); p.StoryCalls = r.ReadInt32(); p.StoryLeaves = r.ReadInt32();
                    p.StorySent = r.ReadInt32(); p.StoryMirrored = r.ReadInt32(); p.EnemyOwner = r.ReadInt32(); p.Ready = r.ReadBoolean(); p.Alive = r.ReadBoolean();
                    p.Paused = r.ReadBoolean(); p.BannerP3 = r.ReadBoolean(); p.EnemyLoaded = r.ReadBoolean(); p.EnemyHealth = r.ReadSingle();
                    p.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    p.EnemyOrigin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); p.EnemyKey = r.ReadString();
                }
                return p;
            }
        }

        static void StoreThree(ThreePacket p)
        {
            Dictionary<int, ThreePacket> round;
            if (!s_threePackets.TryGetValue(p.Step, out round))
            { round = new Dictionary<int, ThreePacket>(); s_threePackets.Add(p.Step, round); }
            round[p.Seat] = p;
        }
        static void ReadThree(BinaryReader r)
        {
            ThreePacket p = ThreePacket.Read(r);
            if (NetPlay.IsHost)
            {
                p.Seat = NetPlay.Sender;
                for (int seat = 2; seat <= (ClientCount == 4 ? 4 : 3); seat++)
                    if (seat != p.Seat) NetPlay.SendTo(seat, MsgThreeTest, true, p.Write);
            }
            StoreThree(p);
        }
        static ThreePacket Three(int step, int seat) { return s_threePackets[step][seat]; }
        static bool HaveThree(int step, int count)
        {
            Dictionary<int, ThreePacket> round;
            if (!s_threePackets.TryGetValue(step, out round)) return false;
            for (int seat = 1; seat <= count; seat++) if (!round.ContainsKey(seat)) return false;
            return true;
        }
        static IEnumerator ThreeRound(TestHarness h, int step, int count = 3)
        {
            ThreePacket p = new ThreePacket(); p.Step = step; p.Seat = Players.LocalSeat;
            p.PuppetCount = Players.Remotes.Count; p.Unique = true;
            HashSet<int> seats = new HashSet<int>(); seats.Add(p.Seat); p.SeatMask = 1 << p.Seat;
            foreach (Puppet puppet in Players.Remotes)
            { if (puppet.Sein == null || !seats.Add(puppet.Seat)) p.Unique = false; p.SeatMask |= 1 << puppet.Seat; }
            p.Keys = Own.Inventory.Keystones; p.Loads = SaveSync.GuestLoads;
            p.Joined = SaveSync.JoinedHost; p.Distance = SaveSync.GuestJoinDistance;
            p.Digest = WorldSync.Digest(out p.WorldCount);
            p.Applied = ProgressSync.ActionsApplied; p.Rejected = ProgressSync.ActionsRejected;
            p.Connected = Connected; p.Ended = s_threeEnded;
            p.Rise = s_threeRise; p.Travel = s_threeTravel; p.Error = s_threeError; p.Frames = s_threeFrames;
            p.CollectedAt = s_collectedAt;
            if (ClientCount == 4) CaptureFour(p);
            StoreThree(p); NetPlay.Send(MsgThreeTest, true, p.Write);
            float deadline = Time.realtimeSinceStartup + (ClientCount == 4 ? (step == 440 || step == 452 ? 90f : 30f) : 12f);
            while (Connected && !HaveThree(step, count) && Time.realtimeSinceStartup < deadline)
            { if (ClientCount == 4) HoldFour(); yield return null; }
            string prefix = ClientCount == 4 ? "4P" : "3P";
            if (!HaveThree(step, count)) throw new InvalidOperationException(prefix + " barrier " + step + " missing observations");
            h.Note(prefix + " barrier " + step + " received " + count + " seats");
            if (ClientCount == 4)
                for (int seat = 1; seat <= 4; seat++)
                {
                    ThreePacket seen = Three(step, seat);
                    h.Note("4P sample " + step + " P" + seat + " down=" + seen.DownMask + " skull=" + seen.Skulls
                        + " fx=" + seen.Forwarded + "/" + seen.Glows + "/" + seen.GlowEffects + " revivedMsg=" + seen.RevivedMessages
                        + " menu=" + seen.MenuMask + " paused=" + seen.Paused + " travel=" + F(seen.Travel)
                        + " enemy=P" + seen.EnemyOwner + "/" + F(seen.EnemyHealth) + " story=" + seen.StoryCalls + "/" + seen.StoryLeaves
                        + " wipes=" + seen.Wipes + " restores=" + seen.Restores + " loads=" + seen.Loads + " ready=" + seen.Ready);
                }
        }

        static Puppet PuppetAt(int seat)
        { foreach (Puppet puppet in Players.Remotes) if (puppet.Seat == seat) return puppet; return null; }
        static IEnumerator ThreeMovement(TestHarness h, int owner)
        {
            bool moving = Players.LocalSeat == owner;
            Puppet puppet = moving ? null : PuppetAt(owner);
            SeinCharacter observed = moving ? Own : puppet.Sein;
            Vector3 origin = observed.Position;
            s_threeRise = s_threeTravel = 0; s_threeError = 0; s_threeFrames = 0;
            long firstFrame = NetPlay.TestFramesPlayed(owner);
            double error = 0; int samples = 0;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 7f)
            {
                float t = Time.realtimeSinceStartup - start;
                float x = t < 2f ? 1f : (t >= 2.5f && t < 4.5f ? -1f : 0f);
                bool jump = (t >= 1f && t < 1.25f) || (t >= 3.5f && t < 3.75f);
                Bot(moving ? x : 0f, moving && jump ? P2Input.BTN_A : 0);
                s_threeRise = Mathf.Max(s_threeRise, observed.Position.y - origin.y);
                s_threeTravel = Mathf.Max(s_threeTravel, observed.Position.x - origin.x);
                if (!moving) { error += NetPlay.TestAvgError(owner); samples++; }
                yield return null;
            }
            Bot(0, 0);
            if (!moving) { s_threeError = samples == 0 ? float.PositiveInfinity : (float)(error / samples); s_threeFrames = NetPlay.TestFramesPlayed(owner) - firstFrame; }
            h.Note("3P observe P" + owner + " right=" + F(s_threeTravel) + " rise=" + F(s_threeRise) + " AvgError=" + F(s_threeError) + " played=" + s_threeFrames);
        }

        static void CollectSharedPickup()
        {
            GameObject go = new GameObject("OriCoop 3P shared pickup"); go.SetActive(false);
            try
            {
                KeystonePickup pickup = go.AddComponent<KeystonePickup>();
                pickup.MoonGuid = new MoonGuid(0x3350434f, 0x4f505049, 0x434b5550, 1); pickup.Amount = 3;
                pickup.OnValidate();
                s_collectedAt = Clock();
                ProgressSync.PickupCollector(Own).OnCollectKeystonePickup(pickup);
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        static bool s_fourActive, s_fourPinned;
        static Vector3 s_fourPin, s_fourEnemyOrigin;
        static int s_fourForwarded, s_fourGlows, s_fourRevivedMessages, s_fourStoryCalls, s_fourForcedOwner;
        static string s_fourEnemyKey = "";
        static readonly HashSet<int> s_fourDoneSeats = new HashSet<int>();
        static readonly MoonGuid FourStoryId = new MoonGuid(0x34505354, 0x4f525954, 0x45535430, 1);
        static bool FourTesting { get { return TestHarness.Enabled && ClientCount == 4 && s_fourActive; } }
        static bool FourAlive { get { return Own != null && Own.Active && Own.Mortality.Health.Amount > 0 && !Revive.IsDowned(Own); } }
        static bool FourReady
        {
            get { return FourAlive && SaveSync.Ready && NetPlay.LocalInGameplay() && Players.Remotes.Count == 3 && !Revive.AnyDowned; }
        }
        static int FourDownMask()
        { int mask = 0; for (int seat = 1; seat <= 4; seat++) if (Revive.DownedAtSeat(seat) != null) mask |= 1 << seat; return mask; }
        static bool FourSkull(int seat)
        {
            foreach (string name in new[] { "s_icon", "s_skull" })
            {
                object marker = ((Array)AccessTools.Field(typeof(ReviveHud), name).GetValue(null)).GetValue(seat);
                if (marker == null) continue;
                GameObject go = (GameObject)AccessTools.Field(marker.GetType(), "Go").GetValue(marker);
                if (go != null && go.activeInHierarchy) return true;
            }
            return false;
        }
        static void CaptureFour(ThreePacket p)
        {
            p.DownMask = FourDownMask(); p.Ready = FourReady; p.Alive = FourAlive;
            for (int seat = 1; seat <= 4; seat++)
            { if (FourSkull(seat)) p.Skulls |= 1 << seat; if (SessionRules.InMenu(seat)) p.MenuMask |= 1 << seat; }
            p.Forwarded = s_fourForwarded; p.Glows = s_fourGlows; p.GlowEffects = PartnerFx.ReviveEffects;
            p.RevivedMessages = s_fourRevivedMessages; p.RevivesSent = Revive.NetRevivesSent;
            p.Wipes = Revive.NetBothDown; p.Restores = TestHarness.RestoreCheckpointCalls;
            p.Paused = SessionRules.LocalPaused || SessionRules.PausedByPartner;
            p.BannerP3 = SessionRules.PauseText == L10n.F("pause.by", "P3");
            p.StoryCalls = s_fourStoryCalls; p.StoryLeaves = (int)AccessTools.Field(typeof(NetStoryTest), "s_leafCalls").GetValue(null);
            p.StorySent = StorySync.StartsSent; p.StoryMirrored = StorySync.StartsMirrored;
            p.Position = Own.Position; p.EnemyKey = s_fourEnemyKey; p.EnemyOrigin = s_fourEnemyOrigin;
            EnemySync.Probe enemy = EnemySync.Inspect(s_fourEnemyKey);
            p.EnemyLoaded = enemy.Present && enemy.Mirrored && enemy.Known && enemy.PlaceholderLoaded;
            p.EnemyOwner = enemy.OwnerSeat; p.EnemyHealth = enemy.Health;
            p.Frames = NetPlay.LocalTick;
        }
        static void HoldFour()
        {
            if (!FourTesting || s_goAt == 0 || !FourAlive) return;
            Own.Mortality.DamageReciever.IsImmortal = true;
            if (s_fourPinned) { Own.Position = s_fourPin; Own.Speed = Vector3.zero; }
        }
        static IEnumerator FourAwait(TestHarness h, Func<bool> ready, float timeout, string waiting)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (!ready() && Time.realtimeSinceStartup < end) { HoldFour(); yield return null; }
            if (!ready()) h.Note("4P timeout awaiting " + waiting);
        }
        static IEnumerator FourTicks(TestHarness h, int ticks, Action observe = null)
        {
            uint start = NetPlay.LocalTick;
            yield return FourAwait(h, delegate {
                if (observe != null) observe();
                return NetPlay.LocalTick - start >= ticks;
            }, 6f, ticks + " input frames");
        }
        static bool AllFour(int step, Func<ThreePacket, bool> predicate)
        { for (int seat = 1; seat <= 4; seat++) if (!predicate(Three(step, seat))) return false; return true; }
        static IEnumerator FourPlace(TestHarness h, Vector3 anchor)
        {
            s_fourPinned = false; Bot(0, 0);
            float side = Players.LocalSeat == 2 ? 0f : Players.LocalSeat == 3 ? 0.8f : Players.LocalSeat == 4 ? -0.8f : -1.4f;
            Vector3 position;
            if (!EnemyGroundPosition(anchor, side, out position)) position = anchor + Vector3.right * side;
            Plugin.Teleport(Own, position); SessionRules.PositionChanged();
            yield return FourAwait(h, delegate { return Own.PlatformBehaviour.PlatformMovement.IsOnGround; }, 8f, "group on safe ground");
            s_fourPin = Own.Position; s_fourPinned = true;
            yield return FourAwait(h, delegate {
                bool[] have = (bool[])AccessTools.Field(typeof(Revive), "s_haveSafe").GetValue(null);
                Vector3[] safe = (Vector3[])AccessTools.Field(typeof(Revive), "s_safe").GetValue(null);
                return have[Players.LocalSeat] && Vector3.Distance(safe[Players.LocalSeat], s_fourPin) < 0.5f;
            }, 5f, "revive safe-ground cache at group");
        }
        static void FourKill()
        {
            Own.Mortality.DamageReciever.IsImmortal = false;
            Own.Mortality.DamageReciever.ResetInviciblity();
            Own.Mortality.DamageReciever.OnRecieveDamage(new Damage(1000f, Vector2.zero, Own.Position, DamageType.Spikes, null));
        }
        [HarmonyPatch(typeof(PartnerFx), "OnNetPuppetFx")]
        static class FourForwardObservation
        {
            static bool Prepare() { return TestHarness.Enabled && ClientCount == 4; }
            static void Prefix(BinaryReader r)
            {
                if (!FourTesting) return;
                long at = r.BaseStream.Position;
                int seat = r.ReadByte(); r.BaseStream.Position = at;
                if (NetPlay.Sender == 2 && seat == 2) s_fourForwarded++;
            }
        }
        [HarmonyPatch(typeof(Revive), "OnNetRevived")]
        static class FourRevivedObservation
        {
            static bool Prepare() { return TestHarness.Enabled && ClientCount == 4; }
            static void Prefix(BinaryReader r, out bool __state)
            {
                __state = false; if (!FourTesting) return;
                long at = r.BaseStream.Position;
                int seat = r.ReadByte(); r.BaseStream.Position = at;
                __state = seat == 2 && NetPlay.Sender == 2 && Revive.DownedAtSeat(2) != null;
            }
            static void Postfix(bool __state)
            { if (__state && Revive.DownedAtSeat(2) == null) s_fourRevivedMessages++; }
        }
        [HarmonyPatch(typeof(ActionSequence), "Perform")]
        static class FourStoryObservation
        {
            static bool Prepare() { return TestHarness.Enabled && ClientCount == 4; }
            static void Postfix(ActionSequence __instance)
            { if (FourTesting && __instance.MoonGuid == FourStoryId) s_fourStoryCalls++; }
        }
        [HarmonyPatch(typeof(EnemySync), "DesiredOwner")]
        static class FourEnemyOwnerFixture
        {
            static bool Prepare() { return TestHarness.Enabled && ClientCount == 4; }
            static bool Prefix(object a, ref int __result)
            {
                if (!FourTesting || s_fourForcedOwner == 0 || (string)AccessTools.Field(a.GetType(), "Key").GetValue(a) != s_fourEnemyKey) return true;
                __result = s_fourForcedOwner; return false;
            }
        }

        static IEnumerator FourClients(TestHarness h)
        {
            s_fourActive = true; s_fourPinned = false; s_fourForcedOwner = 0; s_fourEnemyKey = "";
            s_fourDoneSeats.Clear();
            s_fourForwarded = s_fourGlows = s_fourRevivedMessages = s_fourStoryCalls = 0;
            bool immortal = false, captured = false;
            Action<SeinCharacter> oldHook = PartnerFx.ReviveHook;
            PartnerFx.ReviveHook = delegate(SeinCharacter sein) {
                if (FourTesting && Players.SeatOf(sein) == 2) s_fourGlows++;
                if (oldHook != null) oldHook(sein);
            };
            try
            {
                h.Note("4P SKIP two-client-only enemy/E3/E5/E6, revive/R, story/S4 and lag phases; using per-seat 4P scenarios");
                yield return FourAwait(h, delegate { return FourReady; }, 150f, "three puppets and save handshake");
                if (!FourReady)
                { h.Check(false, "4P everyone sees three puppets with unique seats 1/2/3/4"); yield break; }
                immortal = Own.Mortality.DamageReciever.IsImmortal; captured = true; HoldFour();
                yield return StartBarrier(h);
                if (s_goAt == 0) yield break;
                yield return ThreeRound(h, 400, 4);
                h.Check(AllFour(400, p => p.PuppetCount == 3 && p.Unique && p.SeatMask == 30 && p.Ready),
                    "4P everyone sees three puppets with unique seats 1/2/3/4");
                Vector3 arena = Three(400, 1).Position;
                if (Players.LocalSeat == 2)
                    NetPlay.Send(66, true, delegate(BinaryWriter w) {
                        w.Write((byte)2); w.Write((byte)0); w.Write((byte)DamageType.Spikes);
                        w.Write((byte)0); w.Write(1f); w.Write(0f);
                    });
                int before = Three(400, Players.LocalSeat).Forwarded;
                yield return FourAwait(h, delegate { return Players.LocalSeat == 2 || s_fourForwarded > before; }, 8f, "forwarded P2 effect");
                yield return ThreeRound(h, 401, 4);
                h.Check(AllFour(401, p => p.Forwarded - Three(400, p.Seat).Forwarded == (p.Seat == 2 ? 0 : 1)),
                    "4P P2 message 66 reaches host/P3/P4 once with sender P2");

                yield return FourPlace(h, arena);
                yield return FourRevive(h);
                yield return FourPause(h);
                yield return FourStory(h);
                yield return FourEnemy(h);
                yield return FourPlace(h, arena);
                yield return FourWipe(h);
                h.Check(h.ErrorCount == 0, "4P no Unity errors/exceptions");
                yield return ThreeRound(h, 499, 4);
                if (NetPlay.IsHost)
                {
                    NetPlay.SendTestSignal("done");
                    float end = Time.realtimeSinceStartup + 12f;
                    while (s_fourDoneSeats.Count < 3 && Time.realtimeSinceStartup < end) yield return null;
                    if (s_fourDoneSeats.Count != 3) h.Check(false, "4P all guests received completion");
                }
                else
                {
                    yield return FourAwait(h, delegate { return s_done; }, 12f, "host completion");
                    if (!s_done) h.Check(false, "4P host completion received");
                    NetPlay.SendTestSignal("4P done"); NetPlay.Peer.Flush();
                    yield return FourAwait(h, delegate { return NetPlay.Peer == null || NetPlay.Peer.Stats.ReliablePending == 0; }, 8f, "completion acknowledged before quitting");
                }
            }
            finally
            {
                Bot(0, 0); s_fourPinned = false; s_fourForcedOwner = 0;
                if (Players.LocalSeat == 3 && SessionRules.InMenu(3)) UI.Menu.HideMenuScreen(true);
                PartnerFx.ReviveHook = oldHook; s_fourActive = false;
                if (captured && Own != null) Own.Mortality.DamageReciever.IsImmortal = immortal;
            }
        }
        static IEnumerator FourRevive(TestHarness h)
        {
            yield return ThreeRound(h, 410, 4);
            if (Players.LocalSeat == 2) FourKill();
            yield return FourAwait(h, delegate {
                return Revive.DownedAtSeat(2) != null && (Players.LocalSeat != 1 && Players.LocalSeat != 4 || FourSkull(2));
            }, 8f, "P2 down and visible skull");
            yield return ThreeRound(h, 411, 4);
            h.Check((Three(411, 1).DownMask & 4) != 0 && (Three(411, 4).DownMask & 4) != 0
                && (Three(411, 1).Skulls & 4) != 0 && (Three(411, 4).Skulls & 4) != 0,
                "4P host and P4 see P2 skull");
            if (Players.LocalSeat == 2)
            {
                yield return FourAwait(h, delegate { return Revive.SpectateActive && Revive.SpectateTarget == 3; }, 5f, "P2 spectates P3");
                h.Check(Revive.SpectateActive && Revive.SpectateTarget == 3, "4P P2 starts spectating P3");
                Revive.SpectatePrevious();
                yield return null;
                h.Check(Revive.SpectateTarget == 1 && UI.Cameras.Current != null && PuppetAt(1) != null
                    && UI.Cameras.Current.Target == PuppetAt(1).Sein.Transform, "4P P2 switches spectate camera to P1");
                Revive.SpectateNext();
                yield return null;
                h.Check(Revive.SpectateTarget == 3 && UI.Cameras.Current != null && PuppetAt(3) != null
                    && UI.Cameras.Current.Target == PuppetAt(3).Sein.Transform, "4P spectate Next switches P1 to P3 camera");
            }
            yield return ThreeRound(h, 413, 4);
            if (Players.LocalSeat == 3) Bot(0, P2Input.BTN_X);
            yield return FourAwait(h, delegate { return Revive.DownedAtSeat(2) == null && s_fourGlows > Three(410, Players.LocalSeat).Glows; },
                8f, "P3 completes P2 revive");
            Bot(0, 0);
            yield return FourAwait(h, delegate { return !FourSkull(2); }, 3f, "P2 skull hidden after revive");
            yield return ThreeRound(h, 412, 4);
            bool revived = AllFour(412, p => (p.DownMask & 4) == 0 && (p.Skulls & 4) == 0 && p.Glows - Three(410, p.Seat).Glows == 1
                && p.Wipes == Three(410, p.Seat).Wipes && p.Restores == Three(410, p.Seat).Restores);
            h.Check(revived && Three(412, 2).Alive && Three(412, 3).RevivesSent == Three(410, 3).RevivesSent + 1,
                "4P P3 revives P2 and all four clear the skull");
            ThreePacket p4 = Three(412, 4), baseline = Three(410, 4);
            h.Check(p4.Glows == baseline.Glows + 1 && p4.GlowEffects == baseline.GlowEffects + 1 && p4.RevivedMessages == baseline.RevivedMessages + 1,
                "4P P4 plays P2 revive glow once from MsgRevived");
        }
        static IEnumerator FourPause(TestHarness h)
        {
            s_fourPinned = false; Bot(0, 0);
            yield return ThreeRound(h, 420, 4);
            if (Players.LocalSeat == 3) AccessTools.Method(typeof(MenuScreenManager), "ShowMenuScreen",
                new Type[] { typeof(MenuScreenManager.Screens), typeof(bool) }).Invoke(UI.Menu,
                new object[] { MenuScreenManager.Screens.Pause, false });
            // T-044: online nothing pauses. P3's menu holds only P3's Ori; the other three keep playing, no banner.
            yield return FourAwait(h, delegate {
                return Players.LocalSeat != 3 || (UI.Menu != null && UI.Menu.MainMenuVisible && SessionRules.LocalLocked);
            }, 8f, "P3 menu open without a pause");
            Vector3 origin = Own.Position; s_threeTravel = 0; Bot(1, 0);
            yield return FourTicks(h, 30, delegate { s_threeTravel = Mathf.Max(s_threeTravel, Vector3.Distance(origin, Own.Position)); });
            Bot(0, 0);
            yield return ThreeRound(h, 421, 4);
            h.Check(AllFour(421, p => !p.Paused && !p.BannerP3 && (p.Seat == 3 ? p.Travel < 0.3f : p.Travel > 0.5f) && p.Frames > Three(420, p.Seat).Frames),
                "4P P3's menu pauses nobody: P3 held, the other three keep moving, no banner");
            if (Players.LocalSeat == 3) UI.Menu.HideMenuScreen(true);
            yield return FourAwait(h, delegate { return !SessionRules.InMenu(3) && !SessionRules.LocalPaused && !SessionRules.PausedByPartner; }, 8f, "P3 menu closed");
            origin = Own.Position; Bot(1, 0);
            yield return FourAwait(h, delegate { return Mathf.Abs(Own.Position.x - origin.x) > 0.5f; }, 5f, "movement after resume");
            Bot(0, 0); s_threeTravel = Mathf.Abs(Own.Position.x - origin.x);
            yield return ThreeRound(h, 422, 4);
            h.Check(AllFour(422, p => !p.Paused && p.MenuMask == 0 && p.Travel > 0.5f),
                "4P closing P3 menu resumes movement on all four");
            s_fourPin = Own.Position; s_fourPinned = true;
        }
        static IEnumerator FourStory(TestHarness h)
        {
            GameObject go = new GameObject("OriCoop 4P S-test sequence");
            ActionSequence sequence = go.AddComponent<ActionSequence>(); sequence.MoonGuid = new MoonGuid(FourStoryId);
            LockPlayerInputAction storyLock = go.AddComponent<LockPlayerInputAction>(); storyLock.DurationPlayerCantMove = 0.4f;
            sequence.Actions.Add(storyLock);
            WaitAction wait = go.AddComponent<WaitAction>(); wait.Duration = 0.2f; sequence.Actions.Add(wait);
            sequence.Actions.Add(go.AddComponent<NetStoryTest.NoStoryAction>());
            try
            {
                s_fourPinned = false; Bot(0, 0);
                yield return FourAwait(h, delegate { return !StorySync.StoryActive && !CutsceneGuard.PedestalWindow && !GameController.Instance.InputLocked; },
                    12f, "previous story finished");
                yield return ThreeRound(h, 430, 4);
                ThreePacket before = Three(430, Players.LocalSeat);
                if (Players.LocalSeat == 4) sequence.Perform(null);
                yield return FourAwait(h, delegate {
                    return s_fourStoryCalls > before.StoryCalls
                        && (int)AccessTools.Field(typeof(NetStoryTest), "s_leafCalls").GetValue(null) > before.StoryLeaves
                        && !sequence.IsRunning && !StorySync.StoryActive;
                }, 15f, "P4 native story and End");
                yield return ThreeRound(h, 431, 4);
                h.Check(AllFour(431, p => p.StoryCalls == Three(430, p.Seat).StoryCalls + 1 && p.StoryLeaves == Three(430, p.Seat).StoryLeaves + 1
                    && (p.Seat == 4 ? p.StorySent == Three(430, 4).StorySent + 1 : p.StoryMirrored == Three(430, p.Seat).StoryMirrored + 1)),
                    "4P seat-4 story plays once on all four");
            }
            finally { UnityEngine.Object.Destroy(go); }
        }
        static string FourFindEnemy()
        {
            foreach (string kind in new[] { "SpitterEnemy", "JumperEnemy", "RammingEnemy", "AcidSlugEnemy", "StarSlugEnemy" })
            {
                string key = EnemySync.FindTestEnemyOfClass(Own.Position, kind, null);
                if (!string.IsNullOrEmpty(key)) return key;
            }
            return "";
        }
        static IEnumerator FourEnemy(TestHarness h)
        {
            s_fourPinned = false; Bot(0, 0);
            Vector3 saved = Own.Position;
            if (NetPlay.IsHost)
            {
                s_fourEnemyKey = FourFindEnemy();
                if (s_fourEnemyKey == "" && Core.Scenes.Manager != null)
                {
                    List<RuntimeSceneMetaData> areas = new List<RuntimeSceneMetaData>();
                    foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                        if (!scene.DependantScene && scene.CanBeLoaded && scene.SceneBoundaries.Count > 0
                            && Game.Checkpoint.SaveGameData.Scenes.ContainsKey(scene.SceneMoonGuid)
                            && scene.Scene.IndexOf("boss", StringComparison.OrdinalIgnoreCase) < 0
                            && scene.Scene.IndexOf("escape", StringComparison.OrdinalIgnoreCase) < 0) areas.Add(scene);
                    areas.Sort(delegate(RuntimeSceneMetaData a, RuntimeSceneMetaData b) {
                        return Vector3.Distance(a.PlaceholderPosition, saved).CompareTo(Vector3.Distance(b.PlaceholderPosition, saved)); });
                    for (int attempt = 0; attempt < areas.Count && attempt < 4 && s_fourEnemyKey == ""; attempt++)
                    {
                        Vector3 point = areas[attempt].PlaceholderPosition;
                        h.Note("4P enemy search saved scene=" + areas[attempt].Scene + " position=" + point);
                        EnemySync.LoadTestArea(point); Plugin.Teleport(Own, point);
                        s_fourPin = point; s_fourPinned = true;
                        yield return FourAwait(h, delegate { s_fourEnemyKey = FourFindEnemy(); return s_fourEnemyKey != ""; }, 6f, "loaded E-test enemy");
                    }
                }
                s_fourEnemyOrigin = EnemySync.Inspect(s_fourEnemyKey).Origin;
            }
            yield return ThreeRound(h, 440, 4);
            s_fourEnemyKey = Three(440, 1).EnemyKey; s_fourEnemyOrigin = Three(440, 1).EnemyOrigin;
            if (s_fourEnemyKey == "")
            {
                h.Note("4P no mirrored E-test enemy reachable in current or four nearest saved scenes");
                h.Check(false, "4P enemy owner is seat 3 on all four");
                h.Check(false, "4P seat-2 duplicate hit removes exactly 3 health on all four");
                yield break;
            }
            try
            {
                h.Note("4P real E-test enemy=" + s_fourEnemyKey + "; injecting DesiredOwner=P3, native Decide/Grant/Handoff and hit routing unchanged");
                if (NetPlay.IsHost) s_fourForcedOwner = 1;
                EnemySync.LoadTestArea(s_fourEnemyOrigin);
                Vector3 ground;
                float side = (Players.LocalSeat - 3) * 3f;
                if (!EnemyGroundPosition(s_fourEnemyOrigin, side, out ground)) ground = s_fourEnemyOrigin + new Vector3(side, 1f, 0);
                Plugin.Teleport(Own, ground); SessionRules.PositionChanged(); s_fourPin = ground; s_fourPinned = true;
                yield return FourAwait(h, delegate {
                    EnemySync.Probe p = EnemySync.Inspect(s_fourEnemyKey);
                    return p.Present && p.PlaceholderLoaded && p.Known && p.OwnerSeat == 1;
                }, 15f, "enemy loaded and initially owned by host");
                if (NetPlay.IsHost && EnemySync.Inspect(s_fourEnemyKey).Owner) EnemySync.PrepareTestEnemy(s_fourEnemyKey, 80f);
                yield return FourAwait(h, delegate { return Mathf.Abs(EnemySync.Inspect(s_fourEnemyKey).Health - 80f) < 0.01f; }, 8f, "enemy health 80");
                yield return ThreeRound(h, 441, 4);
                if (NetPlay.IsHost) s_fourForcedOwner = 3;
                yield return FourAwait(h, delegate {
                    EnemySync.Probe p = EnemySync.Inspect(s_fourEnemyKey);
                    return p.OwnerSeat == 3 && p.Present && Mathf.Abs(p.Health - 80f) < 0.01f;
                }, 10f, "grant owner P3 on every end");
                yield return ThreeRound(h, 442, 4);
                h.Check(AllFour(442, p => p.EnemyLoaded && p.EnemyOwner == 3 && Mathf.Abs(p.EnemyHealth - 80f) < 0.01f),
                    "4P enemy owner is seat 3 on all four");
                if (Players.LocalSeat == 2 && EnemySync.Inspect(s_fourEnemyKey).Present) EnemySync.TestAttack(s_fourEnemyKey, 3f, true);
                yield return FourAwait(h, delegate { return EnemySync.Inspect(s_fourEnemyKey).Health <= 77f; }, 8f, "P2 hit applied by P3");
                yield return FourTicks(h, 30);
                yield return ThreeRound(h, 443, 4);
                h.Check(AllFour(443, p => p.EnemyLoaded && p.EnemyOwner == 3 && Mathf.Abs(p.EnemyHealth - 77f) < 0.01f
                    && Mathf.Abs(Three(442, p.Seat).EnemyHealth - p.EnemyHealth - 3f) < 0.01f),
                    "4P seat-2 duplicate hit removes exactly 3 health on all four");
            }
            finally
            {
                if (NetPlay.IsHost) EnemySync.EndTestEnemy(s_fourEnemyKey);
                s_fourForcedOwner = 0; s_fourPinned = false;
            }
        }
        static IEnumerator FourWipe(TestHarness h)
        {
            Bot(0, 0); yield return ThreeRound(h, 450, 4);
            ThreePacket baseline = Three(450, Players.LocalSeat);
            if (Players.LocalSeat != 1) FourKill();
            yield return FourAwait(h, delegate { return FourDownMask() == 28; }, 8f, "P2/P3/P4 all down with host alive");
            yield return FourTicks(h, 30);
            yield return ThreeRound(h, 451, 4);
            h.Check(Three(451, 1).Alive && AllFour(451, p => p.DownMask == 28 && p.Wipes == Three(450, p.Seat).Wipes
                && p.Restores == Three(450, p.Seat).Restores && p.Loads == Three(450, p.Seat).Loads),
                "4P P2/P3/P4 down with host alive causes no wipe");
            s_fourPinned = false;
            if (Players.LocalSeat == 1) FourKill();
            yield return FourAwait(h, delegate {
                return FourReady && Revive.NetBothDown > baseline.Wipes && TestHarness.RestoreCheckpointCalls > baseline.Restores
                    && (Players.LocalSeat == 1 || SaveSync.GuestLoads > baseline.Loads && SaveSync.JoinedHost);
            }, 75f, "one host-decided wipe and all checkpoint reloads");
            yield return ThreeRound(h, 452, 4);
            h.Check(AllFour(452, p => p.Ready && p.Alive && p.DownMask == 0 && p.PuppetCount == 3
                && p.Wipes == Three(450, p.Seat).Wipes + 1 && p.Restores == Three(450, p.Seat).Restores + 1
                && (p.Seat == 1 || p.Joined && p.Loads == Three(450, p.Seat).Loads + 1)),
                "4P all down causes one wipe and reload on all four");
        }

        static IEnumerator ThreeClients(TestHarness h)
        {
            h.Note("3P SKIP two-client-only enemy/E3, revive/R, story/S4, and lag/blackout/teleport phases C/D/E");
            yield return ThreeRound(h, 0);
            bool everyone = true;
            for (int seat = 1; seat <= 3; seat++)
            { ThreePacket p = Three(0, seat); everyone &= p.PuppetCount == 2 && p.Unique && p.SeatMask == 14; }
            h.Check(everyone, "3P everyone sees two puppets with unique seats 1/2/3");
            ThreePacket late = Three(0, 3);
            h.Note("3P late join loads=" + late.Loads + " placed=" + late.Joined + " distance=" + F(late.Distance));
            h.Check(late.Loads > 0 && late.Joined && late.Distance <= 3f, "3P late guest loads host save and lands within 3 units");

            yield return ThreeMovement(h, 1);
            yield return ThreeRound(h, 1);
            ThreePacket a = Three(1, 2), b = Three(1, 3);
            h.Check(a.Travel > 2f && b.Travel > 2f && a.Rise > 0.8f && b.Rise > 0.8f,
                "3P host right > 2 and rise > 0.8 on both guests");
            h.Check(a.Frames > 0 && b.Frames > 0 && a.Error < 0.3f && b.Error < 0.3f,
                "3P host AvgError < 0.3 on both guests");

            yield return ThreeMovement(h, 2);
            yield return ThreeRound(h, 2);
            a = Three(2, 1); b = Three(2, 3);
            h.Check(a.Travel > 2f && b.Travel > 2f && a.Rise > 0.8f && b.Rise > 0.8f,
                "3P seat-2 right > 2 and rise > 0.8 on host and guest 3");
            h.Check(a.Frames > 0 && b.Frames > 0 && a.Error < 0.3f && b.Error < 0.3f,
                "3P seat-2 AvgError < 0.3 on host and guest 3");

            yield return Wait(5f);
            yield return ThreeRound(h, 3);
            a = Three(3, 1); b = Three(3, 2); ThreePacket c = Three(3, 3);
            h.Note("3P world digests P1=" + a.Digest.ToString("X16") + " P2=" + b.Digest.ToString("X16") + " P3=" + c.Digest.ToString("X16"));
            h.Check(a.WorldCount > 0 && a.WorldCount == b.WorldCount && a.WorldCount == c.WorldCount
                && a.Digest == b.Digest && a.Digest == c.Digest, "3P world digests equal on all three after 5 s");

            yield return ThreeRound(h, 4);
            int keys = Three(4, 1).Keys;
            if (NetPlay.IsHost)
            { s_pickupAt = Clock() + 1.0; NetPlay.SendTestSignal("pickup:" + s_pickupAt.ToString("R", CultureInfo.InvariantCulture)); }
            float by = Time.realtimeSinceStartup + 12f;
            while (s_pickupAt == 0 && Time.realtimeSinceStartup < by) yield return null;
            if (s_pickupAt == 0) throw new InvalidOperationException("3P pickup signal missing");
            while (Clock() < s_pickupAt) yield return null;
            if (!NetPlay.IsHost) CollectSharedPickup();
            by = Time.realtimeSinceStartup + 3f;
            while (Own.Inventory.Keystones != keys + 3 && Time.realtimeSinceStartup < by) yield return null;
            yield return Wait(0.5f);
            yield return ThreeRound(h, 5);
            a = Three(5, 1); b = Three(5, 2); c = Three(5, 3);
            h.Check(b.CollectedAt > 0 && c.CollectedAt > 0 && Math.Abs(b.CollectedAt - c.CollectedAt) <= 1.0,
                "3P guests collect the same pickup within 1 s");
            h.Note("3P pickup keys=" + a.Keys + "/" + b.Keys + "/" + c.Keys + " applied=" + (a.Applied - Three(4, 1).Applied) + " rejected=" + (a.Rejected - Three(4, 1).Rejected));
            h.Check(Three(4, 2).Keys == keys && Three(4, 3).Keys == keys && a.Keys == keys + 3 && b.Keys == a.Keys && c.Keys == a.Keys
                && a.Applied - Three(4, 1).Applied == 1 && a.Rejected - Three(4, 1).Rejected == 1,
                "3P pickup applied once, duplicate rejected, all keys agree");

            yield return ThreeRound(h, 6);
            // T-111: in server mode the room outlives P1, so P1 is the one who leaves. The arbiter moves to seat 2 and
            // seat 3 must obey its host-only messages (a gate count here; intro and wipe use the same sender check).
            if (NetRoute.ServerMode)
            {
                if (Players.LocalSeat == 1) { h.Note("3P server: P1 leaving; harness completion quits this client"); yield break; }
                by = Time.realtimeSinceStartup + 30f;
                while ((PuppetAt(1) != null || Players.LocalSeat == 2 && !NetPlay.IsHost) && Time.realtimeSinceStartup < by) yield return null;
                yield return Wait(1f);   // the arbiter change reaches seat 3 with the same server state
                HostLeftInServerMode = PuppetAt(1) == null;
                h.Check(PuppetAt(1) == null && NetPlay.IsHost == (Players.LocalSeat == 2),
                    "3P server: P1 left and seat 2 arbitrates now (local P" + Players.LocalSeat + ", host seat " + NetRoute.HostSeat + ")");
                if (Players.LocalSeat == 2)
                {
                    yield return Wait(1f);
                    ForcedSync.TestSendCount("t111-gate", 1, 2);
                    h.Note("3P server: new host sent gate count t111-gate 1/2");
                    yield return Wait(10f);   // seat 3 checks within this window
                }
                else
                {
                    by = Time.realtimeSinceStartup + 10f;
                    while (ForcedSync.TestCount("t111-gate") != 1 && Time.realtimeSinceStartup < by) yield return null;
                    h.Check(ForcedSync.TestCount("t111-gate") == 1, "3P server: seat 3 applies the new host's gate count (T-111)");
                }
                ForcedSync.TestForget("t111-gate");
                h.Check(Connected, "3P server: remaining players stay connected after the host left");
                // T-152: a session that drops and then leaves online play frees its socket (the parked stale client).
                if (Players.LocalSeat == 3 && CServerLink.Client != null)
                {
                    // A real drop: the peer times out inside NetPlay's poll, so the link parks the client and NetPlay retries
                    // 1 s later (a local Disconnect is replaced by an immediate reconnect before its event runs). Leave first.
                    CServerLink.Client.Peer.TimeoutMs = 1;
                    float dropBy = Time.realtimeSinceStartup + 1f;
                    while (!CServerLink.HasStale && Time.realtimeSinceStartup < dropBy) yield return null;
                    bool parked = CServerLink.HasStale;
                    NetPlay.End("T-152 test leave");
                    h.Check(parked && !CServerLink.HasStale, "3P server: a dropped session that leaves online play frees its socket (T-152, parked " + parked + ")");
                }
                yield break;
            }
            if (NetPlay.IsHost) NetPlay.SendTestSignal("leave");
            if (Players.LocalSeat == 3)
            {
                by = Time.realtimeSinceStartup + 12f;
                while (!s_leaveReleased && Time.realtimeSinceStartup < by) yield return null;
                if (!s_leaveReleased) throw new InvalidOperationException("3P host leave release missing");
                h.Check(Connected && !s_threeEnded, "3P guest 3 connected before leaving");
                h.Note("3P guest 3 leaving; harness completion quits this client");
                yield break;
            }
            by = Time.realtimeSinceStartup + 7f;
            while (PuppetAt(3) != null && Time.realtimeSinceStartup < by) yield return null;
            h.Check(PuppetAt(3) == null && Players.Remotes.Count == 1, "3P seat-3 puppet removed within 7 s");
            int remaining = Players.LocalSeat == 1 ? 2 : 1;
            long before = NetPlay.TestFramesPlayed(remaining);
            yield return Wait(2f);
            s_threeFrames = NetPlay.TestFramesPlayed(remaining) - before;
            yield return ThreeRound(h, 7, 2);
            a = Three(7, 1); b = Three(7, 2);
            h.Check(a.Connected && b.Connected && !a.Ended && !b.Ended,
                "3P remaining players stay connected without SessionEnded");
            h.Check(a.Frames > 0 && b.Frames > 0, "3P remaining players keep exchanging frames");
            if (NetPlay.IsHost) { NetPlay.SendTestSignal("done"); yield return Wait(0.5f); }
            else
            {
                by = Time.realtimeSinceStartup + 12f;
                while (!s_done && Time.realtimeSinceStartup < by) yield return null;
                if (!s_done) throw new InvalidOperationException("3P host completion signal missing");
            }
        }
        // Independent, numbered barriers keep W out of the movement phases' single "go" signal.
        const byte MsgWorldTest = 25;
        static readonly Dictionary<int, WorldTestPacket> s_worldPackets = new Dictionary<int, WorldTestPacket>();
        static readonly Dictionary<int, WorldTestPacket> s_ownWorldPackets = new Dictionary<int, WorldTestPacket>();
        static WorldEvents s_worldFixture;
        static MoonGuid s_worldId = new MoonGuid(0x57434f4f, 0x50544553, 0x54574f52, 0x4c443032);
        static int s_worldKeys, s_worldGuestTarget, s_worldXP, s_worldEventValue;

        sealed class WorldTestPacket
        {
            public int Step, Keys, XP, RawXP, Level, Loads, SaveAttempts, SaveWrites, WorldValue, WorldCount;
            public float SaveDistance, JoinElapsed;
            public byte[] WorldBytes;
            public ulong Digest, ProgressHash;
            public bool DigestMatch, JoinedHost, CollectorReady, Failed;
        }

        static void ReadWorldTest(BinaryReader r)
        {
            if (!TestHarness.Enabled) return;
            WorldTestPacket p = new WorldTestPacket();
            p.Step = r.ReadInt32(); p.Keys = r.ReadInt32(); p.XP = r.ReadInt32();
            p.RawXP = r.ReadInt32(); p.Level = r.ReadInt32(); p.Loads = r.ReadInt32();
            p.SaveAttempts = r.ReadInt32(); p.SaveWrites = r.ReadInt32(); p.SaveDistance = r.ReadSingle();
            p.JoinElapsed = r.ReadSingle(); p.JoinedHost = r.ReadBoolean();
            p.CollectorReady = r.ReadBoolean(); p.Failed = r.ReadBoolean(); p.ProgressHash = r.ReadUInt64();
            p.WorldValue = r.ReadInt32();
            int length = r.ReadInt32();
            if (length < 0 || length > 16384) throw new InvalidDataException("W invalid fixture bytes");
            p.WorldBytes = r.ReadBytes(length);
            if (p.WorldBytes.Length != length) throw new EndOfStreamException();
            p.Digest = r.ReadUInt64(); p.WorldCount = r.ReadInt32(); p.DigestMatch = r.ReadBoolean();
            s_worldPackets[p.Step] = p;
        }

        static WorldEventsRuntime FixtureState()
        {
            if (s_worldFixture == null)
            {
                // An isolated event avoids changing a story event that could launch an escape or cutscene.
                // The runtime dictionary is still serialized by GameController, exactly like baked events.
                GameObject go = new GameObject("OriCoop W world event fixture");
                go.SetActive(false);
                s_worldFixture = go.AddComponent<WorldEvents>();
                s_worldFixture.MoonGuid = new MoonGuid(s_worldId);
            }
            return WorldEventsManager.Instance.Find(s_worldFixture);
        }

        static WorldTestPacket MakeWorldTest(int step)
        {
            WorldTestPacket p = new WorldTestPacket(); p.Step = step;
            p.Keys = Own.Inventory.Keystones; p.XP = Own.Level.TotalExperience;
            p.RawXP = Own.Level.Experience; p.Level = Own.Level.Current;
            p.Loads = SaveSync.GuestLoads; p.SaveAttempts = SaveSync.GuestSaveAttempts;
            p.SaveWrites = SaveSync.GuestSaveWrites;
            p.SaveDistance = SaveSync.GuestJoinDistance; p.JoinElapsed = SaveSync.GuestJoinElapsed;
            p.JoinedHost = SaveSync.JoinedHost; p.CollectorReady = ProgressSync.PickupCollector(Own) != null;
            p.ProgressHash = ProgressSync.Hash();
            WorldEventsRuntime state = FixtureState();
            p.WorldValue = state.Value; p.WorldBytes = WorldSync.CaptureBytes(state);
            p.Digest = WorldSync.Digest(out p.WorldCount); p.DigestMatch = WorldSync.LastDigestMatch;
            return p;
        }

        static void SendWorldTest(WorldTestPacket p)
        {
            NetPlay.Send(MsgWorldTest, true, delegate(BinaryWriter w)
            {
                w.Write(p.Step); w.Write(p.Keys); w.Write(p.XP); w.Write(p.RawXP); w.Write(p.Level);
                w.Write(p.Loads); w.Write(p.SaveAttempts); w.Write(p.SaveWrites); w.Write(p.SaveDistance);
                w.Write(p.JoinElapsed); w.Write(p.JoinedHost); w.Write(p.CollectorReady); w.Write(p.Failed); w.Write(p.ProgressHash);
                w.Write(p.WorldValue); w.Write(p.WorldBytes.Length); w.Write(p.WorldBytes);
                w.Write(p.Digest); w.Write(p.WorldCount); w.Write(p.DigestMatch);
            });
        }

        static void PublishWorldBarrier(TestHarness h, int step, bool actionFailed)
        {
            WorldTestPacket local;
            try { local = MakeWorldTest(step); }
            catch (Exception e)
            {
                h.Check(false, "W barrier " + step + " capture threw " + e.GetType().Name + ": " + e.Message);
                local = new WorldTestPacket(); local.Step = step; local.Failed = true;
                local.Keys = local.XP = int.MinValue; local.SaveDistance = local.JoinElapsed = float.PositiveInfinity;
                local.WorldBytes = new byte[0];
            }
            local.Failed = local.Failed || actionFailed;
            s_ownWorldPackets[step] = local;
            try { SendWorldTest(local); }
            catch (Exception e) { h.Check(false, "W barrier " + step + " send threw " + e.GetType().Name + ": " + e.Message); }
        }

        static IEnumerator WorldBarrier(TestHarness h, int step)
        {
            float deadline = Time.realtimeSinceStartup + 12f;
            while (Connected && !s_worldPackets.ContainsKey(step) && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(s_worldPackets.ContainsKey(step), "W barrier " + step + " received");
            if (s_worldPackets.ContainsKey(step)) h.Check(!s_worldPackets[step].Failed, "W partner barrier " + step + " capture completed");
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void CollectTestKeystones(int amount)
        {
            SeinCharacter own = Own;
            SeinPickupProcessor collector = ProgressSync.PickupCollector(own);
            if (collector == null) throw new InvalidOperationException("W no pickup processor bound to local Sein (PickupHandler="
                + (own != null && own.PickupHandler != null) + ")");
            GameObject go = new GameObject("OriCoop W keystone fixture");
            go.SetActive(false);
            try
            {
                KeystonePickup pickup = go.AddComponent<KeystonePickup>();
                // Runtime orbs may have no stable world GUID. This still exercises the production
                // pickup collector prefix, reliable event, host effect, and progress replication.
                pickup.MoonGuid = MoonGuid.Empty; pickup.Amount = amount;
                pickup.OnValidate();
                collector.OnCollectKeystonePickup(pickup);
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        // Drive child iterators ourselves: Unity's StartCoroutine returns an opaque Coroutine, so
        // catching MoveNext on its parent cannot catch an exception thrown by that child.
        static IEnumerator Safely(TestHarness h, string label, IEnumerator routine, Action onFailure = null)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>(); stack.Push(routine);
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator current = stack.Peek(); bool moved = false; object yielded = null;
                    Exception failure = null;
                    try { moved = current.MoveNext(); if (moved) yielded = current.Current; }
                    catch (Exception e) { failure = e; }
                    if (failure != null)
                    {
                        if (onFailure != null) onFailure();
                        h.Check(false, label + " threw " + failure.GetType().Name + ": " + failure.Message); Plugin.Log.LogError(label + " threw: " + failure);
                    }
                    if (!moved)
                    {
                        stack.Pop(); DisposeIterator(h, label, current, onFailure); continue;
                    }
                    IEnumerator child = yielded as IEnumerator;
                    if (child != null) stack.Push(child);
                    else yield return yielded;
                }
            }
            finally
            {
                while (stack.Count > 0) DisposeIterator(h, label, stack.Pop(), onFailure);
            }
        }

        static void DisposeIterator(TestHarness h, string label, IEnumerator routine, Action onFailure)
        {
            IDisposable disposable = routine as IDisposable;
            if (disposable == null) return;
            try { disposable.Dispose(); }
            catch (Exception e)
            {
                if (onFailure != null) onFailure();
                h.Check(false, label + " cleanup threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        static WorldTestPacket PartnerWorld(int step)
        {
            WorldTestPacket packet;
            // Failed may mean that the previous action threw; its successfully captured state
            // still provides a valid baseline for an unrelated next action.
            if (!s_worldPackets.TryGetValue(step, out packet) || packet.Keys == int.MinValue)
                throw new InvalidOperationException("W partner has no usable observation for step " + step);
            return packet;
        }

        static IEnumerator SharedWorld(TestHarness h)
        {
            h.Note("phase W shared save, world, and host progress"); Bot(0, 0);
            try
            {
                // An action failure is reported, but its barrier always runs and later tests continue.
                // A peer therefore never waits for an action's coroutine to complete successfully.
                for (int step = 1; step <= 9; step++)
                {
                    bool failed = false, completed = false;
                    try
                    {
                        yield return Safely(h, "W step " + step, WorldStep(h, step), delegate { failed = true; });
                        completed = true;
                    }
                    finally
                    {
                        // This also runs if the parent iterator is disposed while a step is suspended.
                        // Publishing is synchronous; yielding to wait for the peer belongs below.
                        PublishWorldBarrier(h, step, failed || !completed);
                    }
                    // No wait after the last step: the guest moves on to the quit test right away, so its final
                    // barrier may still be unacknowledged when it closes.
                    if (step < 9) yield return Safely(h, "W barrier " + step, WorldBarrier(h, step));
                }
            }
            finally
            {
                if (s_worldFixture != null) { UnityEngine.Object.Destroy(s_worldFixture.gameObject); s_worldFixture = null; }
            }
        }

        static IEnumerator WorldStep(TestHarness h, int step)
        {
            bool host = NetPlay.IsHost; float start = Time.realtimeSinceStartup;
            if (step == 1)
            {
                while ((!SaveSync.Ready || !ProgressSync.Ready || ProgressSync.PickupCollector(Own) == null)
                    && Connected && Time.realtimeSinceStartup - start < 20f) yield return null;
                h.Check(SaveSync.Ready, "W save transfer ready");
                h.Check(WorldSync.CanonicalSelfTest(), "W canonical world hash self-test " + WorldSync.CanonicalSelfTestFailure);
                h.Check(ProgressSync.Ready && ProgressSync.Hash() != 0, "W real progress fields ready; hash=" + ProgressSync.Hash().ToString("X16"));
                h.Check(ProgressSync.PickupCollector(Own) != null, "W native pickup processor bound to local Sein");
                if (!host)
                {
                    GameController.Instance.SaveGameController.PerformSave();
                    SaveSlotBackupsManager.CreateCurrentBackup();
                    h.Check(SaveSync.GuestBackupAttempts > 0, "W guest automatic backup blocked (attempts=" + SaveSync.GuestBackupAttempts + ")");
                    string newGame = Environment.GetEnvironmentVariable("ORICOOP_NEWGAME");
                    if (newGame == "1" || newGame == "noslot")
                    {
                        string scene = Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null ? Core.Scenes.Manager.CurrentScene.Scene : "?";
                        h.Check(GuestJoin.Intercepts == 1 && GuestJoin.TitleLoads == 1 && GuestJoin.Aborts == 0,
                            "W empty slot: no new game, host save loaded from the title (intercepts=" + GuestJoin.Intercepts + ", titleLoads=" + GuestJoin.TitleLoads + ", aborts=" + GuestJoin.Aborts + " " + GuestJoin.LastAbort + ")");
                        h.Check(scene.IndexOf("Intro", StringComparison.OrdinalIgnoreCase) < 0 && scene != "sunkenGladesRunaway",
                            "W empty slot: not in the new-game prologue (scene " + scene + ")");
                        h.Check(CoopSaves.SessionSlot == SaveSlotsManager.CurrentSlotIndex,
                            "W empty slot / no slot (" + newGame + "): the loaded slot holds the co-op copy (copy " + CoopSaves.SessionSlot + ", playing " + SaveSlotsManager.CurrentSlotIndex + ")");
                    }
                    h.Check(SessionScreens.SaveScreensShown > 0 && !SessionScreens.Blocking,
                        "W guest save-transfer screen shown during the join and cleared (shown=" + SessionScreens.SaveScreensShown + ", phase=" + SessionScreens.Phase + ")");
                }
                yield break;
            }
            if (step == 2)
            {
                WorldTestPacket partner = PartnerWorld(1), local = s_ownWorldPackets[1];
                WorldTestPacket save = host ? partner : local;
                h.Note("W save guestLoads=" + save.Loads + " hostJoinDistance=" + F(save.SaveDistance)
                    + " joinElapsed=" + F(save.JoinElapsed) + " joinedHost=" + save.JoinedHost
                    + " PerformSave attempts=" + save.SaveAttempts + " writes=" + save.SaveWrites);
                h.Check(save.JoinedHost && save.Loads > 0 && save.SaveDistance <= 3f && save.JoinElapsed <= 2f,
                    "W guest joins host live position within 3 units in 2 s (distance=" + F(save.SaveDistance) + ", seconds=" + F(save.JoinElapsed) + ")");
                h.Check(save.SaveAttempts > 0 && save.SaveWrites == 0, "W guest PerformSave blocked (attempts=" + save.SaveAttempts + ", writes=" + save.SaveWrites + ")");
                h.Check(local.ProgressHash != 0 && partner.ProgressHash != 0 && local.ProgressHash == partner.ProgressHash,
                    "W progress hash nonzero and equal (local=" + local.ProgressHash.ToString("X16") + ", remote=" + partner.ProgressHash.ToString("X16") + ")");
                h.Check(local.CollectorReady && partner.CollectorReady, "W both native pickup collectors available");
                s_worldKeys = host ? local.Keys : partner.Keys;
                h.Check(local.Keys == s_worldKeys && partner.Keys == s_worldKeys, "W keystone baseline=" + s_worldKeys);
                if (host) CollectTestKeystones(2);
                while (Own.Inventory.Keystones != s_worldKeys + 2 && Time.realtimeSinceStartup - start < 1f) yield return null;
                h.Note("W host pickup old=" + s_worldKeys + " amount=2 now=" + Own.Inventory.Keystones + " elapsedMs=" + F((Time.realtimeSinceStartup - start) * 1000f));
                h.Check(Own.Inventory.Keystones == s_worldKeys + 2, "W host keystone +2 within 1 s");
                yield break;
            }
            if (step == 3)
            {
                WorldTestPacket partner = PartnerWorld(2), local = s_ownWorldPackets[2];
                h.Check(partner.Keys == s_worldKeys + 2, "W partner observed host keystone +2");
                int baseline = host ? local.Keys : partner.Keys;
                s_worldGuestTarget = baseline + 3;
                h.Check(local.Keys == baseline && partner.Keys == baseline, "W guest pickup fresh host baseline=" + baseline);
                if (!host) CollectTestKeystones(3);
                while (Own.Inventory.Keystones != s_worldGuestTarget && Time.realtimeSinceStartup - start < 1f) yield return null;
                h.Note("W guest pickup old=" + baseline + " amount=3 now=" + Own.Inventory.Keystones + " elapsedMs=" + F((Time.realtimeSinceStartup - start) * 1000f));
                h.Check(Own.Inventory.Keystones == s_worldGuestTarget, "W guest pickup reaches host and guest within 1 s");
                yield return Wait(0.35f);
                h.Check(Own.Inventory.Keystones == s_worldGuestTarget, "W guest keystone event applied exactly once (" + Own.Inventory.Keystones + ")");
                yield break;
            }
            if (step == 4)
            {
                WorldTestPacket partner = PartnerWorld(3);
                h.Check(partner.Keys == s_worldGuestTarget, "W partner observed guest keystone exactly once");
                s_worldEventValue = (host ? FixtureState().Value : partner.WorldValue) + 1;
                if (host) FixtureState().Value = s_worldEventValue;
                while (FixtureState().Value != s_worldEventValue && Time.realtimeSinceStartup - start < 1f) yield return null;
                h.Note("W world event=" + s_worldId + " expected=" + s_worldEventValue + " actual=" + FixtureState().Value + " elapsedMs=" + F((Time.realtimeSinceStartup - start) * 1000f));
                h.Check(FixtureState().Value == s_worldEventValue, "W world event value replicated within 1 s");
                yield break;
            }
            if (step == 5)
            {
                WorldTestPacket partner = PartnerWorld(4), local = s_ownWorldPackets[4];
                byte[] localEvent = WorldSync.CaptureBytes(FixtureState());
                h.Check(BytesEqual(localEvent, partner.WorldBytes), "W world event bytes identical (local=" + BitConverter.ToString(localEvent) + ", remote=" + BitConverter.ToString(partner.WorldBytes) + ")");
                s_worldXP = host ? local.XP : partner.XP;
                h.Check(local.XP == s_worldXP && partner.XP == s_worldXP, "W concurrent XP baseline=" + s_worldXP);
                Own.Level.GainExperience(host ? 10 : 7);
                while (Own.Level.TotalExperience != s_worldXP + 17 && Time.realtimeSinceStartup - start < 1f) yield return null;
                h.Note("W concurrent XP old=" + s_worldXP + " host=10 guest=7 total=" + Own.Level.TotalExperience + " raw=" + Own.Level.Experience + " level=" + Own.Level.Current + " elapsedMs=" + F((Time.realtimeSinceStartup - start) * 1000f));
                h.Check(Own.Level.TotalExperience == s_worldXP + 17, "W simultaneous XP total old+17 within 1 s");
                yield break;
            }
            if (step == 6)
            {
                h.Check(PartnerWorld(5).XP == s_worldXP + 17, "W partner simultaneous XP total old+17");
                float nextDigest = start;
                while (Time.realtimeSinceStartup - start < 5f)
                {
                    if (Time.realtimeSinceStartup >= nextDigest) { WorldSync.SendDigest(); nextDigest += 0.5f; }
                    if (WorldSync.LastDigestAt >= start && WorldSync.LastDigestMatch) break;
                    yield return null;
                }
                yield break;
            }
            if (step == 7)
            {
                WorldTestPacket remote = PartnerWorld(6), local = MakeWorldTest(7);
                h.Note("W final digest local=" + local.Digest.ToString("X16") + " objects=" + local.WorldCount + " remote=" + remote.Digest.ToString("X16") + " objects=" + remote.WorldCount + " localMatch=" + local.DigestMatch + " remoteMatch=" + remote.DigestMatch);
                h.Check(local.DigestMatch && remote.DigestMatch && local.Digest == remote.Digest && local.WorldCount == remote.WorldCount, "W both world digests match");
                h.Check(host || SaveSync.GuestSaveWrites == 0, "W guest save writes remain zero");
                yield break;
            }
            if (step == 8) { yield return SkillStep(h, host, start, false); yield break; }
            if (step == 9)
            {
                WorldTestPacket partner = PartnerWorld(8), local = s_ownWorldPackets[8];
                h.Check(local.ProgressHash != 0 && local.ProgressHash == partner.ProgressHash,
                    "W skills: shared progress still equal after the guest's purchase (local=" + local.ProgressHash.ToString("X16") + ", remote=" + partner.ProgressHash.ToString("X16") + ")");
                yield return SkillStep(h, host, start, true);
            }
        }

        // T-047 / #9: each player has their own skill tree and points. The buyer (guest in step 8, host in step 9) pays,
        // gets the skill and sees the gain-skill flash (SkillTreeManager.GainSkillEffect); the other player gets none of it.
        static IEnumerator SkillStep(TestHarness h, bool host, float start, bool hostBuys)
        {
            string who = hostBuys ? "host" : "guest";
            SkillTreeManager m = SkillTreeManager.Instance;
            h.Check(m != null && m.GainSkillEffect != null,
                "W skills: skill tree loaded (manager=" + (m != null) + ")");
            if (m == null || m.GainSkillEffect == null) yield break;
            string clone = m.GainSkillEffect.name + "(Clone)";
            if (!hostBuys && host) { Own.Level.SkillPoints += 10; yield return Wait(0.5f); }   // a shared gain: every player gets it
            if (!hostBuys) while (Own.Level.SkillPoints < 10 && Time.realtimeSinceStartup - start < 5f) yield return null;
            string treeBefore = OwnedTreeSkills(m); int pointsBefore = Own.Level.SkillPoints, flashesBefore = CountNamed(clone);
            if (host == hostBuys)
            {
                SkillItem pick = null;
                foreach (SkillItem item in m.GetComponentsInChildren<SkillItem>(true))
                    if (!Own.PlayerAbilities.HasAbility(item.Ability) && item.CanEarnSkill
                        && (pick == null || item.ActualRequiredSkillPoints < pick.ActualRequiredSkillPoints)) pick = item;
                h.Check(pick != null, "W skills: the " + who + " has an affordable skill (points=" + pointsBefore + ")");
                if (pick == null) yield break;
                int cost = pick.ActualRequiredSkillPoints;
                SkillItem previous = m.CurrentSkillItem;
                pick.HasSkillItem = false;
                m.CurrentSkillItem = pick;
                try { m.OnMenuItemPressed(); }
                finally { m.CurrentSkillItem = previous; }
                int shown = CountNamed(clone) - flashesBefore;
                h.Check(shown == 1, "W skills: the " + who + " who bought " + pick.Ability + " sees the gain-skill flash (" + shown + ")");
                yield return Wait(3f);   // snapshots both ways must not undo the purchase
                h.Check(Own.PlayerAbilities.HasAbility(pick.Ability) && Own.Level.SkillPoints == pointsBefore - cost,
                    "W skills: the " + who + " keeps " + pick.Ability + " and paid " + cost + " (points " + pointsBefore + " -> " + Own.Level.SkillPoints + ")");
            }
            else
            {
                // Watch every frame for a NEW flash object (one already there may expire meanwhile).
                HashSet<int> seen = NamedIds(clone); int flashes = 0;
                float until = Time.realtimeSinceStartup + 4f;
                while (Time.realtimeSinceStartup < until)
                {
                    foreach (int id in NamedIds(clone)) if (seen.Add(id)) flashes++;
                    yield return null;
                }
                string treeNow = OwnedTreeSkills(m);
                h.Check(flashes == 0, "W skills: the " + (host ? "host" : "guest") + " does not see the " + who + "'s skill-tree flash (" + flashes + ")");
                h.Check(treeNow == treeBefore && Own.Level.SkillPoints == pointsBefore,
                    "W skills: the " + who + "'s purchase leaves the " + (host ? "host" : "guest") + "'s tree and points alone (tree [" + treeBefore + "] -> ["
                    + treeNow + "], points " + pointsBefore + " -> " + Own.Level.SkillPoints + ")");
            }
        }

        // The skill-tree abilities this player owns, read from the game's own skill items (independent of ProgressSync).
        static string OwnedTreeSkills(SkillTreeManager m)
        {
            List<string> owned = new List<string>();
            foreach (SkillItem item in m.GetComponentsInChildren<SkillItem>(true))
                if (Own.PlayerAbilities.HasAbility(item.Ability)) owned.Add(item.Ability.ToString());
            owned.Sort();
            return string.Join(",", owned.ToArray());
        }

        static HashSet<int> NamedIds(string name)
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (GameObject go in UnityEngine.Object.FindObjectsOfType<GameObject>()) if (go.name == name) ids.Add(go.GetInstanceID());
            return ids;
        }

        static int CountNamed(string name)
        {
            int n = 0;
            foreach (GameObject go in UnityEngine.Object.FindObjectsOfType<GameObject>()) if (go.name == name) n++;
            return n;
        }
        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        // E3 has its own reliable numbered barriers; no shared single-slot test signals.
        sealed class EnemyTestPacket
        {
            public string Key; public float Health, X, Y, DeadAt; public bool Dead, Owner;
            public bool Present, Ready; public float Travel;
            public int Seat, OwnerSeat; public uint Epoch;
        }
        sealed class EnemyScenario { public string Key; public bool Valid; public int OwnerSeat; public uint Epoch; }
        static bool s_enemyReady; static float s_enemyTravel;
        static readonly Dictionary<int, EnemyTestPacket> s_enemyPackets = new Dictionary<int, EnemyTestPacket>();
        static readonly Dictionary<int, EnemyTestPacket> s_ownEnemyReports = new Dictionary<int, EnemyTestPacket>();
        static readonly Dictionary<int, float> s_ownEnemyHealth = new Dictionary<int, float>();
        sealed class EnemyTracePoint { public float Tick; public Vector3 Position; }
        static readonly List<EnemyTracePoint> s_enemyOwnerTrace = new List<EnemyTracePoint>();
        static void ReadEnemyTrace(BinaryReader r)
        {
            if (!TestHarness.Enabled) return; int count = r.ReadUInt16();
            if (count > 2048) throw new InvalidDataException("E3 trace count");
            s_enemyOwnerTrace.Clear();
            for (int i = 0; i < count; i++) { EnemyTracePoint p = new EnemyTracePoint(); p.Tick = r.ReadSingle();
                p.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); s_enemyOwnerTrace.Add(p); }
        }
        static void ReadEnemyTest(BinaryReader r)
        {
            int step = r.ReadInt32(); EnemyTestPacket p = new EnemyTestPacket();
            p.Key = r.ReadString(); p.Health = r.ReadSingle(); p.X = r.ReadSingle(); p.Y = r.ReadSingle();
            p.DeadAt = r.ReadSingle(); p.Dead = r.ReadBoolean(); p.Owner = r.ReadBoolean(); s_enemyPackets[step] = p;
            p.Present = r.ReadBoolean(); p.Ready = r.ReadBoolean(); p.Travel = r.ReadSingle();
            p.Seat = r.ReadInt32(); p.OwnerSeat = r.ReadInt32(); p.Epoch = r.ReadUInt32();
        }
        static IEnumerator EnemyBarrier(TestHarness h, int step, string key)
        {
            EnemySync.Probe p = EnemySync.Inspect(key);
            s_ownEnemyHealth[step] = p.Health;
            s_ownEnemyReports[step] = new EnemyTestPacket { Key = key ?? "", Health = p.Health, Owner = p.Owner,
                Present = p.Present, Dead = p.Dead, Ready = s_enemyReady, Travel = s_enemyTravel,
                Seat = Players.LocalSeat, OwnerSeat = p.OwnerSeat, Epoch = p.Epoch };
            NetPlay.Send(49, true, delegate(BinaryWriter w) {
                w.Write(step); w.Write(key ?? ""); w.Write(p.Health); w.Write(p.Origin.x); w.Write(p.Origin.y);
                w.Write(p.DeathTime); w.Write(p.Dead); w.Write(p.Owner);
                w.Write(p.Present); w.Write(s_enemyReady); w.Write(s_enemyTravel);
                w.Write(Players.LocalSeat); w.Write(p.OwnerSeat); w.Write(p.Epoch);
            });
            float end = Time.realtimeSinceStartup + (step == 0 ? 30f : step >= 100 ? 25f : 12f);
            while (Connected && !s_enemyPackets.ContainsKey(step) && Time.realtimeSinceStartup < end) yield return null;
            h.Check(s_enemyPackets.ContainsKey(step), "E3 barrier " + step + " received");
        }
        static bool EnemyGroundPosition(Vector3 origin, float side, out Vector3 position)
        {
            position = origin + new Vector3(side, 1, 0);
            RaycastHit[] hits = Physics.RaycastAll(origin + new Vector3(side, 2, 0), Vector3.down, 12f, Own.Controller.RayTestLayerMask);
            float highest = float.NegativeInfinity; bool found = false;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || hit.collider.isTrigger || hit.normal.y < 0.6f
                    || hit.collider.GetComponentInParent<Entity>() != null || hit.collider.GetComponentInParent<SeinCharacter>() != null) continue;
                if (hit.point.y <= highest || hit.point.y < origin.y - 8f) continue;
                highest = hit.point.y; found = true;
                position = hit.point + (Own.Position - Own.PlatformBehaviour.PlatformMovement.FeetPosition) + Vector3.up * 0.1f;
            }
            return found;
        }
        static bool EnemyReadyHere(EnemySync.Probe p, Vector3 origin)
        {
            return p.Present && p.Mirrored && p.Known && !p.Dead
                && EnemyOwnership.ExpectedRole(NetRoute.ServerMode, NetPlay.IsHost, Players.LocalSeat, p.Owner, p.OwnerSeat)
                && Own.PlatformBehaviour.PlatformMovement.IsOnGround && Mathf.Abs(Own.Position.x - origin.x) <= 15f
                && Mathf.Abs(Own.Position.y - origin.y) <= 12f;
        }
        static bool EnemyAgreement(string key, int step)
        {
            EnemyTestPacket local = s_ownEnemyReports[step], partner = s_enemyPackets[step];
            return local.Key == key && partner.Key == key && EnemyOwnership.PeerAgreement(NetRoute.ServerMode, NetPlay.IsHost,
                local.Seat, local.Owner, local.OwnerSeat, local.Epoch, partner.Seat, partner.Owner, partner.OwnerSeat, partner.Epoch);
        }
        static IEnumerator MovingEnemyScenario(TestHarness h, string first, EnemyScenario result)
        {
            HashSet<string> rejected = new HashSet<string>(); string key = first;
            for (int attempt = 0; attempt < 8 && Connected; attempt++)
            {
                int proposal = attempt == 0 ? 0 : 100 + attempt * 4, ready = 200 + attempt * 4, measured = ready + 1;
                if (attempt > 0)
                {
                    key = NetPlay.IsHost ? EnemySync.FindTestEnemy(Own.Position, rejected) : "";
                    yield return EnemyBarrier(h, proposal, key);
                    if (!NetPlay.IsHost && s_enemyPackets.ContainsKey(proposal)) key = s_enemyPackets[proposal].Key;
                }
                if (string.IsNullOrEmpty(key)) break;
                Vector3 origin = NetPlay.IsHost ? EnemySync.Inspect(key).Origin
                    : new Vector3(s_enemyPackets[proposal].X, s_enemyPackets[proposal].Y, Own.Position.z);
                float side = NetPlay.IsHost ? -6f : 6f;
                EnemySync.LoadTestArea(origin); Plugin.Teleport(Own, origin + new Vector3(side, 1, 0));
                float start = Time.realtimeSinceStartup, loadAt = start;
                while (!EnemySync.Inspect(key).PlaceholderLoaded && Time.realtimeSinceStartup - start < 8f)
                {
                    if (Time.realtimeSinceStartup >= loadAt) { EnemySync.LoadTestArea(origin); loadAt = Time.realtimeSinceStartup + 0.25f; }
                    yield return null;
                }
                Vector3 ground; bool foundGround = EnemyGroundPosition(origin, side, out ground);
                if (foundGround) Plugin.Teleport(Own, ground);
                yield return Wait(1.5f); // Gravity, collision rays, camera and companion must settle before observing AI.
                EnemySync.Probe initial = EnemySync.Inspect(key); Vector3 previous = initial.Position;
                bool active = false; float warmTravel = 0; start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < 5f)
                {
                    EnemySync.Probe p = EnemySync.Inspect(key);
                    if (p.Present) { warmTravel += Vector3.Distance(previous, p.Position); previous = p.Position;
                        if (warmTravel > 0.1f || p.StateName != initial.StateName) active = true; }
                    if (active && foundGround && EnemyReadyHere(p, origin)) break;
                    yield return null;
                }
                s_enemyReady = foundGround && active && EnemyReadyHere(EnemySync.Inspect(key), origin); s_enemyTravel = 0;
                h.Note("E3 candidate=" + attempt + " localOri=" + Own.Position + " ground=" + foundGround + " aiActive=" + active
                    + " warmTravel=" + F(warmTravel) + " ready=" + s_enemyReady + " " + EnemySync.Describe(key));
                yield return EnemyBarrier(h, ready, key);
                EnemyTestPacket partner;
                if (!s_enemyPackets.TryGetValue(ready, out partner)) yield break;
                EnemyTestPacket agreed = s_ownEnemyReports[ready];
                if (!agreed.Ready || !partner.Ready || !EnemyAgreement(key, ready))
                {
                    rejected.Add(key);
                    h.Note("E3 retry candidate: both sides must be grounded, active and agree on one owner/epoch; ownerSeat="
                        + agreed.OwnerSeat + " epoch=" + agreed.Epoch + " peerOwnerSeat=" + partner.OwnerSeat + " peerEpoch=" + partner.Epoch);
                    continue;
                }
                bool owner = NetRoute.ServerMode ? agreed.Owner : NetPlay.IsHost;
                s_enemyOwnerTrace.Clear();
                List<EnemyTracePoint> trace = new List<EnemyTracePoint>();
                double newestErrors = 0; int newestSamples = 0; float travel = 0; previous = EnemySync.Inspect(key).Position;
                long bytes = EnemySync.BytesSent, wireBytes = EnemySync.EstimatedWireBytesSent; start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < 5f)
                {
                    EnemySync.Probe p = EnemySync.Inspect(key);
                    if (!p.Present || p.Dead || !p.Mirrored || !EnemyReadyHere(p, origin)) s_enemyReady = false;
                    if (NetRoute.ServerMode && (p.OwnerSeat != agreed.OwnerSeat || p.Epoch != agreed.Epoch)) s_enemyReady = false;
                    if (p.Present)
                    {
                        travel += Vector3.Distance(previous, p.Position); previous = p.Position;
                        float tick = owner ? NetPlay.LocalTick : p.RenderTick;
                        if (trace.Count == 0 || trace[trace.Count - 1].Tick != tick)
                        { EnemyTracePoint row = new EnemyTracePoint(); row.Tick = tick; row.Position = p.Position; if (trace.Count < 2048) trace.Add(row); }
                        if (!owner && p.HasSnapshot) { newestErrors += Vector3.Distance(p.Position, p.OwnerPosition); newestSamples++; }
                    }
                    yield return null;
                }
                float seconds = Time.realtimeSinceStartup - start;
                long payloadDelta = EnemySync.BytesSent - bytes, wireDelta = EnemySync.EstimatedWireBytesSent - wireBytes;
                s_enemyTravel = travel;
                if (owner) NetPlay.Send(48, true, delegate(BinaryWriter w) {
                    w.Write((ushort)trace.Count); foreach (EnemyTracePoint p in trace) { w.Write(p.Tick); w.Write(p.Position.x); w.Write(p.Position.y); w.Write(p.Position.z); }
                });
                yield return EnemyBarrier(h, measured, key);
                if (!s_enemyPackets.TryGetValue(measured, out partner)) yield break;
                double errors = 0; int samples = 0, i = 1;
                if (!owner) foreach (EnemyTracePoint rendered in trace)
                {
                    while (i < s_enemyOwnerTrace.Count && s_enemyOwnerTrace[i].Tick < rendered.Tick) i++;
                    if (i >= s_enemyOwnerTrace.Count || rendered.Tick < s_enemyOwnerTrace[i - 1].Tick) continue;
                    EnemyTracePoint a = s_enemyOwnerTrace[i - 1], b = s_enemyOwnerTrace[i];
                    float blend = b.Tick == a.Tick ? 0 : (rendered.Tick - a.Tick) / (b.Tick - a.Tick);
                    errors += Vector3.Distance(rendered.Position, Vector3.Lerp(a.Position, b.Position, blend)); samples++;
                }
                float mean = samples == 0 ? float.PositiveInfinity : (float)(errors / samples);
                h.Note("E3 follow seconds=" + F(seconds) + " ownerSeat=" + agreed.OwnerSeat + " epoch=" + agreed.Epoch
                    + " matchedOwnerTickSamples=" + samples + " meanUnits=" + (owner ? "owner-reference" : F(mean))
                    + " newestSnapshotLagError=" + (newestSamples == 0 ? "owner-reference" : F((float)(newestErrors / newestSamples)))
                    + " travel=" + F(travel) + " partnerTravel=" + F(partner.Travel) + " localOri=" + Own.Position
                    + " enemyPayloadKBps=" + F(payloadDelta / 1024f / seconds) + " enemyEstimatedWireKBps=" + F(wireDelta / 1024f / seconds) + " " + EnemySync.Describe(key));
                if (!s_ownEnemyReports[measured].Ready || !partner.Ready || travel <= 1f || partner.Travel <= 1f
                    || !EnemyAgreement(key, measured))
                { rejected.Add(key); h.Note("E3 retry candidate: 5 s travel must exceed 1 on both peers; present=" + EnemySync.Inspect(key).Present); continue; }
                result.Key = key; result.Valid = true; result.OwnerSeat = agreed.OwnerSeat; result.Epoch = agreed.Epoch;
                h.Note("E3 using scenario=" + key + " " + EnemySync.Describe(key));
                h.Check(EnemyAgreement(key, measured), NetRoute.ServerMode
                    ? "E3 exactly one owner; both peers agree on owner/epoch" : "E3 host owns the moving mirrored enemy");
                h.Check(travel > 1f && partner.Travel > 1f, "E3 moving enemy travel >1 on both peers (local=" + F(travel) + " remote=" + F(partner.Travel) + ")");
                if (!owner) h.Check(samples > 20 && mean < 0.5f, "E3 moving puppet mean <0.5 at matched owner ticks (" + F(mean) + ")");
                yield break;
            }
            h.Check(false, "E3 no loaded mirrored candidate sustained >1 travel with both Oris grounded nearby");
        }
        static IEnumerator SharedEnemies(TestHarness h)
        {
            h.Note("phase E3 shared enemies"); Bot(0, 0);
            h.Check(EnemySync.ProtocolSelfTest(), "E3 protocol self-test " + EnemySync.ProtocolSelfTestFailure);
            string key = "";
            Vector3 saved = Own.Position;
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true;
            try
            {
                if (NetPlay.IsHost)
                {
                    key = EnemySync.FindTestEnemy(Own.Position);
                    if (string.IsNullOrEmpty(key) && Core.Scenes.Manager != null)
                    {
                        List<RuntimeSceneMetaData> areas = new List<RuntimeSceneMetaData>();
                        foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                            if (!scene.DependantScene && scene.CanBeLoaded && scene.SceneBoundaries.Count > 0
                                && Game.Checkpoint.SaveGameData.Scenes.ContainsKey(scene.SceneMoonGuid)
                                && scene.Scene.IndexOf("boss", StringComparison.OrdinalIgnoreCase) < 0
                                && scene.Scene.IndexOf("escape", StringComparison.OrdinalIgnoreCase) < 0) areas.Add(scene);
                        areas.Sort(delegate(RuntimeSceneMetaData a, RuntimeSceneMetaData b) {
                            return Vector3.Distance(a.PlaceholderPosition, saved).CompareTo(Vector3.Distance(b.PlaceholderPosition, saved)); });
                        for (int attempt = 0; attempt < areas.Count && attempt < 4 && string.IsNullOrEmpty(key); attempt++)
                        {
                            Vector3 point = areas[attempt].PlaceholderPosition;
                            h.Note("E3 search saved scene=" + areas[attempt].Scene + " guid=" + areas[attempt].SceneMoonGuid + " position=" + point);
                            EnemySync.LoadTestArea(point); Plugin.Teleport(Own, point);
                            Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(new Rect(point.x - 22, point.y - 14, 44, 28), true, true, false);
                            yield return Wait(2f); key = EnemySync.FindTestEnemy(Own.Position);
                        }
                    }
                }
                yield return EnemyBarrier(h, 0, key);
                if (!NetPlay.IsHost && s_enemyPackets.ContainsKey(0)) key = s_enemyPackets[0].Key;
                h.Check(!string.IsNullOrEmpty(key), "E3 loaded enemy scenario available");
                if (string.IsNullOrEmpty(key)) yield break;
                EnemyScenario scenario = new EnemyScenario();
                yield return MovingEnemyScenario(h, key, scenario);
                if (!scenario.Valid) yield break;
                key = scenario.Key; float start;
                // Fixed health is a fixture prerequisite; damage still traverses the production attack receiver.
                bool owner = NetRoute.ServerMode ? scenario.OwnerSeat == Players.LocalSeat : NetPlay.IsHost;
                EnemySync.Probe current = EnemySync.Inspect(key);
                if (owner && (!NetRoute.ServerMode || (current.Owner && current.OwnerSeat == scenario.OwnerSeat && current.Epoch == scenario.Epoch)))
                    EnemySync.PrepareTestEnemy(key, 80f);
                yield return Wait(0.5f);
                // Read the owner's baseline before the barrier; the mirror can attack before the owner returns.
                float ownerBaseline = owner ? EnemySync.Inspect(key).Health : 0f;
                yield return EnemyBarrier(h, 1, key);
                if (!s_enemyPackets.ContainsKey(1)) yield break;
                bool agreed = EnemyAgreement(key, 1) && (!NetRoute.ServerMode
                    || (s_ownEnemyReports[1].OwnerSeat == scenario.OwnerSeat && s_ownEnemyReports[1].Epoch == scenario.Epoch
                    && s_enemyPackets[1].OwnerSeat == scenario.OwnerSeat && s_enemyPackets[1].Epoch == scenario.Epoch));
                h.Check(agreed, "E3 peers agree on one owner/epoch before damage");
                if (!agreed) yield break;
                float baseline = owner ? ownerBaseline : s_enemyPackets[1].Health;
                if (!owner) EnemySync.TestAttack(key, 3f, true);
                yield return Wait(0.5f); yield return EnemyBarrier(h, 2, key);
                float hp = owner ? s_ownEnemyHealth[2] : s_enemyPackets[2].Health;
                h.Note("E3 mirror damage old=" + F(baseline) + " requested=3 duplicateHit=yes ownerNow=" + F(hp) + " delta=" + F(baseline - hp) + " " + EnemySync.Describe(key));
                h.Check(Mathf.Abs(baseline - hp - 3f) < 0.01f, "E3 mirror hit applied exactly once");
                baseline = hp;
                EnemySync.TestAttack(key, owner ? 2f : 3f, false);
                yield return Wait(0.5f); yield return EnemyBarrier(h, 3, key);
                hp = owner ? s_ownEnemyHealth[3] : s_enemyPackets[3].Health;
                h.Note("E3 concurrent owner=2 mirror=3 old=" + F(baseline) + " now=" + F(hp) + " sum=" + F(baseline - hp));
                h.Check(Mathf.Abs(baseline - hp - 5f) < 0.01f, "E3 simultaneous hits total=5");
                start = Time.realtimeSinceStartup;
                if (!owner) EnemySync.TestAttack(key, 1000f, false);
                while (!EnemySync.Inspect(key).Dead && Time.realtimeSinceStartup - start < 0.5f) yield return null;
                h.Note("E3 death observedSeconds=" + F(Time.realtimeSinceStartup - start) + " dead=" + EnemySync.Inspect(key).Dead);
                h.Check(EnemySync.Inspect(key).Dead, "E3 death within 0.5 s");
                yield return EnemyBarrier(h, 4, key);
                h.Check(s_enemyPackets.ContainsKey(4) && s_enemyPackets[4].Dead, "E3 partner also dead");
                // 5 s of GAME time (the respawn timer is 3 s of game time; T-071: real seconds failed on a busy PC).
                bool early = false; float respawnAt = -1, respawnReal = -1; TestClock.Window window = new TestClock.Window(5f);
                while (window.Open)
                {
                    EnemySync.Probe p = EnemySync.Inspect(key);
                    if (!owner && p.Present && p.Dead) early = true;
                    if (!p.Dead && respawnAt < 0) { respawnAt = window.GameElapsed; respawnReal = window.RealElapsed; }
                    yield return null;
                }
                h.Note("E3 earlyMirrorRespawn=" + early + " observedRespawnSeconds=" + F(respawnAt) + " realSeconds=" + F(respawnReal)
                    + " gameRate=" + F(window.Rate) + " " + EnemySync.Diagnostics());
                h.Check(!early, "E3 mirror respawn blocked until owner permission");
                h.Check(respawnAt >= 0 && !EnemySync.Inspect(key).Dead && EnemySync.Inspect(key).Present, "E3 owner respawn observed within 5 s (" + F(respawnAt) + ")");
                yield return EnemyBarrier(h, 5, key);
                h.Check(s_enemyPackets.ContainsKey(5) && !s_enemyPackets[5].Dead, "E3 both peers observe respawn");
            }
            finally { EnemySync.EndTestEnemy(key); Own.Mortality.DamageReciever.IsImmortal = immortal; Plugin.Teleport(Own, saved - Vector3.up * 0.5f); }
        }
        static bool Connected { get { return NetPlay.Peer != null && NetPlay.Peer.State == PeerState.Connected; } }
        static string F(float n) { return n.ToString("F3", CultureInfo.InvariantCulture); }
        static void Bot(float x, int buttons) { BotLX = x; BotButtons = buttons; }
        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        // T-061: every teammate's status bar along the bottom. The guest hurts itself, then uses up its energy, and tells
        // the host (test message 81) what it now has and when (wall clock: both games run on this PC). The host's bar for
        // P2 must show exactly that within 1 s. Both sides run the same 6 s, so later phases stay in step.
        const byte MsgVitalsTest = 81;
        static readonly List<long> s_vitalsAt = new List<long>();
        static readonly List<float> s_vitalsHealth = new List<float>(), s_vitalsEnergy = new List<float>();
        static void ReadVitalsTest(BinaryReader r)
        {
            if (!TestHarness.Enabled) return;
            s_vitalsAt.Add(r.ReadInt64()); s_vitalsHealth.Add(r.ReadSingle()); s_vitalsEnergy.Add(r.ReadSingle());
        }

        static IEnumerator BottomBars(TestHarness h, bool host)
        {
            float start = Time.realtimeSinceStartup;
            int partner = host ? 2 : 1;
            float h0 = 0f, e0 = 0f;
            if (!host)
            {
                h0 = Own.Mortality.Health.Amount; e0 = Own.Energy.Current;
                yield return Wait(1f);
                Own.Mortality.DamageReciever.OnRecieveDamage(new Damage(3f, Vector2.zero, Own.Position, DamageType.Spikes, null));
                float hp = Own.Mortality.Health.Amount, en = Own.Energy.Current;
                NetPlay.Send(MsgVitalsTest, true, w => { w.Write(DateTime.UtcNow.Ticks); w.Write(hp); w.Write(en); });
                h.Note("bottom bars: guest hurt itself " + F(h0) + " -> " + F(hp));
                yield return Wait(1.5f);
                Own.Energy.Current = 0f;
                hp = Own.Mortality.Health.Amount; en = Own.Energy.Current;
                NetPlay.Send(MsgVitalsTest, true, w => { w.Write(DateTime.UtcNow.Ticks); w.Write(hp); w.Write(en); });
                h.Note("bottom bars: guest used its energy " + F(e0) + " -> " + F(en));
            }
            else
            {
                int seen = 0;
                List<float> took = new List<float>();
                while (Time.realtimeSinceStartup - start < 4.5f)
                {
                    yield return null;
                    float bh, bmh, be, bme;
                    while (seen < s_vitalsAt.Count && BottomHud.TryShown(2, out bh, out bmh, out be, out bme)
                        && Mathf.Abs(bh - s_vitalsHealth[seen]) < 0.01f && Mathf.Abs(be - s_vitalsEnergy[seen]) < 0.01f)
                    {
                        took.Add((float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - s_vitalsAt[seen]).TotalSeconds);
                        seen++;
                    }
                }
                float shH, shMH, shE, shME;
                BottomHud.TryShown(2, out shH, out shMH, out shE, out shME);
                h.Note("bottom bars: guest reported " + s_vitalsAt.Count + " changes, host bar matched " + took.Count + " (latency " + string.Join(", ", took.ConvertAll(t => F(t) + " s").ToArray())
                    + "); bar now health " + F(shH) + "/" + F(shMH) + " energy " + F(shE) + "/" + F(shME) + "; vitals received " + BottomHud.VitalsReceived);
                h.Check(s_vitalsAt.Count == 2 && took.Count == 2, "bottom bars: host's P2 bar showed both guest changes (damage, energy use)");
                h.Check(took.Count == 2 && took[0] <= 1f && took[1] <= 1f, "bottom bars: each within 1 s of the guest's change");
            }
            h.Check(BottomHud.Count == 1 && BottomHud.Visible(partner) && BottomHud.LabelText(partner) == Roles.Tag(partner),
                "bottom bars: one bar, for " + Roles.Tag(partner) + ", shown on the " + (host ? "host" : "guest") + " (" + BottomHud.Count + ")");
            h.Check(BottomHud.VitalsSent > 0 && BottomHud.VitalsReceived > 0, "bottom bars: vitals sent " + BottomHud.VitalsSent + " and received " + BottomHud.VitalsReceived);
            try
            {
                string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(h.ResultPath)), "net-bottomhud-" + (host ? "host" : "guest") + ".png");
                Application.CaptureScreenshot(p);
                h.Note("screenshot " + p);
            }
            catch (Exception e) { h.Note("bottom bars screenshot failed: " + e.Message); }
            while (Time.realtimeSinceStartup - start < 5f) yield return null;
            if (!host) { Own.Mortality.Health.SetAmount(Mathf.Max(h0, Own.Mortality.Health.Amount)); Own.Energy.Current = Mathf.Max(e0, Own.Energy.Current); }
            while (Time.realtimeSinceStartup - start < 6f) yield return null;
        }

        // Roles on both screens: host = P1, guest = P2. The partner's one overhead tag says so (P2Hud), and a
        // screenshot of each side is kept next to the result file (net-roles-host.png / net-roles-guest.png).
        static IEnumerator RoleTag(TestHarness h, bool host)
        {
            // In the game's language: "P2 · 伙伴" / "P2 - Partner" (host side), "P1 · 主机" / "P1 - Host" (guest side).
            string want = L10n.F("roles.label", Roles.Tag(host ? 2 : 1), L10n.T(host ? "roles.partner" : "roles.host"));
            float by = Time.realtimeSinceStartup + 3f;
            while (!P2Hud.Visible && Time.realtimeSinceStartup < by) yield return null;
            h.Check(Roles.Mine == (host ? 1 : 2) && Roles.Partner == (host ? 2 : 1), "net roles: this PC is " + (host ? "P1 (host)" : "P2 (guest)"));
            h.Check(P2Hud.Visible && P2Hud.Text.StartsWith(want) && P2Hud.Text.IndexOf('●') < 0 && P2Hud.Text.IndexOf('○') < 0,
                "net roles: partner tag reads '" + want + "' with no health dots (got '" + P2Hud.Text.Replace("\n", " / ") + "')");
            try
            {
                string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(h.ResultPath)), "net-roles-" + (host ? "host" : "guest") + ".png");
                Application.CaptureScreenshot(p);
                h.Note("screenshot " + p);
            }
            catch (Exception e) { h.Note("roles screenshot failed: " + e.Message); }
            yield return Wait(0.5f);
        }

        sealed class PhaseSample
        {
            public float StartX, StartY, MaxX, Drop, Rise;
            public double ErrorSum;
            public long Samples;
            public PhaseSample()
            {
                if (P2Context.P2 != null) { StartX = MaxX = P2Context.P2.Position.x; StartY = P2Context.P2.Position.y; }
            }
            public float Mean { get { return Samples == 0 ? float.PositiveInfinity : (float)(ErrorSum / Samples); } }
            public void Sample()
            {
                ErrorSum += NetPlay.AvgError; Samples++;
                if (P2Context.P2 == null) return;
                Vector3 p = P2Context.P2.Position;
                MaxX = Mathf.Max(MaxX, p.x);
                Drop = Mathf.Max(Drop, MaxX - p.x);
                Rise = Mathf.Max(Rise, p.y - StartY);
            }
        }

        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class LocalBotPatch
        {
            static bool Prefix()
            {
                if (!TestHarness.Enabled || !NetPlay.Enabled || !BotActive) return true;
                s_tmp.Swap();
                P2Input.Fill(s_tmp, BotButtons, BotLX, 0, 0, 0, false, P2Input.BotRT, false);   // BotRT: Glide / grab for phase P
                s_tmp.Swap();
                return false;
            }
        }

        // This postfix observes positions at the same point in the fixed step as the sent frame and correction.
        [HarmonyPatch(typeof(NetPlay), "FixedTick")]
        static class TrackPatch
        {
            static void Prefix(out long __state) { __state = NetPlay.FramesPlayed; }
            static void Postfix(long __state)
            {
                if (!TestHarness.Enabled || !NetPlay.Enabled || !s_tracking) return;
                bool played = NetPlay.FramesPlayed != __state;
                if (played && s_sample != null) s_sample.Sample();
                try
                {
                    uint ownTick = NetPlay.LocalTick, remoteTick = NetPlay.LastPlayedTick;
                    // Sample each stream on its own tenth tick so rows can be matched exactly across clients.
                    if (Own != null && ownTick % 10 == 0 && ownTick != s_ownWritten)
                    { WriteTrack("own", ownTick, Own.Position); s_ownWritten = ownTick; }
                    if (played && P2Context.P2 != null && remoteTick % 10 == 0 && remoteTick != s_remoteWritten)
                    { WriteTrack("puppet", remoteTick, P2Context.P2.Position); s_remoteWritten = remoteTick; }
                }
                catch (Exception e)
                {
                    s_tracking = false;
                    s_harness.Check(false, "F track write: " + e.Message);
                }
            }
        }

        static void WriteTrack(string kind, uint tick, Vector3 p)
        {
            s_track.WriteLine(kind + "," + tick.ToString(CultureInfo.InvariantCulture) + "," + p.x.ToString("R", CultureInfo.InvariantCulture) + "," + p.y.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void Stop()
        {
            Bot(0, 0); BotActive = false; s_tracking = false; s_sample = null;
            if (s_track != null) { s_track.Dispose(); s_track = null; }
            NetPlay.SetSim(0, 0, 0, 0);
        }

        static IEnumerator Movement(TestHarness h, string phase, bool moving)
        {
            h.Note("phase " + phase + " localMoving=" + moving + " saveScreen=" + SessionScreens.Phase
                + " pausedByPartner=" + SessionRules.PausedByPartner + " localPaused=" + SessionRules.LocalPaused);
            long snaps = NetPlay.Snaps, played = NetPlay.FramesPlayed;
            PhaseSample sample = new PhaseSample(); s_sample = sample;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 7f)
            {
                float t = Time.realtimeSinceStartup - start;
                float x = t < 2f ? 1f : (t >= 2.5f && t < 4.5f ? -1f : 0f);
                int b = (t >= 1f && t < 1.25f) || (t >= 3.5f && t < 3.75f) ? P2Input.BTN_A : (t >= 5f && t < 5.1f ? P2Input.BTN_X : 0);
                Bot(moving ? x : 0, moving ? b : 0);
                yield return null;
            }
            Bot(0, 0); s_sample = null;
            h.Note(phase + " right=" + F(sample.MaxX - sample.StartX) + " left=" + F(sample.Drop) + " rise=" + F(sample.Rise) + " AvgError=" + F(sample.Mean) + " snaps=" + (NetPlay.Snaps - snaps) + " played=" + (NetPlay.FramesPlayed - played));
            if (!moving)
            {
                h.Check(sample.MaxX - sample.StartX > 2f, phase + " puppet right > 2 (" + F(sample.MaxX - sample.StartX) + ")");
                h.Check(sample.Drop > 2f, phase + " puppet left > 2 (" + F(sample.Drop) + ")");
                h.Check(sample.Rise > 0.8f, phase + " puppet rise > 0.8 (" + F(sample.Rise) + ")");
                h.Check(sample.Mean < 0.5f, phase + " AvgError < 0.5 (" + F(sample.Mean) + ")");
                h.Check(NetPlay.Snaps == snaps, phase + " no snaps (" + (NetPlay.Snaps - snaps) + ")");
            }
        }

        public static IEnumerator Run(TestHarness h)
        {
            s_readySeats.Clear(); s_threePackets.Clear();
            s_goAt = s_pickupAt = s_collectedAt = 0; s_threeEnded = s_leaveReleased = s_done = false;
            NetPlay.TestSignalReceived += ReadTestSignal;
            NetPlay.SessionEnded += ThreeSessionEnded;
            if (ClientCount == 3 || ClientCount == 4 || Environment.GetEnvironmentVariable("ORICOOP_NET_LOCAL_PLAYERS") == "1") NetPlay.On(MsgThreeTest, ReadThree);
            try { yield return Safely(h, ClientCount == 4 ? "4P suite" : "net suite", RunCore(h)); }
            finally
            {
                NetPlay.TestSignalReceived -= ReadTestSignal;
                NetPlay.SessionEnded -= ThreeSessionEnded;
                Stop();
            }
        }

        // ORICOOP_PHASES=E3,R (T-078): run only these net phases of the 2-player suite; unset = all. Phases:
        //   INTRO MOVE(A-F) W(shared world) E3 E5 R S4 P WO CAM CUT. Start-up (session, save handshake, start barrier,
        //   roles) and the closing G checks always run. src\phase-map.psd1 maps source files to phases (run-affected).
        // The older ORICOOP_NET_ONLY=P3|WO|CAM|S4 still works (P3 = CUT; WO = WO,CAM,CUT; CAM = CAM,CUT; S4 = S4, as before).
        public static readonly string[] AllPhases = { "INTRO", "MOVE", "W", "E3", "E5", "R", "S4", "P", "WO", "CAM", "CUT" };
        static HashSet<string> s_phases;
        internal static bool PhaseOn(string phase)
        {
            if (s_phases == null)
            {
                string list = Environment.GetEnvironmentVariable("ORICOOP_PHASES") ?? "";
                string only = Environment.GetEnvironmentVariable("ORICOOP_NET_ONLY") ?? "";
                if (list.Trim().Length == 0)
                    list = only == "P3" ? "CUT" : only == "WO" ? "WO,CAM,CUT" : only == "CAM" ? "CAM,CUT" : only == "S4" ? "S4" : string.Join(",", AllPhases);
                s_phases = new HashSet<string>();
                foreach (string part in list.Split(',', ' ', ';')) if (part.Trim().Length > 0) s_phases.Add(part.Trim().ToUpperInvariant());
                if (Environment.GetEnvironmentVariable("ORICOOP_NET_NO_WO") == "1") s_phases.Remove("WO");
            }
            return s_phases.Contains(phase);
        }
        // T-086: a phase name that does not exist selects nothing; the run then passed with only the start-up and G checks.
        static string UnknownPhases()
        {
            PhaseOn("INTRO");
            List<string> bad = new List<string>();
            foreach (string p in s_phases) if (Array.IndexOf(AllPhases, p) < 0) bad.Add(p);
            return string.Join(",", bad.ToArray());
        }
        // "=== phase X" lines mark where each phase starts in the result file: run-affected reruns only the phases of the
        // checks that failed (T-096). Checks after "=== phase END" (closing barrier, G, errors) belong to no phase.
        static bool Mark(TestHarness h, string phase) { h.Note("=== phase " + phase); return true; }
        static bool PhasesSelected { get { foreach (string p in AllPhases) if (!PhaseOn(p)) return true; return false; } }

        static IEnumerator RunCore(TestHarness h)
        {
            bool host = NetPlay.IsHost;
            s_worldPackets.Clear(); s_ownWorldPackets.Clear(); NetPlay.On(MsgWorldTest, ReadWorldTest);
            s_vitalsAt.Clear(); s_vitalsHealth.Clear(); s_vitalsEnergy.Clear(); NetPlay.On(MsgVitalsTest, ReadVitalsTest);
            s_enemyPackets.Clear(); s_ownEnemyReports.Clear(); s_ownEnemyHealth.Clear(); s_enemyOwnerTrace.Clear(); NetPlay.On(49, ReadEnemyTest); NetPlay.On(48, ReadEnemyTrace);
            s_harness = h; BotActive = true; Bot(0, 0);
            h.Note("net role=" + (host ? "host" : "guest"));
            if (Environment.GetEnvironmentVariable("ORICOOP_NET_LOCAL_PLAYERS") == "1")
            { yield return LocalPlayersChecks(h); yield break; }
            if (ClientCount != 2 || (PhaseOn("INTRO") && Mark(h, "INTRO"))) yield return NetIntroTest.Run(h, ClientCount);
            if (ClientCount == 2) h.Note("net phases: " + string.Join(",", new List<string>(AllPhases).FindAll(PhaseOn).ToArray()));
            if (Environment.GetEnvironmentVariable("ORICOOP_INTRO_ONLY") == "1") yield break;
            if (ClientCount == 4) { yield return FourClients(h); yield break; }
            float deadline = Time.realtimeSinceStartup + (ClientCount == 3 ? 120f : 40f);
            while ((Players.Remotes.Count != ClientCount - 1 || !SaveSync.Ready) && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(Players.Remotes.Count == ClientCount - 1, "net " + (ClientCount == 3 ? "two puppets appear within 120 s; " : "puppet appears within 40 s; ") + NetPlay.StatusLine());
            h.Check(SaveSync.Ready, "net save handshake completes before movement phases");
            if (Players.Remotes.Count != ClientCount - 1 || !SaveSync.Ready) yield break;
            h.Check(Own != null && Own != P2Context.P2, "net own Ori and puppet are separate");
            if (Own == null || Own == P2Context.P2) yield break;
            yield return StartBarrier(h);
            if (s_goAt == 0) yield break;
            h.Note("timeline start");
            if (ClientCount == 3)
            {
                yield return ThreeClients(h);
                h.Check(h.ErrorCount == 0, "net no Unity errors/exceptions (" + h.ErrorCount + ")");
                yield break;
            }
            try
            {
                try
                {
                    s_track = new StreamWriter(h.ResultPath + ".track.csv", false);
                    s_track.AutoFlush = true;
                    s_ownWritten = s_remoteWritten = 0;
                    s_tracking = true;
                }
                catch (Exception e) { h.Check(false, "F open track: " + e.Message); }
                yield return Wait(1.5f);
                yield return RoleTag(h, host);
                { string bad = UnknownPhases(); h.Check(bad.Length == 0, "net every selected phase exists" + (bad.Length > 0 ? " (unknown: " + bad + "; phases are " + string.Join(",", AllPhases) + ")" : "")); }
                yield return BottomBars(h, host);
                Vector3 phaseAStart = Own.Position;
                if (Environment.GetEnvironmentVariable("ORICOOP_NET_ONLY") != "SWEEP") goto AfterSweep; yield return Safely(h, "SWEEP", NetSceneSweep.Run(h)); yield break; AfterSweep:
                if (Environment.GetEnvironmentVariable("ORICOOP_NET_ONLY") == "S4R")   // only the S4 drop + rejoin step (T-030)
                {
                    yield return NetStoryTest.PlaceAtStart(h, phaseAStart, 62);
                    yield return Safely(h, "S4R", NetStoryTest.RunReconnect(h));
                    goto G;
                }
                if (!PhaseOn("MOVE")) { s_tracking = false; goto AfterMove; }
                Mark(h, "MOVE");
                yield return Movement(h, "A", host);
                yield return Wait(1.5f);
                yield return Movement(h, "B", !host);
                yield return Wait(1.5f);

                h.Note("phase C lossy 60,20,10,2");
                NetPlay.SetSim(60, 20, 10, 2);
                long snaps = NetPlay.Snaps, played = NetPlay.FramesPlayed;
                PhaseSample sample = new PhaseSample(); s_sample = sample;
                float start = Time.realtimeSinceStartup;
                // RTT includes the partner's simulated lag, and the two copies don't enter or leave phase C at the
                // same moment (no barrier): the end-of-phase value could already miss the partner's lag (guest RTT
                // read ~half). Sample the middle of the phase, when both copies are lagging, and take the median.
                List<float> rttSamples = new List<float>();
                float nextRtt = 3f;
                while (Time.realtimeSinceStartup - start < 10f)
                {
                    float t = Time.realtimeSinceStartup - start, cycle = t % 3f;
                    if (t >= nextRtt && t <= 8f && NetPlay.Peer != null) { rttSamples.Add(NetPlay.Peer.Stats.RttMs); nextRtt += 0.25f; }
                    float x = t >= 8f ? 0 : (cycle < 1.5f ? 1f : (cycle < 2.25f ? -1f : 0));
                    Bot(host ? x : -x, t < 8f && cycle > 0.5f && cycle < 0.7f ? P2Input.BTN_A : 0);
                    yield return null;
                }
                Bot(0, 0); s_sample = null;
                rttSamples.Sort();
                float rtt = rttSamples.Count > 0 ? rttSamples[rttSamples.Count / 2] : (NetPlay.Peer == null ? -1 : NetPlay.Peer.Stats.RttMs);
                float loss = NetPlay.Peer == null ? -1 : NetPlay.Peer.Stats.LossPercent;
                h.Note("C oneWayFromPartner=" + F(NetPlay.OneWayMs) + " ms (same-PC clock) spread=" + F(NetPlay.LatenessSpreadMs) + " targetDelay=" + NetPlay.TargetDelay);
                h.Note("C RTT=" + F(rtt) + " loss=" + F(loss) + " AvgError=" + F(sample.Mean) + " snaps=" + (NetPlay.Snaps - snaps) + " played=" + (NetPlay.FramesPlayed - played) + " " + NetPlay.StatusLine());
                // C-server: RTT is to the server (each client simulates only its own link), so the range starts lower.
                float rttMin = OriCoop.Authority.NetRoute.ServerMode ? 50f : 100f;
                h.Check(rtt >= rttMin && rtt <= 260, "C RTT " + rttMin + "..260 (" + F(rtt) + ")");
                h.Check(loss >= 3 && loss <= 25, "C loss 3..25 (" + F(loss) + ")");
                h.Check(sample.Mean < 1f, "C AvgError < 1.0 (" + F(sample.Mean) + ")");
                h.Check(NetPlay.Snaps - snaps <= 3, "C snaps <= 3 (" + (NetPlay.Snaps - snaps) + ")");
                h.Check(NetPlay.FramesPlayed - played > 300, "C played > 300 (" + (NetPlay.FramesPlayed - played) + ")");
                h.Check(Connected, "C still Connected");
                NetPlay.SetSim(0, 0, 0, 0);
                yield return Wait(1.5f);

                h.Note("phase D guest blackout 2 s");
                if (!host)
                {
                    NetPlay.SetSim(0, 0, 100, 0); Bot(1, 0);
                    yield return Wait(2f);
                    NetPlay.SetSim(0, 0, 0, 0); Bot(0, 0);
                    yield return Wait(2f);
                    h.Note("D blackout=2 recoveryError=" + F(NetPlay.LastError) + " " + NetPlay.StatusLine());
                    h.Check(Connected, "D still Connected");
                    // Keep both phase schedules the same length while the host observes arrival/recovery.
                    yield return Wait(2f);
                }
                else
                {
                    bool sawBlackout = false, recovered = false, converged = false, sawHint = false;
                    float minX = 0, maxX = 0, recoveryStart = 0, recoveryError = float.PositiveInfinity;
                    float lastReceivingX = P2Context.P2 == null ? 0 : P2Context.P2.Position.x;
                    long lastReceivingFrame = NetPlay.FramesPlayed, blackoutFrame = lastReceivingFrame;
                    start = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - start < 6f)
                    {
                        // SilenceMs covers both modes: the guest's peer (P2P) or the guest's last message via the C-server.
                        bool silent = NetPlay.SilenceMs > 350;
                        if (NetPlay.SignalLost && NetHud.SignalLostText().Length > 0
                            && NetHud.SignalBars(NetPlay.Connected, NetPlay.SignalLost, NetPlay.RttMs, NetPlay.LossPercent) == 0) sawHint = true;
                        if (silent && P2Context.P2 != null)
                        {
                            float x = P2Context.P2.Position.x;
                            if (!sawBlackout) { sawBlackout = true; minX = maxX = lastReceivingX; blackoutFrame = NetPlay.FramesPlayed; }
                            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                        }
                        if (!sawBlackout && P2Context.P2 != null && NetPlay.FramesPlayed != lastReceivingFrame)
                        { lastReceivingX = P2Context.P2.Position.x; lastReceivingFrame = NetPlay.FramesPlayed; }
                        if (sawBlackout && !silent && !recovered && NetPlay.FramesPlayed > blackoutFrame)
                        { recovered = true; recoveryStart = Time.realtimeSinceStartup; }
                        if (recovered && Time.realtimeSinceStartup - recoveryStart <= 2f)
                        { recoveryError = Mathf.Min(recoveryError, NetPlay.LastError); if (NetPlay.LastError < 0.6f) converged = true; }
                        yield return null;
                    }
                    h.Note("D observed=" + sawBlackout + " blackoutDx=" + F(maxX - minX) + " recovered=" + recovered + " recoveryError=" + F(recoveryError) + " " + NetPlay.StatusLine());
                    h.Check(Connected, "D still Connected");
                    h.Check(sawBlackout && maxX - minX < 4f, "D blackout puppet dx < 4 (" + F(maxX - minX) + ")");
                    h.Check(recovered && converged, "D LastError < 0.6 within 2 s (" + F(recoveryError) + ")");
                    // Lag test ②: a 2 s blackout shows the "信号中断" hint (bars 0, "-- ms") and clears after recovery.
                    h.Check(sawHint && !NetPlay.SignalLost, "D HUD signal-lost hint shown during blackout, cleared after");
                    // Lag test 7 (200 / 400 / 800 ms look different) is pure logic: unit tests "signal ..." in src\Tests\Ui (T-096).
                }
                yield return Wait(1.5f);

                // Both sides establish the snap baseline before the host's teleport, despite startup skew.
                h.Note("phase E teleport armed");
                snaps = NetPlay.Snaps;
                yield return Wait(1.5f);
                start = Time.realtimeSinceStartup;
                if (host)
                {
                    h.Note("E teleport from=" + Own.Position + " to=" + (phaseAStart + new Vector3(0, 0.5f, 0)));
                    Own.Position = phaseAStart + new Vector3(0, 0.5f, 0); Own.Speed = Vector3.zero;
                }
                bool sawSnap = false;
                float snapAt = -1, errorAfter = float.PositiveInfinity;
                while (Time.realtimeSinceStartup - start < 1f)
                {
                    if (NetPlay.Snaps > snaps && !sawSnap) { sawSnap = true; snapAt = Time.realtimeSinceStartup - start; }
                    yield return null;
                }
                if (!host) h.Check(sawSnap, "E snaps >= 1 within 1 s (" + (NetPlay.Snaps - snaps) + ", observedAt=" + F(snapAt) + ")");
                // E + 2 seconds: finish the movement measurement before the shared-world phase.
                while (Time.realtimeSinceStartup - start < 2f)
                {
                    errorAfter = NetPlay.LastError;
                    yield return null;
                }
                h.Note("E snaps=" + (NetPlay.Snaps - snaps) + " LastError=" + F(errorAfter));
                if (!host) h.Check(errorAfter < 0.5f, "E LastError < 0.5 (" + F(errorAfter) + ")");
                s_tracking = false;
                h.Check(s_ownWritten > 0 && s_remoteWritten > 0, "F own and puppet track rows recorded");
            AfterMove:
                if (PhaseOn("W") && Mark(h, "W")) yield return SharedWorld(h);
                if (PhaseOn("E3") && Mark(h, "E3")) yield return Safely(h, "E3", SharedEnemies(h));
                if (PhaseOn("E5") && Mark(h, "E5")) yield return Safely(h, "E5", EnemySyncTest.Run(h));   // enemy AI/death/damage/difficulty (EnemySyncTest.cs, enemy-sync)
                if (PhaseOn("R") && Mark(h, "R")) { yield return NetStoryTest.PlaceAtStart(h, phaseAStart, 60); yield return Safely(h, "R", NetReviveTest.Run(h)); }   // online downed/revive
                if (PhaseOn("S4") && Mark(h, "S4")) { yield return NetStoryTest.PlaceAtStart(h, phaseAStart, 62); yield return Safely(h, "S4", NetStoryTest.Run(h)); }
                if (PhaseOn("P") && Mark(h, "P")) yield return Safely(h, "P", NetPhysTest.Run(h));   // pushable stones (NetPhysTest.cs, world-physics)
                if (PhaseOn("WO") && Mark(h, "WO")) yield return Safely(h, "WO", NetWorldObjTest.Run(h));   // dynamic world objects (NetWorldObjTest.cs, world-physics)
                if (PhaseOn("CAM") && Mark(h, "CAM")) yield return Safely(h, "CAM", NetCameraTest.Run(h, phaseAStart));   // online influence camera (NetCameraTest.cs, T-058)
                if (PhaseOn("CUT") && Mark(h, "CUT")) { yield return NetStoryTest.PlaceAtStart(h, phaseAStart, 64); yield return Safely(h, "P3", NetCutsceneTest.Run(h)); }
                h.Note("=== phase END");
                // With selected phases the last one is followed by G at once: the end that finished first left the
                // session while the other still waited for that phase's last barrier (E5 alone: "E5L barrier 44").
                // Meet once more before G (T-078). A full run keeps its old ending.
                if (PhasesSelected)
                {
                    PublishWorldBarrier(h, 9900, false);
                    float endAt = Time.realtimeSinceStartup + 60f;
                    while (Connected && !s_worldPackets.ContainsKey(9900) && Time.realtimeSinceStartup < endAt) yield return null;
                    h.Check(s_worldPackets.ContainsKey(9900), "net both ends finished the selected phases");
                    // The guest leaves first in G. Seeing the host's 9900 did not mean the host had the guest's (big,
                    // reliable) 9900 yet: on a lossy link the guest left with it still unsent and the host failed the
                    // check above (3 of 4 WO-only runs, T-078). The host confirms with 9901; the guest waits for it.
                    if (host && s_worldPackets.ContainsKey(9900)) PublishWorldBarrier(h, 9901, false);
                    else if (!host && s_worldPackets.ContainsKey(9900))
                    {
                        endAt = Time.realtimeSinceStartup + 60f;
                        while (Connected && !s_worldPackets.ContainsKey(9901) && Time.realtimeSinceStartup < endAt) yield return null;
                        h.Check(s_worldPackets.ContainsKey(9901), "net the host confirmed both ends finished");
                    }
                }
            G:
                if (host)
                {
                    start = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - start < 7f && !(P2Context.P2 == null && NetPlay.CurrentPhase == NetPlay.Phase.Waiting)) yield return null;
                    h.Note("G elapsed=" + F(Time.realtimeSinceStartup - start) + " puppet=" + (P2Context.P2 != null) + " " + NetPlay.StatusLine());
                    h.Check(P2Context.P2 == null, "G puppet removed within 7 s");
                    h.Check(NetPlay.CurrentPhase == NetPlay.Phase.Waiting, "G host Listening within 7 s");
                }
                // Lag test ①: a persistent world-digest mismatch must not trigger a full resync every 5 s (was 9-19 per run).
                h.Note("digest resyncs=" + WorldSync.DigestResyncs + " deferred=" + WorldSync.DigestResyncsDeferred);
                // Phase WO teleports through nine scenes on purpose; its scene-load resyncs are not counted (NetWorldObjTest.Resyncs).
                if (!host) h.Check(WorldSync.DigestResyncs - NetWorldObjTest.Resyncs <= 1, "digest-mismatch full resyncs <= 1 per run (" + WorldSync.DigestResyncs + ", " + NetWorldObjTest.Resyncs + " during phase WO)");
                h.Check(h.ErrorCount == 0, "net no Unity errors/exceptions (" + h.ErrorCount + ")");
            }
            finally { Stop(); }
        }
    }
}
