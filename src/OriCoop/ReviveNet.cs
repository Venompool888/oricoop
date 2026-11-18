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
    // Both down is decided by the host only, so simultaneous deaths can't trigger two different reloads. The host
    // runs the normal death/checkpoint reload; the guest runs its own normal death too, and SaveSync brings its
    // world back in line with the host's save afterwards.
    public static partial class Revive
    {
        public const byte MsgDowned = 60, MsgRevive = 61, MsgRevived = 62, MsgBothDown = 63, MsgProgress = 64;
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
            Services.Net.PlayerLeft += ClearSeat;
        }

        static void ClearSeat(int seat)
        {
            Downed d = s_roster.Take(seat);
            RestoreDowned(d, false);
            s_haveSafe[seat] = false;
            if (Online && Services.Net.IsHost && AnyDowned && AllDown(null))
            {
                FinalDeaths++; NetBothDown++;
                Services.Net.Send(MsgBothDown, true, w => { });
                ReleaseRemoteDowned();
                KillP1Normally();
            }
        }

        // Called from the OnKill prefix for the LOCAL Ori online. true = turned into downed (skip the game's death).
        public static bool HandleDeathOnline(SeinCharacter p1)
        {
            if (s_passThrough || !Online || p1 == null || p1 != Host.P1) return false;
            RegisterNet(); Subscribe();
            if (IsDowned(p1)) return true;
            bool partnerDown = AllDown(p1);
            if (ReviveLogic.DecideOnlineDeath(partnerDown, partnerDown && Services.Net.IsHost) == DeathOutcome.Checkpoint)
            {
                // The guest is already down and now the host: both out. Tell the guest, then die normally.
                FinalDeaths++; NetBothDown++;
                Services.Log.Info("online: both Oris are down (host decides): checkpoint reload");
                Services.Net.Send(MsgBothDown, true, w => { });
                ReleaseRemoteDowned(); // T-026: the partners' skulls go now, not when their puppets later vanish
                return false;
            }
            // Down; the partner can revive us. (Guest with the host already down: the host decides on our message.)
            MakeDowned(p1);
            int seat = Host.LocalSeat;
            Vector3 m = s_down[seat].Marker;
            Services.Net.Send(MsgDowned, true, w => ReviveWire.WritePosition(w, seat, m.x, m.y, m.z));
            NetDownedSent++;
            return true;
        }

        // Every frame while online (from Plugin's online path).
        public static void TickOnline()
        {
            if (Services.Net.Enabled) PartnerFx.EnsureRegistered(); // partner hurt visuals also when revive is off
            if (!Online)
            {
                ResetSpectate();
                if (AnyDowned && Services.Net.Enabled && !Services.Net.Connected) ClearAll("partner disconnected", Host.P1 != null);
                return;
            }
            RegisterNet(); Subscribe();
            TrackSafeGround();
            Downed own = s_down[Host.LocalSeat];
            if (own != null)
            {
                KeepPinned(own);
                if (Host.RemoteCount > 1 && !GameController.Instance.GameplaySuspended && !SessionRules.LocalPaused && !SessionRules.PausedByPartner)
                {
                    SeinCharacter teammate = ReviveLogic.RespawnDue(own, Time.deltaTime) ? NearestLiving(own.Marker, own.Sein) : null;
                    if (teammate != null)
                    {
                        s_down[own.Player] = null;
                        RestoreDowned(own, true, teammate, own.Sein.Mortality.Health.MaxHealth);
                        PartnerFx.PlayRevive(own.Sein, own.Sein.Position);
                        SendRevived(own.Player, own.Sein.Position);
                        Services.Log.Info("online: P" + own.Player + " respawned beside P" + PlayerOf(teammate));
                    }
                }
            }
            foreach (int seat in PresentSeats)
            {
                Downed d = s_down[seat];
                if (seat != Host.LocalSeat && d != null)
                {
                    if (d.Sein == null) d.Sein = SeinAt(seat);
                    KeepHidden(d); Step(d);
                    if (s_down[seat] == d) SendProgress(d);
                }
            }
            if (s_down[Host.LocalSeat] != null || SpectateActive)
            {
                s_spectate.Tick(new[] { Host.LocalSeat }, PresentSeats, DownedSeats());
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
                bool partnerGone = Host.PuppetCount == 0 || (Services.Net.Enabled && !Services.Net.Connected);
                if (partnerGone) ClearAll("partner gone while someone was downed", Host.P1 != null || Characters.Sein != null);
            }
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
            if (!ReviveLogic.ShouldSend(d, Host.LocalSeat, Host.RemoteCount > 1, Time.unscaledTime)) return;
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
            Services.Net.Send(MsgRevive, true, w => ReviveWire.WriteRevive(w, d.Player, health));
            NetRevivesSent++; Revives++;
            Services.Log.Info("online: revived the partner, giving " + health.ToString("F1") + " health");
        }

        // ---- messages ---------------------------------------------------------------------------------------------
        static void OnNetDowned(BinaryReader r)
        {
            ReviveWire.Position message = ReviveWire.ReadPosition(r);
            int seat = message.Seat;
            Vector3 m = new Vector3(message.X, message.Y, message.Z);
            if (!Online || seat != Services.Net.Sender) return;
            NetDownedReceived++;
            if (s_down[seat] == null)
            {
                Downed d = new Downed();
                d.Sein = SeinAt(seat); d.Player = seat; d.Marker = m; d.Remote = true;
                s_roster.TryAdd(seat, d);
                KeepHidden(d);
                EnsureWatchdog();
                Services.Log.Info("online: partner downed at " + m + " (hold Spirit Flame near the skull to revive)");
            }
            if (Services.Net.IsHost && AllDown(null))
            {
                // We (host) were already down and now the guest: both out.
                FinalDeaths++; NetBothDown++;
                Services.Log.Info("online: both Oris are down (host decides): checkpoint reload");
                Services.Net.Send(MsgBothDown, true, w => { });
                ReleaseRemoteDowned();
                KillP1Normally();
            }
        }

        // Both down: every partner's downed state ends with the checkpoint reload. Clear it at once (host and guest
        // alike) so no skull stays on screen while this PC's death and reload play (T-026).
        static void ReleaseRemoteDowned()
        {
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++)
                if (seat != Host.LocalSeat && s_down[seat] != null) { Downed d = s_down[seat]; s_down[seat] = null; RestoreDowned(d, false); }
        }

        static void OnNetRevive(BinaryReader r)
        {
            ReviveWire.Revive message = ReviveWire.ReadRevive(r);
            int seat = message.Seat;
            float health = message.Health;
            if (!Online || s_down[seat] == null) return;
            Downed d = s_down[seat];
            d.Rescuer = Services.Net.Sender;
            if (seat != Host.LocalSeat) return;
            s_down[seat] = null;
            RestoreDowned(d, true, SeinAt(Services.Net.Sender), health);
            if (d.Sein != null) PartnerFx.PlayRevive(d.Sein, d.Sein.Position);
            NetRevivesApplied++;
            SendRevived(seat, d.Sein.Position);
            Services.Log.Info("online: revived by the partner with " + health.ToString("F1") + " health");
        }

        static void OnNetRevived(BinaryReader r)
        {
            ReviveWire.Position message = ReviveWire.ReadPosition(r);
            int seat = message.Seat;
            Vector3 position = new Vector3(message.X, message.Y, message.Z);
            Downed d = s_roster.TakeRevived(seat, Services.Net.Sender);
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
            if (!Services.Net.Enabled || Services.Net.Sender != 1) return;
            FinalDeaths++; NetBothDown++;
            Services.Log.Info("online: host says both Oris are down: checkpoint reload");
            ReleaseRemoteDowned();
            KillP1Normally();
        }

        static void OnNetProgress(BinaryReader r)
        {
            ReviveWire.Progress message = ReviveWire.ReadProgress(r);
            s_roster.MergeProgress(message.Seat, message.Rescuer, Services.Net.Sender, Host.LocalSeat, message.Value, message.Given);
        }
    }
}
