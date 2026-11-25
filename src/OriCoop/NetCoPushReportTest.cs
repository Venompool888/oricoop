using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    public static partial class NetPhysTest
    {
        static IEnumerator PushReports(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion rotation, bool host)
        {
            yield return CoopReset(h, 410, block, resting, rotation, host);
            PushBlockSync.St stone = PushBlockSync.StateOf(block);
            int guest = host ? NetPlay.RemoteSeats[0] : Players.LocalSeat;
            byte[] delayed = null;
            bool stopped = false;
            if (!host)
            {
                PushBlockSync.TestObserveReport = delegate(int id, Action<BinaryWriter> encode) {
                    if (id != stone.Id) return;
                    MemoryStream bytes = new MemoryStream(); encode(new BinaryWriter(bytes));
                    byte[] report = bytes.ToArray();
                    if ((sbyte)report[4] != 0) delayed = report;
                    else if (delayed != null) stopped = true;
                };
                try
                {
                    GrabSetup grab = new GrabSetup();
                    yield return GrabBlock(block, grab, CoopDir);
                    Bot(true, CoopDir);
                    float pushBy = Time.realtimeSinceStartup + 2f;
                    while (delayed == null && Time.realtimeSinceStartup < pushBy) yield return null;
                    NetTestHarness.BotLX = 0; // Keep holding the grab trigger; the native state goes from Push to Idle.
                    float stopBy = Time.realtimeSinceStartup + 2f;
                    while (!stopped && Time.realtimeSinceStartup < stopBy) yield return null;
                    h.Check(grab.Started && Own.Abilities.GrabBlock.IsGrabbing && delayed != null && stopped,
                        "PR native guest sends push then stop while it keeps holding the stone");
                }
                finally { PushBlockSync.TestObserveReport = null; }
            }
            yield return Barrier(h, 411, block, stopped);
            if (host && Puppet != null)
                AccessTools.Method(typeof(SeinGrabBlock), "ReleaseBlock").Invoke(Puppet.Abilities.GrabBlock, null);
            int heard = PushBlockSync.CoPushHeard;
            yield return Barrier(h, 416, block);
            if (!host && delayed != null) NetPlay.Send(PushBlockSync.MsgCoPush, false, w => w.Write(delayed));
            if (host)
            {
                float until = Time.realtimeSinceStartup + 3f;
                while (PushBlockSync.CoPushHeard == heard && Time.realtimeSinceStartup < until) yield return null;
                h.Check(PushBlockSync.CoPushHeard > heard, "PR the delayed push reaches the receiver after the stop");
                h.Check(PushBlockSync.RecentSeatPush(stone, guest, Time.realtimeSinceStartup) == 0,
                    "PR a delayed push after a stop leaves the puppet's re-grab target clear");
                yield return Wait(0.12f);
                h.Check(Puppet != null && !Puppet.Abilities.GrabBlock.IsGrabbing,
                    "PR the puppet stays released when an older push follows the native stop");
            }
            yield return Barrier(h, 412, block);
            Bot(false);
            if (!NetPlay.MultiSeatSupported) yield break;
            yield return CoopReset(h, 417, block, resting, rotation, host);
            const int extraSeat = 3;
            if (!host) h.Check(OnlineLocalPlayers.Add(new InputDevice(InputDevice.DeviceKind.Pad, 3)),
                "PR the guest adds a second local Ori");
            float spawnBy = Time.realtimeSinceStartup + 8f;
            while (Players.OriOf(extraSeat) == null && Time.realtimeSinceStartup < spawnBy) yield return null;
            SeinCharacter extra = Players.OriOf(extraSeat);
            bool oldBot = P2Input.BotActive;
            yield return Barrier(h, 413, block, extra != null);
            try
            {
                Sample peer;
                if (extra == null || !s_partnerSteps.TryGetValue(413, out peer) || !peer.GrabStarted)
                { h.Check(false, "PR both PCs have the guest's secondary Ori"); yield break; }
                if (!host)
                {
                    P2Input.BotActive = true; P2Input.BotRT = false; P2Input.BotLX = 0;
                    Bounds bounds = BlockBounds(block);
                    float half = extra.PlatformBehaviour.PlatformMovement.CapsuleCollider.bounds.extents.x;
                    float x = CoopDir > 0 ? bounds.min.x - half - 0.02f : bounds.max.x + half + 0.02f;
                    Plugin.Teleport(extra, new Vector3(x, bounds.min.y + 0.09f, block.Position.z));
                    extra.PlatformBehaviour.Visuals.SpriteMirror.FaceLeft = CoopDir < 0;
                    P2Input.BotLX = CoopDir;
                    float grabBy = Time.realtimeSinceStartup + 5f;
                    while (!extra.Abilities.GrabBlock.InRange && Time.realtimeSinceStartup < grabBy) yield return null;
                    P2Input.BotRT = true;
                }
                float poseBy = Time.realtimeSinceStartup + 3f;
                bool pushing = false;
                while (Time.realtimeSinceStartup < poseBy)
                {
                    pushing = host ? PushBlockSync.RecentSeatPush(stone, extraSeat, Time.realtimeSinceStartup) != 0
                        : extra.Abilities.GrabBlock.IsGrabbing && PushBlockSync.PushDir(extra.PlatformBehaviour.PlatformMovement) != 0;
                    if (pushing) break;
                    yield return null;
                }
                if (!host) h.Check(pushing, "PR the secondary Ori pushes the real stone on its own PC");
                yield return Barrier(h, 414, block, pushing);
                if (host)
                {
                    h.Check(pushing, "PR the remote stone records the pushing secondary Ori's own seat");
                    AccessTools.Method(typeof(SeinGrabBlock), "ReleaseBlock").Invoke(extra.Abilities.GrabBlock, null);
                    float regainBy = Time.realtimeSinceStartup + 0.8f;
                    while (!extra.Abilities.GrabBlock.IsGrabbing && Time.realtimeSinceStartup < regainBy) yield return null;
                    h.Check(extra.Abilities.GrabBlock.IsGrabbing, "PR the secondary puppet re-grabs after losing its hold while its owner keeps pushing");
                }
                yield return Barrier(h, 415, block);
            }
            finally
            {
                if (!host)
                {
                    P2Input.BotRT = false; P2Input.BotLX = 0; P2Input.BotActive = oldBot;
                    NetPlay.ReleaseLocalSeat(extraSeat);
                }
            }
        }
    }
}
