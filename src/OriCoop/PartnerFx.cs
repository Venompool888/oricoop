using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using UnityEngine;

using OriCoop.Seams;
using OriCoop.Rules;

namespace OriCoop
{
    // Partner visuals (playtest 2026-11-16, P4):
    // - Online the partner's Ori is a puppet that never takes damage here (NetPlay.PuppetNoDamage), so its hurt flash,
    //   hurt animation, knockback and death burst never showed. The owner's game now sends message 66 when its own Ori
    //   is hurt or goes down, and this PC replays the game's own hurt / death visuals on the puppet
    //   (SeinDamageReciever: sound, HurtEffect, red FlashSprite, HurtAnimation + knockback, DeathEffectProvider).
    // - A downed Ori (local or online) skips the game's OnKill, so its death burst is played here too.
    // - Revive: the game's own checkpoint-respawn glow (SeinHealthController.RespawnEffect) plus a native chime, the
    //   revived Ori hidden for GlowSeconds and then faded in from white. Both PCs play it from the revive messages they
    //   already get (no extra message).
    //   66 PuppetFx  owner -> others   { byte seat, byte kind (0 hurt, 1 down), byte damageType, byte flags (1 badly hurt), float drop, float forceX }  reliable
    public static class PartnerFx
    {
        static IRulesHost Host { get { return SessionRules.Host; } }
        public const byte MsgPuppetFx = 66;
        public const byte MsgSoulLink = 80;   // docs/net-msg-ids.md (T-029)
        public const byte KindHurt = 0, KindDown = 1;
        public const float GlowSeconds = 0.55f, FadeSeconds = 0.4f;

        // counters for tests / logs
        public static int HurtSent, DownSent, HurtReceived, DownReceived, HurtPlayed, DownPlayed, LocalDownPlayed;
        public static int RevivesPlayed, ReviveEffects, ReviveSounds;
        public static string LastReviveEffect = "", LastReviveSound = "";
        public static float LastHurtPlayedAt = -1f, LastReviveAt = -1f;
        public static bool LastHurtStateOn, LastHurtAnimOn;
        public static int SoulLinksSent, SoulLinksShown;

        static bool s_registered;

        static void Log(string msg)
        {
            Services.Log.Info("[fx " + DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.time.ToString("F2") + "] " + msg);
        }

        public static void EnsureRegistered()
        {
            if (s_registered) return;
            s_registered = true;
            Services.Net.On(MsgPuppetFx, OnNetPuppetFx);
            Services.Net.On(MsgSoulLink, OnNetSoulLink);
        }

        // ---- soul link (T-029) -----------------------------------------------------------------------------------------
        // Saving with a soul link places the game's own flame (SeinSoulFlame.CheckpointMarker, with its appear flash). The
        // partner's game blocks the puppet's soul-flame logic online, so it never placed one: send where ours appeared and
        // that game spawns the same native flame on our puppet (replacing the previous one; it goes with the puppet).
        [HarmonyPatch(typeof(SeinSoulFlame), "CastSoulFlame")]
        static class SoulLinkWatch
        {
            static void Postfix(SeinSoulFlame __instance, SoulFlame ___m_soulFlame)
            {
                try
                {
                    if (!Online || Host.P1 == null || Host.P1.SoulFlame != __instance || ___m_soulFlame == null) return;
                    int seat = Host.LocalSeat; Vector3 at = ___m_soulFlame.Position;
                    Services.Net.Send(MsgSoulLink, true, w => { w.Write((byte)seat); w.Write(at.x); w.Write(at.y); w.Write(at.z); });
                    SoulLinksSent++;
                    Log("soul link at " + at + " sent to the partner");
                }
                catch (Exception e) { Services.Log.Warn("soul link send: " + e.Message); }
            }
        }

        static void OnNetSoulLink(BinaryReader r)
        {
            int seat = r.ReadByte();
            Vector3 at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            SeinCharacter s = Revive.SeinAt(seat);
            if (s == null || s == Host.P1 || s.SoulFlame == null) { Log("got soul link for seat " + seat + " but no puppet"); return; }
            s.SoulFlame.DestroySoulFlame();
            s.SoulFlame.SpawnSoulFlame(at);
            SoulLinksShown++;
            Log("partner P" + seat + " soul link flame shown at " + at);
            Host.Trace("player", "soullink.fx", "who", "P" + seat, "pos", at);
        }

        static bool Online { get { return Services.Net.Enabled && Services.Net.Connected && Host.P1 != null; } }

        // ---- owner side: our Ori was hurt / went down ---------------------------------------------------------------
        struct Before { public SeinCharacter Sein; public float Health; public bool Downed; public Vector3 Pos; public bool Badly; }

        // The game's own "badly hurt" sound choice: the damage after difficulty scaling vs BadlyHurtAmount.
        static bool BadlyHurt(SeinDamageReciever dr, Damage damage)
        {
            if (damage == null) return false;
            float a = Mathf.Round(damage.Amount);
            if (a < 100f && DifficultyController.Instance != null)
            {
                DifficultyMode m = DifficultyController.Instance.Difficulty;
                if (m == DifficultyMode.Easy)
                {
                    if (damage.Type != DamageType.Lava && damage.Type != DamageType.Spikes) a /= 2f;
                    else { int n = Mathf.RoundToInt(a / 4f); if (n > 3) n = Mathf.FloorToInt((n - 3) * 0.5f) + 3; a = n * 4; }
                }
                else if (m == DifficultyMode.Hard) { a *= 2f; if (a < 8f) a = 8f; }
            }
            return a >= dr.BadlyHurtAmount;
        }

        [HarmonyPatch(typeof(SeinDamageReciever), "OnRecieveDamage")]
        static class HurtWatch
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix(SeinDamageReciever __instance, Damage damage, out Before __state)
            {
                __state = new Before();
                try
                {
                    SeinCharacter s = __instance.Sein;
                    if (s == null) return;
                    __state.Sein = s;
                    __state.Health = s.Mortality.Health.Amount;
                    __state.Downed = Revive.IsDowned(s);
                    __state.Pos = s.Position;
                    __state.Badly = BadlyHurt(__instance, damage);
                }
                catch (Exception) { }
            }

            static void Postfix(SeinDamageReciever __instance, Damage damage, Before __state)
            {
                try { After(__instance, damage, __state); }
                catch (Exception e) { Services.Log.Warn("[fx] hurt watch failed: " + e.Message); }
            }
        }

        static void After(SeinDamageReciever dr, Damage damage, Before b)
        {
            SeinCharacter s = b.Sein;
            if (s == null || b.Downed) return;
            float now = s.Mortality.Health.Amount;
            bool downNow = Revive.IsDowned(s);
            float drop = b.Health - now;
            if (!downNow && drop <= 0f) return;      // not accepted (invincible, immortal, puppet, ...)
            if (Services.Net.Enabled && Host.IsPuppet(s)) return; // an online puppet: its owner's game reports it
            bool isOwn = s == Host.P1;
            DamageType type = damage != null ? damage.Type : DamageType.Spikes;
            float forceX = damage != null ? damage.Force.x : 0f;

            if (downNow)
            {
                // The game's OnKill was skipped (downed instead): play its death burst ourselves.
                PlayDeath(s, b.Pos, type, Mathf.Max(drop, 1f));
                LocalDownPlayed++;
                Log("P" + Host.SeatOf(s) + " down: death burst played locally at " + b.Pos + " (" + type + ")");
                Host.Trace("player", "down.fx", "who", "P" + Host.SeatOf(s), "local", 1, "pos", b.Pos, "type", type);
            }
            if (!isOwn || !Online) return;
            EnsureRegistered();
            int seat = Host.LocalSeat;
            if (now <= 0f) downNow = true;           // a real death (both down): the game shows ours, the partner sees the burst
            byte kind = downNow ? KindDown : KindHurt;
            float d = drop;
            Services.Net.Send(MsgPuppetFx, true, w => { w.Write((byte)seat); w.Write(kind); w.Write((byte)type); w.Write((byte)(b.Badly ? 1 : 0)); w.Write(d); w.Write(forceX); });
            if (downNow) DownSent++; else HurtSent++;
            Log("sent " + (downNow ? "down" : "hurt") + " seat=" + seat + " drop=" + d.ToString("F1") + " type=" + type + " forceX=" + forceX.ToString("F1"));
            Host.Trace("player", downNow ? "down.send" : "hurt.send", "who", "P" + seat, "drop", d, "type", type, "badly", b.Badly, "forceX", forceX);
        }

        // ---- other side: replay on the puppet ------------------------------------------------------------------------
        static void OnNetPuppetFx(BinaryReader r)
        {
            int seat = r.ReadByte();
            byte kind = r.ReadByte();
            DamageType type = (DamageType)r.ReadByte();
            bool badly = (r.ReadByte() & 1) != 0;
            float drop = r.ReadSingle();
            float forceX = r.ReadSingle();
            if (kind == KindDown) DownReceived++; else HurtReceived++;
            SeinCharacter s = Revive.SeinAt(seat);
            if (s == null || s == Host.P1) { Log("got " + kind + " for seat " + seat + " but no puppet"); return; }
            try
            {
                if (kind == KindDown) { PlayDeath(s, s.Position, type, Mathf.Max(drop, 1f)); DownPlayed++; }
                else PlayHurt(s, type, badly, drop, forceX);
                Log("partner P" + seat + " " + (kind == KindDown ? "down: death burst" : "hurt: flash + animation") + " played on the puppet (drop=" + drop.ToString("F1") + ", " + type + ")");
                if (kind == KindDown) Host.Trace("player", "down.fx", "who", "P" + seat, "local", 0, "pos", s.Position, "type", type);
                else Host.Trace("player", "hurt.anim", "who", "P" + seat, "played", 1, "state", LastHurtStateOn, "anim", LastHurtAnimOn, "drop", drop, "type", type);
            }
            catch (Exception e) { Services.Log.Warn("[fx] puppet " + kind + " failed: " + e); }
        }

        static readonly System.Reflection.FieldInfo s_hurtTime = AccessTools.Field(typeof(SeinDamageReciever), "m_hurtTimeRemaining");

        // The cosmetic half of SeinDamageReciever.OnRecieveDamage, on a puppet.
        public static void PlayHurt(SeinCharacter s, DamageType type, bool badly, float drop, float forceX)
        {
            SeinDamageReciever dr = s.Mortality.DamageReciever;
            if (dr == null || !s.gameObject.activeInHierarchy || Revive.IsDowned(s)) return;
            Damage dmg = new Damage(drop, new Vector2(forceX, 0f), s.Position, type, null);
            SeparateMaterials();
            PlaySound(badly ? dr.SeinBadlyHurtSound : dr.SeinHurtSound, dmg, s);
            try { Attacking.DamageDisplayText.Create(new Damage(Mathf.Max(1f, Mathf.Round(drop / 4f)), Vector2.zero, s.Position, type, null), s.transform); } catch (Exception) { }
            dr.StartCoroutine(dr.FlashSprite());
            if (type != DamageType.Drowning)
            {
                PlatformMovement pm = dr.PlatformMovement;
                if (dr.HurtEffect != null)
                {
                    GameObject fx = (GameObject)InstantiateUtility.Instantiate(dr.HurtEffect);
                    fx.transform.position = dr.transform.position;
                    Vector3 v = (Vector3)pm.LocalSpeed.normalized + new Vector3(forceX, 0f, 0f).normalized;
                    fx.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(v.y, v.x) * 57.29578f);
                }
                dr.Active = true;
                pm.LocalSpeed = forceX > 0f ? dr.HurtSpeed : new Vector2(-dr.HurtSpeed.x, dr.HurtSpeed.y);
                if (s_hurtTime != null) s_hurtTime.SetValue(dr, dr.HurtDuration);
                var st = s.PlatformBehaviour.Visuals.Animation.Play(dr.HurtAnimation, 140, dr.ShouldHurtAnimationKeepPlaying);
                LastHurtStateOn = dr.Active;
                LastHurtAnimOn = st != null && dr.HurtAnimation != null;
            }
            HurtPlayed++;
            LastHurtPlayedAt = Time.time;
        }

        // The game's death burst (SeinDamageReciever.InstantiateDeathEffect) and death sound, at `at`.
        public static void PlayDeath(SeinCharacter s, Vector3 at, DamageType type, float amount)
        {
            SeinDamageReciever dr = s.Mortality.DamageReciever;
            if (dr == null) return;
            Damage dmg = new Damage(amount, Vector2.zero, at, type, null);
            PlaySound(dr.SeinDeathSound, dmg, null, at);
            if (dr.DeathEffectProvider == null) return;
            GameObject prefab = dr.DeathEffectProvider.Prefab(new DamageContext(dmg));
            if (prefab == null) return;
            GameObject go = (GameObject)InstantiateUtility.Instantiate(prefab);
            try { dmg.DealToComponents(go); } catch (Exception) { }
            Transform mirror = s.PlatformBehaviour.Visuals.SpriteMirror.transform;
            go.transform.position = new Vector3(at.x, at.y, mirror.position.z);
            go.transform.localScale = mirror.localScale;
            go.transform.localRotation = mirror.localRotation;
        }

        static void PlaySound(DamageBasedSoundProvider p, Damage dmg, SeinCharacter attach, Vector3? at = null)
        {
            if (p == null) return;
            SoundDescriptor sd = p.GetSoundForDamage(dmg);
            if (sd == null) return;
            Vector3 pos = at ?? (attach != null ? attach.Position : Vector3.zero);
            SoundPlayer sp = Core.Sound.Play(sd, pos, null);
            if (sp != null && attach != null) sp.AttachTo = attach.PlatformBehaviour.transform;
        }

        // ---- revive --------------------------------------------------------------------------------------------------
        // Preferred native chimes, by SoundProvider asset name (first one found wins); fallback: soul-link "ready".
        // Picked by listening to the game's sound providers: Ori's own respawn sound first.
        public static readonly string[] ReviveSoundNames = { "seinRespawnSoundProvider", "seinSoulFlameReadySoundProvider", "checkpointSpawnSoundProvider" };
        static SoundProvider s_reviveSound;

        static SoundProvider ReviveSound(SeinCharacter s)
        {
            if (s_reviveSound != null) return s_reviveSound;
            if (ReviveSoundNames.Length > 0)
            {
                try
                {
                    SoundProvider[] all = Resources.FindObjectsOfTypeAll<SoundProvider>();
                    foreach (string n in ReviveSoundNames)
                        foreach (SoundProvider p in all)
                            if (p != null && p.name == n) { s_reviveSound = p; return p; }
                }
                catch (Exception) { }
            }
            SeinCharacter src = Host.P1 ?? s;
            // Not cached: a named provider may only be loaded later.
            return src != null && src.SoulFlame != null ? src.SoulFlame.SoulFlameReadySoundProvider : null;
        }

        // Glow -> reappear on the revived Ori `s`, centred at `at` (where it comes back).
        public static void PlayRevive(SeinCharacter s, Vector3 at)
        {
            if (s == null) return;
            try
            {
                RevivesPlayed++;
                LastReviveAt = Time.time;
                GameObject glowPrefab = s.Mortality.Health.RespawnEffect;
                if (glowPrefab == null && Host.P1 != null) glowPrefab = Host.P1.Mortality.Health.RespawnEffect;
                if (glowPrefab != null)
                {
                    InstantiateUtility.Instantiate(glowPrefab, at, Quaternion.identity);
                    ReviveEffects++;
                    LastReviveEffect = glowPrefab.name;
                }
                SoundProvider sp = ReviveSound(s);
                if (sp != null)
                {
                    SoundDescriptor sd = sp.GetSound(null);
                    if (sd != null) { Core.Sound.Play(sd, at, null); ReviveSounds++; LastReviveSound = sp.name; }
                }
                Runner.Get().StartCoroutine(Reappear(s));
                if (ReviveHook != null) { try { ReviveHook(s); } catch (Exception) { } }
                Host.Trace("player", "revive.fx", "who", "P" + Host.SeatOf(s), "pos", at, "glow", glowPrefab != null ? glowPrefab.name : "none", "sound", sp != null ? sp.name : "none");
                Log("revive P" + Host.SeatOf(s) + " at " + at + ": glow=" + (glowPrefab != null ? glowPrefab.name : "none") + " sound=" + (sp != null ? sp.name : "none"));
            }
            catch (Exception e) { Services.Log.Warn("[fx] revive effect failed: " + e); }
        }

        // One running revive sequence per Ori; a newer one takes over the renderers the older one hid.
        sealed class FxState { public int Token; public List<Renderer> Hidden = new List<Renderer>(); }
        static readonly Dictionary<int, FxState> s_fx = new Dictionary<int, FxState>();

        static IEnumerator Reappear(SeinCharacter s)
        {
            int id = s.GetInstanceID();
            FxState st;
            if (!s_fx.TryGetValue(id, out st)) { st = new FxState(); s_fx[id] = st; }
            int token = ++st.Token;
            SeparateMaterials();
            foreach (Renderer r in s.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; st.Hidden.Add(r); }
            SeinDamageReciever dr = s.Mortality.DamageReciever;
            float t = 0f;
            while (t < GlowSeconds)
            {
                yield return null; t += Time.deltaTime;
                if (s == null) { s_fx.Remove(id); yield break; }
                if (st.Token != token) yield break;          // superseded: the newer sequence owns st.Hidden
                if (Revive.IsDowned(s)) { HandBackToRevive(s, st); yield break; }
            }
            foreach (Renderer r in st.Hidden) if (r != null) r.enabled = true;
            st.Hidden.Clear();
            for (t = 0f; t < FadeSeconds; t += Time.deltaTime)
            {
                if (s == null || dr == null || st.Token != token || Revive.IsDowned(s)) break;
                float a = 1f - t / FadeSeconds;
                dr.SpriteMaterialTintColor(new Color(a, a, a, a));
                yield return null;
            }
            if (dr != null && (st.Token == token)) dr.SpriteMaterialTintColor(new Color(0f, 0f, 0f, 0f));
            if (st.Token == token) s_fx.Remove(id);
        }

        // Downed again during the glow: Revive now hides this Ori; give it the renderers we hid so its restore shows them.
        static void HandBackToRevive(SeinCharacter s, FxState st)
        {
            Revive.Downed d = Revive.DownedAtSeat(Host.SeatOf(s));
            if (d != null) { foreach (Renderer r in st.Hidden) if (r != null && !d.Hidden.Contains(r)) d.Hidden.Add(r); }
            else foreach (Renderer r in st.Hidden) if (r != null) r.enabled = true;
            st.Hidden.Clear();
            s_fx.Remove(s.GetInstanceID());
        }

        // The game tints Sprite.sharedMaterial (FlashSprite, our fade). A puppet without a skin shares the local Ori's
        // material, so a tint on one would show on both: give such puppets their own copy first.
        static void SeparateMaterials()
        {
            try
            {
                SeinCharacter p1 = Host.P1;
                Renderer mine = p1 != null ? p1.PlatformBehaviour.Visuals.SpriteRenderer : null;
                if (mine == null) return;
                foreach (SeinCharacter remote in Host.RemoteCharacters)
                {
                    if (remote == null) continue;
                    Renderer r = remote.PlatformBehaviour.Visuals.SpriteRenderer;
                    if (r != null && r.sharedMaterial == mine.sharedMaterial) { Material own = r.material; own.name += " (puppet fx)"; MaterialsSeparated++; }
                }
            }
            catch (Exception) { }
        }
        public static int MaterialsSeparated;

        public static Action<SeinCharacter> ReviveHook;   // tests

        public static void RunLater(float seconds, Action a) { Runner.Get().StartCoroutine(Later(seconds, a)); }
        static IEnumerator Later(float seconds, Action a)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
            try { a(); } catch (Exception) { }
        }

        class Runner : MonoBehaviour
        {
            static Runner s_inst;
            public static Runner Get()
            {
                if (s_inst != null) return s_inst;
                GameObject go = new GameObject("OriCoop_PartnerFx");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                s_inst = go.AddComponent<Runner>();
                return s_inst;
            }
        }
    }
}
