using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    // Inject ONLY the host's native intro state through IStoryHost. Input, hide/freeze,
    // terrain resolution, host packets and guest wake placement are the production path.
    internal static class NetIntroTest
    {
        const byte Message = 55;
        sealed class IntroFixture : GameStoryHost
        {
            public bool Playing, Free;
            public override bool IntroPlaying { get { return Playing; } }
            public override bool IntroP1Free { get { return Free; } }
        }
        sealed class Sample
        {
            public Vector3 Position;
            public int VisibleMask;
            public bool Held, Neutral;
        }
        static readonly Dictionary<int, Dictionary<int, Sample>> rounds = new Dictionary<int, Dictionary<int, Sample>>();
        static int count, neutralSteps, inputSteps;
        static bool observeInput;
        [HarmonyPatch(typeof(SeinController), "HandleControllerInput")]
        static class InputObservation
        {
            static void Prefix(SeinController __instance)
            {
                if (!observeInput || P2Context.Inside || __instance.Sein != Own) return;
                if (Core.Input.Horizontal == 0 && !Core.Input.Jump.IsPressed && !Core.Input.SpiritFlame.IsPressed) neutralSteps++;
                else inputSteps++;
            }
        }
        static SeinCharacter Own { get { return P2Context.P1 ?? Characters.Sein; } }
        static bool Visible(SeinCharacter ori)
        {
            if (ori == null || !ori.Active) return false;
            foreach (Renderer r in ori.GetComponentsInChildren<Renderer>(true)) if (r.enabled && r.gameObject.activeInHierarchy) return true;
            return false;
        }
        static void Store(int round, int seat, Sample sample)
        {
            Dictionary<int, Sample> samples;
            if (!rounds.TryGetValue(round, out samples)) rounds[round] = samples = new Dictionary<int, Sample>();
            samples[seat] = sample;
        }
        static void Read(BinaryReader r)
        {
            int round = r.ReadInt32();
            Store(round, Services.Net.Sender, new Sample { Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                VisibleMask = r.ReadInt32(), Held = r.ReadBoolean(), Neutral = r.ReadBoolean() });
        }
        static IEnumerator Round(TestHarness h, int round)
        {
            Sample s = new Sample { Position = Own.Position, Held = StorySync.IntroHeld(Players.LocalSeat),
                Neutral = neutralSteps > 0 && inputSteps == 0 };
            if (Visible(Own)) s.VisibleMask |= 1 << Players.LocalSeat;
            foreach (Puppet p in Players.Remotes) if (Visible(p.Sein)) s.VisibleMask |= 1 << p.Seat;
            Store(round, Players.LocalSeat, s);
            Services.Net.Send(Message, true, delegate(BinaryWriter w) {
                w.Write(round); w.Write(s.Position.x); w.Write(s.Position.y); w.Write(s.Position.z);
                w.Write(s.VisibleMask); w.Write(s.Held); w.Write(s.Neutral);
            });
            float end = Time.realtimeSinceStartup + 30f;
            while (rounds[round].Count < count && Time.realtimeSinceStartup < end) yield return null;
            h.Check(rounds[round].Count == count, "INTRO barrier " + round + " all seats observed");
            if (rounds[round].Count != count) throw new InvalidOperationException("INTRO missing seat observation");
            for (int seat = 1; seat <= count; seat++)
            {
                Sample observed = rounds[round][seat];
                h.Note("INTRO sample " + round + " P" + seat + " pos=" + observed.Position + " visible=" + observed.VisibleMask
                    + " held=" + observed.Held + " neutral=" + observed.Neutral);
            }
        }
        static IEnumerator Wait(float seconds)
        { float end = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < end) yield return null; }
        internal static IEnumerator Run(TestHarness h, int clients)
        {
            count = clients; rounds.Clear(); Services.Net.On(Message, Read); Services.Net.Forward(Message);
            float end = Time.realtimeSinceStartup + 150f;
            while ((Own == null || Players.Remotes.Count != count - 1 || !SaveSync.Ready
                || !Own.PlatformBehaviour.PlatformMovement.IsOnGround || !Own.Controller.CanMove
                || GameController.Instance.InputLocked || StorySync.Watching) && Time.realtimeSinceStartup < end) yield return null;
            h.Check(Own != null && Players.Remotes.Count == count - 1 && SaveSync.Ready, "INTRO gameplay fixture ready");
            if (Own == null || Players.Remotes.Count != count - 1 || !SaveSync.Ready) yield break;
            h.Note("INTRO injected via IStoryHost (not a real new-game movie); spacing minimum 3 units");
            IStoryHost original = StoryHost.Current;
            IntroFixture fixture = new IntroFixture { Playing = true };
            Vector3 originalPosition = Own.Position;
            bool immortal = Own.Mortality.DamageReciever.IsImmortal;
            Own.Mortality.DamageReciever.IsImmortal = true;
            try
            {
                yield return Round(h, 0);
                if (Services.Net.IsHost) StoryHost.Current = fixture;
                NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0;
                yield return Wait(1f);
                yield return Round(h, 1);
                neutralSteps = inputSteps = 0; observeInput = true;
                NetTestHarness.BotLX = 1; NetTestHarness.BotButtons = P2Input.BTN_A | P2Input.BTN_X;
                uint firstTick = NetPlay.LocalTick;
                end = Time.realtimeSinceStartup + 20f;
                while (NetPlay.LocalTick - firstTick < 90 && Time.realtimeSinceStartup < end) yield return null;
                h.Check(NetPlay.LocalTick - firstTick >= 90, "INTRO attempted input for 90 physics steps");
                observeInput = false;
                yield return Round(h, 2);
                for (int seat = 1; seat <= count; seat++)
                {
                    Sample a = rounds[1][seat], b = rounds[2][seat];
                    if (seat == 1) h.Check(!b.Held && !b.Neutral && b.Position.x - a.Position.x > 0.5f, "INTRO P1 alone retains input");
                    else
                    {
                        h.Check(b.Held && b.Neutral && Vector3.Distance(a.Position, b.Position) < 0.05f,
                            "INTRO P" + seat + " input ignored and frozen");
                    }
                    h.Check((b.VisibleMask & ~2) == 0, "INTRO only P1 visible on P" + seat);
                }
                NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0;
                if (Services.Net.IsHost) fixture.Playing = false; // Ending movie is not yet free movement.
                yield return Wait(1f);
                yield return Round(h, 3);
                for (int seat = 2; seat <= count; seat++) h.Check(rounds[3][seat].Held, "INTRO P" + seat + " waits until host wakes");
                if (Services.Net.IsHost) fixture.Free = true;
                yield return Wait(2f);
                yield return Round(h, 4);
                int mask = (1 << (count + 1)) - 2;
                float[] offsets = { 0, 3.5f, -3.5f, 7f };
                for (int seat = 1; seat <= count; seat++)
                {
                    Sample s = rounds[4][seat];
                    h.Check(!s.Held && s.VisibleMask == mask, "INTRO wake all seats visible on P" + seat);
                    h.Check(Mathf.Abs(s.Position.x - rounds[4][1].Position.x - offsets[seat - 1]) < 0.3f,
                        "INTRO P" + seat + " fixed seat spot near P1");
                    for (int other = 1; other < seat; other++)
                        h.Check(Mathf.Abs(s.Position.x - rounds[4][other].Position.x) >= 3f,
                            "INTRO P" + seat + "/P" + other + " spacing >= 3");
                }
                h.Check(Own.PlatformBehaviour.PlatformMovement.IsOnGround, "INTRO wake spot on ground");
                yield return Round(h, 5);
                yield return Wait(1f); // Allow every reliable final observation to be acknowledged before quitting.
            }
            finally
            {
                observeInput = false;
                StoryHost.Current = original;
                NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0;
                Plugin.Teleport(Own, originalPosition - Vector3.up * 0.5f);
                Own.Mortality.DamageReciever.IsImmortal = immortal;
            }
        }
    }
}
