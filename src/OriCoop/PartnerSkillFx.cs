using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Core;
using Game;
using HarmonyLib;
using OriCoop.Fx;
using OriCoop.Seams;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OriCoop
{
    internal static class PartnerSkillFx
    {
        const byte Message = 100;
        sealed class ChargeGlow { public GameObject Effect; public SoundSource Sound; }
        static readonly Dictionary<Puppet, ChargeGlow> s_glows = new Dictionary<Puppet, ChargeGlow>();
        static readonly SkillFxReplay s_replay = new SkillFxReplay();
        internal static Action<int, SkillFxKind, GameObject> Played;
        static bool Online { get { return Services.Net.Enabled && Services.Net.Connected; } }

        internal static void Init()
        {
            Services.Net.On(Message, Receive);
            Services.Net.Forward(Message);
            Services.Net.SessionEnded += ClearGlows;
        }

        static bool Remote(Component component) { return Services.Net.Enabled && Players.IsRemote(component); }
        static void Send(SeinCharacter caster, SkillFxKind kind, Vector3 position, int variant = 0, Vector3 extra = default(Vector3))
        {
            if (!Online || caster == null) return;
            int seat = Players.SeatOf(caster);
            if (!s_replay.CanSend(seat != 0 && !Players.IsRemote(caster) && Players.IsLocal(seat))) return;
            var effect = new SkillFxEvent { Seat = (byte)seat, Kind = kind, Variant = (byte)variant,
                X = position.x, Y = position.y, Z = position.z, A = extra.x, B = extra.y, C = extra.z };
            Services.Net.Send(Message, true, w => SkillFxEvent.Write(w, effect));
        }

        static int ChargeVariant(SeinCharacter caster)
        { return caster.PlayerAbilities.ChargeFlameBlast.HasAbility ? 2 : caster.PlayerAbilities.ChargeFlameBurn.HasAbility ? 1 : 0; }

        [HarmonyPatch(typeof(SeinChargeFlameAbility), "UpdateCharacterState")]
        static class ChargePuppet { static bool Prefix(SeinChargeFlameAbility __instance) { return !Remote(__instance); } }

        [HarmonyPatch(typeof(SeinChargeFlameAbility), "UpdatePrechargingState")]
        static class ChargeStarted
        {
            static void Postfix(SeinChargeFlameAbility __instance, SeinCharacter ___m_sein, GameObject ___m_chargeFlameChargeEffect, fsm.StateMachine ___Logic)
            {
                if (Online && ___m_chargeFlameChargeEffect != null && ___Logic.CurrentState == __instance.State.Charging)
                    Send(___m_sein, SkillFxKind.ChargeStart, ___m_chargeFlameChargeEffect.transform.position, ChargeVariant(___m_sein));
            }
        }
        [HarmonyPatch(typeof(SeinChargeFlameAbility), "OnEnterStartState")]
        static class ChargeStopped
        {
            static void Prefix(SeinCharacter ___m_sein, GameObject ___m_chargeFlameChargeEffect)
            { if (___m_chargeFlameChargeEffect != null) Send(___m_sein, SkillFxKind.ChargeStop, Vector3.zero); }
        }
        [HarmonyPatch(typeof(SeinChargeFlameAbility), "ReleaseChargeBurst")]
        static class ChargeReleased
        {
            static void Prefix(SeinCharacter ___m_sein)
            { if (Online && Characters.Ori != null) Send(___m_sein, SkillFxKind.ChargeBurst, Characters.Ori.Position, ChargeVariant(___m_sein)); }
        }

        // A replay must not deal damage or replace the last real burst used by the armour-break code.
        [HarmonyPatch(typeof(ChargeFlameBurst), "OnEnable")]
        static class BurstAppearance
        {
            static bool Prefix(ChargeFlameBurst __instance)
            {
                if (!s_replay.Active) return true;
                __instance.enabled = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(SeinSpiritFlameAbility), "ThrowSpiritFlames")]
        static class PuppetSpiritFlame { static bool Prefix(SeinSpiritFlameAbility __instance) { return !Remote(__instance); } }

        [HarmonyPatch(typeof(SpiritFlameProjectile), "Start")]
        static class FlameThrown
        {
            static void Postfix(SpiritFlameProjectile __instance)
            {
                SeinCharacter caster = __instance.Sein;
                if (!Online || caster == null || Players.IsRemote(caster) || __instance.SpiritFlame == null || __instance.AttackableTargetTransform == null) return;
                SeinStandardSpiritFlameAbility flame = caster.Abilities.StandardSpiritFlame;
                int index = flame == null ? -1 : Array.IndexOf(flame.StandardSpiritFlames, __instance.SpiritFlame);
                if (index >= 0 && index < 128)
                    Send(caster, SkillFxKind.SpiritFlame, __instance.StartPosition, index | (__instance.DoImpact ? 128 : 0),
                        __instance.AttackableTargetTransform.position + (Vector3)__instance.ImpactOffset);
            }
        }

        [HarmonyPatch(typeof(SeinStomp), "DoStompBlastEffect")]
        static class StompLanded
        {
            static bool Prefix(SeinStomp __instance)
            {
                if (Remote(__instance)) return false;
                if (Online && __instance.StompLandEffect != null)
                    Send(__instance.Sein, SkillFxKind.StompLand, __instance.PlatformMovement.FeetPosition,
                        __instance.Sein.PlayerAbilities.StompUpgrade.HasAbility ? 1 : 0);
                return true;
            }
        }

        [HarmonyPatch(typeof(SeinDashAttack), "PerformDash", new Type[] { typeof(TextureAnimationWithTransitions), typeof(SoundProvider) })]
        static class DashStarted
        {
            static void Prefix(SeinDashAttack __instance, SeinCharacter ___m_sein, ref SoundProvider dashSound)
            {
                if (Remote(___m_sein)) { dashSound = null; return; }
                if (Online && dashSound != null)
                    Send(___m_sein, SkillFxKind.Dash, ___m_sein.Position,
                        dashSound == __instance.RainbowDashSound ? 2 : dashSound == __instance.ChargeDashSound ? 1 : 0);
            }
        }

        [HarmonyPatch]
        static class DashCamera
        {
            struct Speed { public CameraChaseTarget Target; public float X; }
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SeinDashAttack), "UpdateDashing");
                yield return AccessTools.Method(typeof(SeinDashAttack), "UpdateState");
                yield return AccessTools.Method(typeof(SeinDashAttack), "ReturnToNormal");
            }
            static void Prefix(SeinCharacter ___m_sein, out Speed __state)
            {
                __state = new Speed();
                if (!Remote(___m_sein)) return;
                GameplayCamera camera = UI.Cameras.Current;
                if (camera != null && camera.ChaseTarget != null)
                    __state = new Speed { Target = camera.ChaseTarget, X = camera.ChaseTarget.CameraSpeedMultiplier.x };
            }
            static Exception Finalizer(Exception __exception, Speed __state)
            {
                if (__state.Target != null) __state.Target.CameraSpeedMultiplier.x = __state.X;
                return __exception;
            }
        }

        static SeinCharacter s_dashCaster;
        [HarmonyPatch(typeof(SeinDashAttack), "AttackNearbyEnemies")]
        static class DashAttacking
        {
            static void Prefix(SeinCharacter ___m_sein, out SeinCharacter __state)
            { __state = s_dashCaster; s_dashCaster = ___m_sein; }
            static Exception Finalizer(Exception __exception, SeinCharacter __state)
            { s_dashCaster = __state; return __exception; }
        }
        [HarmonyPatch(typeof(InstantiateUtility), "Instantiate", new Type[] { typeof(Object), typeof(Vector3), typeof(Quaternion) })]
        static class DashImpact
        {
            static bool Prefix(Object original, Vector3 position)
            {
                if (!Online || s_replay.Active || s_dashCaster == null) return true;
                SeinDashAttack dash = s_dashCaster.Abilities.Dash;
                if (dash == null || original == null || original != dash.ExplosionEffect) return true;
                if (Remote(s_dashCaster)) return false;
                Send(s_dashCaster, SkillFxKind.ChargeDashImpact, position);
                return true;
            }
        }

        sealed class BashSpawns
        {
            public SeinBashAttack Bash;
            public readonly List<KeyValuePair<SkillFxKind, GameObject>> Effects = new List<KeyValuePair<SkillFxKind, GameObject>>();
        }
        static BashSpawns s_bashSpawns;
        [HarmonyPatch(typeof(SeinBashAttack), "JumpOffTarget")]
        static class BashReleased
        {
            static void Prefix(SeinBashAttack __instance, out BashSpawns __state)
            {
                __state = s_bashSpawns;
                if (Online && !s_replay.Active) s_bashSpawns = new BashSpawns { Bash = __instance };
            }
            static Exception Finalizer(Exception __exception, BashSpawns __state)
            {
                BashSpawns spawns = s_bashSpawns; s_bashSpawns = __state;
                if (__exception != null || spawns == null || spawns == __state) return __exception;
                foreach (var effect in spawns.Effects)
                {
                    if (Remote(spawns.Bash)) InstantiateUtility.Destroy(effect.Value);
                    else
                    {
                        Transform t = effect.Value.transform;
                        Send(spawns.Bash.Sein, effect.Key, t.position, 0, new Vector3(t.eulerAngles.z, t.localScale.x, 0));
                    }
                }
                return __exception;
            }
        }
        [HarmonyPatch(typeof(InstantiateUtility), "Instantiate", new Type[] { typeof(Object) })]
        static class BashEffects
        {
            static void Postfix(Object original, Object __result)
            {
                if (s_replay.Active || s_bashSpawns == null || !(__result is GameObject)) return;
                SeinBashAttack bash = s_bashSpawns.Bash;
                SkillFxKind kind;
                if (original == bash.BashFromFx) kind = SkillFxKind.BashFrom;
                else if (original == bash.BashOffFx) kind = SkillFxKind.BashOff;
                else if (original == bash.BashReleaseEffect) kind = SkillFxKind.BashRelease;
                else return;
                s_bashSpawns.Effects.Add(new KeyValuePair<SkillFxKind, GameObject>(kind, (GameObject)__result));
            }
        }

        internal static void StopGlow(Puppet puppet)
        {
            ChargeGlow glow;
            if (!s_glows.TryGetValue(puppet, out glow)) return;
            s_glows.Remove(puppet);
            if (glow.Sound != null) glow.Sound.StopAndFadeOut(0.5f);
            if (glow.Effect != null) InstantiateUtility.Destroy(glow.Effect);
        }
        static void ClearGlows() { foreach (Puppet puppet in new List<Puppet>(s_glows.Keys)) StopGlow(puppet); }
        [HarmonyPatch(typeof(RestoreCheckpointController), "RestoreCheckpoint")]
        static class CheckpointLoaded { static void Prefix() { ClearGlows(); } }

        static void Receive(BinaryReader reader)
        {
            SkillFxEvent effect = SkillFxEvent.Read(reader);
            int seat = effect.Seat;
            if (NetPlay.DeviceSeat(seat) != Services.Net.Sender || Players.IsLocal(seat)) return;
            Puppet puppet = Players.OfSein(Players.OriOf(seat));
            if (puppet == null || puppet.IsLocal) return;
            if (effect.Kind == SkillFxKind.ChargeStop) { StopGlow(puppet); return; }
            Vector3 position = puppet.Sein.Position;
            if (!effect.IsNear(position.x, position.y, position.z)) return;
            if (effect.Kind == SkillFxKind.ChargeBurst) StopGlow(puppet);
            if (!NetPlay.LocalInGameplay() || !puppet.Sein.gameObject.activeInHierarchy || Revive.IsDowned(puppet.Sein)) return;
            GameObject instance = s_replay.Run(() => Play(puppet, effect));
            if (instance != null)
            {
                CoopTrace.Event("player", "skill.fx", "who", "P" + seat, "kind", effect.Kind, "pos", instance.transform.position);
                if (Played != null) Played(seat, effect.Kind, instance);
            }
        }

        static GameObject Play(Puppet puppet, SkillFxEvent e)
        {
            Vector3 at = new Vector3(e.X, e.Y, e.Z);
            if ((e.Kind == SkillFxKind.ChargeBurst || e.Kind == SkillFxKind.BashRelease)
                && (UI.Cameras.Current == null || !UI.Cameras.Current.IsOnScreenPadded(at, 8f))) return null;
            SeinCharacter sein = puppet.Sein;
            SeinPrefabSet set = sein.Prefabs.SeinPrefabSet;
            switch (e.Kind)
            {
                case SkillFxKind.ChargeStart:
                {
                    if (puppet.Companion == null) return null;
                    StopGlow(puppet);
                    SeinChargeFlameAbility skill = sein.Abilities.ChargeFlame;
                    GameObject fx = Spawn(skill.ChargeFlameSettings.ChargeFlameChargeEffectPrefab, puppet.Companion.Position);
                    if (fx == null) return null;
                    fx.transform.parent = puppet.Companion.transform;
                    foreach (LegacyAnimator animator in fx.GetComponentsInChildren<LegacyAnimator>()) animator.Speed = 1f / skill.ChargeDuration;
                    SoundSource sound = e.Variant == 2 ? skill.ChargingSoundLevelC : e.Variant == 1 ? skill.ChargingSoundLevelB : skill.ChargingSoundLevelA;
                    s_glows[puppet] = new ChargeGlow { Effect = fx, Sound = sound };
                    if (sound != null) sound.Play();
                    return fx;
                }
                case SkillFxKind.ChargeBurst:
                {
                    var settings = sein.Abilities.ChargeFlame.ChargeFlameSettings;
                    GameObject prefab = e.Variant == 2 ? settings.ChargeFlameBurstC : e.Variant == 1 ? settings.ChargeFlameBurstB : settings.ChargeFlameBurstA;
                    return (GameObject)Object.Instantiate(prefab, at, Quaternion.identity);
                }
                case SkillFxKind.SpiritFlame:
                    return PlayFlame(puppet, e, at);
                case SkillFxKind.StompLand:
                {
                    SeinStomp stomp = set.Stomp.GetComponentInChildren<SeinStomp>(true);
                    return Spawn(e.Variant == 0 ? stomp.StompLandEffect : stomp.StompLandEffectUpgraded, at);
                }
                case SkillFxKind.Dash:
                case SkillFxKind.ChargeDashImpact:
                {
                    SeinDashAttack dash = set.Dash.GetComponentInChildren<SeinDashAttack>(true);
                    if (e.Kind == SkillFxKind.ChargeDashImpact) return Spawn(dash.ExplosionEffect, at);
                    SoundProvider sound = e.Variant == 2 ? dash.RainbowDashSound : e.Variant == 1 ? dash.ChargeDashSound : dash.DashSound;
                    if (sound != null) Sound.Play(sound.GetSound(null), at, null);
                    return null;
                }
                default:
                {
                    SeinBashAttack bash = set.Bash.GetComponentInChildren<SeinBashAttack>(true);
                    GameObject prefab = e.Kind == SkillFxKind.BashFrom ? bash.BashFromFx : e.Kind == SkillFxKind.BashOff ? bash.BashOffFx : bash.BashReleaseEffect;
                    GameObject fx = Spawn(prefab, at);
                    if (fx != null && e.Kind != SkillFxKind.BashRelease)
                    {
                        fx.transform.rotation = Quaternion.Euler(0, 0, e.A);
                        Vector3 scale = fx.transform.localScale; scale.x = e.B; fx.transform.localScale = scale;
                    }
                    return fx;
                }
            }
        }

        static GameObject Spawn(GameObject prefab, Vector3 position)
        { return prefab == null ? null : (GameObject)InstantiateUtility.Instantiate(prefab, position, Quaternion.identity); }

        static GameObject PlayFlame(Puppet puppet, SkillFxEvent e, Vector3 at)
        {
            if (puppet.Companion == null) return null;
            SpiritFlame[] flames = puppet.Sein.Abilities.StandardSpiritFlame.StandardSpiritFlames;
            int index = e.Variant & 127;
            if (index >= flames.Length) return null;
            GameObject fx = (GameObject)Object.Instantiate(flames[index].Projectile, at, Quaternion.identity);
            SpiritFlameProjectile projectile = fx.GetComponent<SpiritFlameProjectile>();
            // The line, launch sound and impact use the native projectile, with an empty target instead of a damageable object.
            Transform end = new GameObject("Spirit Flame endpoint").transform;
            end.parent = fx.transform; end.position = new Vector3(e.A, e.B, e.C);
            projectile.StartTarget = puppet.Companion.transform; projectile.AttackableTargetTransform = end;
            projectile.Sein = puppet.Sein; projectile.DoImpact = projectile.HasARealTarget = (e.Variant & 128) != 0;
            puppet.Companion.ShootAnimation.Restart();
            return fx;
        }
    }
}
