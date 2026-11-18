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
}
