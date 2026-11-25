using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace OriCoop
{
    public static partial class NetPhysTest
    {
        static bool StoneMaterial(PushPullBlock block, PhysicMaterial expected)
        {
            bool found = false;
            foreach (Collider collider in block.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
                if (collider.sharedMaterial != expected) return false;
                found = true;
            }
            return found;
        }

        static IEnumerator DepartedHolder(TestHarness h, PushPullBlock block, Vector3 resting, Quaternion rotation, bool host)
        {
            string done = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT")), "pj-host-released.txt");
            if (host && File.Exists(done)) File.Delete(done);
            yield return CoopReset(h, 310, block, resting, rotation, host);
            string endpoint = host ? "" : NetPlay.Peer.Remote.ToString();
            SeinCharacter departing = Puppet;
            GrabSetup grab = new GrabSetup();
            if (!host)
            {
                yield return GrabBlock(block, grab, CoopDir);
                if (grab.Started) Bot(true, CoopDir);
            }
            else
            {
                float until = Time.realtimeSinceStartup + 10f;
                while (departing != null && Time.realtimeSinceStartup < until && !PushBlockSync.Ignores(block, departing.PlatformBehaviour.PlatformMovement))
                { KeepAlive(); yield return null; }
                grab.Started = departing != null && PushBlockSync.Ignores(block, departing.PlatformBehaviour.PlatformMovement)
                    && block.PushPullMaterial != block.PushPullMaterialMoving && StoneMaterial(block, block.PushPullMaterialMoving);
            }
            yield return Barrier(h, 311, block, grab.Started);
            Sample peer;
            bool both = s_partnerSteps.TryGetValue(311, out peer) && grab.Started && peer.GrabStarted;
            h.Check(both, "PJ guest grips the tutorial stone and its host collider uses moving friction");
            if (!both) { Bot(false); yield break; }
            // Wait for the barrier's final reliable packet before the departing guest closes the link.
            yield return Wait(0.25f);
            if (host)
            {
                float until = Time.realtimeSinceStartup + 8f;
                while (departing != null && Time.realtimeSinceStartup < until) { KeepAlive(); yield return null; }
                h.Check(departing == null && block != null, "PJ the guest's original puppet is destroyed while the host keeps the stone loaded");
                h.Check(StoneMaterial(block, block.PushPullMaterial), "PJ departing guest frees the stone's resting friction");
                GrabSetup next = new GrabSetup();
                yield return GrabBlock(block, next, CoopDir);
                if (next.Started) { Bot(true, CoopDir); yield return Wait(0.25f); }
                h.Check(next.Started && StoneMaterial(block, block.PushPullMaterialMoving), "PJ remaining host grabs and pushes the same stone");
                Bot(false);
                until = Time.realtimeSinceStartup + 2f;
                while (Own.Abilities.GrabBlock.IsGrabbing && Time.realtimeSinceStartup < until) { KeepAlive(); yield return null; }
                h.Check(!Own.Abilities.GrabBlock.IsGrabbing && !block.IsGrabbed && StoneMaterial(block, block.PushPullMaterial),
                    "PJ stone returns to resting friction after the last holder releases");
                File.WriteAllText(done, "released");
            }
            else
            {
                NetPlay.End("left the room");
                Bot(false);
                float until = Time.realtimeSinceStartup + 25f;
                while (!File.Exists(done) && Time.realtimeSinceStartup < until) yield return null;
                NetPlay.Begin(false, "direct", endpoint);
            }
            float reconnect = Time.realtimeSinceStartup + 25f;
            while ((!NetPlay.Connected || Puppet == null || !SaveSync.Ready) && Time.realtimeSinceStartup < reconnect) { KeepAlive(); yield return null; }
            h.Check(NetPlay.Connected && Puppet != null && SaveSync.Ready, "PJ guest rejoins the host after checking the unattended stone");
            yield return Barrier(h, 312, block);
        }
    }
}
