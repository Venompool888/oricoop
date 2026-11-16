using System;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Local co-op downed / revive (the owner's design, 2026-11-14):
    // - An Ori that dies while its partner is alive is "downed" instead: a skull marker stays at its last solid ground.
    // - The partner stands near the skull and holds the Spirit Flame button (the game's structure-interaction button:
    //   spirit wells, doors, map stones). Progress 0-100% over 1.5 s of holding; releasing pauses it, progress is kept.
    // - While holding, the saver's health drains at (health when the revive started) / 2 / 1.5 s; what was drained is
    //   the revived Ori's health (at least 1). Any accepted damage to the saver interrupts the hold (drained health and
    //   progress are kept; press again to continue).
    // - The revived Ori appears next to the saver, briefly invincible.
    // - If both are down, the game's normal death runs once (checkpoint reload; game over on One Life) and both come back.
    // Online mode: same rules over the network, see ReviveNet.cs (each PC decides only for its own Ori).
    public static partial class Revive
    {
        public static bool Enabled = true;
        public const float HoldSeconds = 1.5f;
        public const float Radius = 2.5f;

        public class Downed
        {
            public SeinCharacter Sein;
            public int Player;                 // seat
            public Vector3 Marker;
            public float Progress;             // 0..1
            public float DrainRate;            // health per second, fixed when progress starts from 0
            public float Given;                // health drained from the saver so far
            public bool NeedRelease;           // interrupted by damage: release and press again
            public bool Holding;
            public bool Remote;                // online: the partner's Ori, downed in its owner's game
            public List<Renderer> Hidden = new List<Renderer>();
        }

        static readonly Downed[] s_down = new Downed[Players.MaxSeats + 1];
        // TODO(4p): retain slot-based safe-ground history across host/guest role changes in phase 0.
        static Vector3 s_safe1, s_safe2;
        static bool s_haveSafe1, s_haveSafe2;
        static float s_safeTimer;
        static bool s_passThrough;            // our own final-death call must reach the game
        static bool s_subscribed;
        public static int Revives, FinalDeaths, Interrupts;

        public static bool Local { get { return Enabled && !NetPlay.Enabled && P2Context.P1 != null && P2Context.P2 != null; } }
        public static bool AnyDowned { get { foreach (Downed d in s_down) if (d != null) return true; return false; } }
        // TODO(4p): phase 1 removes slot-based query; existing callers use local/puppet slots.
        public static Downed DownedOf(int player) { return s_down[Roles.OfSlot(player)]; }
        internal static Downed DownedAtSeat(int seat) { return s_down[seat]; }
        internal static SeinCharacter SeinAt(int seat)
        {
            if (seat == Players.LocalSeat) return P2Context.P1;
            foreach (Puppet p in Players.Remotes) if (p.Seat == seat) return p.Sein;
            return null;
        }
        internal static IEnumerable<int> PresentSeats
        {
            get
            {
                for (int seat = 1; seat <= Players.MaxSeats; seat++) if (SeinAt(seat) != null) yield return seat;
            }
        }
        public static bool IsDowned(SeinCharacter s)
        {
            if (s == null) return false;
            foreach (Downed d in s_down) if (d != null && d.Sein == s) return true;
            return false;
        }
        static int PlayerOf(SeinCharacter s) { return Players.SeatOf(s); }
        internal static SeinCharacter Partner(SeinCharacter s)
        {
            SeinCharacter first = null;
            foreach (int seat in PresentSeats)
            {
                SeinCharacter other = SeinAt(seat);
                if (other == s) continue;
                if (Alive(other)) return other;
                if (first == null) first = other;
            }
            return first;
        }
        static bool AllDown(SeinCharacter dying)
        {
            foreach (int seat in PresentSeats) if (SeinAt(seat) != dying && s_down[seat] == null) return false;
            return true;
        }
        static bool Alive(SeinCharacter s) { return s != null && s.gameObject.activeInHierarchy && !IsDowned(s) && s.Mortality.Health.Amount > 0f; }

        // The Ori the camera, enemies and screen rules should use while one is down (null = both up).
        public static SeinCharacter OnlyAlive
        {
            get
            {
                if (!AnyDowned) return null;
                SeinCharacter only = null;
                foreach (int seat in PresentSeats)
                {
                    if (s_down[seat] != null) continue;
                    if (only != null) return null;
                    only = SeinAt(seat);
                }
                return only;
            }
        }

        // ---- death ---------------------------------------------------------------------------------------------
        // Called from the OnKill prefix. true = the death was turned into "downed" (skip the game's death).
        public static bool HandleDeath(SeinCharacter who)
        {
            if (s_passThrough || !Local || who == null) return false;
            Subscribe();
            if (IsDowned(who)) return true;
            SeinCharacter other = Partner(who);
            if (!AllDown(who) && Alive(other) && !CoopMenu.IsOpen)
            {
                MakeDowned(who);
                return true;
            }
            // Partner already down: both are out -> the game's normal death, once.
            FinalDeaths++;
            Plugin.Log.LogInfo("both Oris are down: normal death / checkpoint reload");
            if (who == P2Context.P1) return false; // P1's own death runs as the game does
            // P2 went down last while P1 is downed: run P1's death as the game would.
            MakeDowned(who);
            KillP1Normally();
            return true;
        }

        // P1 (downed or not) dies the way the game does: un-hide it, then let its own OnKill run (death, checkpoint
        // reload, or game over on One Life).
        static void KillP1Normally()
        {
            SeinCharacter p1 = P2Context.P1;
            int seat = Players.LocalSeat;
            if (s_down[seat] != null) { RestoreDowned(s_down[seat], false); s_down[seat] = null; }
            if (p1 == null || !p1.gameObject.activeInHierarchy) return;
            s_passThrough = true;
            try { p1.Mortality.DamageReciever.OnKill(new Damage(1000f, Vector2.zero, p1.Position, DamageType.Spikes, null)); }
            catch (Exception e) { Plugin.Log.LogError(e); }
            finally { s_passThrough = false; }
        }

        static void MakeDowned(SeinCharacter s)
        {
            int player = PlayerOf(s);
            Downed d = new Downed();
            d.Sein = s; d.Player = player;
            bool local = s == P2Context.P1;
            bool haveSafe = local ? s_haveSafe1 : s_haveSafe2;
            d.Marker = haveSafe ? (local ? s_safe1 : s_safe2) : s.Position;
            s.Mortality.Health.SetAmount(0f);
            if (local)
            {
                // P1 can't be switched off (the game is built around Characters.Sein existing): freeze it with the
                // game's own suspension, hide it, drop its collisions, pin it at the marker. OnRecieveDamage is blocked.
                foreach (Renderer r in s.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; d.Hidden.Add(r); }
                if (s.PlatformBehaviour != null && s.PlatformBehaviour.CapsuleController != null) s.PlatformBehaviour.CapsuleController.EnableCollider(false);
                SetSuspended(s, true);
                s.Position = d.Marker; s.Speed = Vector3.zero;
            }
            else
            {
                P2Lifecycle.DropCarried(s);
                s.gameObject.SetActive(false);
                P2Extras.SetCompanionActive(false);
            }
            s_down[player] = d;
            EnsureWatchdog();
            Plugin.Log.LogInfo("P" + player + " downed at " + d.Marker + " (hold Spirit Flame near the skull to revive)");
        }

        static void SetSuspended(SeinCharacter s, bool on)
        {
            foreach (MonoBehaviour mb in s.GetComponentsInChildren<MonoBehaviour>(true))
            {
                ISuspendable su = mb as ISuspendable;
                if (su != null && su.IsSuspended != on) su.IsSuspended = on;
            }
        }

        // Bring a downed Ori back. place = true: next to the saver with `health`.
        static void RestoreDowned(Downed d, bool place, SeinCharacter saver = null, float health = 0f)
        {
            if (d == null || d.Sein == null) return;
            SeinCharacter s = d.Sein;
            if (!d.Remote && d.Player == Players.LocalSeat)
            {
                foreach (Renderer r in d.Hidden) if (r != null) r.enabled = true;
                if (s.PlatformBehaviour != null && s.PlatformBehaviour.CapsuleController != null) s.PlatformBehaviour.CapsuleController.EnableCollider(true);
                SetSuspended(s, false);
            }
            else if (d.Remote)
            {
                // Online partner (network puppet): it was only hidden.
                foreach (Renderer r in d.Hidden) if (r != null) r.enabled = true;
            }
            else
            {
                s.gameObject.SetActive(true);
                P2Extras.SetCompanionActive(true);
            }
            if (place && saver != null)
            {
                Plugin.Teleport(s, saver.Position + new Vector3(saver.FaceLeft ? 1f : -1f, 0f, 0f));
                s.Mortality.Health.SetAmount(Mathf.Clamp(health, 1f, s.Mortality.Health.MaxHealth));
                s.Mortality.DamageReciever.MakeInvincible(1.5f);
                if (Players.OfSein(s) != null) P2Extras.ReturnCompanion("revived");
            }
        }

        // ---- every frame (local co-op only) ---------------------------------------------------------------------
        public static void Tick()
        {
            if (!Local) { if (AnyDowned) ClearAll("left local co-op", P2Context.P1 != null); return; }
            Subscribe();
            TrackSafeGround();
            if (s_down[Players.LocalSeat] != null) KeepPinned(s_down[Players.LocalSeat]);
            foreach (int seat in PresentSeats) Step(s_down[seat]);
        }

        static void TrackSafeGround()
        {
            s_safeTimer -= Time.deltaTime;
            if (s_safeTimer > 0f) return;
            s_safeTimer = 0.2f;
            SeinCharacter p1 = P2Context.P1, p2 = P2Context.P2;
            if (Alive(p1) && Grounded(p1)) { s_safe1 = p1.Position; s_haveSafe1 = true; }
            if (Alive(p2) && Grounded(p2)) { s_safe2 = p2.Position; s_haveSafe2 = true; }
        }

        static bool Grounded(SeinCharacter s)
        {
            try { return s.PlatformBehaviour.PlatformMovement.IsOnGround; } catch (Exception) { return false; }
        }

        static void KeepPinned(Downed d)
        {
            SeinCharacter s = d.Sein;
            if (s == null) return;
            SetSuspended(s, true);   // the game's resume-after-pause un-suspends everything; keep P1 frozen
            s.Position = d.Marker; s.Speed = Vector3.zero;
            foreach (Renderer r in s.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; if (!d.Hidden.Contains(r)) d.Hidden.Add(r); }
        }

        public static bool SaverHolding(SeinCharacter saver)
        {
            Puppet p = Players.OfSein(saver);
            if (p != null) return p.Input.SpiritFlame.IsPressed;
            return Core.Input.SpiritFlame.IsPressed; // outside P2 context Core.Input is P1's
        }

        public static bool SaverNear(SeinCharacter saver, Downed d)
        {
            if (saver == null || d == null) return false;
            Vector3 v = saver.Position - d.Marker;
            return Mathf.Abs(v.x) < Radius && Mathf.Abs(v.y) < Radius;
        }

        static void Step(Downed d)
        {
            if (d == null) return;
            SeinCharacter saver = Partner(d.Sein);
            d.Holding = false;
            // No reviving through a menu: online a menu no longer freezes the world, so check the saver's own menus too.
            bool saverInMenu = CoopMenu.IsOpen || (UI.MainMenuVisible && (saver == P2Context.P1 || !NetPlay.Enabled));
            if (!Alive(saver) || GameController.Instance == null || GameController.Instance.GameplaySuspended || saverInMenu) return;
            bool holding = SaverNear(saver, d) && SaverHolding(saver);
            if (!holding) { d.NeedRelease = false; return; }
            if (d.NeedRelease) return;
            d.Holding = true;
            float dt = Time.deltaTime;
            if (d.Progress <= 0f) d.DrainRate = saver.Mortality.Health.Amount / 2f / HoldSeconds;
            float drain = Mathf.Min(d.DrainRate * dt, Mathf.Max(0f, saver.Mortality.Health.Amount - 0.25f));
            saver.Mortality.Health.SetAmount(saver.Mortality.Health.Amount - drain);
            d.Given += drain;
            d.Progress = Mathf.Min(1f, d.Progress + dt / HoldSeconds);
            if (d.Progress >= 1f) Complete(d, saver);
        }

        static void Complete(Downed d, SeinCharacter saver)
        {
            float health = Mathf.Max(1f, d.Given);
            if (d.Remote) { CompleteRemote(d, health); return; } // online: the partner's own game applies it
            s_down[d.Player] = null;
            RestoreDowned(d, true, saver, health);
            PartnerFx.PlayRevive(d.Sein, d.Sein.Position);
            Revives++;
            Plugin.Log.LogInfo("P" + d.Player + " revived by P" + PlayerOf(saver) + " with " + health.ToString("F1") + " health");
        }

        // Accepted damage to a saver interrupts its revive hold.
        public static void OnHealthDamaged(SeinHealthController h)
        {
            foreach (Downed d in s_down)
            {
                if (d == null) continue;
                SeinCharacter saver = Partner(d.Sein);
                if (saver != null && saver.Mortality.Health == h && d.Holding)
                {
                    d.NeedRelease = true; d.Holding = false; Interrupts++;
                    Plugin.Log.LogInfo("revive of P" + d.Player + " interrupted (saver took damage) at " + (d.Progress * 100f).ToString("F0") + "%");
                }
            }
        }

        // ---- checkpoint reload / leaving co-op -----------------------------------------------------------------
        static void Subscribe()
        {
            if (s_subscribed) return;
            s_subscribed = true;
            Game.Checkpoint.Events.OnPostRestore.Add(OnPostRestore);
        }

        static void OnPostRestore()
        {
            if (!AnyDowned) return;
            SeinCharacter p1 = P2Context.P1;
            Downed[] down = (Downed[])s_down.Clone();
            Array.Clear(s_down, 0, s_down.Length);
            RestoreDowned(down[Players.LocalSeat], false);
            for (int seat = 1; seat <= Players.MaxSeats; seat++)
            {
                if (seat == Players.LocalSeat) continue;
                Downed d = down[seat];
                RestoreDowned(d, false);
                if (d != null && seat != Players.LocalSeat && !d.Remote && d.Sein != null && p1 != null)
                {
                    d.Sein.Mortality.Health.RestoreAllHealth();
                    Plugin.Teleport(d.Sein, p1.Position + new Vector3(1f, 0f, 0f));
                    P2Extras.ReturnCompanion("checkpoint reload");
                }
            }
            Plugin.Log.LogInfo("checkpoint reload: both Oris back");
        }

        // P2 leaves (F10, title screen...) or online mode starts while someone is down.
        public static void ClearAll(string why, bool killDownedP1 = true)
        {
            if (!AnyDowned) return;
            Downed[] down = (Downed[])s_down.Clone();
            Array.Clear(s_down, 0, s_down.Length);
            Downed d1 = down[Players.LocalSeat];
            for (int seat = 1; seat <= Players.MaxSeats; seat++)
            {
                Downed d = down[seat];
                if (seat != Players.LocalSeat && d != null && d.Sein != null) RestoreDowned(d, false);
            }
            if (d1 != null && d1.Sein != null)
            {
                // P1 was down with nobody left to revive it: it dies as in single player.
                RestoreDowned(d1, false);
                if (killDownedP1 && d1.Sein.gameObject.activeInHierarchy)
                {
                    s_passThrough = true;
                    try { d1.Sein.Mortality.DamageReciever.OnKill(new Damage(1000f, Vector2.zero, d1.Sein.Position, DamageType.Spikes, null)); }
                    catch (Exception e) { Plugin.Log.LogError(e); }
                    finally { s_passThrough = false; }
                }
            }
            Plugin.Log.LogInfo("downed state cleared: " + why);
        }

        // ---- patches --------------------------------------------------------------------------------------------
        [HarmonyPatch(typeof(SeinDamageReciever), "OnRecieveDamage")]
        static class DownedImmune
        {
            static bool Prefix(SeinDamageReciever __instance)
            {
                if (!AnyDowned) return true;
                SeinCharacter s = __instance.GetComponentInParent<SeinCharacter>();
                return !IsDowned(s);
            }
        }

        [HarmonyPatch(typeof(SeinHealthController), "TakeDamage")]
        static class SaverHit
        {
            static void Postfix(SeinHealthController __instance) { if (AnyDowned) OnHealthDamaged(__instance); }
        }

        // Near a partner's skull, the Spirit Flame button revives instead of attacking.
        [HarmonyPatch(typeof(SeinStandardSpiritFlameAbility), "UpdateCharacterState")]
        static class NoFlameWhileReviving
        {
            static bool Prefix()
            {
                if (!AnyDowned) return true;
                SeinCharacter me = Characters.Sein; // P2 inside P2 context
                if (me == null || IsDowned(me)) return true;
                foreach (Downed d in s_down)
                    if (d != null && d.Sein != me && SaverNear(me, d)) return false;
                return true;
            }
        }

        // A checkpoint created while P2 triggers it (soul link, spirit well, checkpoint zone) saves P2's spot: the game
        // only records P1's registered state, so P1 stands at P2's position for the snapshot.
        static Vector3 s_p1Before; static bool s_moved;
        [HarmonyPatch(typeof(GameController), "CreateCheckpoint")]
        static class CheckpointWhereP2Is
        {
            static void Prefix()
            {
                s_moved = false;
                if (!Local || !P2Context.Inside || P2Context.P1 == null || P2Context.P2 == null || IsDowned(P2Context.P1)) return;
                s_p1Before = P2Context.P1.Position;
                P2Context.P1.Position = P2Context.P2.Position;
                s_moved = true;
                CheckpointsByP2++;
            }
            static void Finalizer()
            {
                if (s_moved && P2Context.P1 != null) P2Context.P1.Position = s_p1Before;
                s_moved = false;
            }
        }
        public static int CheckpointsByP2;
    }
}
