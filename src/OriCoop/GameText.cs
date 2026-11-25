using System;
using System.Reflection;
using Game;
using UnityEngine;

namespace OriCoop
{
    // A piece of text drawn the way the game draws its own hints: a copy of MessageControllerB's HintMessage prefab
    // (game font, Chinese style when the game is in Chinese, button icons through [SpiritFlame] etc.), placed at a
    // world point each frame on the HUD camera. Its fade/auto-destroy component is removed: we show and hide it.
    // If the prefab can't be made, Ok is false and the caller falls back to its own TextMesh.
    public class GameText
    {
        public GameObject Go;
        MessageBox m_box;
        Renderer[] m_renderers;
        string m_text;
        float m_baseH, m_baseW;   // text size in world units at scale 1 (measured after each text change)
        Vector3 m_scale0;
        readonly string m_name;
        bool m_warnedCreate, m_warnedPlace, m_warnedText;
        int m_layer = -1;
        public bool Ok { get { return Go != null && m_box != null; } }
        public bool NoBacking;    // hide the hint's dark backing strip (for small labels drawn over pictures)
        readonly System.Collections.Generic.Dictionary<Renderer, Material> m_own = new System.Collections.Generic.Dictionary<Renderer, Material>();
        bool m_loggedBright;
        internal string BrightDebug = "";

        public GameText(string name) { m_name = name; Create(); }

        void WarnOnce(ref bool warned, string reason)
        {
            if (warned) return;
            warned = true;
            Plugin.Log.LogWarning("game text '" + m_name + "': " + reason);
        }

        bool Create()
        {
            try
            {
                MessageControllerB mc = UI.MessageController;
                if (mc == null || mc.HintMessage == null)
                { WarnOnce(ref m_warnedCreate, "hint prefab unavailable"); return false; }
                DropOwnMaterials();   // a copy the game destroyed (checkpoint restore) left its materials behind
                Go = (GameObject)UnityEngine.Object.Instantiate(mc.HintMessage);
                Go.name = m_name;
                UnityEngine.Object.DontDestroyOnLoad(Go);
                m_box = Go.GetComponentInChildren<MessageBox>(true);
                if (m_box == null || m_box.TextBox == null)
                { WarnOnce(ref m_warnedCreate, "hint prefab has no text box"); Destroy(); return false; }
                foreach (MessageBoxVisibility v in Go.GetComponentsInChildren<MessageBoxVisibility>(true)) UnityEngine.Object.DestroyImmediate(v);
                m_box.Visibility = null;
                if (m_box.WriteOutTextBox != null) { UnityEngine.Object.DestroyImmediate(m_box.WriteOutTextBox); m_box.WriteOutTextBox = null; }
                m_scale0 = Go.transform.localScale;
                if (m_scale0.x <= 0f) m_scale0 = Vector3.one;
                m_text = null; SetHudLayer();
                Go.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                WarnOnce(ref m_warnedCreate, "cannot create hint: " + e.Message);
                Destroy();
                return false;
            }
        }

        public string Text { get { return m_text ?? ""; } }

        public bool SetText(string text)
        {
            // The game's prefab is destroyed on checkpoint restore, even though it stays alive across scene changes.
            if (!Ok && !Create()) return false;
            if (text == m_text && m_baseH > 0f) return true;
            m_text = null;
            bool was = Go.activeSelf;
            if (!was) Go.SetActive(true);
            Go.transform.localScale = m_scale0;
            try { m_box.SetMessage(new MessageDescriptor(text)); m_box.RemoveMessageFade(); }
            catch (Exception e) { WarnOnce(ref m_warnedText, "cannot render hint: " + e.Message); Hide(); return false; }
            // RenderText creates the glyph and icon objects lazily; assign their layer after they exist.
            SetHudLayer();
            System.Collections.Generic.List<Renderer> rs = new System.Collections.Generic.List<Renderer>();
            foreach (var tr in m_box.TextBox.textRenderers)
                if (tr != null) rs.AddRange(tr.GetComponentsInChildren<Renderer>());
            m_renderers = rs.ToArray();
            if (NoBacking && rs.Count > 0)
                foreach (Renderer r in Go.GetComponentsInChildren<Renderer>(true)) if (!rs.Contains(r)) r.enabled = false;
            Brighten(rs);
            Bounds b;
            if (!Measure(out b)) return CannotPlace("no glyph geometry");
            m_baseH = b.size.y / m_scale0.y; m_baseW = b.size.x / m_scale0.x;
            if (!was) Hide();
            m_text = text;
            return true;
        }

        // T-255 (playtest 6: the force-start and leave prompts, and the new hint boxes, looked dim next to the game's own
        // hints). Likely cause, not yet confirmed in game: the copy's renderers share the game's hint materials, and the
        // game's fades write the alpha of shared materials (TitleMenu.FixClonedGlows found that on the title menu). With
        // our copy's MessageBoxVisibility gone nothing drives that alpha for us, so the copy drew at whatever alpha the last
        // native hint had faded the shared materials to. The first SetText logs the alphas it found. Every renderer of the copy now gets its own material: the text and its
        // button icons at full alpha, as a native hint draws when fully shown, the backing strip at the alpha its own fade
        // recorded for it. Our copy never writes the game's materials, and the game's fades no longer dim our copy.
        static readonly FieldInfo s_taData = typeof(TransparencyAnimator).GetField("m_rendererData", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly bool s_mutateDim = NativeGui.MutationOn("ORICOOP_MUTATE_DIM_PROMPTS");   // test: the old, shared materials
        void Brighten(System.Collections.Generic.List<Renderer> glyphs)
        {
            if (s_mutateDim) return;
            try
            {
                System.Collections.Generic.Dictionary<Renderer, float> full = FullAlphas();
                float textBefore = -1f, backBefore = -1f, backAfter = -1f; int owned = 0;
                foreach (Renderer r in Go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    Material shared = r.sharedMaterial;
                    if (shared == null || !shared.HasProperty("_Color")) continue;
                    Material own;
                    if (!m_own.TryGetValue(r, out own) || own == null)
                    {
                        own = new Material(shared); own.hideFlags = HideFlags.HideAndDontSave; m_own[r] = own;
                    }
                    else if (shared != own)
                    {
                        // RenderText can put the game's font material back: keep our instance, take its new properties.
                        own.shader = shared.shader; own.CopyPropertiesFromMaterial(shared);
                    }
                    r.sharedMaterial = own; owned++;
                    Color c = own.GetColor("_Color");
                    float target;
                    if (glyphs.Contains(r))
                    {
                        textBefore = textBefore < 0f ? c.a : Mathf.Min(textBefore, c.a);
                        target = 1f;
                    }
                    else
                    {
                        backBefore = c.a;
                        if (!full.TryGetValue(r, out target) || target <= 0f) target = c.a;
                        backAfter = target;
                    }
                    if (c.a != target) { c.a = target; own.SetColor("_Color", c); }
                }
                Color tc = m_box.TextBox.color;
                BrightDebug = "text a " + textBefore.ToString("0.00") + "->1, backing a " + backBefore.ToString("0.00") + "->" + backAfter.ToString("0.00")
                    + ", text colour " + tc + ", " + owned + " own materials";
                if (!m_loggedBright) { m_loggedBright = true; Plugin.Log.LogInfo("game text '" + m_name + "': " + BrightDebug); }
            }
            catch (Exception e) { WarnOnce(ref m_warnedText, "cannot brighten hint: " + e.Message); }
        }

        // The alpha each renderer of the copy has when its hint is fully shown, as the copy's own fades recorded it.
        System.Collections.Generic.Dictionary<Renderer, float> FullAlphas()
        {
            System.Collections.Generic.Dictionary<Renderer, float> full = new System.Collections.Generic.Dictionary<Renderer, float>();
            if (s_taData == null) return full;
            foreach (TransparencyAnimator ta in Go.GetComponentsInChildren<TransparencyAnimator>(true))
            {
                System.Collections.IList data = s_taData.GetValue(ta) as System.Collections.IList;
                if (data == null) continue;
                foreach (object d in data)
                {
                    if (d == null) continue;
                    Type t = d.GetType();
                    FieldInfo rf = t.GetField("Renderer"), af = t.GetField("OriginalAlpha");
                    Renderer r = rf != null ? rf.GetValue(d) as Renderer : null;
                    if (r != null && af != null) full[r] = (float)af.GetValue(d);
                }
            }
            return full;
        }

        // Lowest material alpha of the visible text and icon renderers (-1 when none is drawn): the test's brightness oracle.
        internal float TextAlpha() { return Ok ? TextAlphaOf(m_box) : -1f; }
        internal static float TextAlphaOf(MessageBox box)
        {
            float a = -1f;
            if (box == null || box.TextBox == null || box.TextBox.textRenderers == null) return a;
            foreach (var tr in box.TextBox.textRenderers)
            {
                if (tr == null || tr.renderedCharCount == 0) continue;
                foreach (Renderer r in tr.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || r.sharedMaterial == null || !r.sharedMaterial.HasProperty("_Color")) continue;
                    float x = r.sharedMaterial.GetColor("_Color").a;
                    a = a < 0f ? x : Mathf.Min(a, x);
                }
            }
            return a;
        }

        bool Measure(out Bounds b)
        {
            b = new Bounds(); bool any = false;
            if (m_renderers == null) return false;
            foreach (Renderer r in m_renderers)
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any && b.size.y > 1e-4f;
        }

        // Place at a world point (as seen by the gameplay camera); the text is `screenFraction` of the screen height
        // tall, and never wider than `maxWidthFraction` of the screen width.
        public bool Place(Vector3 world, float screenFraction, float maxWidthFraction, float alpha)
        {
            if (!Ok) return false;
            if (alpha < 0.5f) { Hide(); return false; }
            Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null;
            if (game == null) return CannotPlace("gameplay camera unavailable");
            Vector3 vp = game.WorldToViewportPoint(world);
            if (vp.z < 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { Hide(); return false; }
            return PlaceViewport(vp.x, vp.y, screenFraction, maxWidthFraction);
        }

        bool CannotPlace(string reason)
        {
            WarnOnce(ref m_warnedPlace, "cannot place hint: " + reason);
            Hide(); return false;
        }

        internal bool PlaceViewport(float x, float y, float screenFraction, float maxWidthFraction)
        {
            if (!Ok) return false;
            Camera gui = P2Hud.GuiCamera();
            if (gui == null || gui.cullingMask == 0) return CannotPlace("HUD camera unavailable");
            if (m_baseH <= 0f) return CannotPlace("no glyph geometry");
            if ((gui.cullingMask & (1 << m_layer)) == 0) SetHudLayer();
            if (!Go.activeSelf) Go.SetActive(true);
            Go.transform.position = gui.ViewportToWorldPoint(new Vector3(x, y, 8f));
            Go.transform.rotation = gui.transform.rotation;
            if (m_baseH > 0f && gui.orthographic)
            {
                float screenH = gui.orthographicSize * 2f, screenW = screenH * gui.aspect;
                float s = screenH * screenFraction / m_baseH;
                if (m_baseW * s > screenW * maxWidthFraction) s = screenW * maxWidthFraction / m_baseW;
                Go.transform.localScale = new Vector3(s, s, m_scale0.z);
            }
            return true;
        }

        void SetHudLayer()
        {
            m_layer = P2Hud.HudLayer();
            foreach (Transform t in Go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = m_layer;
        }

        internal bool OnScreen(bool icon)
        {
            if (!Ok || !Go.activeInHierarchy || string.IsNullOrEmpty(Text)) return false;
            Camera gui = P2Hud.GuiCamera();
            if (gui == null || m_box.TextBox.textRenderers == null) return false;
            foreach (var tr in m_box.TextBox.textRenderers)
            {
                if (tr == null || tr.renderedCharCount == 0 || (tr is CatlikeCoding.TextBox.MoonIconRenderer) != icon) continue;
                foreach (Renderer r in tr.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || !r.isVisible || r.bounds.size.y <= 0f || (gui.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                    Material material = r.sharedMaterial;
                    if (material == null || material.HasProperty("_Color") && material.GetColor("_Color").a <= 0f) continue;
                    Vector3 vp = gui.WorldToViewportPoint(r.bounds.center);
                    if (vp.z >= gui.nearClipPlane && vp.z <= gui.farClipPlane && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f) return true;
                }
            }
            return false;
        }

        public void Hide() { if (Go != null && Go.activeSelf) Go.SetActive(false); }
        public void Destroy()
        {
            if (Go != null) UnityEngine.Object.Destroy(Go);
            DropOwnMaterials();
            Go = null; m_box = null;
        }
        void DropOwnMaterials()
        {
            foreach (Material m in m_own.Values) if (m != null) UnityEngine.Object.Destroy(m);
            m_own.Clear();
        }
    }

    // A soft glowing ring around a point that fills clockwise from the top, like the game's own charge/fill rings.
    public class GlowRing
    {
        public GameObject Go;
        Mesh m_mesh;
        MeshRenderer m_renderer;
        float m_shown = -1f;
        static Material s_mat;
        const int Segments = 72;

        static Material Mat()
        {
            if (s_mat != null) return s_mat;
            Shader sh = Shader.Find("Particles/Additive");
            if (sh == null) sh = Shader.Find("Particles/Alpha Blended");
            if (sh == null) return null;
            s_mat = new Material(sh); s_mat.hideFlags = HideFlags.HideAndDontSave; s_mat.renderQueue = 3000;
            if (s_mat.HasProperty("_TintColor")) s_mat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            return s_mat;
        }

        public GlowRing(string name)
        {
            if (Mat() == null) return;
            Go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(Go);
            m_mesh = new Mesh(); m_mesh.hideFlags = HideFlags.HideAndDontSave; m_mesh.MarkDynamic();
            Go.AddComponent<MeshFilter>().sharedMesh = m_mesh;
            m_renderer = Go.AddComponent<MeshRenderer>();
            m_renderer.sharedMaterial = Mat();
            m_renderer.sortingOrder = 32001;
            Go.layer = P2Hud.HudLayer();
            Go.SetActive(false);
        }

        public bool Ok { get { return Go != null; } }

        // A dim full track plus a bright arc for `fill` (0..1). Three vertex rings per angle (inner edge, core,
        // outer edge) with alpha 0/1/0 give the soft glow.
        void Build(float fill, float alpha)
        {
            int arcSegs = Mathf.Max(1, Mathf.CeilToInt(Segments * Mathf.Clamp01(fill)));
            int n = Segments + 1 + (fill > 0f ? arcSegs + 1 : 0);
            Vector3[] v = new Vector3[n * 3]; Color[] c = new Color[n * 3];
            int[] tri = new int[((Segments) + (fill > 0f ? arcSegs : 0)) * 12];
            int vi = 0, ti = 0;
            Color track = new Color(0.55f, 0.75f, 1f, 0.35f * alpha), core = new Color(0.75f, 0.95f, 1f, 0.95f * alpha);
            AddArc(v, c, tri, ref vi, ref ti, Segments, 1f, 0.40f, 0.46f, 0.52f, track);
            if (fill > 0f) AddArc(v, c, tri, ref vi, ref ti, arcSegs, Mathf.Clamp01(fill), 0.36f, 0.46f, 0.56f, core);
            m_mesh.Clear();
            m_mesh.vertices = v; m_mesh.colors = c; m_mesh.triangles = tri;
            m_mesh.RecalculateBounds();
        }

        static void AddArc(Vector3[] v, Color[] c, int[] tri, ref int vi, ref int ti, int segs, float fill, float rIn, float rMid, float rOut, Color col)
        {
            int start = vi;
            Color clear = new Color(col.r, col.g, col.b, 0f);
            for (int i = 0; i <= segs; i++)
            {
                float ang = Mathf.PI * 0.5f - (fill * 2f * Mathf.PI) * i / segs;   // from the top, clockwise
                float x = Mathf.Cos(ang), y = Mathf.Sin(ang);
                v[vi] = new Vector3(x * rIn, y * rIn, 0f); c[vi++] = clear;
                v[vi] = new Vector3(x * rMid, y * rMid, 0f); c[vi++] = col;
                v[vi] = new Vector3(x * rOut, y * rOut, 0f); c[vi++] = clear;
            }
            for (int i = 0; i < segs; i++)
            {
                int a = start + i * 3, b = a + 3;
                for (int k = 0; k < 2; k++)
                {
                    tri[ti++] = a + k; tri[ti++] = b + k; tri[ti++] = b + k + 1;
                    tri[ti++] = a + k; tri[ti++] = b + k + 1; tri[ti++] = a + k + 1;
                }
            }
        }

        public bool Place(Vector3 world, float screenFraction, float fill, float alpha)
        {
            if (!Ok) return false;
            Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = P2Hud.GuiCamera();
            if (game == null || gui == null || alpha <= 0.01f) { Hide(); return false; }
            Vector3 vp = game.WorldToViewportPoint(world);
            if (vp.z < 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { Hide(); return false; }
            float key = Mathf.Round(fill * 200f) + Mathf.Round(alpha * 20f) * 1000f;
            if (key != m_shown) { Build(fill, alpha); m_shown = key; }
            if (!Go.activeSelf) Go.SetActive(true);
            Go.transform.position = gui.ViewportToWorldPoint(new Vector3(vp.x, vp.y, 8f));
            Go.transform.rotation = gui.transform.rotation;
            float worldH = gui.orthographic ? gui.orthographicSize * 2f * screenFraction : 0.3f;
            Go.transform.localScale = new Vector3(worldH, worldH, 1f);
            return true;
        }

        public void Hide() { if (Go != null && Go.activeSelf) Go.SetActive(false); }
        public void Destroy() { if (Go != null) UnityEngine.Object.Destroy(Go); Go = null; }
    }
}
