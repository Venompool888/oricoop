using System;
using System.IO;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Online downed / revive: the same rules as local co-op, split between the two PCs.
    // On each PC the local player is P1 and the partner's Ori is P2 (a network puppet). Each PC decides only for its
    // own Ori and its own health; messages carry the rest (types 60-69 are reserved for this):
    //   60 Downed    owner -> partner   "my Ori is down, skull at (x,y,z)"          reliable
    //   61 Revive    saver -> owner     "revived you, giving N health"              reliable
    //   62 Revived   owner -> saver     "I'm back" (clears the skull there too)     reliable
    //   63 BothDown  host  -> guest     "we're both down: reload"                   reliable
    //   64 Progress  saver -> owner     revive progress 0..1, for the owner's skull   unreliable
    // Both down is decided by the host only, so simultaneous deaths can't trigger two different reloads. The host
    // runs the normal death/checkpoint reload; the guest runs its own normal death too, and SaveSync brings its
    // world back in line with the host's save afterwards.
    public static partial class Revive
    {
        public const byte MsgDowned = 60, MsgRevive = 61, MsgRevived = 62, MsgBothDown = 63, MsgProgress = 64;
        static bool s_netRegistered;
        static float s_progressSendTimer;
        public static int NetDownedSent, NetDownedReceived, NetRevivesSent, NetRevivesApplied, NetBothDown;

        public static bool Online
        {
            get { return Enabled && NetPlay.Enabled && NetPlay.Connected && P2Context.P1 != null && P2Context.P2 != null; }
        }

        static void RegisterNet()
        {
            PartnerFx.EnsureRegistered();
            if (s_netRegistered) return;
            s_netRegistered = true;
            NetPlay.On(MsgDowned, OnNetDowned);
            NetPlay.On(MsgRevive, OnNetRevive);
            NetPlay.On(MsgRevived, OnNetRevived);
            NetPlay.On(MsgBothDown, OnNetBothDown);
            NetPlay.On(MsgProgress, OnNetProgress);
        }

        // Called from the OnKill prefix for the LOCAL Ori online. true = turned into downed (skip the game's death).
        public static bool HandleDeathOnline(SeinCharacter p1)
        {
            if (s_passThrough || !Online || p1 == null || p1 != P2Context.P1) return false;
            RegisterNet(); Subscribe();
            if (IsDowned(p1)) return true;
            bool partnerDown = AllDown(p1);
            if (partnerDown && NetPlay.IsHost)
            {
                // The guest is already down and now the host: both out. Tell the guest, then die normally.
                FinalDeaths++; NetBothDown++;
                Plugin.Log.LogInfo("online: both Oris are down (host decides): checkpoint reload");
                NetPlay.Send(MsgBothDown, true, w => { });
                return false;
            }
            // Down; the partner can revive us. (Guest with the host already down: the host decides on our message.)
            MakeDowned(p1);
            int seat = Players.LocalSeat;
            Vector3 m = s_down[seat].Marker;
            NetPlay.Send(MsgDowned, true, w => { w.Write((byte)seat); w.Write(m.x); w.Write(m.y); w.Write(m.z); });
            NetDownedSent++;
            return true;
        }

        // Every frame while online (from Plugin's online path).
        public static void TickOnline()
        {
            if (NetPlay.Enabled) PartnerFx.EnsureRegistered(); // partner hurt visuals also when revive is off
            if (!Online) { if (AnyDowned && NetPlay.Enabled && !NetPlay.Connected) ClearAll("partner disconnected", P2Context.P1 != null); return; }
            RegisterNet(); Subscribe();
            TrackSafeGround();
            if (s_down[Players.LocalSeat] != null) KeepPinned(s_down[Players.LocalSeat]);
            foreach (int seat in PresentSeats)
            {
                Downed d = s_down[seat];
                if (seat != Players.LocalSeat && d != null) { KeepHidden(d); Step(d); if (s_down[seat] == d) SendProgress(d); } // Step may complete the revive (kit ad8f4bc crashed here)
            }
        }

        // Watchdog that does not depend on Plugin's per-frame paths (those stop once the partner's Ori is gone):
        // our Ori is down and nobody can revive it any more (partner left / disconnected / P2 removed) -> it dies
        // the normal way, as in single player. A downed partner's skull is simply cleared.
        class Watchdog : MonoBehaviour
        {
            void Update()
            {
                if (!AnyDowned) return;
                bool partnerGone = P2Context.P2 == null || (NetPlay.Enabled && !NetPlay.Connected);
                if (partnerGone) ClearAll("partner gone while someone was downed", P2Context.P1 != null || Characters.Sein != null);
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
            if (d.Progress <= 0f) return;
            s_progressSendTimer -= Time.unscaledDeltaTime;
            if (s_progressSendTimer > 0f) return;
            s_progressSendTimer = 0.1f;
            float p = d.Progress;
            NetPlay.Send(MsgProgress, false, w => w.Write(p));
        }

        // Our revive of the partner reached 100%: give the health over the network; their game applies it.
        static void CompleteRemote(Downed d, float health)
        {
            s_down[d.Player] = null;
            RestoreDowned(d, false);
            // The partner's game puts its Ori next to us (RestoreDowned there); the glow plays at that spot here.
            SeinCharacter me = P2Context.P1;
            if (me != null) PartnerFx.PlayRevive(d.Sein, me.Position + new Vector3(me.FaceLeft ? 1f : -1f, 0f, 0f));
            NetPlay.Send(MsgRevive, true, w => { w.Write((byte)d.Player); w.Write(health); });
            NetRevivesSent++; Revives++;
            Plugin.Log.LogInfo("online: revived the partner, giving " + health.ToString("F1") + " health");
        }

        // ---- messages ---------------------------------------------------------------------------------------------
        static void OnNetDowned(BinaryReader r)
        {
            int seat = r.ReadByte();
            Vector3 m = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!Online) return;
            NetDownedReceived++;
            if (s_down[seat] == null)
            {
                Downed d = new Downed();
                d.Sein = SeinAt(seat); d.Player = seat; d.Marker = m; d.Remote = true;
                s_down[seat] = d;
                KeepHidden(d);
                EnsureWatchdog();
                Plugin.Log.LogInfo("online: partner downed at " + m + " (hold Spirit Flame near the skull to revive)");
            }
            if (NetPlay.IsHost && AllDown(null))
            {
                // We (host) were already down and now the guest: both out.
                FinalDeaths++; NetBothDown++;
                Plugin.Log.LogInfo("online: both Oris are down (host decides): checkpoint reload");
                NetPlay.Send(MsgBothDown, true, w => { });
                KillP1Normally();
            }
        }

        static void OnNetRevive(BinaryReader r)
        {
            int seat = r.ReadByte();
            float health = r.ReadSingle();
            if (!Online || s_down[seat] == null) return;
            Downed d = s_down[seat];
            s_down[seat] = null;
            RestoreDowned(d, true, SeinAt(NetPlay.Sender), health);
            if (d.Sein != null) PartnerFx.PlayRevive(d.Sein, d.Sein.Position);
            NetRevivesApplied++;
            NetPlay.Send(MsgRevived, true, w => w.Write((byte)seat));
            Plugin.Log.LogInfo("online: revived by the partner with " + health.ToString("F1") + " health");
        }

        static void OnNetRevived(BinaryReader r)
        {
            int seat = r.ReadByte();
            if (s_down[seat] == null) return;
            Downed d = s_down[seat]; s_down[seat] = null;
            RestoreDowned(d, false);
        }

        static void OnNetBothDown(BinaryReader r)
        {
            if (!NetPlay.Enabled) return;
            FinalDeaths++; NetBothDown++;
            Plugin.Log.LogInfo("online: host says both Oris are down: checkpoint reload");
            foreach (int seat in PresentSeats)
                if (seat != Players.LocalSeat && s_down[seat] != null) { RestoreDowned(s_down[seat], false); s_down[seat] = null; }
            KillP1Normally();
        }

        static void OnNetProgress(BinaryReader r)
        {
            float p = r.ReadSingle();
            Downed d = s_down[Players.LocalSeat];
            if (d != null) d.Progress = Mathf.Max(d.Progress, p);
        }
    }
}
