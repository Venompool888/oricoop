using System;
using System.Collections.Generic;

namespace OriCoop.Ui
{
    // T-171 / T-173 review: one physical press = one action on the NativeGui pages (room page, 我的房间 pages).
    // The pages run every rendered frame, but the game's buttons (Core.Input.*: OnPressed / Pressed) only change on a fixed
    // step, while Input.GetKey / GetKeyDown change every frame. Reading both made one press count twice (a DEL that deleted
    // without the second press, an Enter that ended code editing and then wiped the code, an Esc that closed the page and
    // the native menu under it). The gate works on the held state instead (key held this frame OR the game's button held as
    // of its last fixed step) and fires on its rising edge only:
    //  - a press fires once, however many frames or fixed steps it spans;
    //  - after a KEY press fires, a press that comes from a game button only is swallowed for Window seconds (at least one
    //    fixed step): it is the same physical key showing up again on the next fixed step through a second binding (Space =
    //    a shortcut key and the game's A button). A key never lags behind a game button, so another key press (Down then
    //    Enter, two quick Downs) always fires, and a game button press does not start the window;
    //  - Block: for a while nothing fires (after a page opens, after code editing ends) and a press that is down when the
    //    block starts, or that starts inside it, never fires; the key has to be released and pressed again.
    // Pure logic (no Unity): the page passes the held states and Time.unscaledTime. Unit-tested (UiTests).
    internal sealed class PressGate
    {
        public float Window = 0.1f;
        readonly Dictionary<int, bool> m_held = new Dictionary<int, bool>();
        float m_blockUntil = float.MinValue;    // Block: nothing fires
        float m_echoUntil = float.MinValue;     // after a key press: a game-button-only press is that key's echo

        // A key (Input.GetKey, per frame) only.
        public bool Edge(int id, bool keyHeld, float now) { return Edge(id, keyHeld, false, now); }

        // keyHeld: Unity keys this frame; buttonHeld: the game's buttons (Core.Input, per fixed step).
        // An input never seen yet counts as held, so a key that is already down the first time the page looks never fires.
        public bool Edge(int id, bool keyHeld, bool buttonHeld, float now)
        {
            bool held = keyHeld || buttonHeld;
            bool was;
            if (!m_held.TryGetValue(id, out was)) was = true;
            m_held[id] = held;
            if (!held || was || now < m_blockUntil) return false;
            if (!keyHeld && now < m_echoUntil) return false;
            if (keyHeld) m_echoUntil = now + Window;
            return true;
        }

        public void Block(float now, float seconds)
        {
            m_blockUntil = Math.Max(m_blockUntil, now + seconds);
            List<int> ids = new List<int>(m_held.Keys);
            foreach (int id in ids) m_held[id] = true;   // whatever is down now has to be released first
        }

        public bool Blocked(float now) { return now < m_blockUntil; }
    }
}
