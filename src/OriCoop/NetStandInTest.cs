using System;
using Game;
using UnityEngine;

namespace OriCoop
{
    // Net phase MOVE, check WALL (T-251): the partner's stand-in is pulled toward its owner's position by writing the
    // position (no collision). A pull whose straight path crosses level geometry must jump instead (Net\StandInCorrection.cs,
    // unit tests "stand-in: ..." in src\Tests\NetCore). This checks the game side of that decision, NetPlay's wall probe
    // (line cast on the character's ray-test layers; triggers, enemies and characters skipped), on this PC's own Ori where
    // it stands: a pull from it to a point under its floor jumps, a short pull into the open air above it does not.
    // Instant (no waiting), so both PCs keep the same phase schedule. Mutation ORICOOP_MUTATE_STANDIN_SLIDES=1 must fail it.
    public static class NetStandInTest
    {
        static string V(Vector3 v) { return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + ")"; }

        public static void Run(TestHarness h)
        {
            SeinCharacter own = P2Context.P1;
            if (own == null || own.Controller == null) { h.Note("WALL not judged: no own Ori"); return; }
            Vector3 at = own.Position;
            RaycastHit floor = new RaycastHit(); bool found = false;
            foreach (RaycastHit hit in Physics.RaycastAll(at, Vector3.down, 3f, own.Controller.RayTestLayerMask, QueryTriggerInteraction.Ignore))
            {
                Collider c = hit.collider;
                if (c == null || c.isTrigger || c.GetComponentInParent<Entity>() != null || c.GetComponentInParent<SeinCharacter>() != null) continue;
                if (!found || hit.distance < floor.distance) { floor = hit; found = true; }
            }
            if (!found) { h.Note("WALL not judged: no floor within 3 under " + V(at)); return; }
            Vector3 under = floor.point + Vector3.down * 0.4f, above = at + Vector3.up * 0.3f;
            OriCoop.Net.StandInCorrection.Fix through = NetPlay.TestDecide(own, at, under);
            OriCoop.Net.StandInCorrection.Fix open = NetPlay.TestDecide(own, at, above);
            h.Note("WALL at=" + V(at) + " floor=" + V(floor.point) + " (" + floor.collider.name + ") through-floor=" + through + " open-air=" + open);
            h.Check(through == OriCoop.Net.StandInCorrection.Fix.Snap, "WALL a stand-in pull through the floor jumps instead of sliding (T-251; " + through + ")");
            h.Check(open == OriCoop.Net.StandInCorrection.Fix.Blend, "WALL a short stand-in pull into open air stays smooth (T-251; " + open + ")");
        }
    }
}
