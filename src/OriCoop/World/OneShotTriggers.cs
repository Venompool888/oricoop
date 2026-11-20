using System.Collections.Generic;

namespace OriCoop.World
{
    // TriggerOnce hazards that already fired on this PC (WorldObjSync message 75 kind 5): the passer's PC sends the event
    // once, the partner's PC runs it once. T-144: a checkpoint reload re-arms the game's triggers (Trigger.Serialize
    // restores Active), so it must forget them here too; before, only the session Reset did, and after a wipe the rock
    // fell only on the passer's PC (its send was skipped, the partner refused the id).
    public sealed class OneShotTriggers
    {
        readonly HashSet<int> m_fired = new HashSet<int>();
        public int Count { get { return m_fired.Count; } }
        public bool HasFired(int id) { return m_fired.Contains(id); }
        // True the first time: the caller sends / runs it. False = it already fired in this checkpoint's life.
        public bool TryFire(int id) { return m_fired.Add(id); }
        public void Forget(int id) { m_fired.Remove(id); }
        public void CheckpointRestored() { m_fired.Clear(); }
        public void Clear() { m_fired.Clear(); }
    }
}
