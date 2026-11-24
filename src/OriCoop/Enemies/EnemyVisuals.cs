using System;
using System.Collections.Generic;

namespace OriCoop.Enemies
{
    internal static class EnemyVisuals
    {
        static readonly string[] MirroredClasses = { "JumperEnemy", "RammingEnemy", "ArmouredRammingEnemy", "SpitterEnemy",
            "AcidSlugEnemy", "StarSlugEnemy", "DropSlugEnemy", "DashOwlEnemy", "FishEnemy", "KamikazeSootEnemy", "SwarmEnemy" };

        internal static bool MirroredClass(string name) { return Array.IndexOf(MirroredClasses, name) >= 0; }

        internal static T FindByName<T>(string name, IEnumerable<T> candidates, Func<T, string> getName) where T : class
        {
            foreach (T candidate in candidates)
                if (candidate != null && getName(candidate) == name) return candidate;
            return null;
        }
    }
}
