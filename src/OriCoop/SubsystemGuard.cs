using System;
using System.Collections.Generic;

namespace OriCoop
{
    internal static class SubsystemGuard
    {
        static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();

        internal static IEnumerable<KeyValuePair<string, int>> Counts { get { return s_counts; } }

        internal static void Run(string name, Action action)
        {
            int count;
            s_counts.TryGetValue(name, out count);
            if (count >= 50) return;
            try { action(); }
            catch (Exception e)
            {
                count++;
                s_counts[name] = count;
                if (count == 1) Plugin.Log.LogError("subsystem " + name + " threw: " + e);
                if (count == 50) Plugin.Log.LogError("subsystem " + name + " faulted after 50 throws");
            }
        }
    }
}
