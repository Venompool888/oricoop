using System;
using System.IO;
using Game;
using HarmonyLib;
using UnityEngine;

using OriCoop.Seams;
using OriCoop.Rules;

namespace OriCoop
{
    // T-041 (#15, rules in docs\tickets\T-041.md), on top of T-043 room saves:
    // - The latest soul link is the room's latest save point: any player's soul link, spirit well or skill purchase
    //   that became the room's save (a guest's through its SaveOffer, adopted by the host). The host tells everyone
    //   where it is (msg 69 RoomLink); a guest tells the host where the save point it offers is (same message).
    // - A downed player holds Y (pad) / Q (keyboard) for 2 s to revive at the latest soul link, alone: the world does not
    //   reload and the others keep playing. Health: the creator's health right after saving if the link was made while
    //   this player was down, otherwise full. Energy is not touched. No skill rollback (that is only the team wipe's).
    // - Death save: a living player who makes a soul link while a teammate is down loses half its health (at least one
    //   cell is kept); energy is not touched. The cost is taken before the checkpoint, so the save keeps it.
    // - Everyone else sees "Pn made a new soul link" (PartnerFx, msg 80). There is no timed respawn any more.
    public static partial class Revive
    {
        public const byte MsgRoomLink = 69;   // docs/net-msg-ids.md
        const string PadY = "<icon>i</>", KeyQ = "<icon>I</>";   // the game's own button icons (ButtonIconUtility)

        // Planted bugs (docs/testing-rules.md): each one must make its net test fail.
        static readonly bool s_mutateNoDeathSave = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_DEATH_SAVE") == "1";
        static readonly bool s_mutateReviveInPlace = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SELF_REVIVE_IN_PLACE") == "1";
        static readonly bool s_mutateLinkHostOnly = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_LINK_HOST_ONLY") == "1";

        static bool s_haveLink;
        static Vector3 s_linkPos;
        static int s_linkSeat;
        static SeinCharacter s_caster;       // inside SeinSoulFlame.CastSoulFlame: the Ori making the soul link
        static bool s_charged;               // the death-save cost was taken for this cast
        // The latest checkpoint this PC made: who saved, where, with what health (after the death-save cost).
        static bool s_haveCheckpoint;
        static ReviveWire.Link s_checkpoint;
        // Host: where each guest device's offered save point is (msg 69 from that guest, just before its SaveOffer).
        static readonly ReviveWire.Link[] s_offered = new ReviveWire.Link[ReviveLogic.MaxSeats + 1];
        static readonly bool[] s_haveOffered = new bool[ReviveLogic.MaxSeats + 1];
        static readonly GameText[] s_selfHint = new GameText[ReviveLogic.MaxSeats + 1];
        static bool s_linkRegistered;

        public static int LinksNoted, RoomLinksSent, SelfRevives, DeathSaves;
        public static float LastDeathSaveBefore, LastDeathSaveAfter, LastSelfReviveHealth;
        public static string LastSelfHint;
        internal static bool TestHoldSelf;   // test hook: every downed Ori of this PC holds Y
        public static bool HaveLink { get { return s_haveLink; } }
        public static Vector3 LinkPosition { get { return s_linkPos; } }
        public static int LinkSeat { get { return s_linkSeat; } }

        // From Plugin start-up: the room link message, and the checkpoint restore that loading a game runs (the host's
        // first latest link) happens before any co-op tick.
        public static void Init()
        {
            RegisterLink();
            Subscribe();
        }

        static void RegisterLink()
        {
            if (s_linkRegistered) return;
            s_linkRegistered = true;
            Services.Net.On(MsgRoomLink, OnNetRoomLink);
            Services.Net.SessionEnded += ResetLinkState;
        }

        // ---- soul link cast: the death save ---------------------------------------------------------------------
        [HarmonyPatch(typeof(SeinSoulFlame), "CastSoulFlame")]
        static class SoulLinkCast
        {
            static void Prefix(SeinCharacter ___m_sein) { s_caster = ___m_sein; s_charged = false; }
            static void Finalizer() { s_caster = null; }
        }

        // This PC plays `s`: its own Ori or one of its extra local players (local co-op, extras on an online device).
        static bool PlayedHere(SeinCharacter s)
        {
            if (s == null) return false;
            if (s == Host.P1) return true;
            Puppet p = Players.OfSein(s);
            return p != null && (p.IsLocal || !Services.Net.Enabled);
        }

        [HarmonyPatch(typeof(GameController), "CreateCheckpoint")]
        static class CheckpointRecord
        {
            // The game's checkpoint is taken inside CastSoulFlame: take the death-save cost first so the save keeps it.
            static void Prefix()
            {
                SeinCharacter s = s_caster;
                if (s == null || s_charged || !AnyDowned || IsDowned(s) || !(Local || Online) || !PlayedHere(s)) return;
                s_charged = true;
                if (s_mutateNoDeathSave) return;
                float before = s.Mortality.Health.Amount;
                float after = SoulLinkRules.DeathSaveHealth(before);
                s.Mortality.Health.SetAmount(after);
                DeathSaves++; LastDeathSaveBefore = before; LastDeathSaveAfter = after;
                NetHud.Toast(L10n.T("revive.death_save"));
                Services.Log.Info("death save by P" + PlayerOf(s) + ": health " + before.ToString("F1") + " -> " + after.ToString("F1"));
            }

            // Remember who saved and where (offline P2 at a spirit well: P1 stands at P2's spot, CheckpointWhereP2Is).
            static void Postfix()
            {
                try
                {
                    SeinCharacter s = s_caster != null ? s_caster : Local && Host.Inside && Host.P2 != null ? Host.P2 : Host.P1;
                    if (s == null) return;
                    Vector3 at = s.Position;
                    s_checkpoint.Seat = PlayerOf(s); s_checkpoint.X = at.x; s_checkpoint.Y = at.y; s_checkpoint.Z = at.z;
                    s_checkpoint.Health = s.Mortality.Health.Amount;
                    s_haveCheckpoint = true;
                }
                catch (Exception e) { Services.Log.Warn("checkpoint record: " + e.Message); }
            }
        }

        // ---- the room's save point ------------------------------------------------------------------------------
        [HarmonyPatch(typeof(SaveGameController), "PerformSave")]
        static class RoomSaveWatch
        {
            // A guest's save becomes a SaveOffer (SaveSync.GuestSavePatch). Tell the host where it is first; reliable
            // messages from one device arrive in order, so the host has it when it adopts the offer.
            [HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                try
                {
                    if (!Online || Services.Net.IsHost) return;
                    if (!s_haveCheckpoint) { Services.Log.Info("room link: guest save without a recorded checkpoint"); return; }
                    RegisterLink();
                    ReviveWire.Link c = s_checkpoint;
                    // Send, not SendTo: a guest talks only to the host in P2P; in server mode the others ignore it.
                    Services.Net.Send(MsgRoomLink, true, w => ReviveWire.WriteLink(w, c.Seat, c.X, c.Y, c.Z, c.Health));
                    Services.Log.Info("room link: offering P" + c.Seat + "'s save point at (" + c.X + ", " + c.Y + ") with health " + c.Health.ToString("F1"));
                }
                catch (Exception e) { Services.Log.Warn("room link offer: " + e.Message); }
            }

            static void Postfix(bool __runOriginal)
            {
                try
                {
                    if (!__runOriginal) return;   // a guest's suppressed save, or a promoted save that was blocked
                    if (Local) { if (s_haveCheckpoint) ApplyLink(s_checkpoint, "local save"); return; }
                    if (!Online || !Services.Net.IsHost) return;
                    RegisterLink();
                    int offerer = SaveSync.AdoptingOfferSeat;
                    if (offerer > 0)
                    {
                        if (s_mutateLinkHostOnly) return;
                        ReviveWire.Link l;
                        if (offerer <= ReviveLogic.MaxSeats && s_haveOffered[offerer]) l = s_offered[offerer];
                        else
                        {
                            // No spot from the guest (older build): its live position is the best we have.
                            Vector3 at;
                            if (!Host.RemotePos(offerer, out at)) return;
                            l.Seat = offerer; l.X = at.x; l.Y = at.y; l.Z = at.z; l.Health = 0f;
                        }
                        BroadcastLink(l, "seat " + offerer + "'s save offer");
                    }
                    else if (s_haveCheckpoint) BroadcastLink(s_checkpoint, "host save");
                }
                catch (Exception e) { Services.Log.Warn("room link: " + e.Message); }
            }
        }

        static void BroadcastLink(ReviveWire.Link l, string why)
        {
            ApplyLink(l, why);
            SendRoomLink();
        }

        static void SendRoomLink()
        {
            if (!s_haveLink || !Services.Net.Enabled || !Services.Net.Connected || !Services.Net.IsHost) return;
            int seat = s_linkSeat; Vector3 at = s_linkPos; float health = s_lastLinkHealth;
            Services.Net.Send(MsgRoomLink, true, w => ReviveWire.WriteLink(w, seat, at.x, at.y, at.z, health));
            RoomLinksSent++;
        }

        static float s_lastLinkHealth;
        static void ApplyLink(ReviveWire.Link l, string why)
        {
            s_haveLink = true; s_linkSeat = l.Seat; s_linkPos = new Vector3(l.X, l.Y, l.Z); s_lastLinkHealth = l.Health;
            if (l.Health > 0f) SoulLinkRules.NoteLink(s_roster, l.Health);
            LinksNoted++;
            Services.Log.Info("latest soul link (" + why + "): P" + l.Seat + " at " + s_linkPos + ", creator health " + l.Health.ToString("F1"));
            Host.Trace("player", "soullink.room", "who", "P" + l.Seat, "pos", s_linkPos);
        }

        static void OnNetRoomLink(BinaryReader r)
        {
            if (r.BaseStream.Length - r.BaseStream.Position < ReviveWire.LinkBytes) return;
            ReviveWire.Link l = ReviveWire.ReadLink(r);
            if (l.Seat < 1 || l.Seat > ReviveLogic.MaxSeats) return;
            int sender = Services.Net.Sender;
            if (Services.Net.IsHost)
            {
                // A guest's save point spot, ahead of its SaveOffer. Only its own seats count.
                if (sender == Services.Net.HostSeat || sender < 1 || sender > ReviveLogic.MaxSeats || NetPlay.DeviceSeat(l.Seat) != sender)
                { Services.Log.Info("room link: dropped a save point spot for P" + l.Seat + " from seat " + sender); return; }
                s_offered[sender] = l; s_haveOffered[sender] = true;
                Services.Log.Info("room link: seat " + sender + " offers its save point at (" + l.X + ", " + l.Y + "), health " + l.Health.ToString("F1"));
                return;
            }
            if (sender != Services.Net.HostSeat) return;   // a guest -> host message relayed to us (server mode)
            ApplyLink(l, "room save");
        }

        // After a checkpoint restore on the room's save authority (offline, or the host: a team wipe, loading the
        // game) its Ori stands on the room's save point: that is the latest link until a new one. Guests learn it
        // from the host, which re-sends it whenever a teammate's Ori appears again (TickOnline).
        static void NoteRestoredLink()
        {
            if (Host.P1 == null || Services.Net.Enabled && !Services.Net.IsHost) return;
            ReviveWire.Link l;
            l.Seat = Host.LocalSeat; l.X = Host.P1.Position.x; l.Y = Host.P1.Position.y; l.Z = Host.P1.Position.z; l.Health = 0f;
            ApplyLink(l, "checkpoint restore");
            if (Services.Net.Enabled) SendRoomLink();
        }

        // ---- the downed player's own option: revive at the latest soul link ------------------------------------
        static bool SelfBlocked
        {
            get
            {
                GameController gc = GameController.Instance;
                return gc == null || gc.GameplaySuspended || gc.IsLoadingGame || gc.InputLocked || SessionRules.LocalPaused;
            }
        }

        // Each Ori reads its own controls: P1 the game's input (pad Y, or Q on the keyboard), every other local Ori
        // (local co-op P2-P4, extra players on an online device) its own puppet input, where Y is the game's Bash.
        static bool HoldingY(SeinCharacter s)
        {
            if (TestHoldSelf) return true;
            Puppet p = Players.OfSein(s);
            return p != null ? p.Input.Bash.IsPressed : Core.Input.Bash.IsPressed || Input.GetKey(KeyCode.Q);
        }

        // T-169 (escape levels, #249) sets this from its own files: while it returns true, holding Y/Q
        // does not revive it at the soul link (in an escape the same button is mashed to come back beside a teammate)
        // and the hold-Y hint is hidden. The hold starts over once it returns false.
        public static Func<bool> GiveUpSuppressed = delegate { return false; };
        public static int GiveUpsSuppressedFrames;

        // Every frame for each downed Ori that this PC plays.
        static void TickSelf(Downed d)
        {
            if (d == null || d.Sein == null || d.Remote) return;
            bool suppressed = false;
            try { suppressed = GiveUpSuppressed != null && GiveUpSuppressed(); }
            catch (Exception e) { Services.Log.Warn("GiveUpSuppressed: " + e.Message); }
            if (suppressed)
            {
                SoulLinkRules.HoldSelf(d, false, true, 0f);   // starts over
                GiveUpsSuppressedFrames++;
                HideSelfHint(d.Player);
                return;
            }
            if (s_haveLink && SoulLinkRules.HoldSelf(d, HoldingY(d.Sein), SelfBlocked, Time.deltaTime))
            {
                HideSelfHint(d.Player);
                ReviveAtLink(d);
                return;
            }
            ShowSelfHint(d);
        }

        static void ReviveAtLink(Downed d)
        {
            float health = s_mutateReviveInPlace ? d.Sein.Mortality.Health.MaxHealth : SoulLinkRules.SelfReviveHealth(d, d.Sein.Mortality.Health.MaxHealth);
            Vector3 at = s_mutateReviveInPlace ? d.Marker : s_linkPos;
            SoulLinkRules.CancelRescue(d);
            s_down[d.Player] = null;
            RestoreDowned(d, false);
            Host.Teleport(d.Sein, at);
            d.Sein.Mortality.Health.SetAmount(Mathf.Clamp(health, 1f, d.Sein.Mortality.Health.MaxHealth));
            d.Sein.Mortality.DamageReciever.MakeInvincible(1.5f);
            if (Host.IsPuppet(d.Sein)) Host.ReturnCompanion(d.Sein, "revived at the soul link");
            PartnerFx.PlayRevive(d.Sein, d.Sein.Position);
            if (Online) SendRevived(d.Player, d.Sein.Position);
            SelfRevives++; LastSelfReviveHealth = health;
            Services.Log.Info("P" + d.Player + " revived at the latest soul link " + at + " with " + health.ToString("F1") + " health");
        }

        static void ShowSelfHint(Downed d)
        {
            int seat = d.Player;
            bool primary = d.Sein == Host.P1;
            bool pad = !primary || GameSettings.Instance != null && GameSettings.Instance.CurrentControlScheme == ControlScheme.Controller;
            string text = s_haveLink ? L10n.F("revive.self_hold", pad ? PadY : KeyQ) : null;
            LastSelfHint = text;
            GameplayCamera cam = UI.Cameras.Current;
            if (text == null || cam == null || cam.Camera == null) { HideSelfHint(seat); return; }
            // Online the camera is elsewhere (spectating): the primary Ori's hint sits low on the screen. Other local
            // Oris (local co-op, extra players on a device) get theirs under their own skull.
            Vector3 at = Services.Net.Enabled && primary
                ? cam.Camera.ViewportToWorldPoint(new Vector3(0.5f, 0.24f, -cam.Camera.transform.position.z))
                : d.Marker + new Vector3(0f, -1.6f, 0f);
            if (s_selfHint[seat] == null) s_selfHint[seat] = new GameText("OriCoop_SelfReviveHint_P" + seat);
            if (s_selfHint[seat].Ok) { s_selfHint[seat].SetText(text); s_selfHint[seat].Place(at, 0.032f, 0.4f, 1f); }
        }

        static void HideSelfHint(int seat)
        {
            if (s_selfHint[seat] != null) s_selfHint[seat].Hide();
        }

        // Hide the hints of seats that are no longer down here (revived, reload, left).
        static void HideStaleSelfHints()
        {
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++)
                if (s_down[seat] == null || s_down[seat].Remote) HideSelfHint(seat);
        }

        static void ResetLinkState()
        {
            s_haveLink = false; s_haveCheckpoint = false; s_lastLinkHealth = 0f;
            Array.Clear(s_haveOffered, 0, s_haveOffered.Length);
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++) HideSelfHint(seat);
        }
    }
}
