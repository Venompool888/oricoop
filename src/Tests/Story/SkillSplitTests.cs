using System;
using OriCoop.Story;

// T-047: per-player skill trees and skill points (Story\SkillSplit.cs).
static partial class StoryTests
{
    // Abilities 0..4: 1 and 3 are skill-tree abilities costing 1 and 2; 0, 2, 4 come from the story (pedestals).
    const ulong Tree = (1UL << 1) | (1UL << 3);
    static readonly int[] Prices = { 0, 1, 0, 2, 0 };

    static void SkillCases()
    {
        Check("skills: earned is the same for host and guest after each buys different skills", delegate {
            int earned = 5;                                               // both start with 5 points, no tree skill
            ulong host = (1UL << 0) | (1UL << 3), guest = (1UL << 0) | (1UL << 1);
            int hostPoints = SkillSplit.Points(earned, host, Tree, Prices), guestPoints = SkillSplit.Points(earned, guest, Tree, Prices);
            Equal(3, hostPoints); Equal(4, guestPoints);
            Equal(earned, SkillSplit.Earned(hostPoints, host, Tree, Prices));
            Equal(earned, SkillSplit.Earned(guestPoints, guest, Tree, Prices));
        });
        Check("skills: a host purchase does not cost the guest a point or give it the skill", delegate {
            ulong hostBefore = 1UL << 0, hostAfter = hostBefore | (1UL << 3);
            int earnedBefore = SkillSplit.Earned(5, hostBefore, Tree, Prices), earnedAfter = SkillSplit.Earned(3, hostAfter, Tree, Prices);
            Equal(earnedBefore, earnedAfter);                             // buying does not change what was earned
            ulong guest = 1UL << 0;
            ulong merged = SkillSplit.Merge(guest, SkillSplit.Shared(hostAfter, Tree), Tree);
            Equal(guest, merged);
            Equal(5, SkillSplit.Points(earnedAfter, merged, Tree, Prices));
        });
        Check("skills: a level-up on the host gives every player one more point", delegate {
            ulong host = 1UL << 3, guest = 1UL << 1;
            int earned = SkillSplit.Earned(3 + 1, host, Tree, Prices);      // host had 3 left, levels up
            Equal(4, SkillSplit.Points(earned, host, Tree, Prices));
            Equal(5, SkillSplit.Points(earned, guest, Tree, Prices));     // guest had 4 left (bought the 1-point skill)
        });
        Check("skills: story abilities stay shared both ways", delegate {
            ulong host = (1UL << 2) | (1UL << 4) | (1UL << 3), guest = 1UL << 1;
            ulong merged = SkillSplit.Merge(guest, SkillSplit.Shared(host, Tree), Tree);
            Equal((1UL << 2) | (1UL << 4) | (1UL << 1), merged);
            Equal(0UL, SkillSplit.Shared(host, Tree) & Tree);
            Equal(0UL, SkillSplit.Merge(1UL << 2, 0UL, Tree) & ~Tree); // the host lost a story ability: so does the guest
        });
        Check("skills: points never go below zero; unknown tree keeps the old shared behaviour", delegate {
            Equal(0, SkillSplit.Points(1, Tree, Tree, Prices));
            Equal(7, SkillSplit.Earned(7, ulong.MaxValue, 0UL, Prices));
            Equal(7, SkillSplit.Points(7, ulong.MaxValue, 0UL, Prices));
            Equal(ulong.MaxValue, SkillSplit.Merge(0UL, ulong.MaxValue, 0UL));
            Equal(0, SkillSplit.Cost(ulong.MaxValue, Tree, null));
        });
    }
}
