using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using OriCoop.Saves;
using UnityEngine;

namespace OriCoop
{
    // Online downed / revive phase "R" for run-nettest.ps1 (called from NetTestHarness after E3). Both PCs run it;
    // each checks what its own game must show. Own numbered barriers on message 65.
    //   R1 guest dies -> guest downed (no reload); host sees the guest's skull.
    //   R2 host holds Spirit Flame at the skull -> host's health drains, guest sees the progress; at 100% the guest is
    //      back with the health the host gave.
    //   R3 host dies (downed), then guest dies -> host decides "both down" -> both games reload a checkpoint.
    public static class NetReviveTest
    {
        const byte MsgBarrier = 65;
        // T-043: barriers also carry the partner's room-save counter and checkpoint fingerprint (step "R save").
        sealed class SaveReport
        {
            public int Adoptions; public string Fingerprint;
            public bool HasPosition; public Vector3 Position;
        }
        static readonly Dictionary<int, SaveReport> s_partnerSteps = new Dictionary<int, SaveReport>();
        static void OnBarrier(BinaryReader r)
        {
            int step = r.ReadInt32();
            SaveReport report = new SaveReport { Adoptions = r.ReadInt32(), Fingerprint = r.ReadString(), HasPosition = r.ReadBoolean() };
            if (report.HasPosition) report.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s_partnerSteps[step] = report;
        }

        static SeinCharacter Own { get { return P2Context.P1; } }
        static SeinCharacter Puppet { get { return P2Context.P2; } }
        static string F(float f) { return f.ToString("F2"); }

        // R4 (T-029): the guest casts a soul link (the save flame, the game's SeinSoulFlame.CheckpointMarker); the host
        // must see the same native flame appear beside the guest's Ori. The puppet's own soul-flame logic is blocked online.
        static readonly FieldInfo s_soulFlameField = typeof(SeinSoulFlame).GetField("m_soulFlame", BindingFlags.Instance | BindingFlags.NonPublic);
        static SoulFlame FlameOf(SeinCharacter s) { return s == null || s.SoulFlame == null ? null : s_soulFlameField.GetValue(s.SoulFlame) as SoulFlame; }
        static IEnumerator SoulLink(TestHarness h, bool host)
        {
            SoulFlame before = FlameOf(host ? Puppet : Own);
            yield return Barrier(h, 11);
            if (Own == null || Puppet == null) { h.Check(false, "R4 needs both Oris"); yield break; }
            if (!host)
            {
                typeof(SeinSoulFlame).GetMethod("CastSoulFlame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Own.SoulFlame, null);
                SoulFlame mine = FlameOf(Own);
                h.Check(mine != null && mine != before && Vector3.Distance(mine.Position, Own.Position) < 0.5f, "R4 guest: soul link cast at own position");
            }
            else
            {
                float end = Time.realtimeSinceStartup + 5f; SoulFlame seen = null;
                while (Time.realtimeSinceStartup < end) { seen = FlameOf(Puppet); if (seen != null && seen != before) break; yield return null; }
                bool appeared = seen != null && seen != before;
                float d = appeared ? Vector3.Distance(seen.Position, Puppet.Position) : -1f;
                h.Check(appeared && seen.gameObject.activeInHierarchy && d < 2f,
                    "R4 host: the guest's soul link flame appears on this screen beside the guest (" + (appeared ? F(d) : "none") + ")");
            }
            yield return Barrier(h, 12);
        }

        // Game seconds (T-071): every wait here is for something the game times (hurt flash, invincibility, death, the
        // revive hold and glow); real seconds failed whenever a busy PC ran the game slower than real time.
        static IEnumerator Wait(float s) { return TestClock.WaitGame(s); }

        static IEnumerator Barrier(TestHarness h, int step, string fingerprint = "", SaveReport save = null)
        {
            NetPlay.Send(MsgBarrier, true, delegate(BinaryWriter w)
            {
                w.Write(step); w.Write(SaveSync.HostSavesAdopted); w.Write(fingerprint ?? "");
                bool hasPosition = save != null && save.HasPosition;
                w.Write(hasPosition);
                if (hasPosition) { w.Write(save.Position.x); w.Write(save.Position.y); w.Write(save.Position.z); }
            });
            float end = Time.realtimeSinceStartup + 30f;
            while (NetPlay.Connected && !s_partnerSteps.ContainsKey(step) && Time.realtimeSinceStartup < end) yield return null;
            h.Check(s_partnerSteps.ContainsKey(step), "R barrier " + step + " reached by both");
        }

        static string CheckpointFingerprint()
        {
            using (MemoryStream stream = new MemoryStream())
            {
                Game.Checkpoint.SaveGameData.SaveToWriter(new BinaryWriter(stream));
                return CoopSaves.Hash(stream.ToArray());
            }
        }

        // T-043 "R save": the guest touches a save point. The host (the room's save authority) names it the saver,
        // takes its save as the room's checkpoint and commits the file to the guest's local copy. Both checkpoints are
        // then identical, and the both-down reload in R3 puts the host at the guest's save point.
        static IEnumerator GuestSave(TestHarness h, SaveReport save)
        {
            bool host = NetPlay.IsHost;
            if (host)
            {
                save.HasPosition = SaveSync.HaveCheckpointPosition;
                save.Position = SaveSync.HostCheckpointPosition;
                if (!save.HasPosition) h.Note("R save: host's previous checkpoint position unavailable before guest save");
            }
            h.Check(NetPlay.LocalInGameplay() && Own != null && Puppet != null && SaveSync.Ready,
                "R save: both Oris in gameplay and SaveSync ready before the guest's save point");
            int adopted0 = SaveSync.HostSavesAdopted, commits0 = CoopSaves.CommitsReceived, offered0 = SaveSync.GuestSavesOffered;
            yield return Barrier(h, 200);
            if (!s_partnerSteps.ContainsKey(200)) yield break;
            if (!host)
            {
                // Saved at full health: the R3 reload restores this save on both PCs, and later phases (S4) expect full
                // health as after the host's save.
                Own.Mortality.Health.RestoreAllHealth();
                save.Position = Own.Position; save.HasPosition = true;
                GameController.Instance.CreateCheckpoint();
                GameController.Instance.SaveGameController.PerformSave();
                h.Check(SaveSync.GuestSavesOffered == offered0 + 1, "R save guest: save point offered to the room ("
                    + offered0 + " -> " + SaveSync.GuestSavesOffered + ")");
            }
            float end = Time.realtimeSinceStartup + 10f;
            while ((host ? SaveSync.HostSavesAdopted == adopted0 : CoopSaves.CommitsReceived == commits0)
                && Time.realtimeSinceStartup < end) yield return null;
            if (!host)
                h.Check(CoopSaves.CommitsReceived > commits0, "R save guest: the room's new save came back as a commit ("
                    + commits0 + " -> " + CoopSaves.CommitsReceived + ")");
            string fingerprint = CheckpointFingerprint();
            yield return Barrier(h, 201, fingerprint, save);
            if (!s_partnerSteps.ContainsKey(201)) yield break;
            int before = host ? adopted0 : s_partnerSteps[200].Adoptions;
            int after = host ? SaveSync.HostSavesAdopted : s_partnerSteps[201].Adoptions;
            h.Check(after == before + 1, "R save: the host named the guest the saver once (adoptions " + before + " -> " + after
                + ", last verdict " + SaveSync.LastOfferVerdict + ")");
            h.Check(fingerprint == s_partnerSteps[201].Fingerprint,
                "R save: checkpoint fingerprints equal (local SaveGameData SHA1=" + fingerprint
                + ", partner SaveGameData SHA1=" + s_partnerSteps[201].Fingerprint + ")");
            if (host)
                h.Check(CoopSaves.Timeline.Length > 0 && CoopSaves.Timeline == CoopSaves.LastCommitHash,
                    "R save host: the guest's save is the room's committed timeline (" + CoopSaves.Timeline + ")");
        }

        static void Hurt(SeinCharacter s, float amount)
        {
            s.Mortality.DamageReciever.OnRecieveDamage(new Damage(amount, Vector2.zero, s.Position, DamageType.Spikes, null));
        }

        static void Shot(TestHarness h, string name)
        {
            try
            {
                string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(h.ResultPath)), name + ".png");
                Application.CaptureScreenshot(p);
                h.Note("screenshot " + p);
            }
            catch (System.Exception e) { h.Note("screenshot failed: " + e.Message); }
        }

        static void HoldSpiritFlame(bool on) { NetTestHarness.BotButtons = on ? P2Input.BTN_X : 0; NetTestHarness.BotLX = 0f; }

        public static IEnumerator Run(TestHarness h)
        {
            s_partnerSteps.Clear();
            NetPlay.On(MsgBarrier, OnBarrier);
            bool host = NetPlay.IsHost;
            h.Note("--- phase R: online downed / revive (" + (host ? "host" : "guest") + ")");
            if (DifficultyController.Instance.Difficulty == DifficultyMode.OneLife) { h.Note("R skipped on One Life difficulty"); yield break; }
            if (Own == null || Puppet == null) { h.Check(false, "R needs both Oris"); yield break; }
            int restores0 = TestHarness.RestoreCheckpointCalls;
            Own.Mortality.Health.RestoreAllHealth();
            yield return Barrier(h, 1);

            // R0: partner hurt visuals (P4). Guest is hurt first (host watches), then the host (guest watches).
            for (int round = 0; round < 2; round++)
            {
                bool iAmHurt = (round == 0) != host;
                int sent0 = PartnerFx.HurtSent, played0 = PartnerFx.HurtPlayed;
                PartnerFx.LastHurtStateOn = PartnerFx.LastHurtAnimOn = false;
                if (!iAmHurt) Plugin.Teleport(Own, Puppet.Position + new Vector3(1.5f, 0f, 0f)); // watch from next to it
                yield return Barrier(h, 100 + round * 2);
                yield return Wait(0.4f);
                if (iAmHurt)
                {
                    float hp0 = Own.Mortality.Health.Amount;
                    Hurt(Own, 4f);
                    h.Check(Own.Mortality.Health.Amount < hp0 && PartnerFx.HurtSent == sent0 + 1, "R0." + round + " " + (host ? "host" : "guest") + ": own hurt accepted and sent (" + F(hp0) + " -> " + F(Own.Mortality.Health.Amount) + ")");
                    yield return Wait(1.2f);
                }
                else
                {
                    bool sawRed = false, sawHurtState = false, ownRed = false;
                    Renderer spr = Puppet.PlatformBehaviour.Visuals.SpriteRenderer;
                    Renderer ownSpr = Own.PlatformBehaviour.Visuals.SpriteRenderer;
                    bool shot = false;
                    float end = Time.realtimeSinceStartup + 4f;
                    while (Time.realtimeSinceStartup < end && !(sawRed && sawHurtState && PartnerFx.HurtPlayed > played0 && shot))
                    {
                        if (spr != null && spr.sharedMaterial.GetColor(ShaderProperties.TintColor).r > 0.5f) sawRed = true;
                        if (ownSpr != null && ownSpr.sharedMaterial.GetColor(ShaderProperties.TintColor).r > 0.5f) ownRed = true;
                        if (PartnerFx.HurtPlayed > played0 && PartnerFx.LastHurtStateOn && PartnerFx.LastHurtAnimOn) sawHurtState = true;
                        if (sawRed && !shot) { Shot(h, "fx-hurt-" + (host ? "host" : "guest") + "-sees-partner"); shot = true; }
                        yield return null;
                    }
                    h.Check(PartnerFx.HurtPlayed > played0, "R0." + round + " " + (host ? "host" : "guest") + ": partner's hurt received and replayed on the puppet");
                    h.Check(sawRed, "R0." + round + " " + (host ? "host" : "guest") + ": puppet flashes red (game's FlashSprite)");
                    h.Check(sawHurtState, "R0." + round + " " + (host ? "host" : "guest") + ": puppet enters the hurt state (animation + knockback)");
                    h.Check(!ownRed, "R0." + round + " " + (host ? "host" : "guest") + ": own Ori does not flash with it");
                }
                yield return Barrier(h, 101 + round * 2);
            }
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.2f); // invincibility from R0 runs out

            // R1: the guest goes down.
            if (!host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                h.Check(Revive.DownedOf(1) != null && TestHarness.RestoreCheckpointCalls == restores0, "R1 guest: own Ori downed, no reload");
                h.Check(Revive.SpectateActive && Revive.SpectateTarget == 1 && UI.Cameras.Current != null
                    && UI.Cameras.Current.Target == Puppet.Transform && CutsceneGuard.IsPlayerCameraTarget(Puppet.Transform),
                    "R1 guest: camera follows host puppet while downed");
                h.Check(Revive.NetDownedSent >= 1, "R1 guest: 'downed' sent to the host");
                h.Check(PartnerFx.LocalDownPlayed >= 1 && PartnerFx.DownSent >= 1, "R1 guest: own death burst played and 'down' visuals sent");
            }
            yield return Barrier(h, 2);
            if (host)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(2) == null && Time.realtimeSinceStartup < end) yield return null;
                yield return Wait(0.3f);
                Revive.Downed d = Revive.DownedOf(2);
                h.Check(d != null && d.Remote, "R1 host: the guest's Ori is shown downed");
                h.Check(PartnerFx.DownPlayed >= 1, "R1 host: the guest's death burst played on the puppet");
                // (The skull label is only drawn while it is on this PC's screen; checked in R2 once the host is there.)
                if (d != null)
                {
                    int vis = 0; foreach (Renderer r in Puppet.GetComponentsInChildren<Renderer>()) if (r.enabled) vis++;
                    h.Check(vis == 0, "R1 host: the guest's puppet is hidden while downed (" + vis + " renderers on)");
                    h.Check(Vector3.Distance(d.Marker, Puppet.Position) < 3f, "R1 host: skull at the guest's spot (d=" + F(Vector3.Distance(d.Marker, Puppet.Position)) + ")");
                }
            }
            yield return Barrier(h, 3);

            // R2: the host revives the guest.
            float h0 = 0f;
            if (host && Revive.DownedOf(2) != null)
            {
                Revive.Downed d = Revive.DownedOf(2);
                Plugin.Teleport(Own, d.Marker + new Vector3(0.8f, 0f, 0f));
                yield return Wait(0.8f);
                h.Check(ReviveHud.SkullShown(2), "R2 host: skull shown for the guest once it is on screen");
                h.Check(ReviveHud.LastHintText.Length > 0, "R2 host: hold-to-revive hint shown at the guest's skull");
                h0 = Own.Mortality.Health.Amount;
                HoldSpiritFlame(true);
                yield return Wait(0.75f);
                HoldSpiritFlame(false);
                yield return Wait(0.1f);
                float p = d.Progress, drained = h0 - Own.Mortality.Health.Amount;
                h.Note("R2 host: after 0.75 s (game) hold progress " + F(p * 100f) + "%, health " + F(h0) + " -> " + F(Own.Mortality.Health.Amount));
                h.Check(p > 0.35f && p < 0.65f, "R2 host: revive progress advances while holding Spirit Flame");
                h.Check(Mathf.Abs(drained - h0 / 2f * p) < 0.6f, "R2 host: own health drains in step with progress");
            }
            yield return Barrier(h, 4);
            if (!host)
            {
                yield return Wait(0.4f);
                Revive.Downed d1 = Revive.DownedOf(1);
                h.Check(d1 != null && d1.Progress > 0.3f, "R2 guest: sees the host's revive progress on its own skull (" + (d1 != null ? F(d1.Progress * 100f) : "-") + "%)");
            }
            yield return Barrier(h, 5);
            int fx0 = PartnerFx.RevivesPlayed, fxEff0 = PartnerFx.ReviveEffects, fxSnd0 = PartnerFx.ReviveSounds;
            // Screenshot this PC's view 0.2 s into the glow, whenever it starts.
            PartnerFx.ReviveHook = s => PartnerFx.RunLater(0.2f, () => Shot(h, "fx-revive-" + (host ? "host" : "guest") + "-glow"));
            if (host && Revive.DownedOf(2) != null)
            {
                HoldSpiritFlame(true);
                TestClock.Window hold = new TestClock.Window(1.2f);
                while (Revive.DownedOf(2) != null && hold.Open) yield return null;
                HoldSpiritFlame(false);
                yield return Wait(0.15f);
                int visGlow = 0; foreach (Renderer r in Puppet.GetComponentsInChildren<Renderer>()) if (r.enabled) visGlow++;
                h.Check(PartnerFx.RevivesPlayed == fx0 + 1 && PartnerFx.ReviveEffects == fxEff0 + 1, "R2 host: revive glow played once (" + PartnerFx.LastReviveEffect + ")");
                h.Check(PartnerFx.ReviveSounds == fxSnd0 + 1, "R2 host: revive sound played (" + PartnerFx.LastReviveSound + ")");
                h.Check(visGlow == 0, "R2 host: guest still hidden during the glow (" + visGlow + " renderers on)");
                yield return Wait(0.6f);
                Shot(h, "fx-revive-host-after");
                h.Check(Revive.DownedOf(2) == null && Revive.NetRevivesSent >= 1, "R2 host: revive completes and is sent");
                h.Note("R2 host: health after revive " + F(Own.Mortality.Health.Amount) + " (started " + F(h0) + ")");
                h.Check(Mathf.Abs(Own.Mortality.Health.Amount - h0 / 2f) < 0.6f, "R2 host: paid half its health");
            }
            yield return Barrier(h, 6);
            if (!host)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(1) != null && Time.realtimeSinceStartup < end) yield return null;
                h.Check(PartnerFx.RevivesPlayed == fx0 + 1 && PartnerFx.ReviveEffects == fxEff0 + 1 && PartnerFx.ReviveSounds == fxSnd0 + 1,
                    "R2 guest: revive glow + sound played on own Ori (" + PartnerFx.LastReviveEffect + ", " + PartnerFx.LastReviveSound + ")");
                yield return Wait(PartnerFx.GlowSeconds + PartnerFx.FadeSeconds + 0.2f);
                h.Check(Revive.DownedOf(1) == null && Revive.NetRevivesApplied >= 1, "R2 guest: revived by the host");
                h.Check(!Revive.SpectateActive && UI.Cameras.Current != null && (UI.Cameras.Current.Target == Own.Transform || OriCoop.Story.StoryHost.Current.IsPluginCameraTarget(UI.Cameras.Current.Target)),
                    "R2 guest: camera returns to own Ori after revive");
                int vis = 0; foreach (Renderer r in Own.GetComponentsInChildren<Renderer>()) if (r.enabled) vis++;
                h.Note("R2 guest: health after revive " + F(Own.Mortality.Health.Amount));
                h.Check(vis > 0 && !Own.Controller.IsSuspended, "R2 guest: own Ori visible and not frozen again");
                h.Check(Own.Mortality.Health.Amount >= 1f && Own.Mortality.Health.Amount <= Own.Mortality.Health.MaxHealth / 2f + 0.6f, "R2 guest: came back with the health the host gave (" + F(Own.Mortality.Health.Amount) + ")");
                h.Check(Vector3.Distance(Own.Position, Puppet.Position) < 3f, "R2 guest: back next to the host");
            }
            yield return Barrier(h, 7);
            if (host)
            {
                yield return Wait(0.5f);
                h.Check(Revive.DownedOf(2) == null, "R2 host: guest's skull gone");
                int vis = 0; foreach (Renderer r in Puppet.GetComponentsInChildren<Renderer>()) if (r.enabled) vis++;
                h.Check(vis > 0, "R2 host: guest's puppet visible again");
            }

            // Make the saver's checkpoint tree different even when running R on its own.
            string tree = ProgressSync.OwnTreeBits.ToString("X");
            yield return Barrier(h, 202, tree);
            SaveReport peer;
            if (s_partnerSteps.TryGetValue(202, out peer) && peer.Fingerprint == tree)
            {
                if (host) Own.Level.SkillPoints += 20;
                else yield return NetTestHarness.BuyCheckpointSkill(h, "R skills setup", 1);
            }
            yield return Barrier(h, 203);
            int records0 = SeatSkillSync.RecordsReceived;
            SaveReport save = new SaveReport();
            yield return GuestSave(h, save);

            float recordBy = Time.realtimeSinceStartup + 5f;
            while ((SeatSkillSync.Current == null || SeatSkillSync.Current.Count != 2
                || !host && SeatSkillSync.RecordsReceived == records0) && Time.realtimeSinceStartup < recordBy) yield return null;
            h.Check(SeatSkillSync.Current != null && SeatSkillSync.Current.Count == 2 && (host || SeatSkillSync.RecordsReceived > records0),
                "R skills: the room record has both seats");
            ulong checkpointTree = ProgressSync.OwnTreeBits;
            int checkpointPoints = Own.Level.SkillPoints;
            yield return Barrier(h, 204);
            if (host) Own.Level.SkillPoints += 20;
            yield return NetTestHarness.BuyCheckpointSkill(h, "R skills", host ? 1 : 2);
            // An auto checkpoint on the guest after its purchase: its own native reload would now keep the skill, only
            // the room record rolls it back. (On the host a checkpoint IS the room's: it would record the purchase.)
            if (!host) GameController.Instance.CreateCheckpoint();
            int loads0 = SaveSync.GuestLoads;

            // R3: both down -> the host decides, both reload.
            Own.Mortality.Health.RestoreAllHealth();
            yield return Barrier(h, 8);
            restores0 = TestHarness.RestoreCheckpointCalls;
            int bothDown0 = Revive.NetBothDown;
            // T-144: a one-shot hazard that fired before the wipe must be re-armed by the reload on this PC.
            const int oneShotId = 0x54313434;
            WorldObjSync.TestMarkFired(oneShotId);
            if (host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                h.Check(Revive.DownedOf(1) != null, "R3 host: own Ori downed first");
            }
            yield return Barrier(h, 9);
            if (!host)
            {
                float end0 = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(2) == null && Time.realtimeSinceStartup < end0) yield return null;
                h.Check(Revive.DownedOf(2) != null, "R3 guest: sees the host's skull");
                Hurt(Own, 1000f);
            }
            // T-026: once "both down" is decided the partner's skull must go at once (0.3 s grace) and stay gone through
            // this PC's death, the reload and the partner's return; it used to stay frozen on the HUD until then.
            float bothAt = -1f, lingerFirst = -1f; int lingerFrames = 0;
            System.Action sampleSkull = () =>
            {
                float now = Time.realtimeSinceStartup;
                if (bothAt < 0f && Revive.NetBothDown > bothDown0) bothAt = now;
                if (bothAt >= 0f && now > bothAt + 0.3f && ReviveHud.SkullShown(2)) { lingerFrames++; if (lingerFirst < 0f) lingerFirst = now - bothAt; }
            };
            float endR = Time.realtimeSinceStartup + 30f;
            while (TestHarness.RestoreCheckpointCalls == restores0 && Time.realtimeSinceStartup < endR) { sampleSkull(); yield return null; }
            h.Check(TestHarness.RestoreCheckpointCalls > restores0, "R3 both down -> checkpoint reload on this PC");
            endR = Time.realtimeSinceStartup + 30f;
            while ((Characters.Sein == null || !Characters.Sein.gameObject.activeInHierarchy || GameController.Instance.InputLocked) && Time.realtimeSinceStartup < endR) { sampleSkull(); yield return null; }
            float endSettle = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < endSettle) { sampleSkull(); yield return null; }
            h.Check(bothAt >= 0f, "R3 'both down' reached this PC");
            h.Check(lingerFrames == 0, "R3 partner's skull hidden within 0.3 s of 'both down' and through the reload (T-026): "
                + lingerFrames + " frames still shown, first at +" + lingerFirst.ToString("F2") + " s");
            h.Check(!Revive.AnyDowned, "R3 nobody downed after the reload");
            h.Check(!WorldObjSync.TestHasFired(oneShotId), "R3 the reload forgets one-shot hazards that fired before it (T-144, resets=" + WorldObjSync.OneShotResets + ")");
            h.Check(Revive.NetBothDown >= 1, "R3 'both down' was decided once (host) and applied here");
            if (host)
            {
                SaveReport guestSave;
                if (!save.HasPosition)
                    h.Note("R save: reload source undecidable: host's previous checkpoint position unavailable before guest save");
                else if (!s_partnerSteps.TryGetValue(201, out guestSave) || !guestSave.HasPosition)
                    h.Note("R save: reload source undecidable: guest save position not received");
                else
                {
                    float previousDistance = Vector3.Distance(guestSave.Position, save.Position);
                    if (previousDistance <= 3f)
                        h.Note("R save: reload source undecidable: guest save position was " + F(previousDistance)
                            + " units from host's previous checkpoint (<= 3 units)");
                    else
                    {
                        float distance = Characters.Sein == null ? float.PositiveInfinity
                            : Vector3.Distance(Characters.Sein.Position, guestSave.Position);
                        h.Check(distance <= 3f,
                            "R save: after both-down reload the host respawns at the guest's save (distance to guest save position="
                            + F(distance) + " <= 3 units; was " + F(previousDistance) + " to the host's previous checkpoint)");
                    }
                }
            }
            yield return NetTestHarness.CheckCheckpointSkills(h, "R skills: back to the checkpoint tree and points",
                checkpointTree, checkpointPoints, loads0);
            yield return Barrier(h, 10);
            yield return SoulLink(h, host);
            HoldSpiritFlame(false);
            PartnerFx.ReviveHook = null;
        }
    }
}
