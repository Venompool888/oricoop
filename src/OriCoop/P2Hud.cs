using System.Reflection;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // P2's health pips above P2, drawn by the game's own HUD camera (guiCamera, the one that draws P1's health bar),
    // not as a mod screen overlay. (The gameplay camera's post-processing recomposes the frame, so extra world objects
    // never reach the screen.) Visibility and opacity copy the game's own P1 HUD (SeinUI): whenever the game hides or fades
    // P1's HUD (pause/menus, cutscene letterbox, screen fades), P2's follows.
    public static class P2Hud
    {
        sealed class Hud
        {
            public GameObject Go;
            public TextMesh Text, Shadow;
            public MeshRenderer Renderer;
            public string Plain = "";
        }
        static readonly Dictionary<Puppet, Hud> s_huds = new Dictionary<Puppet, Hud>();
        static Hud First
        {
            get { Hud hud; return Players.First != null && s_huds.TryGetValue(Players.First, out hud) ? hud : null; }
        }
        static readonly FieldInfo s_opacity = AccessTools.Field(typeof(SeinUI), "m_lastOpacity");

        // 0..1: how visible the game's own HUD is right now (0 when hidden).
        public static float GameHudOpacity()
        {
            SeinUI ui = UI.SeinUI;
            if (ui == null || ui.UI == null || !ui.UI.activeInHierarchy) return 0f;
            float o = s_opacity != null ? (float)s_opacity.GetValue(ui) : 1f;
            return float.IsNaN(o) ? 0f : Mathf.Clamp01(o);
        }

        public static bool Visible { get { return First != null && First.Go != null && First.Go.activeSelf && First.Text.color.a > 0.01f; } }
        public static Renderer Renderer { get { return First == null ? null : First.Renderer; } }
        public static bool TestHide;
        public static string Text { get { return First == null ? "" : First.Plain; } }
        public static Vector3 WorldPosition { get { return First != null && First.Go != null ? First.Go.transform.position : Vector3.zero; } }
        internal static Camera GuiCamera()
        {
            if (UI.Cameras.System != null && UI.Cameras.System.GUICamera != null && UI.Cameras.System.GUICamera.Camera != null) return UI.Cameras.System.GUICamera.Camera;
            return null;
        }

        internal static int HudLayer()
        {
            Camera gui = GuiCamera();
            if (gui != null) for (int i = 0; i < 32; i++) if ((gui.cullingMask & (1 << i)) != 0) return i;
            return 8;
        }

        static void Create(Puppet puppet, Hud hud)
        {
            hud.Go = new GameObject("OriCoop_P" + puppet.Seat + "Hud");
            Object.DontDestroyOnLoad(hud.Go);
            hud.Text = hud.Go.AddComponent<TextMesh>();
            Font font = null;
            try { font = Font.CreateDynamicFontFromOSFont(new string[] { "Arial", "Segoe UI Symbol", "Microsoft YaHei" }, 64); } catch (System.Exception) { }
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            hud.Text.font = font;
            hud.Text.fontSize = 64;
            hud.Text.characterSize = 0.13f;   // about 0.8 world units tall: readable at the zoomed-out camera
            hud.Text.anchor = TextAnchor.LowerCenter;
            hud.Text.alignment = TextAlignment.Center;
            hud.Text.fontStyle = FontStyle.Bold;
            hud.Renderer = hud.Go.GetComponent<MeshRenderer>();
            hud.Renderer.sharedMaterial = font.material;   // the font's own material follows its glyph atlas updates
            hud.Renderer.sharedMaterial.renderQueue = 3000; // ordinary HUD queue: the game's menus and fades draw over it
            hud.Renderer.sortingOrder = 32000;
            hud.Go.layer = HudLayer();
            // A soft dark copy just behind the text keeps it readable over bright backgrounds (light shafts, glow).
            GameObject sh = new GameObject("OriCoop_P2HudShadow");
            sh.transform.SetParent(hud.Go.transform, false);
            hud.Shadow = sh.AddComponent<TextMesh>();
            hud.Shadow.font = font; hud.Shadow.fontSize = hud.Text.fontSize; hud.Shadow.anchor = hud.Text.anchor;
            hud.Shadow.alignment = hud.Text.alignment; hud.Shadow.fontStyle = hud.Text.fontStyle;
            MeshRenderer shr = sh.GetComponent<MeshRenderer>();
            shr.sharedMaterial = font.material;
            shr.sortingOrder = 31999;
            sh.layer = hud.Go.layer;
        }

        // Online: one tag for the partner. Line 1 = role (in the role's colour) + "主机/伙伴" + Steam name, line 2 = the
        // health pips in the role's colour. Local co-op keeps the single "P2 ●●●" line.
        static string Compose(Hud hud, int role, string pips, float a, bool shadow)
        {
            Color white = new Color(0.945f, 0.988f, 1f);
            Color dark = new Color(0f, 0.02f, 0.06f);
            if (!NetPlay.Enabled)
            {
                hud.Plain = "P2 " + pips;
                return "<color=" + Roles.HexOf(shadow ? dark : Roles.P2Color, shadow ? a * 0.55f : a) + ">" + hud.Plain + "</color>";
            }
            string label = Roles.Tag(role) + " · " + Roles.WordZh(role);
            string name = NetPlay.SteamPartnerName;
            if (!string.IsNullOrEmpty(name)) label += " · " + name;
            string rest = label.Substring(Roles.Tag(role).Length);
            hud.Plain = label + "\n" + pips;
            string roleHex = shadow ? Roles.HexOf(dark, a * 0.55f) : Roles.Hex(role, a);
            string restHex = shadow ? roleHex : Roles.HexOf(white, a);
            return "<color=" + roleHex + ">" + Roles.Tag(role) + "</color><color=" + restHex + ">" + rest + "</color>\n"
                + "<size=80><color=" + roleHex + ">" + pips + "</color></size>";
        }

        // Called every frame after the camera update.
        public static void Update(SeinCharacter p2)
        {
            if (p2 == null) { Destroy(); return; }
            Update(Players.OfSein(p2));
        }

        public static void Update(Puppet puppet)
        {
            SeinCharacter p2 = puppet.Sein;
            Hud hud;
            if (!s_huds.TryGetValue(puppet, out hud))
            {
                hud = new Hud(); s_huds.Add(puppet, hud); Create(puppet, hud);
            }
            float a = p2.gameObject.activeInHierarchy && !TestHide ? GameHudOpacity() : 0f;
            if (a <= 0.01f) { if (hud.Go.activeSelf) hud.Go.SetActive(false); return; }
            if (!hud.Go.activeSelf) hud.Go.SetActive(true);
            var h = p2.Mortality.Health;
            int cells = Mathf.Max(1, h.MaxHealth / 4), full = Mathf.Clamp(Mathf.CeilToInt(h.Amount / 4f), 0, cells);
            string pips = new string('●', full) + new string('○', cells - full);
            hud.Text.text = Compose(hud, puppet.Seat, pips, a, false);
            hud.Shadow.text = Compose(hud, puppet.Seat, pips, a, true);
            hud.Text.color = new Color(1f, 0.85f, 0.4f, a);   // Visible reads this alpha; the colour itself comes from the tags
            Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = GuiCamera();
            if (game == null || gui == null) { hud.Go.SetActive(false); return; }
            Vector3 vp = game.WorldToViewportPoint(new Vector3(p2.Position.x, p2.Position.y + 1.3f, p2.Position.z));
            if (vp.z < 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { hud.Go.SetActive(false); return; }
            Vector3 wp = gui.ViewportToWorldPoint(new Vector3(vp.x, vp.y, 8f));
            hud.Go.transform.position = wp;
            hud.Go.transform.rotation = gui.transform.rotation;
            // About 3.5% of the screen height, whatever the HUD camera's scale.
            float worldH = gui.orthographic ? gui.orthographicSize * 2f * 0.035f : 0.3f;
            hud.Text.characterSize = worldH / (hud.Text.fontSize * 0.12f);
            hud.Shadow.characterSize = hud.Text.characterSize;
            // Parent space is the HUD camera's orientation: down-right and a hair farther from the camera.
            float off = worldH * 0.05f / Mathf.Max(0.0001f, hud.Go.transform.lossyScale.x);
            hud.Shadow.transform.localPosition = new Vector3(off, -off, 0.01f);
        }

        public static void Destroy(Puppet puppet)
        {
            Hud hud;
            if (!s_huds.TryGetValue(puppet, out hud)) return;
            if (hud.Go != null) Object.Destroy(hud.Go);
            s_huds.Remove(puppet);
        }

        public static void Destroy()
        {
            foreach (Hud hud in s_huds.Values) if (hud.Go != null) Object.Destroy(hud.Go);
            s_huds.Clear();
        }
    }
}
