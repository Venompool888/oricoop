using System;

namespace OriCoop.Ui
{
    // T-053: the first time a player is in gameplay in an online session, a one-line controls guide (move and
    // jump) appears at the bottom of the screen for a few seconds. It never takes input and never pauses the game.
    // This part is pure (no Unity) so the unit tests can drive it; ControlsGuideHud draws it in the game.
    internal sealed class ControlsGuide
    {
        internal const float Delay = 1.5f;      // gameplay must be steady this long before the guide appears
        internal const float Duration = 12f;    // fully visible time
        internal const float Fade = 0.6f;

        float m_steadySince = -1f, m_shownAt = -1f;
        bool m_done;

        // Already seen on this install (persisted by the caller); Done turns true once it has been shown in full.
        internal ControlsGuide(bool alreadySeen) { m_done = alreadySeen; }

        internal bool Done { get { return m_done; } }
        internal bool Showing { get { return m_shownAt >= 0f && !m_done; } }

        // Returns the guide's opacity (0 = hidden). inGameplay: the local Ori is playable in an online session.
        internal float Update(bool inGameplay, float now)
        {
            if (m_done) return 0f;
            if (m_shownAt < 0f)
            {
                if (!inGameplay) { m_steadySince = -1f; return 0f; }
                if (m_steadySince < 0f) m_steadySince = now;
                if (now - m_steadySince < Delay) return 0f;
                m_shownAt = now;
            }
            float t = now - m_shownAt;
            if (t >= Duration + 2f * Fade) { m_done = true; return 0f; }
            if (!inGameplay) return 0f;   // a menu or cutscene hides it; the clock keeps running
            if (t < Fade) return t / Fade;
            if (t > Duration + Fade) return Math.Max(0f, (Duration + 2f * Fade - t) / Fade);
            return 1f;
        }

        // Keyboard keys for "move left", "move right" and "jump" (UnityEngine.KeyCode names, first binding of each).
        internal static string Text(bool zh, string left, string right, string jump)
        {
            return string.Format(L10n.T("guide.controls", zh), MoveKeys(zh, left, right), KeyName(zh, jump));
        }

        internal static string MoveKeys(bool zh, string left, string right)
        {
            if (left == "LeftArrow" && right == "RightArrow") return L10n.T("guide.key.arrows", zh);
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return L10n.T("guide.key.arrows", zh);
            return KeyName(zh, left) + " / " + KeyName(zh, right);
        }

        internal static string KeyName(bool zh, string key)
        {
            if (string.IsNullOrEmpty(key) || key == "None") return "?";
            switch (key)
            {
                case "Space": return L10n.T("guide.key.space", zh);
                case "LeftArrow": return L10n.T("guide.key.left", zh);
                case "RightArrow": return L10n.T("guide.key.right", zh);
                case "UpArrow": return L10n.T("guide.key.up", zh);
                case "DownArrow": return L10n.T("guide.key.down", zh);
                case "Return": return "Enter";
                case "LeftShift": case "RightShift": return "Shift";
                case "LeftControl": case "RightControl": return "Ctrl";
                case "LeftAlt": case "RightAlt": return "Alt";
            }
            if (key.Length == 6 && key.StartsWith("Alpha")) return key.Substring(5);
            if (key.StartsWith("Keypad")) return "Num " + key.Substring(6);
            if (key.StartsWith("Mouse")) return L10n.T("guide.key.mouse", zh) + " " + key.Substring(5);
            return key;
        }
    }
}
