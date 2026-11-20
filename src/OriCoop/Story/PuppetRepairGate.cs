using System.Collections.Generic;

namespace OriCoop.Story
{
    // T-137: CutsceneGuard clears a cinematic camera puppet that holds the view away from the player after a story
    // (a story cut halfway leaves it). A scene camera framing zone (GameplayToCinematicAnimator) sets the same puppet
    // again on the very next frame, on purpose; clearing it every 2 s made the view jump. A puppet that comes straight
    // back after a repair is driven by the scene: leave that one alone for a while. A stuck one never comes back.
    public sealed class PuppetRepairGate
    {
        public const float ComeBackWindow = 0.5f, DrivenHold = 30f;
        int m_lastId;
        float m_lastAt = -100f;
        readonly Dictionary<int, float> m_drivenUntil = new Dictionary<int, float>();
        public int DrivenDetections;
        public bool Disabled;   // mutation: the guard fights the scene again

        public void Repaired(int id, float now) { m_lastId = id; m_lastAt = now; }

        // Every tick: the puppet the camera is tweened towards now (0 = none). True when a repaired puppet came back.
        public bool Observe(int id, float now)
        {
            if (id == 0 || id != m_lastId || now - m_lastAt > ComeBackWindow) return false;
            m_drivenUntil[id] = now + DrivenHold; m_lastId = 0; DrivenDetections++;
            return true;
        }

        public bool ShouldRepair(int id, float now)
        {
            float until;
            return Disabled || !(m_drivenUntil.TryGetValue(id, out until) && now < until);
        }
    }
}
