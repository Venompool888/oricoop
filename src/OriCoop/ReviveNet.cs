using System;
using System.IO;
using Game;
using UnityEngine;

using OriCoop.Seams;
using OriCoop.Rules;

namespace OriCoop
{
    // Each PC decides only for its own Ori and health; the host decides the wipe.
    //   60 Downed    owner -> partner   "my Ori is down, skull at (x,y,z)"          reliable
    //   61 Revive    saver -> owner     "revived you, giving N health"              reliable
    //   62 Revived   owner -> saver     "I'm back" (clears the skull there too)     reliable
    //   63 BothDown  host  -> guest     "we're both down: reload"                   reliable
    //   64 Progress  saver -> others    downed seat, rescuer seat, progress 0..1     unreliable
    //   69 RoomLink  host  -> all       the room's latest save point + saver health  reliable (T-041, Revive.SoulLink.cs)
    //                guest -> host      where the save point it is offering is       reliable (just before its SaveOffer)
    //   91 DownedState owner -> all     each local seat: down (skull x,y,z) or up    reliable (T-166, SendDownedState)
    // Both down is decided by the host only, so simultaneous deaths can't trigger two different reloads. The host
    // runs the normal death/checkpoint reload; the guest runs its own normal death too, and SaveSync brings its
    // world back in line with the host's save afterwards.
    public static partial class Revive
    {
        public const byte MsgDowned = 60, MsgRevive = 61, MsgRevived = 62, MsgBothDown = 63, MsgProgress = 64, MsgDownedState = 91;
        static bool s_netRegistered;
        static SpectateState s_spectate = new SpectateState();
        internal static bool SpectateActive { get { return s_spectate.Active; } }
        internal static int SpectateTarget { get { return s_spectate.Target; } }
        internal static SeinCharacter SpectateSein { get { return SpectateActive ? Host.SeinAt(SpectateTarget) : null; } }
        internal static void SpectateNext() { s_spectate.Next(); }
        internal static void SpectatePrevious() { s_spectate.Prev(); }
        internal static void ResetSpectate() { if (SpectateActive) s_spectate = new SpectateState(); }
        public static int NetDownedSent, NetDownedReceived, NetRevivesSent, NetRevivesApplied, NetBothDown;

        public static bool Online
        {
            get { return Enabled && Services.Net.Enabled && Services.Net.Connected && Host.P1 != null && Host.PuppetCount != 0; }
        }

        static void RegisterNet()
        {
            PartnerFx.EnsureRegistered();
            if (s_netRegistered) return;
            s_netRegistered = true;
            Services.Net.On(MsgDowned, OnNetDowned);
            Services.Net.On(MsgRevive, OnNetRevive);
            Services.Net.On(MsgRevived, OnNetRevived);
            Services.Net.On(MsgBothDown, OnNetBothDown);
            Services.Net.On(MsgProgress, OnNetProgress);
            Services.Net.On(MsgDownedState, OnNetDownedState);
            Services.Net.PlayerLeft += ClearSeat;
        }

        static void ClearSeat(int seat)
        {
            Downed d = s_roster.Take(seat);
            RestoreDowned(d, false);
            s_haveSafe[seat] = false; s_haveSeen[seat] = false;
            HostWipeIfAllDown();   // the last player standing left: the host's grace decides (TickOnline)
        }

        // T-215 #2: the host declares "everyone is down" only once that stayed true for WipeGrace.Seconds. A teammate's
        // "I'm back" (62) that left before another teammate's "I'm down" (60) can arrive after it; deciding the moment
        // the 60 arrived rolled back a player who was already up.
        static readonly WipeGrace s_wipe = new WipeGrace();
        // Planted bug: decide the moment everyone looks down, as before T-215.
        static readonly bool s_mutateWipeNoGrace = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_WIPE_NO_GRACE") == "1";
        public static int WipesHeldBack;   // times everyone looked down and somebody turned out to be up within the grace

        static void TickWipe()
        {
            if (!Services.Net.IsHost) { s_wipe.Reset(); return; }
            bool armed = s_wipe.Armed;
            bool allDown = AnyDowned && AllDown(null);
            if (s_wipe.Tick(allDown, Time.realtimeSinceStartup, WipeGrace.Seconds)) DeclareWipe();
            else if (armed && !s_wipe.Armed)
            {
                WipesHeldBack++;
                Services.Log.Info("online: everyone looked down, but a teammate was already up: no reload");
            }
        }

        static void DeclareWipe()
        {
            FinalDeaths++; NetBothDown++;
            Services.Log.Info("online: both Oris are down (host decides): checkpoint reload");
            Services.Net.Send(MsgBothDown, true, w => { });
            ReleaseRemoteDowned(); // T-026: the partners' skulls go now, not when their puppets later vanish
            KillP1Normally();
        }

        // Called from the OnKill prefix for the LOCAL Ori online. true = turned into downed (skip the game's death).
        public static bool HandleDeathOnline(SeinCharacter p1)
        {
            if (s_passThrough || !Online || p1 == null || !Players.IsLocal(PlayerOf(p1))) return false;
            RegisterNet(); Subscribe();
            if (IsDowned(p1)) return true;
            bool partnerDown = AllDown(p1);
            if (ReviveLogic.DecideOnlineDeath(partnerDown, partnerDown && Services.Net.IsHost) == DeathOutcome.Checkpoint)
            {
                if (s_mutateWipeNoGrace)
                {
                    // Before T-215: the guest is already down and now the host: both out, decided at once.
                    DeclareWipeNow(p1);
                    return p1 != Host.P1;
                }
                // T-215 #2: the host goes down last. It is down like anyone else; the wipe follows once everyone has
                // been down for the grace (TickWipe), in case a teammate's "I'm back" is still on its way.
                Services.Log.Info("online: the host went down last: reload after the grace unless a teammate is already up");
            }
            // Down; the partner can revive us. (Guest with the host already down: the host decides on our message.)
            MakeDowned(p1);
            int seat = PlayerOf(p1);
            Vector3 m = s_down[seat].Marker;
            Services.Net.Send(MsgDowned, true, w => ReviveWire.WritePosition(w, seat, m.x, m.y, m.z));
            NetDownedSent++;
            return true;
        }

        // Every frame while online (from Plugin's online path).
        public static void TickOnline()
        {
            if (Services.Net.Enabled) RegisterNet(); // partner hurt visuals also when revive is off; T-166 snapshots also while loading
            if (!Online)
            {
                ResetSpectate();
                s_wipe.Reset();
                s_seenRemote = 0;   // back online: tell everyone our seats' state again (T-166)
                if (AnyDowned && Services.Net.Enabled && !Services.Net.Connected) ClearAll("partner disconnected", Host.P1 != null);
                HideStaleSelfHints();
                return;
            }
            RegisterNet(); Subscribe();
            TrackSafeGround();
            bool told = ResolveSavePoint();   // T-202: no room save yet -> the host's latest checkpoint is the save point
            int seen = 0;
            foreach (int seat in PresentSeats) if (!Players.IsLocal(seat)) seen |= 1 << seat;
            if ((seen & ~s_seenRemote) != 0)
            {
                SendDownedState();
                if (Services.Net.IsHost)
                {
                    NoteTeammateArrival();
                    // T-041: a PC that joined or reloaded learns the latest soul link. T-215 #7: as a repeat, so the
                    // PCs that already know it do not take it for a soul link made just now.
                    if (!told) SendRoomLink(true);
                }
            }
            s_seenRemote = seen;
            TickWipe();   // T-215 #2 (host): everyone down for the whole grace -> the reload
            foreach (int localSeat in Players.LocalSeats)
            {
                Downed own = s_down[localSeat];
                if (own == null) continue;
                if (own.Sein == Host.P1) KeepPinned(own);
                Step(own);
                if (s_down[localSeat] != own) continue;
                // T-041: hold Y at the latest soul link. The 3-4 player 30 s respawn beside a teammate is gone (#15:
                // a timed comeback belongs to escape levels only, which the escape ticket handles).
                TickSelf(own);
            }
            HideStaleSelfHints();
            foreach (int seat in PresentSeats)
            {
                Downed d = s_down[seat];
                if (!Players.IsLocal(seat) && d != null)
                {
                    if (d.Sein == null) d.Sein = SeinAt(seat);
                    KeepHidden(d); Step(d);
                    if (s_down[seat] == d) SendProgress(d);
                }
            }
            if (s_down[Host.LocalSeat] != null || SpectateActive)
            {
                // T-108: spectate only once every player on this PC is down (T-042); the primary seat comes first.
                s_spectate.Tick(Players.LocalSeats, PresentSeats, DownedSeats());
                if (SpectateActive)
                {
                    if (Input.GetKeyDown(KeyCode.A) || Core.Input.MenuLeft.OnPressed) SpectatePrevious();
                    if (Input.GetKeyDown(KeyCode.D) || Core.Input.MenuRight.OnPressed) SpectateNext();
                }
            }
        }

        static System.Collections.Generic.IEnumerable<int> DownedSeats()
        {
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++) if (s_down[seat] != null) yield return seat;
        }

        // Watchdog that does not depend on Plugin's per-frame paths (those stop once the partner's Ori is gone):
        // our Ori is down and nobody can revive it any more (partner left / disconnected / P2 removed) -> it dies
        // the normal way, as in single player. A downed partner's skull is simply cleared.
        class Watchdog : MonoBehaviour
        {
            void Update()
            {
                if (!AnyDowned) return;
                // T-169: a teammate loading scenes (no puppet for a moment, e.g. at an escape's exit) is still in the
                // session (T-131): not gone.
                bool partnerGone = Host.PuppetCount == 0 && !TeammateInSession() || (Services.Net.Enabled && !Services.Net.Connected);
                if (partnerGone) ClearAll("partner gone while someone was downed", Host.P1 != null || Characters.Sein != null);
            }
        }
        static bool TeammateInSession()
        {
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++) if (seat != Host.LocalSeat && Host.SeatInSession(seat)) return true;
            return false;
        }
        static GameObject s_watchdog;
        static void EnsureWatchdog()
        {
            if (s_watchdog != null) return;
            s_watchdog = new GameObject("OriCoop_ReviveWatchdog");
            UnityEngine.Object.DontDestroyOnLoad(s_watchdog);
            s_watchdog.hideFlags = HideFlags.HideAndDontSave;
            s_watchdog.AddComponent<Watchdog>();
        }

        static void KeepHidden(Downed d)
        {
            if (d.Sein == null) return;
            foreach (Renderer r in d.Sein.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; if (!d.Hidden.Contains(r)) d.Hidden.Add(r); }
        }

        static void SendProgress(Downed d)
        {
            if (!Players.IsLocal(d.Rescuer) || !ReviveLogic.ShouldSend(d, d.Rescuer, Host.PuppetCount > 1, Time.unscaledTime)) return;
            float p = d.Progress;
            Services.Net.Send(MsgProgress, false, w => ReviveWire.WriteProgress(w, d.Player, d.Rescuer, p, d.Given));
        }

        // Our revive of the partner reached 100%: give the health over the network; their game applies it.
        static void CompleteRemote(Downed d, SeinCharacter saver, float health)
        {
            s_down[d.Player] = null;
            RestoreDowned(d, false);
            // The partner's game puts its Ori next to us (RestoreDowned there); the glow plays at that spot here.
            SeinCharacter me = saver;
            if (me != null) PartnerFx.PlayRevive(d.Sein, me.Position + new Vector3(me.FaceLeft ? 1f : -1f, 0f, 0f));
            Services.Net.Send(MsgRevive, true, w => { ReviveWire.WriteRevive(w, d.Player, health); w.Write((byte)PlayerOf(saver)); });
            NetRevivesSent++; Revives++;
            Services.Log.Info("online: revived the partner, giving " + health.ToString("F1") + " health");
        }

        // ---- messages ---------------------------------------------------------------------------------------------
        static void OnNetDowned(BinaryReader r)
        {
            ReviveWire.Position message = ReviveWire.ReadPosition(r);
            int seat = message.Seat;
            Vector3 m = new Vector3(message.X, message.Y, message.Z);
            if (!Online || NetPlay.DeviceSeat(seat) != Services.Net.Sender || Players.IsLocal(seat)) return;
            NetDownedReceived++;
            if (s_down[seat] == null) AddRemoteDowned(seat, m);
            HostWipeIfAllDown();
        }

        static void AddRemoteDowned(int seat, Vector3 marker)
        {
            Downed d = new Downed();
            d.Sein = SeinAt(seat); d.Player = seat; d.Marker = marker; d.Remote = true;
            s_roster.TryAdd(seat, d);
            KeepHidden(d);
            EnsureWatchdog();
            Services.Log.Info("online: partner downed at " + marker + " (hold Spirit Flame near the skull to revive)");
        }

        // A message changed who is down. The host's decision is TickWipe's (every frame, after the grace); only the
        // planted bug still decides here, the moment the message arrives.
        static void HostWipeIfAllDown()
        {
            if (!s_mutateWipeNoGrace || !Online || !Services.Net.IsHost || !AnyDowned || !AllDown(null)) return;
            DeclareWipe();
        }

        // Planted bug only (ORICOOP_MUTATE_WIPE_NO_GRACE): the host's own death with everyone else down, as before T-215.
        static void DeclareWipeNow(SeinCharacter dying)
        {
            FinalDeaths++; NetBothDown++;
            Services.Log.Info("online: both Oris are down (host decides): checkpoint reload");
            Services.Net.Send(MsgBothDown, true, w => { });
            ReleaseRemoteDowned();
            if (dying != Host.P1) { MakeDowned(dying); KillP1Normally(); }
        }

        // T-166: a PC that was loading (or rejoined) had no puppets meanwhile, so it ignored the downed messages of that
        // time (OnNetDowned needs Online). Whenever this PC sees a teammate's Ori appear again it tells everyone the state
        // of its own seats: down (with the skull) or up. Receivers apply it even before their own puppets are back:
        // a missed down gets its skull, a skull of someone revived meanwhile goes.
        static int s_seenRemote;
        public static int NetDownedStatesSent, NetDownedStatesApplied;

        static void SendDownedState()
        {
            int[] local = Players.LocalSeats;
            Services.Net.Send(MsgDownedState, true, w =>
            {
                w.Write((byte)local.Length);
                foreach (int seat in local)
                {
                    Downed d = s_down[seat];
                    bool down = d != null && !d.Remote;
                    w.Write((byte)seat); w.Write(down);
                    if (down) { w.Write(d.Marker.x); w.Write(d.Marker.y); w.Write(d.Marker.z); }
                }
            });
            NetDownedStatesSent++;
        }

        static void OnNetDownedState(BinaryReader r)
        {
            if (!Enabled || !Services.Net.Enabled || !Services.Net.Connected) return;
            int count = r.ReadByte();
            if (count > ReviveLogic.MaxSeats) return;
            for (int i = 0; i < count; i++)
            {
                int seat = r.ReadByte();
                bool down = r.ReadBoolean();
                Vector3 m = down ? new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : Vector3.zero;
                if (seat < 1 || seat > ReviveLogic.MaxSeats || NetPlay.DeviceSeat(seat) != Services.Net.Sender || Players.IsLocal(seat)) continue;
                Downed d = s_down[seat];
                if (down && d == null) { AddRemoteDowned(seat, m); NetDownedStatesApplied++; }
                else if (!down && d != null && d.Remote)
                {
                    s_down[seat] = null; RestoreDowned(d, false); NetDownedStatesApplied++;
                    Services.Log.Info("online: P" + seat + " is up again (missed its revive while loading)");
                }
            }
            HostWipeIfAllDown();
        }

        // Both down: every partner's downed state ends with the checkpoint reload. Clear it at once (host and guest
        // alike) so no skull stays on screen while this PC's death and reload play (T-026).
        static void ReleaseRemoteDowned()
        {
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++)
                if (seat != Host.LocalSeat && s_down[seat] != null) { Downed d = s_down[seat]; s_down[seat] = null; RestoreUnrescued(d); }
        }

        static void OnNetRevive(BinaryReader r)
        {
            ReviveWire.Revive message = ReviveWire.ReadRevive(r);
            int seat = message.Seat;
            float health = message.Health;
            if (!Online || s_down[seat] == null) return;
            Downed d = s_down[seat];
            int rescuer = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : Services.Net.Sender;
            if (NetPlay.DeviceSeat(rescuer) != Services.Net.Sender) return;
            d.Rescuer = rescuer;
            if (!Players.IsLocal(seat)) return;
            ApplyRevive(d, seat, SeinAt(rescuer), health);
        }

        // This PC's downed Ori is revived by a teammate's PC. saver: the rescuer's Ori here, or null when it is not on
        // this PC any more (T-215 #1: the revive still completes, at the skull).
        static void ApplyRevive(Downed d, int seat, SeinCharacter saver, float health)
        {
            s_down[seat] = null;
            RestoreDowned(d, true, saver, health);
            if (d.Sein != null) PartnerFx.PlayRevive(d.Sein, d.Sein.Position);
            NetRevivesApplied++;
            SendRevived(seat, d.Sein.Position);
            Services.Log.Info("online: revived by the partner with " + health.ToString("F1") + " health");
        }

        // Test hooks (NetReviveTest R7). A teammate's "revived you" arrives after its Ori left this PC (#1); the host
        // tells the room's save point again, as when a PC joins (#7).
        internal static bool TestReviveWithoutRescuer(int seat, float health)
        {
            Downed d = seat >= 1 && seat <= ReviveLogic.MaxSeats ? s_down[seat] : null;
            if (d == null || d.Remote || d.Sein == null) return false;
            ApplyRevive(d, seat, null, health);
            return true;
        }
        internal static void TestResendRoomLink() { SendRoomLink(true); }

        static void OnNetRevived(BinaryReader r)
        {
            ReviveWire.Position message = ReviveWire.ReadPosition(r);
            int seat = message.Seat;
            Vector3 position = new Vector3(message.X, message.Y, message.Z);
            if (NetPlay.DeviceSeat(seat) != Services.Net.Sender || Players.IsLocal(seat)) return;
            Downed d = s_roster.TakeRevived(seat, seat);
            if (d == null) return;
            RestoreDowned(d, false);
            if (d.Sein != null)
            {
                Host.Teleport(d.Sein, position);
                PartnerFx.PlayRevive(d.Sein, position);
            }
        }

        static void SendRevived(int seat, Vector3 position)
        {
            Services.Net.Send(MsgRevived, true, w => ReviveWire.WritePosition(w, seat, position.x, position.y, position.z));
        }

        static void OnNetBothDown(BinaryReader r)
        {
            if (!Services.Net.Enabled || Services.Net.Sender != Services.Net.HostSeat) return;
            FinalDeaths++; NetBothDown++;
            Services.Log.Info("online: host says both Oris are down: checkpoint reload");
            ReleaseRemoteDowned();
            KillP1Normally();
        }

        static void OnNetProgress(BinaryReader r)
        {
            ReviveWire.Progress message = ReviveWire.ReadProgress(r);
            if (NetPlay.DeviceSeat(message.Rescuer) != Services.Net.Sender) return;
            s_roster.MergeProgress(message.Seat, message.Rescuer, message.Rescuer, Host.LocalSeat, message.Value, message.Given);
        }
    }
}
