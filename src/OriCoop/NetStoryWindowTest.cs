using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    internal static class NetStoryWindowTest
    {
        // A camera-only story with an observable native action and no effect on the active lever or pedestal.
        public sealed class CountCamera : MoveCameraHereAction
        {
            public int Calls;
            public override void Perform(IContext context) { Calls++; }
        }

        static MoonGuid observed;
        static uint token;
        static bool ended;

        [HarmonyPatch(typeof(StorySync), "ReceiveStart")]
        static class ObserveStart
        {
            static bool Prepare() { return TestHarness.Enabled; }
            static void Prefix(BinaryReader r)
            {
                if (observed == null || NetPlay.Sender != 2) return;
                long at = r.BaseStream.Position;
                try
                {
                    StoryStart start = StoryWire.ReadStart(r);
                    if (GameStoryHost.ReadGuid(start.Id) == observed) token = start.Token;
                }
                finally { r.BaseStream.Position = at; }
            }
        }

        [HarmonyPatch(typeof(StorySync), "ReceiveEnd")]
        static class ObserveEnd
        {
            static bool Prepare() { return TestHarness.Enabled; }
            static void Prefix(BinaryReader r)
            {
                if (observed == null || token == 0 || NetPlay.Sender != 2) return;
                long at = r.BaseStream.Position;
                try { if (StoryWire.ReadToken(r) == token) ended = true; }
                finally { r.BaseStream.Position = at; }
            }
        }

        static ActionSequence Build(GameObject go, int id, out CountCamera action)
        {
            ActionSequence sequence = go.AddComponent<ActionSequence>();
            sequence.MoonGuid = new MoonGuid(0x54323232, 0x57494e44, 0x53544f52, id);
            action = go.AddComponent<CountCamera>();
            sequence.Actions.Add(action);
            WaitAction tail = go.AddComponent<WaitAction>(); tail.Duration = 0.3f;
            sequence.Actions.Add(tail);
            return sequence;
        }

        // Called immediately after the real game interaction on both PCs. The guest starts the unrelated story
        // inside its window. Once the host has seen the mirror it receives the same native trigger one frame later.
        internal static IEnumerator Interaction(TestHarness h, string label, int step)
        {
            GameObject go = new GameObject("T222 " + label);
            CountCamera action;
            ActionSequence sequence = Build(go, step, out action);
            try
            {
                float limit = Time.realtimeSinceStartup + 4f;
                while (!CutsceneGuard.PedestalWindow && Time.realtimeSinceStartup < limit) yield return null;
                yield return NetCutsceneTest.Barrier(h, step);
                h.Check(CutsceneGuard.PedestalWindow, "T222 " + label + " opened its native interaction window");
                if (!NetPlay.IsHost) sequence.Perform(null);
                limit = Time.realtimeSinceStartup + 4f;
                while (action.Calls == 0 && Time.realtimeSinceStartup < limit) yield return null;
                h.Check(action.Calls == 1, "T222 " + label + " unrelated story announced and played once on each PC");
                if (NetPlay.IsHost)
                {
                    yield return null;
                    sequence.Perform(null);
                    h.Check(action.Calls == 1, "T222 " + label + " native start one frame after the mirror performs no duplicate action");
                }
                yield return NetCutsceneTest.Barrier(h, step + 1);
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        internal static IEnumerator Relay(TestHarness h, Func<int, IEnumerator> barrier)
        {
            GameObject go = new GameObject("T222 3P story");
            CountCamera action;
            ActionSequence sequence = Build(go, 3, out action);
            try
            {
                observed = sequence.MoonGuid; token = 0; ended = false;
                if (NetPlay.IsHost)
                {
                    CutsceneGuard.TestLocalOnlyWindow(12f);
                    StorySync.EnterLocalOnly(sequence);
                    try { sequence.Perform(null); }
                    finally { StorySync.ExitLocalOnly(); }
                }
                yield return barrier(0);
                if (Players.LocalSeat == 2) sequence.Perform(null);
                float limit = Time.realtimeSinceStartup + 7f;
                while (Players.LocalSeat != 2 && !ended && Time.realtimeSinceStartup < limit) yield return null;
                if (Players.LocalSeat != 2)
                    h.Check(token != 0 && ended, "T222 P" + Players.LocalSeat + " received P2's start and its matching end");
                h.Check(action.Calls == 1, "T222 P" + Players.LocalSeat + " performed the story exactly once (got " + action.Calls + ")");
                yield return barrier(1);
            }
            finally { observed = null; if (NetPlay.IsHost) CutsceneGuard.TestLocalOnlyWindow(0f); UnityEngine.Object.Destroy(go); }
        }
    }
}
