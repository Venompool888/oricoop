using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OriCoop.Ui
{
    internal sealed class NativeGuiState
    {
        readonly HashSet<string> m_failures = new HashSet<string>();
        readonly HashSet<string> m_pages = new HashSet<string>();
        readonly HashSet<string> m_labelIds = new HashSet<string>();
        internal readonly HashSet<string> PagesLastFrame = new HashSet<string>();
        internal readonly HashSet<string> LastLabelIds = new HashSet<string>();
        int m_frame = -1, m_labels;
        internal int LabelsLastFrame;
        internal string Page = "";
        internal Action<string> PageChanged;

        internal bool ShouldWarn(string operation, string id, Exception error)
        {
            string key = operation + "|" + id + "|" + error.GetType().FullName + "|" + error.Message;
            return m_failures.Add(key);
        }

        internal bool StartFrame(int frame)
        {
            if (m_frame == frame) return false;
            m_frame = frame; m_labels = 0; m_pages.Clear(); m_labelIds.Clear();
            return true;
        }

        internal void BeginPage(string page)
        {
            Page = page;
            if (PageChanged != null) PageChanged(page);
        }

        internal void EndPage()
        {
            Page = "";
            if (PageChanged != null) PageChanged("");
        }

        internal void AddPage(string page) { m_pages.Add(page); }
        internal void AddLabel(string id) { m_labels++; m_labelIds.Add(id); }

        internal void Capture(int frame)
        {
            PagesLastFrame.Clear(); LastLabelIds.Clear(); LabelsLastFrame = 0;
            if (m_frame == frame - 1)
            {
                foreach (string page in m_pages) PagesLastFrame.Add(page);
                LabelsLastFrame = m_labels;
                foreach (string id in m_labelIds) LastLabelIds.Add(id);
            }
        }
    }

    internal static class NativeGuiLogic
    {
        static readonly Regex s_richText = new Regex(@"</?(color|b|i|size)(=[^>]*)?>");

        internal static string PlainText(string text) { return s_richText.Replace(text, ""); }

        internal static string Wrap(string text, Func<int, float> cursorY)
        {
            StringBuilder wrapped = new StringBuilder();
            float y = cursorY(0);
            for (int i = 0; i < text.Length; i++)
            {
                float nextY = cursorY(i);
                if (nextY > y && i > 0 && text[i - 1] != '\n') wrapped.Append('\n');
                wrapped.Append(text[i]); y = nextY;
            }
            return wrapped.ToString();
        }

        internal static float Align(float origin, float available, float size, int alignment)
        {
            return origin + (available - size) * alignment * 0.5f;
        }

        internal static float FitScale(float scale, float worldWidth, float rectWidth, float unit)
        {
            if (worldWidth * scale > rectWidth * unit)
                scale = rectWidth * unit / (0.0001f > worldWidth ? 0.0001f : worldWidth);
            return scale;
        }

        internal static bool Inside(float xMin, float xMax, float yMin, float yMax,
            float clipXMin, float clipXMax, float clipYMin, float clipYMax)
        {
            return xMin >= clipXMin - 1f && xMax <= clipXMax + 1f && yMin >= clipYMin - 1f && yMax <= clipYMax + 1f;
        }

        internal static bool Highlight(bool focused, bool hovered) { return focused || hovered; }
    }
}
