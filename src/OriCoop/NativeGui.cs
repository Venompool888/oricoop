using System.Collections.Generic;
using BepInEx.Configuration;
using CatlikeCoding.TextBox;
using UnityEngine;

namespace OriCoop
{
    public static class NativeGui
    {
        sealed class Element
        {
            public GameObject Go;
            public GameText Text;
            public MessageBox Box;
            public MeshRenderer Quad;
            public int Frame = -1;
            public string Page;
            public string Key;            // last layout inputs: skip rebuilding the text when nothing changed
            public Vector3 Pos, Scale;
            public Rect LastRect;         // screen rect last drawn into (for scroll-view hiding)
            public Mesh Mesh;
            public Material QuadMaterial;
            public readonly List<RendererMaterial> Materials = new List<RendererMaterial>();
        }

        sealed class RendererMaterial
        {
            public Renderer Renderer;
            public Material Material;
        }

        static readonly Ui.NativeGuiState s_state = new Ui.NativeGuiState();

        internal static void WarnOnce(string operation, string id, System.Exception error)
        {
            // Even logging must never interrupt gameplay or another element's cleanup.
            try
            {
                if (s_state.ShouldWarn(operation, id, error) && Plugin.Log != null)
                    Plugin.Log.LogWarning("NativeGui " + operation + " [" + id + "]: " + error.GetType().Name + ": " + error.Message);
            }
            catch (System.Exception) { }
        }

        static void Hide(string id)
        {
            try
            {
                Element e;
                if (id == null || !s_elements.TryGetValue(id, out e) || e == null) return;
                e.Key = null;
                if (e.Go != null) e.Go.SetActive(false);
            }
            catch (System.Exception error) { WarnOnce("Hide", id, error); }
        }

        static void HideAll(string page)
        {
            try
            {
                foreach (KeyValuePair<string, Element> pair in s_elements)
                    if (page == null || (pair.Value != null && pair.Value.Page == page)) Hide(pair.Key);
            }
            catch (System.Exception error) { WarnOnce("HideAll", page, error); }
        }

        static void Failed(string operation, string id, System.Exception error)
        {
            WarnOnce(operation, id, error);
            Hide(id);
            // Composite controls own these native elements as well as their text.
            Hide(id + ".highlight"); Hide(id + ".background"); Hide(id + ".hover"); Hide(id + ".hit");
        }

        static void DestroyOwned(string id, Object value)
        {
            try { if (value != null) Object.Destroy(value); }
            catch (System.Exception error) { WarnOnce("Destroy", id, error); }
        }

        static void RemoveElement(string id, Element e)
        {
            s_elements.Remove(id); Metrics.Remove(id); s_layoutMeshes.Remove(id);
            if (e == null) return;
            foreach (RendererMaterial owned in e.Materials) DestroyOwned(id, owned.Material);
            e.Materials.Clear();
            DestroyOwned(id, e.QuadMaterial); DestroyOwned(id, e.Mesh); DestroyOwned(id, e.Go);
        }

        static readonly Dictionary<string, Element> s_elements = new Dictionary<string, Element>();
        static readonly List<string> s_deadElements = new List<string>();
        public static readonly HashSet<string> PagesLastFrame = s_state.PagesLastFrame;
        public static int LabelsLastFrame;
        public static int DrawOrder { get; private set; }
        internal static System.Action<string> PageChanged { get { return s_state.PageChanged; } set { s_state.PageChanged = value; } }
        static ConfigEntry<bool> s_enabled;
        static GameText s_hint;

        public static bool Available
        {
            get
            {
                try { return AvailableCore(); }
                catch (System.Exception error)
                {
                    WarnOnce("Available", "*", error); HideAll(null); return false;
                }
            }
        }

        static bool AvailableCore()
        {
            if (s_enabled == null && !object.ReferenceEquals(Plugin.Instance, null))
                s_enabled = Plugin.Instance.Config.Bind("UI", "NativeText", true, "Draw ported UI with the game's font and GUI camera. false = IMGUI.");
            if ((s_enabled != null && !s_enabled.Value) || P2Hud.GuiCamera() == null || Game.UI.MessageController == null || Game.UI.MessageController.HintMessage == null) return false;
            if (s_hint == null || !s_hint.Ok)
            {
                if (s_hint != null) s_hint.Destroy();
                s_hint = new GameText("OriCoopNativeGuiFont");
            }
            return s_hint.Ok;
        }

        static bool Repaint { get { return Event.current != null && Event.current.type == EventType.Repaint; } }
        static void Frame()
        {
            if (!s_state.StartFrame(Time.frameCount)) return;
            DrawOrder = 32010;
            SyncOverlayCamera();
        }

        // Our own overlay camera: renders only OverlayLayer, after every game camera, clearing depth only. It copies
        // the GUI camera's transform and projection each frame, so screen-pixel maths done with the GUI camera holds.
        public const int OverlayLayer = 31;
        static Camera s_overlay;
        static void SyncOverlayCamera()
        {
            Camera gui = P2Hud.GuiCamera();
            if (gui == null) return;
            if (s_overlay == null)
            {
                GameObject go = new GameObject("OriCoop_NativeGuiCamera");
                Object.DontDestroyOnLoad(go);
                s_overlay = go.AddComponent<Camera>();
                s_overlay.clearFlags = CameraClearFlags.Depth;
                s_overlay.cullingMask = 1 << OverlayLayer;
                s_overlay.depth = 100f;
                s_overlay.useOcclusionCulling = false;
            }
            s_overlay.transform.position = gui.transform.position;
            s_overlay.transform.rotation = gui.transform.rotation;
            s_overlay.orthographic = gui.orthographic;
            s_overlay.orthographicSize = gui.orthographicSize;
            s_overlay.fieldOfView = gui.fieldOfView;
            s_overlay.nearClipPlane = gui.nearClipPlane;
            s_overlay.farClipPlane = gui.farClipPlane;
            s_overlay.rect = gui.rect;
        }

        public static void BeginPage(string page)
        {
            try
            {
                s_state.BeginPage(page);
                if (Repaint) { Frame(); s_state.AddPage(page); }
            }
            catch (System.Exception error) { s_state.Page = ""; WarnOnce("BeginPage", page, error); HideAll(page); }
        }
        public static void EndPage()
        {
            try { s_state.EndPage(); }
            catch (System.Exception error) { WarnOnce("EndPage", "*", error); HideAll(null); }
        }

        public static void LateUpdate()
        {
            try
            {
                s_state.Capture(Time.frameCount);
                LabelsLastFrame = s_state.LabelsLastFrame;
                bool available = AvailableCore();
                s_deadElements.Clear();
                foreach (KeyValuePair<string, Element> pair in s_elements)
                {
                    try
                    {
                        Element e = pair.Value;
                        if (e == null || e.Go == null) { s_deadElements.Add(pair.Key); continue; }
                        if ((!available || e.Frame < Time.frameCount - 1) && e.Go.activeSelf) e.Go.SetActive(false);
                    }
                    catch (System.Exception error) { Failed("LateUpdate", pair.Key, error); }
                }
                foreach (string id in s_deadElements) RemoveElement(id, s_elements[id]);
                s_deadElements.Clear();
            }
            catch (System.Exception error) { WarnOnce("LateUpdate", "*", error); HideAll(null); }
        }

        public static bool UsesGameFont(string id)
        {
            try
            {
                Element e;
                if (!s_elements.TryGetValue(id, out e) || e == null || e.Go == null || e.Box == null || e.Box.TextBox == null || e.Box.LanguageStyles == null) return false;
                TextStyleCollection current = e.Box.LanguageStyles.Current, actual = e.Box.TextBox.styleCollection;
                return current != null && actual != null && current.styles != null && actual.styles != null && current.styles.Length > 0 && actual.styles.Length > 0 &&
                    actual.styles[0].font != null && actual.styles[0].font == current.styles[0].font;
            }
            catch (System.Exception error) { Failed("UsesGameFont", id, error); return false; }
        }

        internal static bool PageUsesGameFont(string page)
        {
            try
            {
                foreach (string id in s_state.LastLabelIds)
                    if (s_elements.ContainsKey(id) && s_elements[id] != null && s_elements[id].Go != null && s_elements[id].Page == page && s_elements[id].Go.activeInHierarchy && UsesGameFont(id)) return true;
                return false;
            }
            catch (System.Exception error) { WarnOnce("PageUsesGameFont", page, error); HideAll(page); return false; }
        }

        static void Touch(Element e)
        {
            if (e == null || e.Go == null) throw new System.InvalidOperationException("Native element was destroyed");
            e.Frame = Time.frameCount; e.Page = s_state.Page;
            if (!e.Go.activeSelf) e.Go.SetActive(true);
            foreach (Transform t in e.Go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = OverlayLayer;
        }

        // Just past the near plane: in front of every game UI object, so a depth-writing game shader can't hide us.
        static float QuadZ(Camera camera) { return camera.nearClipPlane + 0.3f; }
        static float TextZ(Camera camera) { return camera.nearClipPlane + 0.2f; }
        // IMGUI fontSize is the em; the game font's glyphs fill only ~58% of its line height, so a line is this many ems.
        const float EmToLine = 1.7f;
        public static readonly Dictionary<string, string> Metrics = new Dictionary<string, string>();   // for tests

        static Vector3 Point(Camera camera, float x, float y, float z)
        {
            return camera.ScreenToWorldPoint(new Vector3(x, Screen.height - y, z));
        }

        static GUIStyle LabelStyle(float px, TextAnchor anchor, Color color)
        {
            GUIStyle st = new GUIStyle(GUI.skin.label);
            st.fontSize = Mathf.RoundToInt(px); st.alignment = anchor; st.wordWrap = false;
            st.normal.textColor = color;
            return st;
        }

        public static void Label(string id, Rect r, string text, float fontPx, TextAnchor anchor, Color color)
        {
            try { LabelCore(id, r, text, fontPx, anchor, color); }
            catch (System.Exception error) { Failed("Label", id, error); }
        }

        static void LabelCore(string id, Rect r, string text, float fontPx, TextAnchor anchor, Color color)
        {
            if (!Repaint) return;
            Frame();
            if (string.IsNullOrEmpty(text))
            {
                Element empty;
                if (s_elements.TryGetValue(id, out empty))
                {
                    if (empty == null || empty.Go == null) RemoveElement(id, empty);
                    else if (empty.Go.activeSelf) empty.Go.SetActive(false);
                }
                return;
            }
            if (!AvailableCore()) { Hide(id); GUI.Label(r, text, LabelStyle(fontPx, anchor, color)); return; }
            text = Ui.NativeGuiLogic.PlainText(text);   // IMGUI rich text means nothing to the game's parser
            if (fontPx <= 0f) fontPx = Mathf.Max(12f, Screen.height / 45f);   // style.fontSize 0 = IMGUI default size
            Element e;
            if (s_elements.TryGetValue(id, out e) && (e == null || e.Go == null || e.Box == null || e.Box.TextBox == null)) RemoveElement(id, e);
            if (!s_elements.TryGetValue(id, out e))
            {
                e = new Element(); s_elements.Add(id, e);
                e.Text = new GameText("OriCoopNativeGui_" + id); e.Text.NoBacking = true;
                e.Go = e.Text.Go;
                if (!e.Text.Ok) throw new System.InvalidOperationException("Native text could not be created");
                e.Box = e.Go.GetComponentInChildren<MessageBox>(true);
            }
            if (e.Box == null || e.Box.TextBox == null) throw new System.InvalidOperationException("Native text has no TextBox");
            Touch(e); e.LastRect = r;
            Camera camera = P2Hud.GuiCamera();
            int order = ++DrawOrder;
            string key = text + "|" + r + "|" + fontPx + "|" + anchor + "|" + color + "|" + Screen.width + "x" + Screen.height + "|" + L10n.Zh;
            if (key == e.Key && e.Go.activeSelf)
            {
                e.Go.transform.rotation = camera.transform.rotation;
                e.Go.transform.localScale = e.Scale; e.Go.transform.position = e.Pos;
                SetOrder(e, order);
                s_state.AddLabel(id);
                return;
            }
            e.Go.transform.rotation = camera.transform.rotation; e.Go.transform.localScale = Vector3.one;
            TextBox tb = e.Box.TextBox;
            tb.paddingLeft = tb.paddingRight = tb.paddingTop = tb.paddingBottom = 0f;
            tb.leftContour = tb.rightContour = new Vector2[] { Vector2.zero };
            tb.width = tb.maxHeight = 100000f;
            tb.horizontalAnchor = HorizontalAnchorMode.Left; tb.verticalAnchor = VerticalAnchorMode.Top;
            tb.alignment = AlignmentMode.Left;
            e.Box.OverrideText = "";
            e.Box.SetMessage(new MessageDescriptor(text));
            tb.size = 1f; tb.color = color; tb.RenderText();
            float width = 0f;
            for (int i = 0; i < tb.LineCount; i++)
            {
                TextBoxLine line = tb.GetLineInfo(i);
                for (int j = line.lastCharIndex; j >= line.firstCharIndex; j--)
                    if (tb.GetCharacterMetaData(j).type == CharType.Visible) { width = Mathf.Max(width, tb.GetCharacterMetaData(j).After); break; }
            }
            tb.width = Mathf.Max(0.0001f, width + 0.001f);
            int horizontal = (int)anchor % 3, vertical = (int)anchor / 3;
            tb.alignment = horizontal == 0 ? AlignmentMode.Left : horizontal == 1 ? AlignmentMode.Center : AlignmentMode.Right;
            tb.RenderText(); e.Box.RemoveMessageFade();
            float lineHeight = tb.LineCount > 0 ? tb.GetLineInfo(0).top - tb.GetLineInfo(0).bottom : 1f;
            float unit = (Point(camera, 0f, 0f, 8f) - Point(camera, 0f, 1f, 8f)).magnitude;
            float scale = fontPx * EmToLine * unit / Mathf.Max(0.0001f, tb.transform.TransformVector(Vector3.up * lineHeight).magnitude);
            float worldWidth = tb.transform.TransformVector(Vector3.right * width).magnitude;
            scale = Ui.NativeGuiLogic.FitScale(scale, worldWidth, r.width, unit);
            float pixelWidth = worldWidth * scale / unit;
            float pixelHeight = tb.transform.TransformVector(Vector3.up * (tb.boundsTop - tb.boundsBottom)).magnitude * scale / unit;
            e.Go.transform.localScale = Vector3.one * scale;
            Vector3 top = tb.transform.TransformPoint(new Vector3(0f, tb.boundsTop, 0f));
            e.Go.transform.position += Point(camera, Ui.NativeGuiLogic.Align(r.x, r.width, pixelWidth, horizontal),
                Ui.NativeGuiLogic.Align(r.y, r.height, pixelHeight, vertical), TextZ(camera)) - top;
            Metrics[id] = "font " + fontPx.ToString("0.#") + "px -> drawn " + pixelWidth.ToString("0") + "x" + pixelHeight.ToString("0") + "px (scale " + scale.ToString("0.####") + ", lineH " + lineHeight.ToString("0.###") + ", unit " + unit.ToString("0.#####") + ", lines " + tb.LineCount + ", rect " + r + ")";
            foreach (Renderer renderer in e.Go.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            e.Pos = e.Go.transform.position; e.Scale = e.Go.transform.localScale;
            SetOrder(e, order);
            e.Key = key; // A failed render must be rebuilt on the next attempt.
            s_state.AddLabel(id);
        }

        // Above every game UI renderer (the save slots use queue ~5433): our own range 7000+, in call order.
        const int QueueBase = 7000;
        static void SetOrder(Element e, int order)
        {
            if (e.Box == null || e.Box.TextBox == null || e.Box.TextBox.textRenderers == null)
                throw new System.InvalidOperationException("Native text has no textRenderers");
            for (int i = e.Materials.Count - 1; i >= 0; i--)
                if (e.Materials[i].Renderer == null)
                {
                    DestroyOwned(e.Page, e.Materials[i].Material);
                    e.Materials.RemoveAt(i);
                }
            foreach (TextRenderer tr in e.Box.TextBox.textRenderers)
            {
                Renderer renderer = tr != null ? tr.GetComponent<Renderer>() : null;
                if (renderer == null) continue;
                renderer.enabled = true; renderer.sortingOrder = order;
                RendererMaterial owned = null;
                foreach (RendererMaterial candidate in e.Materials)
                    if (candidate.Renderer == renderer) { owned = candidate; break; }
                if (owned == null)
                {
                    owned = new RendererMaterial(); owned.Renderer = renderer;
                    e.Materials.Add(owned);
                }
                Material source = renderer.sharedMaterial;
                if (owned.Material == null && source != null)
                {
                    owned.Material = new Material(source);
                    owned.Material.hideFlags = HideFlags.HideAndDontSave;
                }
                else if (source != null && source != owned.Material)
                {
                    // RenderText can restore the game's font material. Keep our instance, refresh its properties.
                    owned.Material.shader = source.shader;
                    owned.Material.CopyPropertiesFromMaterial(source);
                }
                if (owned.Material != null)
                {
                    renderer.sharedMaterial = owned.Material;
                    owned.Material.renderQueue = QueueBase + order;
                }
            }
        }

        public static void Panel(string id, Rect r, Color color)
        {
            try { PanelCore(id, r, color); }
            catch (System.Exception error) { Failed("Panel", id, error); }
        }

        static void PanelCore(string id, Rect r, Color color)
        { TextureCore(id, r, Texture2D.whiteTexture, color); }

        public static void Texture(string id, Rect r, Texture tex, Color tint)
        {
            try { TextureCore(id, r, tex, tint); }
            catch (System.Exception error) { Failed("Texture", id, error); }
        }

        static void TextureCore(string id, Rect r, Texture tex, Color tint)
        {
            if (!Repaint) return;
            Frame();
            if (!AvailableCore())
            {
                Hide(id);
                Color old = GUI.color;
                try { GUI.color = tint; GUI.DrawTexture(r, tex); }
                finally { GUI.color = old; }
                return;
            }
            Element e;
            if (s_elements.TryGetValue(id, out e) && (e == null || e.Go == null || e.Quad == null || e.QuadMaterial == null)) RemoveElement(id, e);
            if (!s_elements.TryGetValue(id, out e))
            {
                e = new Element(); s_elements.Add(id, e);
                e.Go = new GameObject("OriCoopNativeGui_" + id);
                Object.DontDestroyOnLoad(e.Go);
                Mesh mesh = new Mesh(); e.Mesh = mesh;
                mesh.vertices = new Vector3[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, -1f, 0f), new Vector3(0f, -1f, 0f) };
                mesh.uv = new Vector2[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) };
                mesh.colors = new Color[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 }; mesh.RecalculateBounds();
                e.Go.AddComponent<MeshFilter>().sharedMesh = mesh;
                e.Quad = e.Go.AddComponent<MeshRenderer>();
                e.QuadMaterial = new Material(Shader.Find("Particles/Alpha Blended"));
                e.QuadMaterial.hideFlags = HideFlags.HideAndDontSave;
                e.Quad.sharedMaterial = e.QuadMaterial;
            }
            Touch(e); e.LastRect = r;
            Camera camera = P2Hud.GuiCamera();
            e.Go.transform.position = Point(camera, r.x, r.y, QuadZ(camera));
            e.Go.transform.rotation = camera.transform.rotation;
            e.Go.transform.localScale = new Vector3((Point(camera, r.xMax, r.y, QuadZ(camera)) - e.Go.transform.position).magnitude,
                (Point(camera, r.x, r.yMax, QuadZ(camera)) - e.Go.transform.position).magnitude, 1f);
            e.QuadMaterial.mainTexture = tex;
            e.QuadMaterial.SetColor("_TintColor", tint * 0.5f);
            e.Quad.sortingOrder = ++DrawOrder;
            e.QuadMaterial.renderQueue = QueueBase + DrawOrder;
        }

        public static bool Button(string id, Rect r, string text, float fontPx, bool focused, Color color)
        {
            try { return ButtonCore(id, r, text, fontPx, focused, color); }
            catch (System.Exception error) { Failed("Button", id, error); return false; }
        }

        static bool ButtonCore(string id, Rect r, string text, float fontPx, bool focused, Color color)
        {
            if (!AvailableCore()) return GUI.Button(r, text, LabelStyle(fontPx, TextAnchor.MiddleCenter, color));
            bool hovered = Event.current != null && r.Contains(Event.current.mousePosition);
            if (Ui.NativeGuiLogic.Highlight(focused, hovered)) PanelCore(id + ".highlight", r, new Color(color.r, color.g, color.b, 0.14f * color.a));
            LabelCore(id, r, text, fontPx, TextAnchor.MiddleCenter, color);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        static Rect LayoutScreenRect(Rect r)
        {
            Vector2 p = GUIUtility.GUIToScreenPoint(new Vector2(r.x, r.y));
            return new Rect(p.x, p.y, r.width, r.height);
        }

        sealed class LayoutMesh
        {
            public Mesh Mesh;
            public Vector3[] Vertices;
            public Vector2[] UV;
        }

        static readonly Dictionary<string, List<LayoutMesh>> s_layoutMeshes = new Dictionary<string, List<LayoutMesh>>();
        static System.Reflection.PropertyInfo s_visibleRect;

        static void RestoreLayout(string id)
        {
            if (!Repaint) return;
            List<LayoutMesh> meshes;
            if (!s_layoutMeshes.TryGetValue(id, out meshes)) return;
            foreach (LayoutMesh m in meshes)
            {
                m.Mesh.vertices = m.Vertices; m.Mesh.uv = m.UV; m.Mesh.RecalculateBounds();
            }
        }

        // Native meshes bypass GUIClip: crop each glyph/texture quad to the current area or scroll viewport.
        // Inside a GUILayout scroll view: an element that is not fully inside the visible area is hidden
        // (no partial clipping of native text).
        static void ClipLayout(string id)
        {
            if (!Repaint) return;
            Element e;
            if (!s_elements.TryGetValue(id, out e) || e == null || e.Go == null) return;
            // Resolve inside the guarded layout call, never in NativeGui's type initializer.
            if (s_visibleRect == null)
                s_visibleRect = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip")
                    .GetProperty("visibleRect", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Rect clip = LayoutScreenRect((Rect)s_visibleRect.GetValue(null, null));
            Rect r = e.LastRect;
            bool inside = Ui.NativeGuiLogic.Inside(r.xMin, r.xMax, r.yMin, r.yMax, clip.xMin, clip.xMax, clip.yMin, clip.yMax);
            foreach (Renderer renderer in e.Go.GetComponentsInChildren<Renderer>(true))
                if (renderer != null && !inside) renderer.enabled = false;
        }

        static void LayoutText(string id, Rect r, string text, GUIStyle style)
        {
            if (!Repaint) return;
            text = Ui.NativeGuiLogic.PlainText(text);
            if (style.wordWrap)
            {
                GUIContent content = new GUIContent(text);
                text = Ui.NativeGuiLogic.Wrap(text, delegate(int i) { return style.GetCursorPixelPosition(r, content, i).y; });
            }
            r = style.padding.Remove(r);
            int size = style.fontSize;
            if (size == 0) size = style.font != null ? style.font.fontSize : GUI.skin.font != null ? GUI.skin.font.fontSize : 14;
            if (size == 0) size = 14;
            Color color = style.normal.textColor * GUI.color * GUI.contentColor;
            if (!GUI.enabled) color.a *= 0.5f;
            RestoreLayout(id);
            LabelCore(id, LayoutScreenRect(r), text, size, style.alignment, color);
            ClipLayout(id);
        }

        public static void LayoutLabel(string id, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            try { LayoutLabelCore(id, text, style, options); }
            catch (System.Exception error) { Failed("LayoutLabel", id, error); }
        }

        static void LayoutLabelCore(string id, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style, options);
            if (!AvailableCore()) { GUI.Label(r, text, style); return; }
            LayoutText(id, r, text, style);
        }

        public static bool LayoutButton(string id, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            try { return LayoutButtonCore(id, text, style, options); }
            catch (System.Exception error) { Failed("LayoutButton", id, error); return false; }
        }

        static bool LayoutButtonCore(string id, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style, options);
            if (!AvailableCore()) return GUI.Button(r, text, style);
            if (style.normal.background != null)
            {
                RestoreLayout(id + ".background");
                TextureCore(id + ".background", LayoutScreenRect(r), style.normal.background, GUI.color);
                ClipLayout(id + ".background");
            }
            Color color = style.normal.textColor * GUI.color * GUI.contentColor;
            if (!GUI.enabled) color.a *= 0.5f;
            // Button's hit rect stays in the current GUI group; its native drawing uses screen pixels.
            Vector2 mouse = Event.current.mousePosition;
            bool hovered = GUI.enabled && r.Contains(mouse);
            if (hovered)
            {
                RestoreLayout(id + ".hover");
                PanelCore(id + ".hover", LayoutScreenRect(r), new Color(color.r, color.g, color.b, 0.14f * color.a));
                ClipLayout(id + ".hover");
            }
            LayoutText(id, r, text, style);
            return ButtonCore(id + ".hit", r, "", 0f, false, Color.clear);
        }

        public static void LayoutBox(string id, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            try { LayoutBoxCore(id, text, style, options); }
            catch (System.Exception error) { Failed("LayoutBox", id, error); }
        }

        static void LayoutBoxCore(string id, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style, options);
            if (!AvailableCore()) { GUI.Box(r, text, style); return; }
            RestoreLayout(id + ".background");
            PanelCore(id + ".background", LayoutScreenRect(r), new Color(0.12f, 0.12f, 0.12f, 0.95f) * GUI.color);
            ClipLayout(id + ".background");
            LayoutText(id, r, text, style);
        }
    }
}
