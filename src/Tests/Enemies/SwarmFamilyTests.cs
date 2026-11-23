using System;
using System.Collections.Generic;
using OriCoop.Enemies;

// T-195: the pieces of a splitting Swarm are named after their place in the family tree, so every PC gives the same
// piece the same id.
static class SwarmFamilyTests
{
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static string Text(int[] id) { return id[0] + " " + id[1] + " " + id[2] + " " + id[3]; }
    public static void Run(Action<string, Action> run)
    {
        run("swarm tree: a node splits into 2n (left) and 2n+1 (right)", delegate
        {
            Check(SwarmFamily.Child(SwarmFamily.Root, 0) == 2 && SwarmFamily.Child(SwarmFamily.Root, 1) == 3, "root children");
            Check(SwarmFamily.Child(3, 0) == 6 && SwarmFamily.Child(3, 1) == 7, "node 3 children");
            Check(SwarmFamily.Parent(6) == 3 && SwarmFamily.Parent(7) == 3 && SwarmFamily.Parent(2) == SwarmFamily.Root, "parents");
            Check(SwarmFamily.Depth(1) == 0 && SwarmFamily.Depth(3) == 1 && SwarmFamily.Depth(7) == 2 && SwarmFamily.Depth(15) == 3, "depth");
        });
        run("swarm tree: only pieces below the root, within the depth cap, are valid", delegate
        {
            Check(!SwarmFamily.Valid(0) && !SwarmFamily.Valid(SwarmFamily.Root), "root is the placeholder's own id");
            Check(SwarmFamily.Valid(2) && SwarmFamily.Valid(SwarmFamily.MaxNode) && !SwarmFamily.Valid(SwarmFamily.MaxNode + 1), "cap");
            Check(SwarmFamily.Depth(SwarmFamily.MaxNode) == SwarmFamily.MaxDepth, "last node is at the depth cap");
        });
        run("swarm id: the same piece gets the same id on every PC", delegate
        {
            for (int node = 2; node <= SwarmFamily.MaxNode; node++)
                Check(Text(SwarmFamily.Id(11, -22, 33, -44, node)) == Text(SwarmFamily.Id(11, -22, 33, -44, node)), "node " + node);
            // Pinned (computed outside the game code): every build must give these, or two versions name pieces differently.
            Check(Text(SwarmFamily.Id(1, 2, 3, 4, 2)) == "1062123370 438729761 -1183769148 -109583405", "node 2: " + Text(SwarmFamily.Id(1, 2, 3, 4, 2)));
            Check(Text(SwarmFamily.Id(1, 2, 3, 4, 3)) == "974341463 -1145252844 1550910513 -369621162", "node 3: " + Text(SwarmFamily.Id(1, 2, 3, 4, 3)));
        });
        run("swarm id: every piece of a family differs, and none is the placeholder's id", delegate
        {
            HashSet<string> seen = new HashSet<string>(); seen.Add("11 -22 33 -44");
            for (int node = 2; node <= SwarmFamily.MaxNode; node++)
                Check(seen.Add(Text(SwarmFamily.Id(11, -22, 33, -44, node))), "node " + node + " repeats an id");
        });
        run("swarm id: two families never share a piece id", delegate
        {
            HashSet<string> seen = new HashSet<string>();
            int[][] roots = { new[] { 11, -22, 33, -44 }, new[] { 11, -22, 33, -45 }, new[] { -22, 11, 33, -44 }, new[] { 0, 0, 0, 0 }, new[] { 12, -22, 33, -44 } };
            foreach (int[] root in roots)
                for (int node = 2; node <= SwarmFamily.MaxNode; node++)
                    Check(seen.Add(Text(SwarmFamily.Id(root[0], root[1], root[2], root[3], node))), "families collide at node " + node);
        });
        run("swarm id: the four parts of one id are mixed separately", delegate
        {
            int[] id = SwarmFamily.Id(11, -22, 33, -44, 5);
            Check(id[0] != id[1] && id[1] != id[2] && id[2] != id[3] && id[0] != id[3], "parts repeat");
        });
    }
}
