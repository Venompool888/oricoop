using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    internal sealed class NameLabelProbe : MonoBehaviour
    {
        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            Camera camera = UI.Cameras.Current.Camera;
            Vector3 offscreen = camera.ViewportToWorldPoint(new Vector3(2f, 0.5f, 10f)) - Vector3.up * 1.4f;
            NativeGui.BeginPage("hud.signal");
            try { NetHud.DrawPartnerIndicator(1, offscreen, Screen.height / 1080f, 0.7f); }
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
                Color32 seat = Roles.ColorOf(1);
                h.Check(arrow != null && arrow.text.EndsWith("→") && tint.r == seat.r && tint.g == seat.g && tint.b == seat.b
                    && Mathf.Abs(tint.a / 255f - 0.7f) < 0.02f && P2Hud.GuiCamera().WorldToViewportPoint(arrow.transform.position).x > 0.5f,
                    "menu names: the visible offscreen arrow renders the nickname's missing native glyph at the right edge in seat colour");
            }
            finally { Object.Destroy(probe); }
        }
    }
}
