using System.Collections.Generic;

namespace OriCoop.World
{
    // T-172: a story runs on the PC that started it; the others mirror it (StorySync). Objects inside a mirrored
    // sequence (its one-shot actions such as RunActionOnce, the sequence's own progress, a chained sequence) change on
    // both PCs, but only the running PC decides them. WorldSync used to propose any changed object, so a mirror that got
    // to "18. Run 02 (RunActionOnce)" first sent "already run" to the host, and the host's own copy then skipped 02
    // (playtest 3, Spirit Tree: the host never got the Kuro cutscene, the guest was stuck in it).
    // While an object belongs to a story this PC only mirrors, its change stays local. The running PC's value arrives
    // through the normal sync; once the mirror is over, a value that still differs is proposed as usual.
    public sealed class MirrorHold
    {
        readonly HashSet<string> m_held = new HashSet<string>();
        public int Count { get { return m_held.Count; } }
        public int HeldTotal { get; private set; }
        public bool IsHeld(string key) { return m_held.Contains(key); }

        // Called for each object whose local value differs from what the partners know. True: do not send it now.
        public bool Hold(string key, bool insideMirroredStory)
        {
            if (insideMirroredStory) { if (m_held.Add(key)) HeldTotal++; return true; }
            m_held.Remove(key);
            return false;
        }

        // The partner's (running PC's) value was applied here: our own held change is superseded.
        public void Applied(string key) { m_held.Remove(key); }
        public void Clear() { m_held.Clear(); }
    }
}
