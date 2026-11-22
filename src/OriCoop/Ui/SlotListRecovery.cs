using System;

namespace OriCoop.Ui
{
    // T-173 review: 我的房间 -> 新建房间 -> a save slot -> the create (or the enter after it) fails. The save slot list must take
    // input again, never soft-lock:
    //  - an empty slot went through the game's difficulty screen, which only our patch hid: SaveSlotsUI ignores all input
    //    while that screen exists (SelectingDifficulty), so it has to be cancelled properly (CancelDifficultyScreen);
    //  - the list is switched back on a moment later, not at once: the failure can be reported inside the slot pick, and the
    //    game (UsedSaveSlotSelected's caller) or our patch switches the list off right after the pick returns.
    // Pure logic (no Unity): NativeModeMenu asks it what to do and when. Unit-tested (UiTests).
    internal sealed class SlotListRecovery
    {
        public const float Delay = 0.3f;
        float m_at = -1f;

        public bool Pending { get { return m_at >= 0f; } }

        // The create failed. true = cancel the (hidden) difficulty screen first.
        public bool Fail(float now, bool selectingDifficulty)
        {
            m_at = now + Delay;
            return selectingDifficulty;
        }

        // Every frame: true once, when the slot list should be switched back on now (it is on screen and the title orb is not
        // open over it). Past the delay the request is spent either way.
        public bool Due(float now, bool slotsVisible, bool orbOpen)
        {
            if (m_at < 0f || now < m_at) return false;
            m_at = -1f;
            return slotsVisible && !orbOpen;
        }

        public void Cancel() { m_at = -1f; }
    }
}
