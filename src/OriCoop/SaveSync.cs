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
        static readonly SeatReadiness s_guests = new SeatReadiness();
        static byte[] s_pending;
        static byte[] s_loadingBytes;
        static byte[] s_titleLoadBytes;   // guest joining from an empty slot: PerformLoad reads these (GuestJoin.cs)
        static Stopwatch s_loadWatch;
        static float s_loadFinishedAt, s_joinStartedAt, s_adoptedAt, s_diagAt;
        static uint s_joinFrame;
        public static bool Ready;
        public static int GuestLoads, GuestSaveAttempts, GuestSaveWrites, GuestFileAttempts, GuestBackupAttempts;
        public static Vector3 HostCheckpointPosition, GuestLoadPosition;
        public static bool HaveCheckpointPosition;
        public static bool JoinedHost;
        public static float GuestJoinDistance = float.PositiveInfinity, GuestJoinElapsed = float.PositiveInfinity;
        public static Vector3 HostJoinPosition;

        public static void Init()
        {
            SavesHost.Current = new GameSavesHost();
            Services.Net.On(SaveWire.SaveMessage, Receive);
            Services.Net.On(SaveWire.LoadedMessage, delegate(BinaryReader r)
            {
                int seat = Services.Net.Sender;
                if (!Services.Net.IsHost || !s_guests.Loaded(seat)) return;
                SaveWire.ReadLoaded(r); UpdateHostReady();
                Services.Log.Info("net: guest save loaded; sending full world and progress");
                SavesHost.Current.SendWorldSnapshot(seat);
                SavesHost.Current.SendProgressSnapshot(seat);
            });
            Services.Net.SessionStarted += Start;
            Services.Net.SessionEnded += Reset;
            Services.Net.PlayerJoined += delegate(int seat) { if (Services.Net.IsHost) s_guests.Join(seat); };
            Services.Net.PlayerLeft += delegate(int seat) { if (Services.Net.IsHost) { s_guests.Leave(seat); UpdateHostReady(); } };
            CoopSaves.Init();
        }

        static void Reset()
        {
            Ready = s_started = s_loading = s_disabled = s_hostAdopt = false;
            s_guests.Clear();
            s_pending = null;
            s_loadingBytes = null;
            s_titleLoadBytes = null;
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

        internal static void RebindDevice(int previous, int primary) { s_guests.Rebind(previous, primary); }

        public static bool ReadyForSeat(int seat)
        {
            if (Services.Net.IsHost && Players.IsLocal(seat) && !Services.Net.Connected) return true;
            if (!Ready) return false;
            if (Players.IsLocal(seat)) return true;
            return s_guests.ReadyForSeat(NetPlay.DeviceSeat(seat), Services.Net.IsHost, Ready);
        }

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

        static readonly Action s_coopSavesTick = CoopSaves.Tick, s_guestJoinTick = GuestJoin.Tick;

        public static void Tick()
        {
            SubsystemGuard.Run("CoopSaves.Tick", s_coopSavesTick);
            SubsystemGuard.Run("GuestJoin.Tick", s_guestJoinTick);
            if (!Services.Net.Connected || !s_started || s_disabled) return;
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
            Services.Log.Info("net: guest join liveHost=" + HostJoinPosition + " distance=" + GuestJoinDistance
                + " elapsedAfterLoadMs=" + (GuestJoinElapsed * 1000f) + " placed=" + JoinedHost);
            Services.Net.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded);
        }

        // Host only: load the partner's progress (chosen at a co-op save divergence) through the same native
        // teardown + LoadFromBytes + RestoreCheckpoint path the guest uses; the checkpoint is sent afterwards.
        public static bool HostAdopting { get { return s_hostAdopt; } }
        // True while SaveSync itself restarts the game to load a checkpoint (guest transfer, host adopting the
        // partner's progress): RestartGame then is not a return to the title (TitleReturn, RoomGame, T-130).
        public static bool RestartingForLoad { get { return s_loading; } }

        public static void HostAdopt(byte[] bytes)
        {
            if (!Services.Net.IsHost || s_loading || s_disabled || bytes == null) return;
            s_hostAdopt = true; s_pending = bytes; ResetGuests();
        }

        // A host restore uses exactly the join transfer / native teardown / live placement path.
        // Clear readiness before sending so LoadedMessage requests a fresh world/progress snapshot.
        public static void PushCheckpointNow()
        {
            if (!Services.Net.Enabled || !Services.Net.Connected || !Services.Net.IsHost || !s_started || s_disabled) return;
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
                Services.Net.SendTo(seat, SaveWire.SaveMessage, true, delegate(BinaryWriter w)
                {
                    SaveWire.WriteSave(w, HaveCheckpointPosition, HostCheckpointPosition.x, HostCheckpointPosition.y, HostCheckpointPosition.z, bytes);
                });
                s_guests.MarkSent(seat);
                Services.Log.Info("net: sent checkpoint " + bytes.Length + " bytes in " + watch.ElapsedMilliseconds
                    + " ms; live host sample=" + HostCheckpointPosition);
            }
            catch (Exception e) { Disable("checkpoint transfer", e); }
        }

        static void Receive(BinaryReader r)
        {
            if (Services.Net.IsHost || s_loading || s_pending != null || s_disabled) return;
            HaveCheckpointPosition = SaveWire.ReadHasPosition(r);
            SavePosition p = SaveWire.ReadCoordinates(r);
            HostCheckpointPosition = new Vector3(p.X, p.Y, p.Z);
            s_pending = SaveWire.ReadSaveBytes(r);
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

        [HarmonyPatch(typeof(SaveGameController), "PerformSave")]
        static class GuestSavePatch
        {
            static bool Prefix()
            {
                if (!Services.Net.Enabled || Services.Net.IsHost) return true;
                GuestSaveAttempts++;
                Services.Log.Info("net: guest PerformSave suppressed (attempt " + GuestSaveAttempts + ")");
                return false;
            }
        }

        [HarmonyPatch(typeof(SaveGameController), "SaveToFile")]
        static class GuestFilePatch
        {
            static bool Prefix()
            {
                if (!Services.Net.Enabled || Services.Net.IsHost) return true;
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
            }
            static bool Prefix()
            {
                if (!Services.Net.Enabled || Services.Net.IsHost) return true;
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
