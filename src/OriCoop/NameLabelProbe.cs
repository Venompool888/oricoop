using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    internal sealed class NameLabelProbe : MonoBehaviour
    {
        int m_repaints;
        Vector3 m_inputScreen;
        float m_distance;
        const float ArrowOpacity = 0.7f;

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            Camera camera = UI.Cameras.Current.Camera;
            Vector3 own = P2Context.P1.Position;
            float depth = camera.WorldToScreenPoint(own + Vector3.up * 1.4f).z;
            Vector3 head = camera.ScreenToWorldPoint(new Vector3(Screen.width * 2f, Screen.height * 0.5f, depth));
            Vector3 offscreen = head - Vector3.up * 1.4f;
            m_inputScreen = camera.WorldToScreenPoint(head);
            m_distance = Vector3.Distance(own, offscreen);
            m_repaints++;
            NativeGui.BeginPage("hud.signal");
            try { NetHud.DrawPartnerIndicator(1, offscreen, Screen.height / 1080f, ArrowOpacity); }
            finally { NativeGui.EndPage(); }
        }

        static TextMesh VisibleLabel(string id)
        {
            GameObject go = GameObject.Find("OriCoopNativeGui_" + id);
            TextMesh label = go == null ? null : go.GetComponent<TextMesh>();
            if (label == null || label.font == null || !label.font.dynamic || !label.text.Contains("P1 星叶")) return null;
            Renderer renderer = go.GetComponent<Renderer>();
            CharacterInfo glyph;
            return renderer != null && renderer.enabled && renderer.bounds.size.sqrMagnitude > 0f
                && renderer.sharedMaterial != null && renderer.sharedMaterial.mainTexture == label.font.material.mainTexture
                && label.font.GetCharacterInfo('叶', out glyph, label.fontSize, label.fontStyle) && glyph.advance > 0
                ? label : null;
        }

        static string Detail(string id)
        {
            GameObject go = GameObject.Find("OriCoopNativeGui_" + id);
            if (go == null) return "object absent";
            TextMesh label = go.GetComponent<TextMesh>();
            if (label == null) return "no dynamic TextMesh";
            Renderer renderer = go.GetComponent<Renderer>();
            CharacterInfo glyph;
            bool hasGlyph = label.font != null && label.font.GetCharacterInfo('叶', out glyph, label.fontSize, label.fontStyle) && glyph.advance > 0;
            bool atlas = renderer != null && renderer.sharedMaterial != null && label.font != null
                && renderer.sharedMaterial.mainTexture == label.font.material.mainTexture;
            return "text='" + label.text + "' glyph=" + hasGlyph + " atlas=" + atlas + " dynamic=" + (label.font != null && label.font.dynamic)
                + " drawn=" + (renderer != null && renderer.enabled && renderer.bounds.size.sqrMagnitude > 0f)
                + " rgba=" + (Color32)label.color + " screen=" + P2Hud.GuiCamera().WorldToScreenPoint(go.transform.position).ToString("F2");
        }

        // TextMesh stores the nearest byte (including 0.7 -> 179). This Unity version's managed Color32 cast truncates.
        static byte TextMeshByte(float channel) { return (byte)Mathf.FloorToInt(Mathf.Clamp01(channel) * 255f + 0.5f); }

        internal static IEnumerator CheckLabels(TestHarness h)
        {
            NameLabelProbe probe = h.gameObject.AddComponent<NameLabelProbe>();
            try
            {
                TextMesh row = null, arrow = null;
                TestClock.Window window = new TestClock.Window(2f);
                while ((row == null || arrow == null) && window.Open)
                {
                    yield return null;
                    row = VisibleLabel("coop.settings.seat.1");
                    arrow = VisibleLabel("hud.signal.partner.1");
                }
                h.Check(row != null && row.color.a > 0.5f, "menu names: the visible F8 seat row renders the saved nickname's missing native glyph");
                Color32 tint = arrow == null ? Color.clear : arrow.color;
                Color seat = Roles.ColorOf(1);
                Color32 expected = new Color32(TextMeshByte(seat.r), TextMeshByte(seat.g), TextMeshByte(seat.b), TextMeshByte(ArrowOpacity));
                Vector3 input = probe.m_inputScreen;
                bool right = arrow != null && arrow.text.EndsWith("→") && P2Hud.GuiCamera().WorldToViewportPoint(arrow.transform.position).x > 0.5f;
                bool fixture = probe.m_repaints > 0 && input.z > 0f && input.x > Screen.width && Mathf.Abs(input.y - Screen.height * 0.5f) < 2f && probe.m_distance > 1f;
                bool matches = fixture && right && tint.Equals(expected);
                if (!matches)
                    h.Note("menu names: arrow input=" + input.ToString("F1") + " size=" + Screen.width + "x" + Screen.height
                        + " distance=" + probe.m_distance.ToString("F1") + " repaints=" + probe.m_repaints + " expected=" + expected + " " + Detail("hud.signal.partner.1"));
                h.Check(matches, "menu names: the visible offscreen arrow renders the nickname's missing native glyph at the right edge in seat colour");
            }
            finally { Object.Destroy(probe); }
        }
    }
}
