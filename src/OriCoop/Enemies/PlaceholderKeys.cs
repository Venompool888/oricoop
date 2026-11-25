using System.Collections.Generic;

namespace OriCoop.Enemies
{
    // #406 (4): EnemySync.Track runs from several placeholder patches, UpdateSpawnState among them, so for every
    // loaded enemy placeholder on most frames. Each call walked up to its SceneRoot (SceneRoot.FindFromTransform)
    // and built its actor key from two MoonGuid.ToString calls and a concat. Neither changes while the placeholder
    // lives, so both are kept here by the placeholder's instance id. An entry only counts for the same object
    // (instance ids are not reused while it lives, the reference check is a guard), and the caller still checks
    // that the root is alive.
    internal sealed class PlaceholderKeys<TPlaceholder, TRoot> where TPlaceholder : class where TRoot : class
    {
        sealed class Entry
        {
            public TPlaceholder Placeholder;
            public TRoot Root;
            public string Key;
        }

        readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        public int Count { get { return entries.Count; } }

        public bool TryGet(int id, TPlaceholder placeholder, out TRoot root, out string key)
        {
            Entry entry;
            if (placeholder != null && entries.TryGetValue(id, out entry) && ReferenceEquals(entry.Placeholder, placeholder))
            {
                root = entry.Root; key = entry.Key;
                return true;
            }
            root = null; key = null;
            return false;
        }

        public void Set(int id, TPlaceholder placeholder, TRoot root, string key)
        {
            if (placeholder == null || root == null || key == null) return;
            Entry entry;
            if (!entries.TryGetValue(id, out entry)) { entry = new Entry(); entries.Add(id, entry); }
            entry.Placeholder = placeholder; entry.Root = root; entry.Key = key;
        }

        public void Remove(int id) { entries.Remove(id); }

        public void Clear() { entries.Clear(); }
    }
}
