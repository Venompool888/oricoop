using System.Collections.Generic;

namespace OriCoop.World
{
    // T-207 (1): a story sequence's progress (ActionSequenceSerializer: which step, running or not) belongs to each PC
    // (T-196). WorldSync kept a partner's value out of the live object but still wrote it into this PC's checkpoint:
    // after a death, the reload started this PC's copy at the partner's step, skipping the steps before it.
    // A partner's story progress no longer goes into the checkpoint; the entry this PC's own game saved stays.
    public sealed class StoryProgressKeys
    {
        // WorldSync's existing raw-blob type descriptor carries the sender's classification. An
        // ordinary ActionSequenceSerializer still has its native type name and the same wire layout.
        public const string StoryType = "ActionSequenceSerializer/Story";
        readonly HashSet<string> m_keys = new HashSet<string>();
        public int Count { get { return m_keys.Count; } }
        public bool Known(string key) { return m_keys.Contains(key); }
        // Review finding 7: every story sequence's progress object this PC has had loaded is remembered (WorldSync's
        // periodic scan), not only one that was loaded at the moment a partner's value arrived. Before, a PC that ran
        // the story, left the scene and only then got the partner's progress still stored it.
        public void Remember(string key) { m_keys.Add(key); }
        public void Clear() { m_keys.Clear(); }

        // A partner's blob for object `key`: is it written into this PC's checkpoint?
        // loaded: the object is loaded here. liveStory: it is loaded and is a story sequence's progress.
        // A never-loaded sequence uses the sender's classification. When a live object exists,
        // its local classification takes precedence over the remote descriptor.
        public bool StoreInCheckpoint(string key, bool loaded, bool liveStory, bool senderStory = false)
        {
            if (liveStory || (!loaded && senderStory)) { m_keys.Add(key); return false; }
            return loaded || !m_keys.Contains(key);
        }
    }
}
