using System;

namespace OriCoop.Story
{
    internal struct SharedValues
    {
        public int Keys, Maps;
        public float Energy;
    }

    internal sealed class SharedResources
    {
        int keys, maps;
        float energy;
        public bool Initialized { get; private set; }

        public void Reset() { Initialized = false; }
        public void Initialize() { Initialized = true; }
        public int MergeKeys(int p1, int p2) { return Math.Max(0, p1 + (p2 - keys)); }
        public int MergeMaps(int p1, int p2) { return Math.Max(0, p1 + (p2 - maps)); }
        public float MergeEnergy(float p1, float p2, float max)
        {
            float value = p1 + (p2 - energy);
            return value < 0f ? 0f : value > max ? max : value;
        }
        public SharedValues Merge(int p1Keys, int p1Maps, float p1Energy, float maxEnergy,
            int p2Keys, int p2Maps, float p2Energy)
        {
            SharedValues result = new SharedValues();
            if (!Initialized)
            {
                result.Keys = p1Keys; result.Maps = p1Maps; result.Energy = p1Energy;
            }
            else
            {
                result.Keys = MergeKeys(p1Keys, p2Keys);
                result.Maps = MergeMaps(p1Maps, p2Maps);
                result.Energy = MergeEnergy(p1Energy, p2Energy, maxEnergy);
            }
            return result;
        }
        public void Remember(int p2Keys, int p2Maps, float p2Energy)
        { Initialized = true; keys = p2Keys; maps = p2Maps; energy = p2Energy; }

        // T-134: every extra local player (offline P2-P4) shares the pool. After each sync all extras hold the same
        // values (the baseline), so each extra's change since then is added to P1 in turn.
        public SharedValues MergeAll(SharedValues p1, float maxEnergy, SharedValues[] extras)
        {
            if (!Initialized) return p1;
            SharedValues result = p1;
            foreach (SharedValues e in extras)
            {
                result.Keys = MergeKeys(result.Keys, e.Keys);
                result.Maps = MergeMaps(result.Maps, e.Maps);
                result.Energy = MergeEnergy(result.Energy, e.Energy, maxEnergy);
            }
            return result;
        }

        // The values every extra held after the last sync: a player who joins starts from these, so its first sync adds
        // nothing (starting from P1's live values would count P1's own change since that sync twice).
        public SharedValues Baseline
        {
            get { SharedValues v = new SharedValues(); v.Keys = keys; v.Maps = maps; v.Energy = energy; return v; }
        }
    }
}
