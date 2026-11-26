using System.Collections.Generic;

namespace OriCoop.World
{
    internal struct DigestObject
    {
        public readonly string Type;
        public readonly ulong Hash;
        public DigestObject(string type, ulong hash) { Type = type; Hash = hash; }
        public bool Matches(DigestObject other) { return Type == other.Type && Hash == other.Hash; }
    }

    internal struct DigestDifference
    {
        public readonly DigestObject Local, Remote;
        public DigestDifference(DigestObject local, DigestObject remote) { Local = local; Remote = remote; }
        public bool Matches(DigestDifference other) { return Local.Matches(other.Local) && Remote.Matches(other.Remote); }
    }

    internal sealed class DigestRepair
    {
        public enum Decision { None, Wait, Request }
        readonly Dictionary<string, DigestDifference> m_resynced = new Dictionary<string, DigestDifference>();
        readonly Dictionary<string, DigestDifference> m_requested = new Dictionary<string, DigestDifference>();
        int m_streak;

        public static bool Includes(string type) { return type != "AchievementsLogic" && type != "BaseAnimatorSerializer"; }

        // The Gumo mesh in thornfeltSwampActTwoStart's gumoSavesSein timeline
        // follows sprite animation metadata on each PC. Keep the object in the digest with a stable pose hash.
        public static bool ComparesPosePresence(string key, string type)
        {
            return type == "TransformSaveSerialize"
                && key == "-1289149174 680822595 558787450 1729667918:-1517079076 1077808971 1966622865 680266200";
        }

        public static Dictionary<string, DigestDifference> Differences(Dictionary<string, DigestObject> local, Dictionary<string, DigestObject> remote)
        {
            var differences = new Dictionary<string, DigestDifference>();
            DigestObject other;
            foreach (KeyValuePair<string, DigestObject> pair in local)
            {
                if (!Includes(pair.Value.Type)) continue;
                remote.TryGetValue(pair.Key, out other);
                if (!Includes(other.Type)) other = default(DigestObject);
                if (!pair.Value.Matches(other)) differences.Add(pair.Key, new DigestDifference(pair.Value, other));
            }
            foreach (KeyValuePair<string, DigestObject> pair in remote)
                if (Includes(pair.Value.Type) && (!local.TryGetValue(pair.Key, out other) || !Includes(other.Type)))
                    differences.Add(pair.Key, new DigestDifference(default(DigestObject), pair.Value));
            return differences;
        }

        public Decision Observe(Dictionary<string, DigestDifference> differences, bool progressDiffers)
        {
            // Suppress only the same mismatch, until either side changes or the object matches again.
            foreach (string key in new List<string>(m_resynced.Keys))
            {
                DigestDifference current;
                if (!differences.TryGetValue(key, out current) || !m_resynced[key].Matches(current)) m_resynced.Remove(key);
            }
            if (progressDiffers || differences.Count > m_resynced.Count)
                return ++m_streak >= 2 ? Decision.Request : Decision.Wait;
            m_streak = 0;
            return Decision.None;
        }

        public void SnapshotRequested(Dictionary<string, DigestDifference> differences)
        {
            foreach (var pair in differences) m_requested[pair.Key] = pair.Value;
            m_streak = 0;
        }

        public void SnapshotReceived()
        {
            foreach (var pair in m_requested) m_resynced[pair.Key] = pair.Value;
            m_requested.Clear();
        }

        public void Clear()
        {
            m_resynced.Clear(); m_requested.Clear(); m_streak = 0;
        }
    }
}
