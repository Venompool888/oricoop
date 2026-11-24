using System;
using System.Collections;
using System.Reflection;
using Game;
using HarmonyLib;
using OriCoop.Authority;
using OriCoop.Fx;
using UnityEngine;

namespace OriCoop
{
    public static partial class NetTestHarness
    {
        static IEnumerator ChargeFlameFx(TestHarness h)
        {
            bool host = NetPlay.IsHost;
            GameObject glow = null, burst = null;
            int glows = 0, bursts = 0;
            PartnerSkillFx.Played = delegate(int seat, SkillFxKind kind, GameObject effect) {
                if (seat != NetRoute.HostSeat) return;
                if (kind == SkillFxKind.ChargeStart) { glow = effect; glows++; }
                if (kind == SkillFxKind.ChargeBurst) { burst = effect; bursts++; }
            };
            SeinCharacter own = Own;
            bool chargeAbility = own.PlayerAbilities.ChargeFlame.HasAbility;
            bool spiritAbility = own.PlayerAbilities.SpiritFlame.HasAbility;
            float energy = own.Energy.Current;
            try
            {
                if (host)
                {
                    own.PlayerAbilities.ChargeFlame.HasAbility = own.PlayerAbilities.SpiritFlame.HasAbility = true;
                    own.Energy.SetCurrent(Mathf.Max(energy, own.Abilities.ChargeFlame.EnergyCost));
                }
                yield return EnemyBarrier(h, 6, "");
                if (host)
                {
                    SeinChargeFlameAbility skill = own.Abilities.ChargeFlame;
                    var logic = (fsm.StateMachine)AccessTools.Field(typeof(SeinChargeFlameAbility), "Logic").GetValue(skill);
                    Bot(0f, P2Input.BTN_X);
                    TestClock.Window charge = new TestClock.Window(skill.ChargeDuration + 1.5f);
                    while (charge.Open && logic.CurrentState != skill.State.Charged) yield return null;
                    h.Check(logic.CurrentState == skill.State.Charged, "E3 skill FX: host's native Charge Flame charged");
                    yield return TestClock.WaitGame(0.3f);
                    Bot(0f, 0);
                    yield return TestClock.WaitGame(0.4f);
                    h.Check(logic.CurrentState == skill.State.Start, "E3 skill FX: host released Charge Flame");
                }
                else
                {
                    bool sawGlow = false, sawBurst = false, heardBurst = false;
                    FieldInfo playing = AccessTools.Field(typeof(SoundSource), "m_soundPlayer");
                    float end = Time.realtimeSinceStartup + 12f;
                    while (Time.realtimeSinceStartup < end && !(sawGlow && sawBurst && heardBurst && (glow == null || !glow.activeInHierarchy)))
                    {
                        Puppet puppet = PuppetAt(NetRoute.HostSeat);
                        sawGlow |= glow != null && glow.activeInHierarchy && glow.name.StartsWith("chargeFlameChargeEffect", StringComparison.Ordinal)
                            && puppet != null && puppet.Companion != null && glow.transform.parent == puppet.Companion.transform;
                        if (burst != null)
                        {
                            ChargeFlameBurst ability = burst.GetComponent<ChargeFlameBurst>();
                            sawBurst |= burst.activeInHierarchy && burst.name.StartsWith("chargeFlameBurst", StringComparison.Ordinal)
                                && ability != null && !ability.enabled;
                            SoundSource source = burst.GetComponent<SoundSource>();
                            SoundPlayer player = source == null ? null : (SoundPlayer)playing.GetValue(source);
                            heardBurst |= player != null && player.Clip != null && player.IsPlaying();
                        }
                        yield return null;
                    }
                    h.Check(sawGlow && glows == 1 && (glow == null || !glow.activeInHierarchy), "E3 skill FX: native glow followed the host puppet's companion and stopped on release");
                    h.Check(sawBurst && bursts == 1, "E3 skill FX: guest spawned one native burst with damage disabled");
                    h.Check(heardBurst, "E3 skill FX: guest's burst played its native sound");
                }
                yield return EnemyBarrier(h, 7, "");
            }
            finally
            {
                Bot(0f, 0); PartnerSkillFx.Played = null;
                if (host && own != null)
                {
                    own.PlayerAbilities.ChargeFlame.HasAbility = chargeAbility;
                    own.PlayerAbilities.SpiritFlame.HasAbility = spiritAbility;
                    own.Energy.SetCurrent(energy);
                }
            }
        }
    }
}
