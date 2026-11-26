using System;
using System.IO;
using System.Reflection;
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
    //   where it is and which native flame the checkpoint contains (msg 69 RoomLink). A guest sends the same
    //   information for the save it offers. Each device has just one flame, owned by the primary Ori.
    // - A downed player holds Y (pad) / Q (keyboard) for 2 s to revive at the latest soul link, alone: the world does not
    //   reload and the others keep playing. Health: the creator's health right after saving if the link was made while
    //   this player was down, otherwise full. Energy is not touched. No skill rollback (that is only the team wipe's).
    // - Death save: a living player who makes a soul link while a teammate is down loses half its health (at least one
    //   cell is kept); energy is not touched. The cost is taken before the checkpoint, so the save keeps it.
    // - Everyone else sees "Pn made a new soul link" (PartnerFx, msg 80). There is no timed respawn any more.
    // T-202 (#285, rules in docs\tickets\T-202.md): a downed player can always hold Y / Q, on its own. Until the room
    // has had a save in this session (the host loaded or started the game before its teammates' Oris were there), the
    // save point is the host's latest checkpoint, where a team wipe would bring everyone back (Rules.SavePointState).
    // It comes back there with full health; energy is not touched, so nobody needs energy for a new soul link.
    public static partial class Revive
    {
        public const byte MsgRoomLink = 69;   // docs/net-msg-ids.md
        const string PadY = "<icon>i</>", KeyQ = "<icon>I</>";   // the game's own button icons (ButtonIconUtility)

        // Planted bugs (docs/testing-rules.md): each one must make its net test fail.
        static readonly bool s_mutateNoDeathSave = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_DEATH_SAVE") == "1";
        static readonly bool s_mutateReviveInPlace = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SELF_REVIVE_IN_PLACE") == "1";
        static readonly bool s_mutateLinkHostOnly = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_LINK_HOST_ONLY") == "1";
        // T-202 planted bug: only a room save gives a save point, as before T-202 (nothing to hold Y for until then).
        static readonly bool s_mutateNoCheckpointPoint = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_CHECKPOINT_POINT") == "1";

        // The room's save point (the latest soul link, or the host's latest checkpoint while the room has no save yet)
        // and the latest checkpoint this PC made: who saved, where, with what health (after the death-save cost).
        static readonly SavePointState s_point = new SavePointState();
        static Vector3 s_linkPos;
        static int s_linkSeat;
        static readonly FieldInfo s_nativeFlame = AccessTools.Field(typeof(SeinSoulFlame), "m_soulFlame");
        static readonly FieldInfo s_nativeScene = AccessTools.Field(typeof(SeinSoulFlame), "m_sceneCheckpoint");
        static readonly bool s_mutateDropPendingLink = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_DROP_PENDING_LINK") == "1";
        static int s_nativeLinkTries;
        internal static bool RoomFlamePending { get; private set; }
        internal static int TestFailRoomFlameApplies;
        static bool s_usingSharedFlame;
        static SeinCharacter s_caster;       // inside SeinSoulFlame.CastSoulFlame: the Ori making the soul link
        static bool s_charged;               // the death-save cost was taken for this cast
        // Host: where each guest device's offered save point is (msg 69 from that guest, just before its SaveOffer).
        // T-215 #6: one spot serves one offer (Rules.OfferedSpots).
        static readonly OfferedSpots s_offers = new OfferedSpots(ReviveLogic.MaxSeats);
        // T-215 planted bugs. #6: a guest tells its save point's spot only while a teammate's Ori is on its PC, and the
        // host keeps a used spot for later offers. #7: telling the room's save point again looks like a new soul link.
        static readonly bool s_mutateStaleOfferSpot = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STALE_OFFER_SPOT") == "1";
        static readonly bool s_mutateResendAsNew = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_RESEND_AS_NEW") == "1";
        static readonly GameText[] s_selfHint = new GameText[ReviveLogic.MaxSeats + 1];
        static bool s_linkRegistered;

        public static int LinksNoted, RoomLinksSent, SelfRevives, DeathSaves;
        public static float LastDeathSaveBefore, LastDeathSaveAfter, LastSelfReviveHealth;
        public static string LastSelfHint;
        internal static bool TestHoldSelf;   // test hook: every downed Ori of this PC holds Y
        public static bool HaveLink { get { return s_point.Have; } }
        public static Vector3 LinkPosition { get { return s_linkPos; } }
        public static int LinkSeat { get { return s_linkSeat; } }
        // T-202: the save point is the host's latest checkpoint (the room has had no save yet); how often that happened.
        public static bool LinkFromCheckpoint { get { return s_point.FromCheckpoint; } }
        public static int CheckpointPoints;
        // T-202 test read-outs. Host: how many times a teammate's Ori appeared in this session, and whether the room
        // had a save point the first time (before T-202 it had none until the first save with a teammate present).
        public static int TeammateArrivals;
        public static bool FirstArrivalHadPoint, FirstArrivalFromCheckpoint;
        // Test hook: forget the room's save point, as at the start of a session (this PC's own checkpoint stays).
        internal static void TestForgetRoomSave() { s_point.SessionEnded(); RoomFlamePending = false; }
        // Test hook (T-215 #2): this PC's downed Ori completes its hold right now (the "I'm back" of a hold that began
        // two seconds earlier, as the host receives it just after someone else's "I'm down").
        internal static bool TestSelfReviveNow(int seat)
        {
            Downed d = seat >= 1 && seat <= ReviveLogic.MaxSeats ? s_down[seat] : null;
            if (d == null || d.Remote || d.Sein == null || !s_point.Have) return false;
            HideSelfHint(seat);
            ReviveAtLink(d);
            return true;
        }

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
            Services.Net.PlayerLeft += delegate(int seat) { ReviveWire.Link ignored; s_offers.Take(seat, out ignored); };
            Services.Net.SessionEnded += ResetLinkState;
        }

        // ---- soul link cast: the death save ---------------------------------------------------------------------
        [HarmonyPatch(typeof(SeinSoulFlame), "CastSoulFlame")]
        static class SoulLinkCast
        {
            static void Prefix(SeinSoulFlame __instance, SeinCharacter ___m_sein, ref GameObject ___m_checkpointMarkerGameObject,
                ref SoulFlame ___m_soulFlame)
            {
                s_caster = ___m_sein; s_charged = false;
                if (SharedFlameFor(s_caster) != null)
                {
                    // The extra may be borrowing the marker for its update. The new cast replaces it on the primary.
                    ___m_checkpointMarkerGameObject = null;
                    ___m_soulFlame = null;
                }
                else if (s_caster == Host.P1 && (Local || Online)) RemoveNativeFlame(__instance);
            }
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

        static SeinSoulFlame SharedFlameFor(SeinCharacter extra)
        {
            return Host.P1 != null && extra != Host.P1 && PlayedHere(extra) ? Host.P1.SoulFlame : null;
        }

        [HarmonyPatch(typeof(SeinSoulFlame), "SpawnSoulFlame")]
        static class CastOnPrimary
        {
            static bool Prefix(SeinCharacter ___m_sein, ref MoonGuid ___m_sceneCheckpoint, Vector3 position)
            {
                if (s_caster == null || s_caster != ___m_sein) return true;
                SceneRoot scene = Core.Scenes.Manager.FindLoadedSceneRootFromPosition(position);
                if (scene != null) ___m_sceneCheckpoint = new MoonGuid(scene.MetaData.SceneMoonGuid);
                SeinSoulFlame shared = SharedFlameFor(s_caster);
                if (shared == null) return true;
                PlaceNativeFlame(shared, position, new MoonGuid(___m_sceneCheckpoint));
                return false;
            }
        }

        // An extra uses the one marker for the native update (including Rekindle), but never owns it.
        [HarmonyPatch(typeof(SeinSoulFlame), "UpdateCharacterState")]
        static class UseSharedFlame
        {
            static void Prefix(SeinCharacter ___m_sein, ref SoulFlame ___m_soulFlame, ref GameObject ___m_checkpointMarkerGameObject,
                ref MoonGuid ___m_sceneCheckpoint, out MoonGuid __state)
            {
                __state = null;
                SeinSoulFlame shared = SharedFlameFor(___m_sein);
                if (shared == null) return;
                __state = ___m_sceneCheckpoint;
                ___m_sceneCheckpoint = (MoonGuid)s_nativeScene.GetValue(shared);
                ___m_soulFlame = (SoulFlame)s_nativeFlame.GetValue(shared);
                ___m_checkpointMarkerGameObject = ___m_soulFlame != null ? ___m_soulFlame.gameObject : null;
                s_usingSharedFlame = true;
            }
            static void Finalizer(MoonGuid __state, ref SoulFlame ___m_soulFlame, ref GameObject ___m_checkpointMarkerGameObject,
                ref MoonGuid ___m_sceneCheckpoint)
            {
                if (object.ReferenceEquals(__state, null)) return;   // MoonGuid's == returns false for nulls.
                ___m_soulFlame = null;
                ___m_checkpointMarkerGameObject = null;
                ___m_sceneCheckpoint = __state;
                s_usingSharedFlame = false;
            }
        }

        [HarmonyPatch(typeof(SeinSoulFlame), "get_InsideCheckpointMarker")]
        static class InsideSharedFlame
        {
            static void Postfix(SeinCharacter ___m_sein, ref bool __result)
            {
                SeinSoulFlame shared = SharedFlameFor(___m_sein);
                if (shared == null) return;
                // SoulFlame.FixedUpdate measures the primary Ori. Check the extra against the same marker instead.
                SoulFlame marker = (SoulFlame)s_nativeFlame.GetValue(shared);
                __result = marker != null && marker.gameObject.activeInHierarchy
                    && Vector3.Distance(___m_sein.Position, marker.Position) < marker.EnterRadius;
            }
        }

        static void RemoveNativeFlame(SeinSoulFlame flame)
        {
            SoulFlame marker = (SoulFlame)s_nativeFlame.GetValue(flame);
            if (marker == null) return;
            marker.gameObject.SetActive(false);
            flame.DestroySoulFlame();
        }

        static void PlaceNativeFlame(SeinSoulFlame flame, Vector3 at, MoonGuid scene)
        {
            RemoveNativeFlame(flame);
            s_nativeScene.SetValue(flame, scene);
            flame.SpawnSoulFlame(at);
        }

        static ReviveWire.Marker? NativeMarker(SeinSoulFlame flame)
        {
            if (flame == null || !flame.SoulFlameExists) return null;
            Vector3 at = flame.SoulFlamePosition;
            MoonGuid scene = (MoonGuid)s_nativeScene.GetValue(flame);
            return new ReviveWire.Marker { X = at.x, Y = at.y, Z = at.z, Scene = new Guid(scene.ToByteArray()) };
        }

        internal static void TickRoomFlame()
        {
            if (!RoomFlamePending) return;
            if (NetPlay.TestLoading || !NetPlay.LocalInGameplay() || OwnOri.SoulFlame == null)
            {
                if (s_mutateDropPendingLink) RoomFlamePending = false;
                return;
            }
            try
            {
                if (TestFailRoomFlameApplies > 0) { TestFailRoomFlameApplies--; throw new InvalidOperationException("test: native flame not ready"); }
                ReviveWire.Link point = s_point.Point;
                SeinSoulFlame flame = OwnOri.SoulFlame;
                if (SoulLinkRules.NativeMarkerChanged(point, NativeMarker(flame)))
                {
                    if (point.Flame.HasValue)
                    {
                        ReviveWire.Marker m = point.Flame.Value;
                        Vector3 at = new Vector3(m.X, m.Y, m.Z);
                        PlaceNativeFlame(flame, at, new MoonGuid(m.Scene));
                        Host.Trace("player", "soullink.native", "pos", at, "scene", m.Scene);
                    }
                    else RemoveNativeFlame(flame);
                }
                RoomFlamePending = false;
            }
            catch (Exception e)
            {
                if (++s_nativeLinkTries >= 5) { RoomFlamePending = false; Services.Log.Warn("room flame could not be applied: " + e.Message); }
            }
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
                NetHud.Toast(L10n.SessionText("revive.death_save"));
                Services.Log.Info("death save by P" + PlayerOf(s) + ": health " + before.ToString("F1") + " -> " + after.ToString("F1"));
            }

            // Remember who saved and where (offline P2 at a spirit well: P1 stands at P2's spot, CheckpointWhereP2Is).
            // T-202: also while this PC plays alone (no teammate's Ori yet): its latest checkpoint is the room's save
            // point until the room's first save. Not on the title screen (the game makes a checkpoint there too).
            static void Postfix()
            {
                try
                {
                    SeinCharacter s = s_caster != null ? s_caster : (Local || s_usingSharedFlame) && Host.Inside && Host.P2 != null ? Host.P2 : OwnOri;
                    if (s == null || GameStateMachine.Instance != null && GameStateMachine.Instance.IsInExtendedTitleScreen()) return;
                    s_point.NoteCheckpoint(LinkAt(s, s.Mortality.Health.Amount));
                }
                catch (Exception e) { Services.Log.Warn("checkpoint record: " + e.Message); }
            }
        }

        // This PC's own Ori, also before any teammate's Ori exists (Host.P1 is only set while co-op runs).
        static SeinCharacter OwnOri { get { return Host.P1 != null ? Host.P1 : Characters.Sein; } }

        static ReviveWire.Link LinkAt(SeinCharacter s, float health)
        {
            int seat = PlayerOf(s);
            Vector3 at = s.Position;
            return new ReviveWire.Link { Seat = seat >= 1 && seat <= ReviveLogic.MaxSeats ? seat : Host.LocalSeat,
                X = at.x, Y = at.y, Z = at.z, Health = health, MarkerKnown = true, Flame = NativeMarker(OwnOri.SoulFlame) };
        }

        // Back to the title (or a reload through it, which restores a checkpoint afterwards): the checkpoint this PC
        // remembered belongs to the game that just ended.
        // On the save authority the room's save point goes too: it is found again from what loads next (offline
        // nothing else ever cleared it, so a second game kept the first game's soul link).
        [HarmonyPatch(typeof(GameController), "RestartGame")]
        static class CheckpointForget
        {
            static void Prefix()
            {
                s_point.ForgetCheckpoint();
                if (!Services.Net.Enabled || Services.Net.IsHost) { s_point.SessionEnded(); RoomFlamePending = false; }
            }
        }

        // ---- the room's save point ------------------------------------------------------------------------------
        // Send only alongside a prepared SaveOffer, even if the guest has no teammate's Ori loaded.
        // Reliable messages from one device arrive in order, so the host has the spot when it handles the offer.
        internal static void SendLinkOffer()
        {
            try
            {
                if (s_mutateStaleOfferSpot && !Online) return;
                if (!s_point.HaveCheckpoint) { Services.Log.Info("room link: guest save without a recorded checkpoint"); return; }
                RegisterLink();
                ReviveWire.Link c = s_point.Checkpoint;
                Services.Net.Send(MsgRoomLink, true, w => ReviveWire.WriteLink(w, c));
                Services.Log.Info("room link: offering P" + c.Seat + "'s save point at (" + c.X + ", " + c.Y + ") with health " + c.Health.ToString("F1"));
            }
            catch (Exception e) { Services.Log.Warn("room link offer: " + e.Message); }
        }

        [HarmonyPatch(typeof(SaveGameController), "PerformSave")]
        static class RoomSaveWatch
        {
            static void Postfix(bool __runOriginal)
            {
                try
                {
                    if (!__runOriginal) return;   // a guest's suppressed save, or a promoted save that was blocked
                    if (Local) { if (s_point.HaveCheckpoint) ApplyLink(s_point.Checkpoint, "local save"); return; }
                    // The room tracks its accepted marker even if revive is disabled or a teammate is loading.
                    bool hosting = s_mutateStaleOfferSpot ? Online : Services.Net.Enabled && Services.Net.Connected;
                    if (!hosting || !Services.Net.IsHost) return;
                    RegisterLink();
                    int offerer = SaveSync.AdoptingOfferSeat;
                    if (offerer > 0)
                    {
                        if (s_mutateLinkHostOnly) return;
                        ReviveWire.Link l;
                        if (!s_offers.Take(offerer, out l, s_mutateStaleOfferSpot))
                        {
                            // No spot came with this offer (older build): its live position is the best we have.
                            Vector3 at;
                            if (!Host.RemotePos(offerer, out at)) return;
                            l = new ReviveWire.Link { Seat = offerer, X = at.x, Y = at.y, Z = at.z };
                        }
                        // The adopted save is this PC's checkpoint now (LoadFromBytes, no CreateCheckpoint of its own).
                        s_point.NoteCheckpoint(l);
                        BroadcastLink(l, "seat " + offerer + "'s save offer");
                    }
                    else if (s_point.HaveCheckpoint) BroadcastLink(s_point.Checkpoint, "host save");
                }
                catch (Exception e) { Services.Log.Warn("room link: " + e.Message); }
            }
        }

        internal static void RejectLinkOffer(int seat)
        {
            ReviveWire.Link offered;
            if (!s_offers.Take(seat, out offered)) return;
            if (!s_point.Have && !s_point.HaveCheckpoint) return;
            ReviveWire.Link point = s_point.Have ? s_point.Point : s_point.Checkpoint;
            point.Health = 0f;
            point.OfferRejected = true;
            Services.Net.SendTo(seat, MsgRoomLink, true, w => ReviveWire.WriteLink(w, point));
        }

        static void BroadcastLink(ReviveWire.Link l, string why)
        {
            ApplyLink(l, why);
            SendRoomLink();
        }

        // repeat: told again to a PC that joined or came back from loading. T-215 #7: a repeat carries health 0, so on
        // the PCs that already know this save point it is not a soul link made just now (a player who went down after
        // it was made would come back with the creator's health instead of full health).
        static void SendRoomLink(bool repeat = false)
        {
            if (!s_point.Have || !Services.Net.Enabled || !Services.Net.Connected || !Services.Net.IsHost) return;
            ReviveWire.Link point = s_point.Point;
            point.Health = SoulLinkRules.AnnouncedHealth(point.Health, repeat && !s_mutateResendAsNew);
            Services.Net.Send(MsgRoomLink, true, w => ReviveWire.WriteLink(w, point));
            RoomLinksSent++;
        }
        static void ApplyLink(ReviveWire.Link l, string why)
        {
            s_point.NoteSave(l);
            NoteSavePoint(why);
        }

        // s_point.Point just changed: where it is, and (a soul link made right now) what it gives the players who are down.
        static void NoteSavePoint(string why)
        {
            ReviveWire.Link l = s_point.Point;
            s_linkSeat = l.Seat; s_linkPos = new Vector3(l.X, l.Y, l.Z);
            RoomFlamePending = Services.Net.Enabled && l.MarkerKnown; s_nativeLinkTries = 0;
            SoulLinkRules.NoteLink(s_roster, l.Health);   // T-248: a 0-health save point leaves the downed players' revive health alone
            LinksNoted++;
            Services.Log.Info("latest soul link (" + why + "): P" + l.Seat + " at " + s_linkPos + ", creator health " + l.Health.ToString("F1"));
            Host.Trace("player", "soullink.room", "who", "P" + l.Seat, "pos", s_linkPos);
        }

        // T-202: every frame while co-op runs (Tick, TickOnline). On the save authority (offline, or the online host),
        // while the room has had no save yet, the room's save point is this PC's latest checkpoint; guests are told.
        static bool ResolveSavePoint()
        {
            if (s_mutateNoCheckpointPoint) return false;
            if (!s_point.Resolve(!Services.Net.Enabled || Services.Net.IsHost)) return false;
            CheckpointPoints++;
            NoteSavePoint("no room save yet: the latest checkpoint");
            SendRoomLink();
            return true;
        }

        // Host, from TickOnline: a teammate's Ori appeared (it joined, or is back from loading). Test read-out.
        static void NoteTeammateArrival()
        {
            if (TeammateArrivals++ == 0) { FirstArrivalHadPoint = s_point.Have; FirstArrivalFromCheckpoint = s_point.FromCheckpoint; }
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
                s_offers.Note(sender, l);
                Services.Log.Info("room link: seat " + sender + " offers its save point at (" + l.X + ", " + l.Y + "), health " + l.Health.ToString("F1"));
                return;
            }
            if (sender != Services.Net.HostSeat) return;   // a guest -> host message relayed to us (server mode)
            bool rejected = l.OfferRejected;
            l.OfferRejected = false;
            if (rejected && SaveSync.RestoreAcceptedCheckpoint()) s_point.NoteCheckpoint(l);
            ApplyLink(l, rejected ? "save offer rejected" : "room save");
        }

        // After a checkpoint restore on the room's save authority (offline, or the host: a team wipe, loading the
        // game) its Ori stands on the room's save point: that is the latest link until a new one. Guests learn it
        // from the host, which re-sends it whenever a teammate's Ori appears again (TickOnline).
        static void NoteRestoredLink()
        {
            SeinCharacter own = OwnOri;
            if (own == null) return;
            ReviveWire.Link l = LinkAt(own, 0f);
            l.Seat = Host.LocalSeat;
            // T-202: this PC stands on the restored checkpoint, also while it plays alone (a host that loads its game
            // before the teammates arrive: ResolveSavePoint makes it the room's save point once co-op runs).
            s_point.NoteCheckpoint(l);
            if (Host.P1 == null || Services.Net.Enabled && !Services.Net.IsHost) return;
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

        // T-248: what the hold Y / Q of each downed local Ori is doing, as last traced (null: not down / not traced yet).
        static readonly string[] s_selfHoldState = new string[ReviveLogic.MaxSeats + 1];

        // T-248: trace why the hold does (not) count whenever that changes: a playtest log then says why a downed player
        // never came back (no save point, an escape, gameplay suspended, loading, input locked, paused, or no button seen).
        static void TraceSelfHold(Downed d, bool suppressed, bool held)
        {
            GameController gc = GameController.Instance;
            string state = SoulLinkRules.SelfHoldState(s_point.Have, suppressed, gc == null, gc != null && gc.GameplaySuspended,
                gc != null && gc.IsLoadingGame, gc != null && gc.InputLocked, SessionRules.LocalPaused, held);
            int seat = d.Player;
            if (seat < 1 || seat > ReviveLogic.MaxSeats || s_selfHoldState[seat] == state) return;
            s_selfHoldState[seat] = state;
            bool refused = SoulLinkRules.SelfHoldRefused(state);
            Host.Trace("player", "revive.self_hold", "who", "P" + seat, "state", state, "refused", refused,
                "q", Input.GetKey(KeyCode.Q), "bash", Core.Input.Bash.IsPressed,
                "lockInput", gc != null && gc.LockInput, "lockByAction", gc != null && gc.LockInputByAction,
                "localLocked", SessionRules.LocalLocked, "screen", SessionRules.ScreenInputLocked(d.Sein),
                "gateWait", ForcedSync.IsLocked(d.Sein), "timeScale", Time.timeScale, "link", s_linkPos);
            if (refused && held) Services.Log.Info("P" + seat + " holds Y / Q at the latest soul link but it does not count: " + state);
        }

        // Each Ori reads its own controls: P1 the game's input (pad Y, or Q on the keyboard), every other local Ori
        // (local co-op P2-P4, extra players on an online device) its own puppet input, where Y is the game's Bash.
        static bool HoldingY(SeinCharacter s)
        {
            if (TestHoldSelf) return true;
            Puppet p = Players.OfSein(s);
            return p != null ? (Services.Net.Enabled ? p.SelfReviveHeld : p.Input.Bash.IsPressed)
                : Core.Input.Bash.IsPressed || Input.GetKey(KeyCode.Q);
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
            bool held = HoldingY(d.Sein);
            TraceSelfHold(d, suppressed, held);
            if (suppressed)
            {
                SoulLinkRules.HoldSelf(d, false, true, 0f);   // starts over
                GiveUpsSuppressedFrames++;
                HideSelfHint(d.Player);
                return;
            }
            if (s_point.Have && SoulLinkRules.HoldSelf(d, held, SelfBlocked, Time.deltaTime))
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
            if (!s_point.Have) { LastSelfHint = null; HideSelfHint(seat); return; }
            string text = L10n.F("revive.self_hold", pad ? PadY : KeyQ);
            // T-202 (#285): the online camera may follow a teammate, so the ring around the skull can be off screen;
            // the hint, kept in view, also shows how far the hold is (in 10% steps: each new text re-renders the native box).
            if (d.SelfHold > 0f) text += "  " + Mathf.FloorToInt(Mathf.Clamp01(d.SelfHold / SoulLinkRules.SelfHoldSeconds) * 10f) * 10 + "%";
            if (s_selfHint[seat] == null) s_selfHint[seat] = new GameText("OriCoop_SelfReviveHint_P" + seat);
            GameText hint = s_selfHint[seat];
            // The online camera may follow a teammate, not the downed Ori. Keep the primary hint in the viewport.
            bool shown = hint.SetText(text) && (Services.Net.Enabled && primary
                ? hint.PlaceViewport(0.5f, 0.24f, GameText.PromptHeight, 0.9f)   // T-255: native size (was 0.032, half the native hint)
                : hint.Place(d.Marker + new Vector3(0f, -1.6f, 0f), GameText.PromptHeight, 0.9f, 1f));
            LastSelfHint = shown ? text : null;
        }

        // T-255 (playtest screenshot: this hint drew faint grey): its text alpha and what GameText found, for the R6 check.
        internal static float SelfHintHeight(int seat) { GameText hint = s_selfHint[seat]; return hint != null ? hint.ScreenHeight() : -1f; }
        internal static float SelfHintAlpha(int seat) { GameText hint = s_selfHint[seat]; return hint != null ? hint.TextAlpha() : -1f; }
        internal static string SelfHintBrightness(int seat) { GameText hint = s_selfHint[seat]; return hint != null ? hint.BrightDebug ?? "" : "no hint"; }

        internal static bool SelfHintOnScreen(int seat)
        {
            GameText hint = s_selfHint[seat];
            return hint != null && hint.OnScreen(false) && hint.OnScreen(true);
        }

        static void HideSelfHint(int seat)
        {
            if (s_selfHint[seat] != null) s_selfHint[seat].Hide();
        }

        // Hide the hints of seats that are no longer down here (revived, reload, left).
        static void HideStaleSelfHints()
        {
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++)
                if (s_down[seat] == null || s_down[seat].Remote) { HideSelfHint(seat); s_selfHoldState[seat] = null; }
        }

        static void ResetLinkState()
        {
            // The room's save point goes with the session. This PC's own latest checkpoint stays (T-202): its game goes
            // on, and it is the save point of the next session this PC hosts until that room's first save.
            s_point.SessionEnded(); RoomFlamePending = false;
            TeammateArrivals = 0; FirstArrivalHadPoint = FirstArrivalFromCheckpoint = false;
            s_offers.Clear();
            EscapeSessionEnded();   // T-215 #3
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++) HideSelfHint(seat);
        }
    }
}
