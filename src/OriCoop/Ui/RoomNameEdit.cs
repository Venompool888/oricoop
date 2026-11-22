using System;
using System.Text;

namespace OriCoop.Ui
{
    // T-173 (room page layout room-v2-a): typing a new room name on the 房间管理页, as plain data (no Unity; unit-tested).
    // Room names are English only (owner rule, T-171): only the characters RoomNames.Clean keeps are typed (printable
    // ASCII without the markup characters), at most RoomNames.MaxLength. The page feeds it Input.inputString: Backspace
    // deletes, Enter confirms, Esc cancels.
    //   Begin(current)  start editing with the current name in the box
    //   Confirm()       stop editing; the cleaned new name, or null = keep the old one (empty, or unchanged)
    //   Cancel()        stop editing, nothing changes
    internal sealed class RoomNameEdit
    {
        readonly StringBuilder m_text = new StringBuilder();
        string m_old = "";
        bool m_editing;

        public bool Editing { get { return m_editing; } }
        public string Text { get { return m_text.ToString(); } }

        public void Begin(string current)
        {
            m_old = RoomNames.Clean(current);
            m_text.Length = 0;
            m_text.Append(m_old);
            m_editing = true;
        }

        // Typed characters: the ones a room name may hold are added (up to MaxLength), anything else is dropped.
        public void Type(string typed)
        {
            if (!m_editing || typed == null) return;
            foreach (char c in typed)
            {
                if (m_text.Length >= RoomNames.MaxLength) break;
                if (Allowed(c)) m_text.Append(c);
            }
        }

        public void Backspace()
        {
            if (m_editing && m_text.Length > 0) m_text.Length--;
        }

        public string Confirm()
        {
            if (!m_editing) return null;
            m_editing = false;
            string name = RoomNames.Clean(m_text.ToString());
            if (name.Length == 0 || name == m_old) return null;
            return name;
        }

        public void Cancel()
        {
            m_editing = false;
            m_text.Length = 0;
            m_text.Append(m_old);
        }

        // The characters RoomNames.Clean keeps (one char at a time: the spaces are collapsed on Confirm).
        static bool Allowed(char c)
        {
            return RoomNames.Clean("x" + c + "x").Length == 3;
        }
    }
}
