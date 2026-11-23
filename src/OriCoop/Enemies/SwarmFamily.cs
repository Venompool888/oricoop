namespace OriCoop.Enemies
{
    // T-195: a splitting Swarm. The first one comes from a placeholder, so it has a baked id. Every piece it splits into
    // is created at run time and has no id at all, so each piece is named after its place in the family tree instead:
    // node 1 is the placeholder's own enemy, node n splits into 2n (thrown left) and 2n + 1 (thrown right). The tree is
    // the same on every PC, so the same piece gets the same id everywhere without anything extra on the wire.
    internal static class SwarmFamily
    {
        internal const int Root = 1;
        // The game splits three times (large, medium, small, tiny). One spare level; a prefab loop cannot run away.
        internal const int MaxDepth = 4;
        internal const int MaxNode = (1 << (MaxDepth + 1)) - 1;

        internal static int Child(int node, int index) { return node * 2 + (index & 1); }
        internal static int Parent(int node) { return node >> 1; }
        internal static int Depth(int node) { int depth = 0; while (node > 1) { node >>= 1; depth++; } return depth; }
        internal static bool Valid(int node) { return node > Root && node <= MaxNode; }

        // The id of a piece: four ints mixed from the placeholder's id and the node. Never the placeholder's own id
        // (node 1 keeps that), and different for every node of one family.
        internal static int[] Id(int a, int b, int c, int d, int node)
        {
            int[] id = new int[4];
            for (int part = 0; part < 4; part++)
            {
                uint hash = 2166136261u;
                hash = Mix(hash, (uint)a); hash = Mix(hash, (uint)b); hash = Mix(hash, (uint)c); hash = Mix(hash, (uint)d);
                hash = Mix(hash, (uint)node); hash = Mix(hash, 0x5377726Du + (uint)part);   // "Swrm" + part
                id[part] = (int)hash;
            }
            return id;
        }
        static uint Mix(uint hash, uint value)
        {
            for (int shift = 0; shift < 32; shift += 8) { hash ^= (value >> shift) & 0xFF; hash *= 16777619u; }
            return hash;
        }
    }
}
