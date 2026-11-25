using System;
using System.Collections;
using System.IO;
using OriCoop.Authority;
using UnityEngine;

namespace OriCoop
{
    public static partial class NetPhysTest
    {
        static IEnumerator UntilStone(Func<bool> ready, float seconds)
        {
            float by = Time.realtimeSinceStartup + seconds;
            while (!ready() && Time.realtimeSinceStartup < by) { KeepAlive(); yield return null; }
        }

        static IEnumerator SoloRejoin(TestHarness h, RuntimeSceneMetaData scene, PushPullBlock block, Vector3 resting, bool host)
        {
            string roomFile = Environment.GetEnvironmentVariable("ORICOOP_NET_ROOM_FILE");
            if (!NetRoute.ServerMode || string.IsNullOrEmpty(roomFile))
            { h.Check(false, "PK requires a two-device C-server test"); yield break; }
            string code = NetPlay.RoomCode, resume = roomFile + ".pk-" + code;
            string key = PushBlockSync.StateOf(block).Key;
            int guest = host ? Players.First.Seat : Players.LocalSeat;
            IAuthority authority = AuthoritySource.Current;
            Bot(false);
            CoopIdlePlace(block, resting, host);
            if (host) { if (File.Exists(resume)) File.Delete(resume); authority.ReleaseLease(key); }
            yield return Barrier(h, 320, block);
            if (!host) authority.RequestLease(key, 0f, true);
            yield return UntilStone(() => authority.OwnerOf(key) == guest && authority.LatestState(key) != null
                && authority.LatestState(key).Epoch == authority.EpochOf(key), 9f);
            StateUpdate published = authority.LatestState(key);
            bool haveOld = authority.OwnerOf(key) == guest && published != null && published.Data.Length >= 24;
            h.Check(haveOld, "PK the guest owned the real stone and published its pose before leaving");
            yield return Barrier(h, 321, block, haveOld);
            Sample peer;
            if (!s_partnerSteps.TryGetValue(321, out peer) || !peer.GrabStarted || !haveOld) yield break;

            if (!host)
            {
                int loads = SaveSync.GuestLoads;
                NetPlay.End("PK guest leaving the test room");
                yield return UntilStone(() => File.Exists(resume), 35f);
                h.Check(File.Exists(resume), "PK host completed its solo push before the guest rejoined");
                NetPlay.Begin(false, "cserver", (Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER") ?? "") + "|" + code);
                yield return UntilStone(() => NetPlay.PartnerHere && SaveSync.Ready && SaveSync.GuestLoads > loads, 50f);
                h.Check(NetPlay.PartnerHere && SaveSync.Ready && SaveSync.GuestLoads > loads, "PK guest rejoined and loaded the host's checkpoint");
                block = FindBlock(scene, true);
                yield return Barrier(h, 322, block);
                yield return UntilStone(() => AuthoritySource.Current.OwnerOf(key) == NetRoute.HostSeat &&
                    AuthoritySource.Current.LatestState(key) != null && AuthoritySource.Current.LatestState(key).Epoch == AuthoritySource.Current.EpochOf(key), 8f);
            }
            else
            {
                yield return UntilStone(() => !NetPlay.PartnerHere && authority.OwnerOf(key) == 0, 15f);
                bool alone = !NetPlay.PartnerHere && authority.OwnerOf(key) == 0;
                h.Check(alone, "PK last guest left and server released the stone to owner zero");
                // The old owner keeps publishing until its actual departure, including during the setup barrier.
                published = authority.LatestState(key);
                float oldX = BitConverter.ToSingle(published.Data, 0);
                GrabSetup grab = new GrabSetup();
                if (alone)
                {
                    yield return GrabBlock(block, grab, CoopDir);
                    if (grab.Started) { Bot(true, CoopDir); yield return GameWait(1.5f, new float[1]); }
                    Bot(false); yield return Wait(0.7f);
                }
                Vector3 solo = block.Position;
                StateUpdate atRelease = authority.LatestState(key);
                bool unpublished = grab.Started && Mathf.Abs(solo.x - oldX) > 1f && authority.OwnerOf(key) == 0 &&
                    atRelease != null && atRelease.Epoch == published.Epoch && atRelease.Ver == published.Ver;
                h.Check(unpublished, "PK native solo push changed the stone before a new publication (cached=" + F(oldX) + ", live=" + F(solo.x) + ", owner=" + authority.OwnerOf(key) + ")");
                WatchStart(block);
                File.WriteAllText(resume, "pushed");
                yield return UntilStone(() => NetPlay.PartnerHere && SaveSync.ReadyForSeat(guest), 50f);
                yield return Barrier(h, 322, block);
                float moved = Vector3.Distance(block.Position, solo), step = s_watchStep;
                s_watch = null;
                h.Check(unpublished && moved < 0.6f && step < 0.8f,
                    "PK rejoining kept the unpublished live pose (solo=" + F(solo.x) + ", after=" + F(block.Position.x) + ", largest step=" + F(step) + ")");
                authority.RequestLease(key, 0f, true);
                yield return UntilStone(() => authority.Owns(key) && authority.LatestState(key) != null &&
                    authority.LatestState(key).Epoch == authority.EpochOf(key), 8f);
                if (File.Exists(resume)) File.Delete(resume);
            }
            yield return Wait(0.4f);
            block = FindBlock(scene, true);
            yield return Barrier(h, 323, block);
            string detail;
            h.Check(block != null && s_partnerSteps.TryGetValue(323, out peer) && Agree(block, peer, out detail),
                "PK both devices see the owner's first published position after rejoin");
        }
    }
}
