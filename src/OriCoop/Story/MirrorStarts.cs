using System.Collections.Generic;

namespace OriCoop.Story
{
    // T-183: a mirror (StorySync.Resolve) starts the partner's sequence directly, so the game's own way of starting it
    // here never runs. In the spirit-tree chain that way is a TriggerByString: Kuro's onFinish registers
    // "kuroAttackCutsceneFinished" and 03's TriggerByString consumes it when the tree scene is enabled; the world-map
    // sequence registers "returnFromWorldMap" and 04's TriggerByString consumes it. On the mirror PC the registration
    // (mirrored too, often a frame after the mirrored 03) stayed in the list, and the next time the tree scene was
    // enabled (back from the world map) the TriggerByString started 03 again: a second 03 and a second world-map trip
    // on the mirror PC (seen as "03 starts=2/3"; under load it left that PC at the world-map spot).
    // Rule: a mirror's start of a sequence stands in for ONE later firing of a string trigger that would start that
    // sequence here (the firing is consumed: deregistered, not run), within Window seconds. A partner who really
    // starts it again is mirrored again (a new entry), so nothing the partner does is lost.
    public sealed class MirrorStarts
    {
        public const float Window = 300f;
        readonly Dictionary<int, float> m_started = new Dictionary<int, float>();
        public int Skipped { get; private set; }

        public void MirrorPerformed(int sequence, float now) { m_started[sequence] = now; }

        // A string trigger is about to start `sequence` on this PC. True: skip it (consumed by the mirror's start).
        public bool SkipTrigger(int sequence, float now)
        {
            float at;
            if (!m_started.TryGetValue(sequence, out at)) return false;
            m_started.Remove(sequence);
            if (now - at > Window || now < at) return false;
            Skipped++;
            return true;
        }

        public void Clear() { m_started.Clear(); }

        // T-183 (chain run B-4): the mirror PC's copy of 03 started 2.8 s behind the original. The mirrored world-map
        // sequence (started by "spiritTreeWorldMap") arrived while that 03 still waited at its story text; 03 froze while
        // the world map was current and, once the mirrored 04 brought the PC back to the tree, finished its tail: fade to
        // black, register "spiritTreeWorldMap", load the world map. 04 broke (WorldMapUI NRE) and the PC stayed there.
        // A mirrored sequence that still has to register a string (at or after its current action) whose sequence the
        // mirror has just started is overtaken by the original: it is retired, not left to replay the step that led there.
        public static bool Overtaken(IEnumerable<string> registrationsStillAhead, ICollection<string> startedSequenceStrings)
        {
            if (startedSequenceStrings == null || startedSequenceStrings.Count == 0) return false;
            foreach (string s in registrationsStillAhead) if (!string.IsNullOrEmpty(s) && startedSequenceStrings.Contains(s)) return true;
            return false;
        }
    }
}
