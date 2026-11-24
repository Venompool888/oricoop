using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Saves;
using UnityEngine;

namespace OriCoop
{
    // 10: checkpoint bytes and an informational live position; 11: guest loaded and joined the host.
    public static class SaveSync
    {
        static bool s_started, s_loading, s_disabled, s_restartFinished, s_restoreFinished, s_hostAdopt;
        static bool s_joined;   // T-142: this guest finished its join load in this session; later loads are host checkpoints
        static readonly SeatReadiness s_guests = new SeatReadiness();
        static readonly HostPromotion s_promotion = new HostPromotion();
        static byte[] s_pending;
        static byte[] s_hostCheckpointBytes;
        static byte[] s_loadingBytes;
        static byte[] s_titleLoadBytes;   // guest joining from an empty slot: PerformLoad reads these (GuestJoin.cs)
        static SeinCharacter s_pendingSkillSave;
        static Stopwatch s_loadWatch;
        static float s_loadFinishedAt, s_joinStartedAt, s_adoptedAt, s_diagAt;
        static uint s_joinFrame;
        public static bool Ready;
        public static int GuestLoads, GuestSaveAttempts, GuestSaveWrites, GuestFileAttempts, GuestBackupAttempts;
        public static Vector3 HostCheckpointPosition, GuestLoadPosition;
        public static bool HaveCheckpointPosition;
        public static bool JoinedHost;
        public static int CheckpointsBeforeCommit;   // T-129 test: a checkpoint arrived before any co-op save commit
        public static int CopySlotAtCheckpoint = -2;  // T-129 test: co-op copy slot when the first checkpoint arrived (-2 none yet)
        public static float GuestJoinDistance = float.PositiveInfinity, GuestJoinElapsed = float.PositiveInfinity;
        public static Vector3 HostJoinPosition;
        // T-043 (room-owned saves): a guest's save point offered to the host / adopted by the host as the room's save.
        public static int GuestSavesOffered, HostSavesAdopted, OffersRejected;
        public static string LastOfferVerdict = "";
        static bool s_adoptingOffer;
        // T-041: while the host adopts a guest's save offer, the offering device's seat (0 otherwise): the room's
        // latest save point is that guest's (Revive.SoulLink).
        public static int AdoptingOfferSeat { get; private set; }
        static readonly bool s_mutateNoSaveOffer = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_SAVEOFFER") == "1";
        static readonly bool s_mutatePromoteAllReady = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROMOTE_ALL_READY") == "1";
        public static int CheckpointsSent;   // T-213 test: checkpoints this player sent as the host
        // Test only (T-213): as if this seat had joined after this guest and not announced its load yet.
        internal static void TestAwaitSave(int seat) { s_guests.SawJoin(seat); }

        public static void Init()
        {
            SavesHost.Current = new GameSavesHost();
            Services.Net.On(SaveWire.SaveMessage, Receive);
            Services.Net.On(SaveWire.SaveOffer, ReceiveOffer);
            Services.Net.On(SaveWire.LoadedMessage, delegate(BinaryReader r)
            {
                int seat = Services.Net.Sender;
                if (!Services.Net.IsHost) { s_guests.SawLoaded(seat); return; }   // T-213 (10): a guest notes who has loaded
                if (!s_guests.Loaded(seat)) return;
                SaveWire.ReadLoaded(r); UpdateHostReady();
                Services.Log.Info("net: guest save loaded; sending full world and progress");
                SavesHost.Current.SendWorldSnapshot(seat);   // also pays off a resync this load queued (T-254)
                SavesHost.Current.SendProgressSnapshot(seat);
            });
            Services.Net.SessionStarted += Start;
            Services.Net.SessionEnded += Reset;
            // T-213 (10): a guest that has finished its own join notes every seat that joins after it, until that seat
            // announces its load (SeatReadiness.SawJoin): if this guest becomes the host, that seat still needs a save.
            Services.Net.PlayerJoined += delegate(int seat) { if (Services.Net.IsHost) s_guests.Join(seat); else if (s_joined) s_guests.SawJoin(seat); };
            Services.Net.PlayerLeft += delegate(int seat) { s_guests.Leave(seat); if (Services.Net.IsHost) UpdateHostReady(); };
            CoopSaves.Init();
        }

        static void Reset()
        {
            Ready = s_started = s_loading = s_disabled = s_hostAdopt = s_joined = false;
            s_guests.Clear();
            s_promotion.Reset();
            s_pending = null;
            s_hostCheckpointBytes = null;
            s_loadingBytes = null;
            s_titleLoadBytes = null;
            s_pendingSkillSave = null;
            s_loadWatch = null;
            HaveCheckpointPosition = false;
            s_restartFinished = s_restoreFinished = JoinedHost = false;
            GuestJoinDistance = GuestJoinElapsed = float.PositiveInfinity;
        }

        static void Start()
        {
            // Duplicate Hello delivery must not load the same save twice in a session.
            if (s_started) return;
            s_started = true;
            Ready = false;
        }

        internal static void RebindDevice(int previous, int primary)
        {
            s_guests.Rebind(previous, primary);
            CoopSaves.RebindDevice(previous, primary);
            SeatSkillSync.RebindDevice(previous, primary);
        }

        public static bool ReadyForSeat(int seat)
        {
            if (Services.Net.IsHost && Players.IsLocal(seat) && !Services.Net.Connected) return true;
            if (!Ready) return false;
            if (Players.IsLocal(seat)) return true;
            return s_guests.ReadyForSeat(NetPlay.DeviceSeat(seat), Services.Net.IsHost, Ready);
        }

        // T-214 (host): the guest whose checkpoint has been out longest without its load being reported; 0 = none.
        internal static int WaitingGuest(out float seconds) { return s_guests.LongestWait(Services.Clock.RealTime, out seconds); }

        static void UpdateHostReady()
        {
            Ready = false;
            if (s_disabled) return;
            Ready = s_guests.AnyReady;
        }

        static void ResetGuests()
        {
            s_guests.ResetGuests();
            Ready = false;
        }

        static readonly Action s_coopSavesTick = CoopSaves.Tick, s_guestJoinTick = GuestJoin.Tick, s_prologueTick = SharedPrologue.Tick;

        public static void Tick()
        {
            SubsystemGuard.Run("CoopSaves.Tick", s_coopSavesTick);
            SubsystemGuard.Run("SharedPrologue.Tick", s_prologueTick);   // T-203: before the join, which waits while it is busy
            SubsystemGuard.Run("GuestJoin.Tick", s_guestJoinTick);
            if (!Services.Net.Connected || !s_started || s_disabled) return;
            CheckPromotion();
            if (s_pending == null && !s_loading) TickSkillSave();
            if (Services.Net.IsHost && !s_hostAdopt)
            {
                // Co-op save lineage first (CoopSaves): the host may adopt the partner's progress before the
                // first checkpoint goes out.
                bool unsent = s_guests.HasUnsent;
                if (unsent && SavesHost.Current.LocalInGameplay() && CoopSaves.HostGate())
                {
                    foreach (int seat in s_guests.Seats)
                        if (!s_guests.IsSent(seat)) SendCheckpoint(seat);
                }
                else if (unsent && s_adoptedAt > 0f && Services.Clock.RealTime > s_diagAt)
                {
                    s_diagAt = Services.Clock.RealTime + 3f;
                    SeinCharacter s = Characters.Sein;
                    Services.Log.Info("net: host waiting to send after adopt: sein=" + (s != null) + " active=" + (s != null && s.gameObject.activeInHierarchy)
                        + " loadingGame=" + GameController.Instance.IsLoadingGame + " title=" + (GameStateMachine.Instance != null && GameStateMachine.Instance.IsInExtendedTitleScreen())
                        + " state=" + (GameStateMachine.Instance == null ? "?" : GameStateMachine.Instance.CurrentState.ToString())
                        + " seinObjects=" + UnityEngine.Object.FindObjectsOfType(typeof(SeinCharacter)).Length);
                }
                return;
            }
            if (s_loading)
            {
                if (s_loadWatch != null && s_loadWatch.ElapsedMilliseconds > 60000)
                { Disable("native save-load timeout", new TimeoutException("restart/restore did not finish")); return; }
                if (s_restartFinished && s_loadingBytes != null) LoadAfterRestart();
                if (s_restoreFinished) PlaceBesideHost();
                return;
            }
            // Let the player's title/slot load settle before entering the native return-to-menu path.
            if (s_pending != null && !s_loading && SavesHost.Current.LocalInGameplay())
            {
                s_loadingBytes = s_pending;
                s_pending = null;
                s_loading = true;
                s_restartFinished = s_restoreFinished = false;
                s_joinStartedAt = Services.Clock.RealTime;
                s_loadWatch = Stopwatch.StartNew();
                if (!Services.Net.IsHost) SavesHost.Current.HoldOwnSkillTree(s_joined);
                try
                {
                    Plugin.DespawnNow(s_hostAdopt ? "adopting partner progress" : "online checkpoint transfer");
                    // ReturnToTitleScreenAction uses RestartGame and RestartingCleanupNextFrame:
                    // unload scenes, remove gameplay objects, and reset the scheduler once.
                    UI.Fader.Fade(0.25f, 0.1f, 0.25f, BeginRestart, null);
                }
                catch (Exception e) { Disable("checkpoint load", e); }
            }
        }

        static void BeginRestart()
        {
            if (!Services.Net.Connected || !s_loading || s_disabled) return;
            try { GameController.Instance.RestartGame(); }
            catch (Exception e) { Disable("native restart", e); }
        }

        static void LoadAfterRestart()
        {
            byte[] bytes = s_loadingBytes; s_loadingBytes = null;
            try
            {
                SaveSlotBackupsManager.ResetBackupDelay();
                // No menu action sequence remains to run UnlockFinishLoadingAction; explicitly
                // reset its lock before taking the same SetToGame + native restore path.
                InstantLoadScenesController.Instance.OnGameReset();
                GameStateMachine.Instance.SetToGame();
                SaveGameController save = GameController.Instance.SaveGameController;
                if (!save.LoadFromBytes(bytes)) throw new InvalidDataException("LoadFromBytes returned false");
                save.RestoreCheckpoint();
                Services.Log.Info("net: loading host checkpoint after native teardown " + bytes.Length + " bytes");
            }
            catch (Exception e) { Disable("checkpoint load after restart", e); }
        }

        static void PlaceBesideHost()
        {
            SeinCharacter own = Characters.Sein;
            if (own == null || GameController.Instance.IsLoadingGame) return;
            if (InstantLoadScenesController.Instance.IsLoading) return;
            float elapsed = Services.Clock.RealTime - s_loadFinishedAt;
            if (SavesHost.Current.HaveRemotePosition && SavesHost.Current.NewestRemoteTick != s_joinFrame)
            {
                SavePosition p = SavesHost.Current.LatestRemotePosition;
                HostJoinPosition = new Vector3(p.X, p.Y, p.Z);
                Plugin.Teleport(own, HostJoinPosition + Vector3.right);
                GuestJoinDistance = Vector3.Distance(own.Position, HostJoinPosition);
                GuestJoinElapsed = elapsed; JoinedHost = true;
                // This is the local player's new location. Use the native instant-load freeze so
                // a host far beyond the saved checkpoint cannot leave the guest falling into unloaded ground.
                // No remote puppet scene-loader is introduced.
                UI.Cameras.Current.MoveCameraToTargetInstantly();
                Core.Scenes.Manager.SetTargetPositions(own.Position);
                if (Core.Scenes.Manager.IsInsideASceneBoundary(own.Position))
                {
                    GameController.Instance.IsLoadingGame = true;
                    InstantLoadScenesController.Instance.LoadScenesAtPosition(delegate
                    {
                        GameController.Instance.IsLoadingGame = false;
                        Services.Log.Info("net: guest local scenes ready at live host join position");
                    }, true, false);
                    InstantLoadScenesController.Instance.FreezeIfLoadingScenes();
                }
                else Services.Log.Warn("net: live host join position has no local scene boundary; using normal local streaming");
                FinishJoin();
            }
            else if (elapsed >= 2f)
            {
                GuestJoinElapsed = elapsed; JoinedHost = false;
                Services.Log.Warn("net: no fresh host frame within 2 s after save load; join placement failed");
                FinishJoin(); // Expose failure to W instead of hanging the session.
            }
        }

        static void FinishJoin()
        {
            s_loading = s_restoreFinished = false; Ready = true;
            if (!Services.Net.IsHost)
            {
                s_joined = true;
                ProgressSync.GuestLoaded();
                SeatSkillSync.Loaded();
            }
            s_promotion.Joined();
            Services.Log.Info("net: guest join liveHost=" + HostJoinPosition + " distance=" + GuestJoinDistance
                + " elapsedAfterLoadMs=" + (GuestJoinElapsed * 1000f) + " placed=" + JoinedHost);
            Services.Net.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded);
        }

        // Host only: load the partner's progress (chosen at a co-op save divergence) through the same native
        // teardown + LoadFromBytes + RestoreCheckpoint path the guest uses; the checkpoint is sent afterwards.
        public static bool HostAdopting { get { return s_hostAdopt; } }
        // True while SaveSync itself restarts the game to load a checkpoint (guest transfer, host adopting the
        // partner's progress): RestartGame then is not a return to the title (TitleReturn, RoomGame, T-130).
        public static bool RestartingForLoad { get { return OriCoop.Saves.LoadRestart.IsOwn(s_loading, s_disabled); } }   // T-157: not after a failed load

        public static void HostAdopt(byte[] bytes)
        {
            if (!Services.Net.IsHost || s_loading || s_disabled || bytes == null) return;
            s_hostAdopt = true; s_pending = SeatSkillSync.ForLoad(bytes, "partner progress"); ResetGuests();
        }

        // A host restore uses exactly the join transfer / native teardown / live placement path.
        // Clear readiness before sending so LoadedMessage requests a fresh world/progress snapshot.
        public static void PushCheckpointNow()
        {
            if (!Services.Net.Enabled || !Services.Net.Connected || !Services.Net.IsHost || !s_started || s_disabled) return;
            ProgressSync.RestoreHostSkills();
            ResetGuests();
            foreach (int seat in s_guests.Seats) SendCheckpoint(seat);
        }

        static void SendCheckpoint(int seat)
        {
            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                byte[] bytes = GameController.Instance.SaveGameController.SaveToBytes();
                if (bytes.Length > SaveWire.MaximumSaveBytes) throw new InvalidDataException("save exceeds NetPeer's 4 MB limit");
                // The concrete PlatformMovement serializer prefixes velocity and four contact normals
                // before base Position (decomp PlatformMovementRigidbodyMoonCharacterControllerPenetrate:463).
                // Never infer a checkpoint position by reading the first twelve bytes of that archive.
                HostCheckpointPosition = Characters.Sein.Position; HaveCheckpointPosition = true;
                SeatSkillSync.SendPushRecord(seat);
                Services.Net.SendTo(seat, SaveWire.SaveMessage, true, delegate(BinaryWriter w)
                {
                    SaveWire.WriteSave(w, HaveCheckpointPosition, HostCheckpointPosition.x, HostCheckpointPosition.y, HostCheckpointPosition.z, bytes);
                });
                s_guests.MarkSent(seat, Services.Clock.RealTime); CheckpointsSent++;
                Services.Log.Info("net: sent checkpoint " + bytes.Length + " bytes in " + watch.ElapsedMilliseconds
                    + " ms; live host sample=" + HostCheckpointPosition);
            }
            catch (Exception e) { Disable("checkpoint transfer", e); }
        }

        // ---- T-043: a guest's save point becomes the room's save --------------------------------------------------
        // The host is the room's save authority (server mode: the seat holding the room, which uploads it). A guest's
        // save point is offered with the timeline it is based on; the host names it the saver only when that base is
        // the room's current timeline (SaverPick), adopts the bytes as its checkpoint and writes the room's slot. The
        // normal host save path then runs: CoopSaves commits the file to every seat's local copy and RoomGame uploads
        // it to the room. A team wipe restores this checkpoint on everyone (SessionRules -> PushCheckpointNow).

        static void OfferSave(SaveGameController save)
        {
            if (s_mutateNoSaveOffer) return;   // test-only: the T-021 bug (a guest's save point is swallowed)
            if (!Ready || s_loading || s_disabled || s_pending != null)
            {
                Services.Log.Info("net: guest save not offered: ready=" + Ready + " loading=" + s_loading + " pending=" + (s_pending != null));
                return;
            }
            try
            {
                byte[] bytes = save.SaveToBytes();
                if (bytes.Length > SaveWire.MaximumSaveBytes) throw new InvalidDataException("save exceeds NetPeer's 4 MB limit");
                string timeline = CoopSaves.Timeline;
                SeatSkillSync.Report();
                Revive.SendLinkOffer();
                Services.Net.Send(SaveWire.SaveOffer, true, delegate(BinaryWriter w) { SaveWire.WriteSaveOffer(w, timeline, bytes); });
                GuestSavesOffered++;
                Services.Log.Info("net: guest save offered to the room (" + bytes.Length + " bytes, base " + Short(timeline) + ")");
            }
            catch (Exception e) { Services.Log.Warn("net: offering the guest save failed: " + e.Message); }
        }

        static void ReceiveOffer(BinaryReader r)
        {
            int seat = Services.Net.Sender;
            string timeline;
            byte[] bytes;
            bool host = Services.Net.IsHost;
            try { bytes = SeatSkillSync.ForLoad(SaveWire.ReadSaveOffer(r, out timeline), "save offer"); }
            catch (Exception e)
            {
                if (host) Revive.RejectLinkOffer(seat);
                Services.Log.Warn("net: bad save offer from seat " + seat + ": " + e.Message);
                return;
            }
            OfferVerdict verdict = SaverPick.Judge(host, !Ready || s_loading || s_hostAdopt || s_disabled || s_adoptingOffer,
                host && s_guests.ReadyForSeat(seat, true, Ready),
                CoopSaves.RoomSlotLive, CoopSaves.Timeline, timeline);
            LastOfferVerdict = verdict.ToString();
            if (verdict != OfferVerdict.Accept)
            {
                OffersRejected++;
                if (host) Revive.RejectLinkOffer(seat);
                Services.Log.Info("net: save offer from seat " + seat + " not taken: " + verdict + " (base " + Short(timeline)
                    + ", room " + Short(CoopSaves.Timeline) + ")");
                return;
            }
            try
            {
                SaveGameController save = GameController.Instance.SaveGameController;
                s_adoptingOffer = true; AdoptingOfferSeat = seat;
                try
                {
                    SaveSlotBackupsManager.CreateCurrentBackup();   // time travel: the room's previous save stays a backup
                    if (!save.LoadFromBytes(bytes)) throw new InvalidDataException("LoadFromBytes returned false");
                    SeatSkillSync.Capture("seat " + seat + "'s save");   // the guest reported its tree before offering
                    save.PerformSave();   // writes the room slot; CoopSaves commits it to every seat, RoomGame uploads it
                }
                finally { s_adoptingOffer = false; AdoptingOfferSeat = 0; }
                HostSavesAdopted++;
                Services.Log.Info("net: seat " + seat + " is the saver; its save is the room's checkpoint now (room "
                    + Short(CoopSaves.Timeline) + ")");
            }
            catch (Exception e) { Disable("room save adoption", e); }
        }

        static string Short(string hash) { return string.IsNullOrEmpty(hash) ? "-" : hash.Length > 8 ? hash.Substring(0, 8) : hash; }

        internal static bool RestoreAcceptedCheckpoint()
        {
            byte[] bytes = CoopSaves.CommitBytes ?? s_hostCheckpointBytes;
            // Load only the in-memory checkpoint; the live game keeps running.
            return bytes != null && GameController.Instance.SaveGameController.LoadFromBytes(bytes);
        }

        static void Receive(BinaryReader r)
        {
            if (Services.Net.IsHost || s_loading || s_pending != null || s_disabled) return;
            HaveCheckpointPosition = SaveWire.ReadHasPosition(r);
            SavePosition p = SaveWire.ReadCoordinates(r);
            HostCheckpointPosition = new Vector3(p.X, p.Y, p.Z);
            s_pending = s_hostCheckpointBytes = SeatSkillSync.ForLoad(SaveWire.ReadSaveBytes(r), "checkpoint transfer");
            if (CoopSaves.CommitsReceived == 0) CheckpointsBeforeCommit++;
            if (CopySlotAtCheckpoint == -2) CopySlotAtCheckpoint = CoopSaves.SessionSlot;
            Ready = false; JoinedHost = false;
            Services.Log.Info("net: received checkpoint " + s_pending.Length + " bytes; waiting for gameplay load path");
        }

        // Guest joining from an empty slot (GuestJoin.cs): no game runs yet, so there is nothing to tear down. The
        // game's own load-from-title action runs and its PerformLoad reads the host's checkpoint (TitleLoadPatch);
        // FinishLoading then places the guest beside the host exactly as after a teardown reload.
        public static bool HavePendingCheckpoint { get { return !Services.Net.IsHost && s_started && s_pending != null && !s_loading && !s_disabled; } }

        public static bool BeginTitleLoad()
        {
            if (!HavePendingCheckpoint) return false;
            SavesHost.Current.HoldOwnSkillTree(s_joined);
            s_titleLoadBytes = s_pending; s_pending = null;
            s_loading = true;
            s_restartFinished = s_restoreFinished = false;
            s_joinStartedAt = Services.Clock.RealTime;
            s_loadWatch = Stopwatch.StartNew();
            return true;
        }

        public static void TitleLoadFailed(Exception e)
        {
            s_titleLoadBytes = null;
            if (s_loading) Disable("checkpoint load from the title", e);
        }

        [HarmonyPatch(typeof(SaveGameController), "PerformLoad")]
        static class TitleLoadPatch
        {
            static bool Prefix(SaveGameController __instance, ref bool __result)
            {
                byte[] bytes = s_titleLoadBytes;
                if (bytes == null) return true;
                s_titleLoadBytes = null;
                try
                {
                    if (!__instance.LoadFromBytes(bytes)) throw new InvalidDataException("LoadFromBytes returned false");
                    __instance.RestoreCheckpoint();
                    __result = true;
                    Services.Log.Info("net: loading host checkpoint from the title " + bytes.Length + " bytes into slot " + SaveSlotsManager.CurrentSlotIndex);
                }
                catch (Exception e) { __result = false; Disable("checkpoint load from the title", e); }
                return false;
            }
        }

        // T-126: the server made this guest the host (legacy arbiter) mid-session. The players still here already play
        // the co-op world (ready, no checkpoint); saving moves off the slot this player had loaded before joining.
        // Runs from Tick and before every save path, so a save in the same frame as the role change is covered.
        static void CheckPromotion()
        {
            if (!Services.Net.Enabled || !Services.Net.Connected || !s_started || s_disabled) return;
            if (!s_promotion.Check(Services.Net.IsHost)) return;
            try
            {
                int[] seats = NetPlay.RemoteSeats;
                // T-213 (10): not every remote seat plays the co-op world yet. One that joined after this player and has
                // not announced its load still waits for its first save: it stays unsent, and Tick sends it this (the
                // new) host's checkpoint. Marked sent and ready like the others, it waited for the old host for ever.
                if (s_mutatePromoteAllReady) foreach (int seat in seats) s_guests.JoinReady(seat);
                else s_guests.Promote(seats);
                Ready = true;
                Services.Log.Info("net: this guest is now the host; " + seats.Length + " remote seat(s)"
                    + (s_guests.HasUnsent ? ", at least one still waits for its first save: sending this host's checkpoint" : ", all already in the world"));
                CoopSaves.Promote();
            }
            catch (Exception e) { Services.Log.Warn("net: taking over as host: " + e); }
        }

        // Host-side saves are off: promoted without a slot that may hold the co-op world (CoopSaves.Promote).
        static bool PromotedSaveBlocked(string what)
        {
            CheckPromotion();
            if (!Services.Net.IsHost || !CoopSaves.SavesBlocked) return false;
            Services.Log.Info("net: promoted host " + what + " suppressed (no slot for the co-op save)");
            return true;
        }

        static void Disable(string item, Exception e)
        {
            // Do not invent an alternate serializer/load path if the game's API fails.
            s_disabled = true; Ready = false;
            Services.Log.Warn("net: SaveSync DISABLED (" + item + "): " + e);
        }

        static void RecordFileWrite()
        {
            if (Services.Net.Enabled && !Services.Net.IsHost) GuestSaveWrites++;
        }

        [HarmonyPatch(typeof(RestoreCheckpointController), "FinishLoading")]
        static class LoadedPatch
        {
            static void Postfix()
            {
                s_pendingSkillSave = null;
                if (!Services.Net.Connected || (Services.Net.IsHost && !s_hostAdopt) || !s_loading || s_disabled) return;
                if (Services.Net.IsHost)
                {
                    s_loading = s_restartFinished = s_restoreFinished = false; s_hostAdopt = false;
                    s_adoptedAt = Services.Clock.RealTime;
                    Services.Log.Info("net: host loaded the partner's progress in " + (s_loadWatch == null ? 0 : s_loadWatch.ElapsedMilliseconds) + " ms; sein="
                        + (Characters.Sein == null ? "null" : Characters.Sein.Position.ToString()) + " p1=" + (P2Context.P1 == null ? "null" : "set"));
                    CoopSaves.HostAdoptFinished();
                    return;
                }
                SeinCharacter own = Characters.Sein;
                if (own == null) { Disable("FinishLoading", new InvalidOperationException("no local Sein")); return; }
                GuestLoadPosition = own.Position;
                GuestLoads++;
                s_restoreFinished = true; s_loadFinishedAt = Services.Clock.RealTime;
                s_joinFrame = SavesHost.Current.NewestRemoteTick;
                Services.Log.Info("net: host checkpoint restored in " + (s_loadWatch == null ? 0 : s_loadWatch.ElapsedMilliseconds)
                    + " ms; guest=" + GuestLoadPosition + "; awaiting fresh host position");
            }
        }

        // Carry and DamageReciever subscribe in Start but remove unconditionally in OnDestroy.
        // Block same-update spawn/despawn during the handshake rather than hiding their native error.
        [HarmonyPatch(typeof(Plugin), "SpawnPuppet")]
        static class JoinSpawnGate
        {
            static bool Prefix(int seat) { return !Services.Net.Enabled || ReadyForSeat(seat); }
        }

        [HarmonyPatch(typeof(NetPlay), "LocalInGameplay")]
        static class ReloadGameplayGate
        {
            static void Postfix(ref bool __result)
            { if (Services.Net.Enabled && (!Services.Net.IsHost || s_hostAdopt) && s_loading) __result = false; }
        }

        [HarmonyPatch(typeof(GameController), "RestartingCleanupNextFrame")]
        static class NativeTeardownFinished
        {
            static void Postfix(ref IEnumerator __result)
            { if (s_loading && (!Services.Net.IsHost || s_hostAdopt)) __result = ObserveRestart(__result); }
        }

        static IEnumerator ObserveRestart(IEnumerator original)
        {
            bool complete = false;
            try
            {
                while (original.MoveNext()) yield return original.Current;
                complete = true;
            }
            finally
            {
                IDisposable disposable = original as IDisposable;
                if (disposable != null) disposable.Dispose();
                if (complete && s_loading && !s_disabled)
                {
                    s_restartFinished = true;
                    Services.Log.Info("net: native restart teardown complete in " + (Services.Clock.RealTime - s_joinStartedAt) + " s");
                }
            }
        }

        static readonly FieldInfo DelegateRegisters = AccessTools.Field(typeof(UberDelegate), "m_registers");
        [HarmonyPatch(typeof(UberDelegate), "Remove")]
        static class ExplainMissingDelegate
        {
            static void Prefix(UberDelegate __instance, Action act)
            {
                if (!s_loading || act == null || DelegateRegisters == null) return;
                List<Action> registered = DelegateRegisters.GetValue(__instance) as List<Action>;
                if (registered != null && !registered.Contains(act))
                    Services.Log.Warn("net: missing teardown subscription " + act.Method.DeclaringType.FullName
                        + "." + act.Method.Name + " target=" + act.Target + " @ " + new StackTrace(1, true));
                // Keep the original error if a different lifecycle defect remains.
            }
        }

        static bool SkillSaveReady(SeinCharacter ori)
        { return !Revive.IsDowned(ori) && ori.PlatformBehaviour.PlatformMovement.IsOnGround; }

        static void SaveSkillCheckpoint(SaveGameController save)
        {
            s_pendingSkillSave = null;
            GameController.Instance.CreateCheckpoint();
            save.PerformSave();
        }

        static void TickSkillSave()
        {
            SeinCharacter ori = s_pendingSkillSave;
            if (ori != null && ori == Characters.Sein && NetPlay.LocalInGameplay() && SkillSaveReady(ori))
                SaveSkillCheckpoint(GameController.Instance.SaveGameController);
        }

        // Test-only: buy without a save to exercise rolling back an unaccepted purchase.
        public static bool TestSkipPurchaseSave;
        [HarmonyPatch(typeof(SkillTreeManager), "OnMenuItemPressed")]
        static class SkillPurchaseSavePatch
        {
            static void Save(SaveGameController save)
            {
                if (TestSkipPurchaseSave) return;
                SeinCharacter ori = Characters.Sein;
                if (SessionRules.NoPause && !SkillSaveReady(ori)) s_pendingSkillSave = ori;
                else SaveSkillCheckpoint(save);
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo checkpoint = AccessTools.Method(typeof(GameController), "CreateCheckpoint");
                MethodInfo save = AccessTools.Method(typeof(SaveGameController), "PerformSave");
                // Run the snapshot and save together, at the same position as any guest offer.
                foreach (CodeInstruction code in instructions)
                {
                    if (code.Calls(checkpoint)) { code.opcode = OpCodes.Pop; code.operand = null; }
                    else if (code.Calls(save)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(SkillPurchaseSavePatch), "Save"); }
                    yield return code;
                }
            }
        }

        [HarmonyPatch(typeof(SaveGameController), "PerformSave")]
        static class GuestSavePatch
        {
            static bool Prefix(SaveGameController __instance)
            {
                if (!Services.Net.Enabled) return true;
                if (PromotedSaveBlocked("PerformSave")) return false;
                if (Services.Net.IsHost) return true;
                GuestSaveAttempts++;
                Services.Log.Info("net: guest PerformSave suppressed (attempt " + GuestSaveAttempts + "); offering it to the room");
                if (Services.Net.Connected) OfferSave(__instance);
                return false;
            }
        }

        [HarmonyPatch(typeof(SaveGameController), "SaveToFile")]
        static class GuestFilePatch
        {
            static bool Prefix()
            {
                if (!Services.Net.Enabled) return true;
                if (PromotedSaveBlocked("SaveToFile")) return false;
                if (Services.Net.IsHost) return true;
                GuestFileAttempts++;
                Services.Log.Info("net: guest SaveToFile suppressed (attempt " + GuestFileAttempts + ")");
                return false;
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                // Instrument the ORIGINAL body, which a skipped prefix never executes. The W test can
                // therefore detect an actual guest write if the guard ever regresses.
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SaveSync), "RecordFileWrite"));
                foreach (CodeInstruction instruction in instructions) yield return instruction;
            }
        }

        // SaveGameAction and SeinSoulFlame back up the slot BEFORE PerformSave. These bypass
        // SaveGameController entirely (SaveSlotBackupsManager.cs:86/117/123).
        [HarmonyPatch]
        static class GuestBackupPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SaveSlotBackupsManager), "CreateCurrentBackup");
                yield return AccessTools.Method(typeof(SaveSlotBackupsManager), "CreateBackup");
                // T-127: the native One Life death (SeinDamageReciever.cs:366) deletes every backup of the current slot.
                // On a guest that slot is its own save (its single-player game when it joined from one): keep them.
                yield return AccessTools.Method(typeof(SaveSlotBackupsManager), "DeleteAllBackups");
            }
            static bool Prefix()
            {
                if (!Services.Net.Enabled) return true;
                if (PromotedSaveBlocked("save backup")) return false;
                if (Services.Net.IsHost) return true;
                GuestBackupAttempts++;
                Services.Log.Info("net: guest save backup suppressed (attempt " + GuestBackupAttempts + ")");
                return false;
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SaveSync), "RecordFileWrite"));
                foreach (CodeInstruction instruction in instructions) yield return instruction;
            }
        }
    }
}
