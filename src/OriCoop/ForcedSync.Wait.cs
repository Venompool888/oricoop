using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;
using Input = Core.Input;

namespace OriCoop
{
    public static partial class ForcedSync
    {
        sealed class LocalWait
        {
            public SeinCharacter Ori;
            public readonly GateWait<Vector3> State = new GateWait<Vector3>();
            public readonly HoldMeter Meter = new HoldMeter();
            public bool LeaveHeld, LeavePressed, ConsumeLeave, ForceHeld;
            public float ForceAt;
            public readonly GatePositions<Vector3> Positions = new GatePositions<Vector3>();
            public Rect? LeaveBounds;

            public void ClearPositions() { Positions.Clear(); State.ClearBeforeEntry(); }
            public void UpdateScene()
            {
                if (Positions.SetScene(SceneAt(Ori.Position))) State.ClearBeforeEntry();
            }

            public Vector3? BeforeEntry(Gate gate)
            {
                UpdateScene();
                return Positions.BeforeEntry(Now, p => !Inside(gate, p));
            }

            public void Pin()
            {
                Ori.PlatformBehaviour.PlatformMovement.Position = State.Anchor;
                Ori.Speed = Vector3.zero;
            }

            public void ResetHold() { Meter.Reset(); LeavePressed = ForceHeld = false; }
            public bool JoinStory(bool alreadyPresent)
            {
                ResetHold();
                return State.JoinStory(alreadyPresent);
            }
            public void Release(string key)
            {
                if (State.Key != key) return;
                State.Release(key); ResetHold();
            }
        }

        static readonly LocalWait[] Waits = new LocalWait[GateSeats.MaxSeat + 1];
        static bool MenuOpen { get { return UI.MainMenuVisible || CoopMenu.IsOpen || SessionRules.LocalPaused; } }
        static bool ExtraUsesKeyboard(int seat)
        {
            return Services.Net.Enabled ? OnlineLocalPlayers.InputOf(seat).Kind == InputDevice.DeviceKind.KeyboardMouse
                : seat > 2 && GameSeatView.ExtraInput(seat) == -1;
        }
        static LocalWait WaitForSeat(int seat, SeinCharacter ori)
        {
            LocalWait wait = Waits[seat];
            if (wait == null || wait.Ori != ori) Waits[seat] = wait = new LocalWait { Ori = ori };
            return wait;
        }
        static LocalWait WaitForOri(SeinCharacter ori)
        {
            if (ori != null)
                foreach (LocalWait wait in Waits) if (wait != null && wait.Ori == ori) return wait;
            return null;
        }
        static LocalWait LockedWait(SeinCharacter ori)
        {
            LocalWait wait = WaitForOri(ori);
            return wait != null && wait.State.Key != null ? wait : null;
        }
        internal static bool IsLocked(SeinCharacter ori) { return LockedWait(ori) != null; }
        static void ReleaseLocks(string key)
        {
            foreach (LocalWait wait in Waits)
                if (wait != null) wait.Release(key);
        }
        internal static void BeforeTeleport(SeinCharacter ori)
        {
            LocalWait wait = WaitForOri(ori);
            if (wait == null) return;
            wait.Release(wait.State.Key); wait.ClearPositions();
        }
        internal static void StoryStarted()
        {
            foreach (LocalWait wait in Waits)
                if (wait != null) wait.Release(wait.State.Key);
        }
        static int SceneAt(Vector3 position)
        {
            if (Core.Scenes.Manager != null)
                foreach (SceneManagerScene scene in Core.Scenes.Manager.ActiveScenes)
                    if (scene.SceneRoot != null && scene.IsVisible && !scene.MetaData.DependantScene && scene.MetaData.IsInsideSceneBounds(position))
                        return scene.SceneRoot.GetInstanceID();
            return 0;
        }
        static void UpdateWaits(int[] seats)
        {
            bool menu = MenuOpen;
            for (int seat = 1; seat <= GateSeats.MaxSeat; seat++)
            {
                SeinCharacter ori = System.Array.IndexOf(seats, seat) < 0 ? null : Players.OriOf(seat);
                if (!CanUseGate(ori)) { Waits[seat] = null; continue; }
                LocalWait wait = WaitForSeat(seat, ori);
                wait.UpdateScene();
                if (wait.LeaveBounds.HasValue && !wait.LeaveBounds.Value.Contains((Vector2)ori.Position))
                {
                    wait.LeaveBounds = null; wait.State.ExitedBounds();
                }
                string key = wait.State.Key;
                if (key == null) { wait.Positions.Sample(ori.Position, ori.IsOnGround, Now); continue; }
                Gate gate;
                if (!Gates.TryGetValue(key, out gate) || !TriggerArmed(gate)
                    || !IsHost && gate.AppliedAt >= 0f && Now - gate.AppliedAt >= GateTable.FizzleSeconds)
                {
                    wait.Release(key);
                    continue;
                }
                Vector3? target;
                if (!menu && wait.LeavePressed && !s_table.RecentlyFired(key, Now) && wait.State.TryLeave(Now, out target))
                {
                    wait.ResetHold(); wait.ConsumeLeave = true;
                    Presence(gate, seat, false);
                    if (target.HasValue) Plugin.Teleport(ori, target.Value - Vector3.up * 0.5f);
                    else wait.LeaveBounds = BoundsWithMargin(gate, PresenceMargin);
                }
                else wait.Pin();
                wait.LeavePressed = false;
            }
        }

        static void LockInside(Gate gate, LocalWait wait)
        {
            if (wait.State.Key == null && wait.State.TryEnter(gate.Key, wait.Ori.Position, wait.BeforeEntry(gate), Now)) wait.Pin();
        }

        static void TickForce(int[] seats)
        {
            bool menu = MenuOpen;
            foreach (int seat in seats)
            {
                LocalWait wait = Waits[seat];
                Gate gate;
                if (wait == null || wait.State.Key == null || !Gates.TryGetValue(wait.State.Key, out gate)
                    || s_table.RecentlyFired(gate.Key, Now)) continue;
                if (wait.Meter.Update(!menu && wait.ForceHeld, Time.deltaTime))
                {
                    Forces++; Log("force", gate.Key, seat, gate.Count, gate.Total);
                    Vector3 at = gate.Object.transform.position;
                    if (IsHost) Force(gate.Key, Players.LocalSeat, true, at);
                    else Services.Net.Send(ForceMessage, true, w => GateWire.WriteForce(w, gate.Key, at.x, at.y, at.z));
                    wait.ForceAt = Now;
                }
                else if (wait.Meter.Progress >= 1f && GateForce.Rearm(Now - wait.ForceAt, false))
                {
                    wait.Meter.Reset(); ForceRearms++;
                    Plugin.Log.LogInfo("gate force had no effect after " + GateForce.RetryAfter.ToString("0") + " s; the ring can be held again key=" + gate.Key);
                }
            }
        }

        static void FilterInput(SeinCharacter ori, bool extraKeyboard = false)
        {
            LocalWait wait = WaitForOri(ori);
            if (wait == null) return;
            // Controller B is Soul Flame in gameplay. Its keyboard binding is E, separate from Start and Bash.
            bool leave = extraKeyboard ? MoonInput.GetKey(KeyCode.E) : Input.SoulFlame.IsPressed;
            bool locked = wait.State.Key != null;
            if (locked)
            {
                wait.LeavePressed |= leave && !wait.LeaveHeld && !Input.Start.IsPressed && !MenuOpen;
                wait.ForceHeld = extraKeyboard ? MoonInput.GetKey(KeyCode.C) : Input.Bash.IsPressed;

                Input.Horizontal = Input.Vertical = 0f;
                Input.HorizontalDigiPad = Input.VerticalDigiPad = 0;
                Input.HorizontalAnalogLeft = Input.VerticalAnalogLeft = 0f;
                Silence(Input.Left); Silence(Input.Right); Silence(Input.Up); Silence(Input.Down);
                Silence(Input.Jump); Silence(Input.SpiritFlame); Silence(Input.Bash);
                Silence(Input.ChargeJump); Silence(Input.Glide); Silence(Input.Grab);
                Silence(Input.LeftShoulder); Silence(Input.RightShoulder);
            }
            if (locked || wait.ConsumeLeave) Silence(Input.SoulFlame);
            if (extraKeyboard && wait.ConsumeLeave && leave) Silence(Input.Bash);
            if (!leave) wait.ConsumeLeave = false;
            wait.LeaveHeld = leave;
        }
        static void Silence(Input.InputButtonProcessor button) { button.WasPressed = button.IsPressed = false; }
        internal static void FilterInput(int seat, SeinCharacter ori, InputState input)
        {
            if (WaitForOri(ori) == null) return;
            input.Swap();
            try { FilterInput(ori, ExtraUsesKeyboard(seat)); }
            finally { input.Swap(); }
        }
        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class PrimaryWaitInput
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix() { FilterInput(Own); }
        }
        [HarmonyPatch(typeof(SeinController), "get_InputLocked")]
        static class NoWaitingActions
        {
            static void Postfix(SeinController __instance, ref bool __result)
            { if (IsLocked(__instance.Sein)) __result = true; }
        }
        [HarmonyPatch(typeof(SeinCharacter), "set_Position")]
        static class ClearWaitSamplesOnReposition
        {
            static void Prefix(SeinCharacter __instance, Vector3 value)
            {
                LocalWait wait = WaitForOri(__instance);
                if (wait != null && value != __instance.Position) wait.ClearPositions();
            }
        }
        [HarmonyPatch]
        static class PinWaitingPlayer
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PlatformingMovement), "FixedUpdate");
                yield return AccessTools.Method(typeof(PlatformMovementRigidbodyMoonCharacterControllerPenetrate), "FixedUpdate");
            }
            static bool Prefix(PlatformMovement __instance, Rigidbody ___m_rigidbody)
            {
                if (__instance.IsSuspended) return true;
                foreach (LocalWait wait in Waits)
                {
                    if (wait == null || wait.State.Key == null || wait.Ori == null || wait.Ori.PlatformBehaviour.PlatformMovement != __instance) continue;
                    wait.Pin(); ___m_rigidbody.velocity = Vector3.zero;
                    return false;
                }
                return true;
            }
        }
        [HarmonyPatch(typeof(SceneRoot), "Unload")]
        static class UnloadWaits
        {
            static void Prefix(SceneRoot __instance)
            {
                foreach (Gate gate in Gates.Values)
                {
                    if (gate.Object == null || SceneRoot.FindFromTransform(gate.Object.transform) != __instance) continue;
                    gate.Unloading = true;
                    ReleaseLocks(gate.Key);
                }
            }
        }
        [HarmonyPatch(typeof(SeinDamageReciever), "OnRecieveDamage")]
        static class WaitingImmune
        {
            static bool Prefix(SeinDamageReciever __instance) { return !IsLocked(__instance.Sein); }
        }
    }
}
