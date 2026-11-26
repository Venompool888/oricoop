using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Opt in with ORICOOP_PHASES=R and ORICOOP_REVIVE_REPLAY=ginso|audio.
    public static partial class NetReviveTest
    {
        static IEnumerator LoadReportedSpot(TestHarness h, Vector3 at)
        {
            // A portal temporarily removes the remote puppet and P2Context.P1 with it. The
            // local game character survives that transition.
            SeinCharacter local = Characters.Sein;
            Core.Scenes.Manager.AdditivelyLoadScenesAtPosition(at, true);
            Plugin.Teleport(local, at);
            float end = Time.realtimeSinceStartup + 20f;
            while (!Core.Scenes.Manager.SceneVisibleAtPosition(at) && Time.realtimeSinceStartup < end) yield return null;
            Plugin.Teleport(local, at);
            yield return Wait(0.4f);
            // Streaming a distant area briefly despawns and recreates the peer. Wait until both
            // seats can act there before passing the phase barrier.
            float stable = Time.realtimeSinceStartup;
            end = stable + 20f;
            while (Time.realtimeSinceStartup < end && Time.realtimeSinceStartup - stable < 1f)
            {
                if (Own == null || Puppet == null || !NetPlay.LocalInGameplay()) stable = Time.realtimeSinceStartup;
                yield return null;
            }
            h.Check(Core.Scenes.Manager.SceneVisibleAtPosition(at), "reported spot loaded " + at + " in "
                + (Core.Scenes.Manager.CurrentScene == null ? "?" : Core.Scenes.Manager.CurrentScene.Scene));
        }

        static IEnumerator GinsoSave(TestHarness h, bool host)
        {
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            try
            {
                Own.Mortality.DamageReciever.IsImmortal = true;
                yield return ReplayGinsoSave(h, host);
            }
            finally { if (Own != null) Own.Mortality.DamageReciever.IsImmortal = immortal; }
        }

        static IEnumerator ReplayGinsoSave(TestHarness h, bool host)
        {
            Vector3 at = host ? new Vector3(539.4f, 264.1f) : new Vector3(520.2f, 273.7f);
            yield return LoadReportedSpot(h, at);
            // In the reported save the successful guest soul link cost 24 of 48 health.
            Own.Mortality.Health.MaxHealth = 48;
            Own.Mortality.Health.RestoreAllHealth();
            Own.Energy.RestoreAllEnergy();
            yield return Barrier(h, 220);
            int self = Revive.SelfRevives, adopted = SaveSync.HostSavesAdopted;
            int restores = TestHarness.RestoreCheckpointCalls;
            if (host)
            {
                Plugin.Teleport(Own, at - Vector3.up * 0.5f);
                ReportedHit(1000f);
                yield return Wait(0.35f);
                Revive.Downed d = Revive.DownedOf(1);
                h.Check(d != null && Vector2.Distance(d.Marker, at) < 2f, "T248 host down at reported Ginso location");
            }
            yield return Barrier(h, 221);
            if (!host)
            {
                // Recreate the first offer's recorded zero-health value without killing the guest.
                float health = Own.Mortality.Health.Amount;
                try
                {
                    Own.Mortality.Health.SetAmount(0f);
                    GameController.Instance.CreateCheckpoint();
                    GameController.Instance.SaveGameController.PerformSave();
                }
                finally { Own.Mortality.Health.SetAmount(health); }
            }
            if (host)
            {
                float end = Time.realtimeSinceStartup + 8f;
                while (SaveSync.HostSavesAdopted <= adopted && Time.realtimeSinceStartup < end) yield return null;
                h.Check(SaveSync.HostSavesAdopted > adopted && Revive.DownedOf(1) != null && Revive.DownedOf(1).LinkHealth == 0f,
                    "T248 first guest checkpoint adopted while host stays down, no positive death-save health");
            }
            yield return Barrier(h, 222);
            if (!host)
            {
                Plugin.Teleport(Own, at - Vector3.up * 0.5f);
                CastSoulLink(Own);
                h.Check(Vector2.Distance(Own.Position, at) < 1f, "T248 guest cast at reported Ginso link");
            }
            yield return Barrier(h, 223, host ? "" : Send(Own.Mortality.Health.Amount));
            if (host)
            {
                float end = Time.realtimeSinceStartup + 8f;
                while (SaveSync.HostSavesAdopted < adopted + 2 && Time.realtimeSinceStartup < end) yield return null;
                float creator = s_partnerSteps.ContainsKey(223) ? Parse(s_partnerSteps[223].Fingerprint) : -1f;
                h.Check(SaveSync.HostSavesAdopted >= adopted + 2 && Revive.LinkSeat == 2 && Mathf.Abs(creator - 24f) < 0.01f
                    && Revive.DownedOf(1) != null && Mathf.Abs(Revive.DownedOf(1).LinkHealth - creator) < 0.01f,
                    "T248 host adopted new soul link, creator health " + creator);
                UI.Menu.ShowMenuScreen();
                yield return Wait(0.5f);
                h.Check(UI.MainMenuVisible && UI.Menu.CurrentScreen == MenuScreenManager.Screens.Pause, "T248 pause stays open while down");
                UI.Menu.HideMenuScreen(true);
                yield return Wait(0.4f);
                try
                {
                    NetTestHarness.BotButtons = P2Input.BTN_Y;
                    yield return Wait(1f);
                    Revive.Downed d = Revive.DownedOf(1);
                    h.Check(Core.Input.Bash.IsPressed && d != null && d.SelfHold > 0.5f && d.SelfHold < 2f,
                        "T248 game reads held Y and counts the first second, progress " + (d == null ? -1 : d.SelfHold));
                    TestClock.Window remaining = new TestClock.Window(3f);
                    while (Revive.DownedOf(1) != null && remaining.Open) yield return null;
                }
                finally { NetTestHarness.BotButtons = 0; }
                SeinCharacter revived = Own;
                h.Check(revived != null && Revive.SelfRevives == self + 1 && Revive.DownedOf(1) == null
                    && Vector2.Distance(revived.Position, new Vector2(520.2f, 273.7f)) < 2f
                    && Mathf.Abs(revived.Mortality.Health.Amount - creator) < 0.01f,
                    "T248 host revived at guest Ginso link " + (revived == null ? "no active Ori"
                    : revived.Position + ", health " + revived.Mortality.Health.Amount));
            }
            yield return Barrier(h, 224);
            h.Check(TestHarness.RestoreCheckpointCalls == restores, "T248 no checkpoint reload");
        }

        static IEnumerator SpectatorAudio(TestHarness h, bool host)
        {
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            try
            {
                // The recorded host was jumping over water. Keep both stationary test players alive
                // between injected hits; the local damage and its network effect still run normally.
                Own.Mortality.DamageReciever.IsImmortal = true;
                yield return CompareSpectatorAudio(h, host);
            }
            finally { Own.Mortality.DamageReciever.IsImmortal = immortal; }
        }

        static void ReportedHit(float amount)
        {
            Own.Mortality.DamageReciever.IsImmortal = false;
            try { Hurt(Own, amount); }
            finally { Own.Mortality.DamageReciever.IsImmortal = true; }
        }

        static IEnumerator CompareSpectatorAudio(TestHarness h, bool host)
        {
            // The guest's skull and the watched host were about 90 units apart.
            Vector3 hostAt = new Vector3(185.5f, -142f), guestAt = new Vector3(101.6f, -110.5f);
            yield return LoadReportedSpot(h, hostAt);
            Own.Mortality.Health.RestoreAllHealth();
            yield return Barrier(h, 230);
            for (int round = 0; round < 2; round++)
            {
                bool watching = round == 1;
                if (watching && !host)
                {
                    yield return LoadReportedSpot(h, guestAt);
                    ReportedHit(1000f);
                    yield return Wait(1.2f);
                }
                yield return Barrier(h, 231 + 2 * round);
                int played = PartnerFx.HurtPlayed;
                if (host)
                {
                    yield return Wait(0.4f);
                    Own.Mortality.Health.RestoreAllHealth();
                    int sent = PartnerFx.HurtSent;
                    ReportedHit(4f);
                    h.Check(PartnerFx.HurtSent > sent, "T252 host sent real hurt in round " + round);
                }
                else
                {
                    string label = watching ? "T252 spectating" : "T252 alive";
                    if (watching) h.Check(Revive.DownedOf(1) != null && Revive.SpectateSein == Puppet
                        && UI.Cameras.Current.Target == Puppet.Transform && Vector2.Distance(Own.Position, Puppet.Position) > 50f,
                        label + " camera watches remote host far from skull, distance " + F(Vector2.Distance(Own.Position, Puppet.Position)));
                    int listeners = 0;
                    float unityDistance = -1f;
                    foreach (AudioListener listener in Object.FindObjectsOfType<AudioListener>())
                        if (listener.enabled && listener.gameObject.activeInHierarchy)
                        { listeners++; unityDistance = Vector2.Distance(listener.transform.position, Puppet.Position); }
                    h.Check(listeners == 1 && !AudioListener.pause, label + " has one active unpaused Unity listener (to partner "
                        + F(unityDistance) + ", TestQuiet master " + AudioListener.volume + ")");
                    float gameDistance = Vector2.Distance(Core.Sound.SoundListenerPosition, Puppet.Position);
                    h.Check(gameDistance < 15f, label + " native game-effect distance to partner " + F(gameDistance));
                    string clip = "";
                    float gain = 0f, rangedGain = -1f;
                    float end = Time.realtimeSinceStartup + 5f;
                    while (Time.realtimeSinceStartup < end && clip.Length == 0)
                    {
                        if (PartnerFx.HurtPlayed > played)
                        {
                            foreach (SoundPlayer effect in SoundPlayer.All)
                            {
                                if (effect == null || effect.AttachTo != Puppet.PlatformBehaviour.transform) continue;
                                AudioSource source = effect.GetComponent<AudioSource>();
                                if (source == null || !source.isPlaying || source.clip == null) continue;
                                clip = source.clip.name;
                                effect.UpdateVolumeProperties();
                                gain = source.volume;
                                // A second playback of this clip with the game's default spatial falloff. Some
                                // hurt providers play everywhere while ambient and movement effects have a radius.
                                SoundPlayer probe = Core.Sound.Play(new SoundDescriptor(source.clip, 1f), Puppet.Position, null);
                                if (probe != null)
                                {
                                    probe.UpdateVolumeProperties();
                                    rangedGain = probe.GetComponent<AudioSource>().volume;
                                    probe.Stop();
                                }
                                break;
                            }
                        }
                        yield return null;
                    }
                    h.Check(clip.Length > 0 && gain > 0.01f, label + " remote hurt clip plays, clip=" + clip + " source gain=" + F(gain));
                    h.Check(rangedGain > 0.01f, label + " native spatial playback at watched partner has source gain " + F(rangedGain));
                }
                yield return Barrier(h, 232 + 2 * round);
                yield return Wait(1.2f);
            }
        }
    }
}
