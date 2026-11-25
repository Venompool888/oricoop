using System.Reflection;
using System.Collections.Generic;
using CatlikeCoding.TextBox;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // The name tag above a teammate (no health dots any more), drawn by the game's own HUD camera (guiCamera, the one that draws P1's health bar),
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
            public GameText GameText;
            public MessageBox Box;
            public Renderer GameRenderer;
            public string Plain = "";
            // Per-frame caches (UpdateTag runs every frame per teammate): the composed tag strings, what the game-font
            // text was last rendered with, and our own copy of each glyph renderer's material.
            public bool ComposedOnline; public int ComposedRole; public float ComposedAlpha = -1f;
            public string ComposedName, ComposedTagFormat, ComposedWord, ComposedLabelFormat, Composed, ComposedShadow;
            public string MeshText, MeshShadow;
            public MessageBox RenderedBox; public string RenderedText; public Color32 RenderedColor;
            public readonly List<KeyValuePair<Renderer, Material>> Materials = new List<KeyValuePair<Renderer, Material>>();
        }
        static readonly Dictionary<Puppet, Hud> s_huds = new Dictionary<Puppet, Hud>();
        static readonly HashSet<string> s_failures = new HashSet<string>();
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

        static bool HasGameText(Hud hud)
        {
            return hud != null && hud.GameText != null && hud.GameText.Ok && hud.Box != null && hud.Box.TextBox != null;
        }
        public static bool Visible
        {
            get
            {
                Hud hud = First;
                return hud != null && ((HasGameText(hud) && hud.GameText.Go.activeSelf && hud.Box.TextBox.color.a > 0.01f)
                    || (hud.Go != null && hud.Go.activeSelf && hud.Text != null && hud.Text.color.a > 0.01f));
            }
        }
        public static Renderer Renderer
        {
            get
            {
                Hud hud = First;
                if (hud == null) return null;
                return HasGameText(hud) && hud.GameText.Go.activeSelf && hud.GameRenderer != null ? hud.GameRenderer
                    : hud.Renderer != null ? hud.Renderer : null;
            }
        }
        public static bool TestHide;
        public static string Text { get { Hud hud = First; return hud == null ? "" : hud.Plain ?? ""; } }
        public static Vector3 WorldPosition
        {
            get
            {
                Hud hud = First;
                if (HasGameText(hud) && hud.GameText.Go.activeSelf) return hud.GameText.Go.transform.position;
                return hud != null && hud.Go != null ? hud.Go.transform.position : Vector3.zero;
            }
        }

        static void Warn(string operation, System.Exception error)
        {
            string failure = operation + ": " + error;
            if (!s_failures.Add(failure)) return;
            try { if (Plugin.Log != null) Plugin.Log.LogWarning("P2Hud " + failure); }
            catch (System.Exception) { } // Diagnostics must not abort the rest of the frame either.
        }

        static void Hide(Hud hud)
        {
            if (hud == null) return;
            try { if (hud.Go != null) hud.Go.SetActive(false); }
            catch (System.Exception e) { Warn("hide TextMesh", e); }
            HideGameText(hud);
        }

        static void HideGameText(Hud hud)
        {
            try { if (hud.GameText != null) hud.GameText.Hide(); }
            catch (System.Exception e) { Warn("hide GameText", e); }
        }

        static void DestroyGameText(Hud hud)
        {
            try { if (hud.GameText != null) hud.GameText.Destroy(); }
            catch (System.Exception e) { Warn("destroy GameText", e); }
            hud.GameText = null; hud.Box = null; hud.GameRenderer = null; hud.RenderedBox = null;
            foreach (KeyValuePair<Renderer, Material> pair in hud.Materials) if (pair.Value != null) Object.Destroy(pair.Value);
            hud.Materials.Clear();
        }
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
            font = OriCoop.Ui.OsFonts.Shared(new string[] { "Arial", "Segoe UI Symbol", "Microsoft YaHei" }, 64);   // T-153: one font, not one per tag
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null) return;
            hud.Text.font = font;
            hud.Text.fontSize = 64;
            hud.Text.characterSize = 0.13f;   // about 0.8 world units tall: readable at the zoomed-out camera
            hud.Text.anchor = TextAnchor.LowerCenter;
            hud.Text.alignment = TextAlignment.Center;
            hud.Text.fontStyle = FontStyle.Bold;
            hud.Renderer = hud.Go.GetComponent<MeshRenderer>();
            if (hud.Renderer == null || font.material == null) return;
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
            if (shr == null) return;
            shr.sharedMaterial = font.material;
            shr.sortingOrder = 31999;
            sh.layer = hud.Go.layer;
        }

        // Online: one tag with the seat in its colour, the role, and the Steam name. Local co-op: "P2". The
        // health dots that used to follow it are gone (T-061b): health is on the status bars at the bottom left.
        // Compose(.., false) and Compose(.., true) rebuilt (format, substring, 4 hex colours) every frame; the inputs
        // only change on a fade, a language or role change, or a new partner name.
        static void ComposeBoth(Hud hud, int role, float a)
        {
            bool online = NetPlay.Enabled;
            string name = null, tagFormat = null, word = null, labelFormat = null;
            if (online)
            {
                name = NetPlay.SteamPartnerName;
                tagFormat = L10n.T("roles.tag");
                word = Roles.Word(role);
                labelFormat = L10n.T(string.IsNullOrEmpty(name) ? "roles.label" : "roles.named_label");
            }
            if (hud.Composed != null && hud.ComposedOnline == online && hud.ComposedAlpha == a && hud.ComposedRole == role
                && hud.ComposedName == name && (object)hud.ComposedTagFormat == tagFormat && hud.ComposedWord == word
                && (object)hud.ComposedLabelFormat == labelFormat) return;
            hud.Composed = Compose(hud, role, a, false);
            hud.ComposedShadow = Compose(hud, role, a, true);
            hud.ComposedOnline = online; hud.ComposedAlpha = a; hud.ComposedRole = role; hud.ComposedName = name;
            hud.ComposedTagFormat = tagFormat; hud.ComposedWord = word; hud.ComposedLabelFormat = labelFormat;
        }

        // TextMesh.text rebuilds its mesh on every assignment; the tag used to set it twice per frame (label, then "").
        static void SetMesh(Hud hud, string text, string shadow)
        {
            if ((object)hud.MeshText != text) { hud.Text.text = text; hud.MeshText = text; }
            if ((object)hud.MeshShadow != shadow) { hud.Shadow.text = shadow; hud.MeshShadow = shadow; }
        }

        // Our own instance of a glyph renderer's material (sorting/queue changes must not touch the game's font).
        // `renderer.material` made a fresh copy every time RenderText had put the game's font material back.
        static Material OwnMaterial(Hud hud, Renderer renderer)
        {
            Material source = renderer.sharedMaterial, own = null;
            foreach (KeyValuePair<Renderer, Material> pair in hud.Materials)
                if (pair.Key == renderer) { own = pair.Value; break; }
            if (own == null)
            {
                if (source == null) return null;
                own = new Material(source); own.hideFlags = HideFlags.HideAndDontSave;
                hud.Materials.Add(new KeyValuePair<Renderer, Material>(renderer, own));
            }
            else if (source != null && source != own)
            {
                own.shader = source.shader;
                own.CopyPropertiesFromMaterial(source);
            }
            renderer.sharedMaterial = own;
            return own;
        }

        static string Compose(Hud hud, int role, float a, bool shadow)
        {
            Color white = new Color(0.945f, 0.988f, 1f);
            Color dark = new Color(0f, 0.02f, 0.06f);
            if (!NetPlay.Enabled)
            {
                hud.Plain = "P2";
                return "<color=" + Roles.HexOf(shadow ? dark : Roles.P2Color, shadow ? a * 0.55f : a) + ">" + hud.Plain + "</color>";
            }
            string name = NetPlay.SteamPartnerName;
            string label = string.IsNullOrEmpty(name) ? L10n.F("roles.label", Roles.Tag(role), Roles.Word(role))
                : L10n.F("roles.named_label", Roles.Tag(role), Roles.Word(role), name);
            string rest = label.Substring(Roles.Tag(role).Length);
            hud.Plain = label;
            string roleHex = shadow ? Roles.HexOf(dark, a * 0.55f) : Roles.Hex(role, a);
            string restHex = shadow ? roleHex : Roles.HexOf(white, a);
            return "<color=" + roleHex + ">" + Roles.Tag(role) + "</color><color=" + restHex + ">" + rest + "</color>";
        }

        // Called every frame after the camera update.
        public static void Update(SeinCharacter p2)
        {
            if (p2 == null) { Destroy(); return; }
            Update(Players.OfSein(p2));
        }

        public static void Update(Puppet puppet)
        {
            Hud hud = null;
            try
            {
                if (puppet == null) return;
                s_huds.TryGetValue(puppet, out hud);
                if (puppet.Sein == null) { Hide(hud); return; }
                UpdateTag(puppet, ref hud);
            }
            catch (System.Exception e)
            {
                Warn("update tag", e);
                Hide(hud);
            }
        }

        static void UpdateTag(Puppet puppet, ref Hud hud)
        {
            SeinCharacter p2 = puppet.Sein;
            if (hud == null)
            {
                hud = new Hud(); s_huds[puppet] = hud;
            }
            if (hud.Go == null || hud.Text == null || hud.Shadow == null || hud.Renderer == null)
            {
                if (hud.Go != null) Object.Destroy(hud.Go);
                Create(puppet, hud);
                hud.MeshText = hud.MeshShadow = null;   // new TextMeshes: nothing set on them yet
            }
            if (hud.Go == null || hud.Text == null || hud.Shadow == null || hud.Renderer == null
                || p2.Mortality == null || p2.Mortality.Health == null) { Hide(hud); return; }
            float a = p2.gameObject.activeInHierarchy && !TestHide ? GameHudOpacity() : 0f;
            if (a <= 0.01f) { Hide(hud); return; }
            if (!hud.Go.activeSelf) hud.Go.SetActive(true);
            ComposeBoth(hud, puppet.Seat, a);
            SetMesh(hud, hud.Composed, hud.ComposedShadow);
            hud.Text.color = new Color(1f, 0.85f, 0.4f, a);   // Visible reads this alpha; the colour itself comes from the tags
            Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = GuiCamera();
            if (game == null || gui == null) { Hide(hud); return; }
            Vector3 vp = game.WorldToViewportPoint(new Vector3(p2.Position.x, p2.Position.y + 1.3f, p2.Position.z));
            if (vp.z < 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { Hide(hud); return; }
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
            // Online the tag is drawn in the game's own font (below); the TextMesh stays as the fallback.
            if (!NetPlay.Enabled) { HideGameText(hud); return; }
            try
            {
                if (!HasGameText(hud))
                {
                    DestroyGameText(hud);
                    // Do not retain a failed wrapper when the hint prefab is not ready yet.
                    MessageControllerB mc = UI.MessageController;
                    if (mc != null && mc.HintMessage != null)
                    {
                        hud.GameText = new GameText("OriCoop_P" + puppet.Seat + "GameHud") { NoBacking = true };
                        if (hud.GameText.Ok) hud.Box = hud.GameText.Go.GetComponentInChildren<MessageBox>(true);
                        if (HasGameText(hud))
                        {
                            hud.Box.TextBox.horizontalAnchor = HorizontalAnchorMode.Center;
                            hud.Box.TextBox.verticalAnchor = VerticalAnchorMode.Bottom;
                            hud.Box.TextBox.alignment = AlignmentMode.Center;
                        }
                    }
                }
                if (HasGameText(hud))
                {
                    hud.GameText.SetText(hud.Plain);
                    Color color = Roles.ColorOf(puppet.Seat);
                    color.a = a;
                    Color32 shown = color;
                    // Re-render only when the text, colour or alpha changed (RenderText is a full layout + mesh rebuild).
                    bool render = hud.GameRenderer == null || hud.RenderedBox != hud.Box || hud.RenderedText != hud.Plain
                        || shown.r != hud.RenderedColor.r || shown.g != hud.RenderedColor.g || shown.b != hud.RenderedColor.b || shown.a != hud.RenderedColor.a;
                    if (render)
                    {
                        hud.GameRenderer = null;
                        hud.Box.TextBox.color = color;
                        hud.Box.TextBox.RenderText();
                        TextRenderer[] renderers = hud.Box.TextBox.textRenderers;
                        if (renderers != null) foreach (TextRenderer tr in renderers)
                        {
                            // MoonIconRenderer is a TextRenderer without a Unity Renderer on its own object.
                            Renderer renderer = tr != null ? tr.GetComponent<Renderer>() : null;
                            if (renderer == null) continue;
                            Material material = OwnMaterial(hud, renderer);
                            if (material == null) continue;
                            renderer.sortingOrder = 32000;
                            material.renderQueue = 3000;
                            hud.GameRenderer = renderer;
                        }
                        hud.RenderedBox = hud.GameRenderer != null ? hud.Box : null;
                        hud.RenderedText = hud.Plain; hud.RenderedColor = shown;
                    }
                    if (hud.GameRenderer != null && hud.GameText.Place(new Vector3(p2.Position.x, p2.Position.y + 1.3f, p2.Position.z), 0.035f, 1f, 1f))
                    {
                        SetMesh(hud, "", "");
                        return;
                    }
                }
                else hud.GameRenderer = null;
            }
            catch (System.Exception e) { Warn("GameText tag", e); DestroyGameText(hud); }
            HideGameText(hud); // The already positioned TextMesh remains visible as the fallback.
        }

        public static void Destroy(Puppet puppet)
        {
            if (puppet == null) return;
            Hud hud;
            if (!s_huds.TryGetValue(puppet, out hud)) return;
            if (hud.Go != null) Object.Destroy(hud.Go);
            DestroyGameText(hud);
            s_huds.Remove(puppet);
        }

        public static void Destroy()
        {
            foreach (Hud hud in s_huds.Values)
            {
                if (hud.Go != null) Object.Destroy(hud.Go);
                DestroyGameText(hud);
            }
            s_huds.Clear();
        }
    }
}
