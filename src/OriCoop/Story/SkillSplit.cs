using System;

namespace OriCoop.Story
{
    // T-047 (and #9 / T-028): every player has their own skill tree and skill points; everything else in the progress
    // snapshot stays shared.
    //
    // Model (absolute, so a checkpoint reload or a new server-mode arbiter heals itself):
    //   earned  = points left + price of the skill-tree abilities this player owns     (the same for every player)
    //   points  = earned - price of the skill-tree abilities THIS player owns          (per player)
    // The host sends `earned` instead of its own points, and its abilities without the skill-tree ones. A guest keeps
    // its own skill-tree abilities and derives its points from `earned`. Level-ups and ability cells only change the
    // host's points, so they raise `earned` and reach every player; a purchase changes only the buyer's tree and points.
    //
    // `tree`: bit i set = PlayerAbilities.Abilities[i] is bought in the skill tree; cost[i] = its price.
    internal static class SkillSplit
    {
        public static int Cost(ulong owned, ulong tree, int[] cost)
        {
            int total = 0;
            ulong mine = owned & tree;
            for (int i = 0; i < 64 && cost != null && i < cost.Length; i++)
                if ((mine & (1UL << i)) != 0) total += Math.Max(0, cost[i]);
            return total;
        }

        public static int Earned(int points, ulong owned, ulong tree, int[] cost) { return points + Cost(owned, tree, cost); }

        public static int Points(int earned, ulong owned, ulong tree, int[] cost) { return Math.Max(0, earned - Cost(owned, tree, cost)); }

        // What the host shares: every ability except the skill-tree ones.
        public static ulong Shared(ulong abilities, ulong tree) { return abilities & ~tree; }

        // A guest applying a snapshot: shared abilities from the host, its own skill-tree abilities kept.
        public static ulong Merge(ulong local, ulong remote, ulong tree) { return (remote & ~tree) | (local & tree); }
    }

    // T-043: every seat rolls back to its own recorded tree, not the saver's. A guest holds that tree here until the
    // first snapshot merge, which derives its points from the checkpoint's `earned`. Without a record entry only an
    // already-joined guest holds its live bits (T-142); a new seat starts from the save (T-047).
    internal sealed class OwnSkillTree
    {
        bool held;
        ulong bits;
        public bool Held { get { return held; } }

        // Two pushes before the next merge: the second load starts from the host's bits, so the first hold wins.
        public void Hold(ulong owned) { if (held) return; bits = owned; held = true; }

        // The `local` abilities for the next snapshot merge: the held ones once, then the live ones again.
        public ulong Local(ulong live)
        {
            if (!held) return live;
            held = false; return bits;
        }

        public void Clear() { held = false; bits = 0; }
    }
}
