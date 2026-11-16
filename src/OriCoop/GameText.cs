using System;
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
        public bool Ok { get { return Go != null && m_box != null; } }
        public bool NoBacking;    // hide the hint's dark backing strip (for small labels drawn over pictures)

        public GameText(string name)
        {
            try
            {
                MessageControllerB mc = UI.MessageController;
                if (mc == null || mc.HintMessage == null) return;
                Go = (GameObject)UnityEngine.Object.Instantiate(mc.HintMessage);
                Go.name = name;
                UnityEngine.Object.DontDestroyOnLoad(Go);
                m_box = Go.GetComponentInChildren<MessageBox>();
                if (m_box == null) { Destroy(); return; }
                foreach (MessageBoxVisibility v in Go.GetComponentsInChildren<MessageBoxVisibility>(true)) UnityEngine.Object.DestroyImmediate(v);
                m_box.Visibility = null;
                if (m_box.WriteOutTextBox != null) { UnityEngine.Object.DestroyImmediate(m_box.WriteOutTextBox); m_box.WriteOutTextBox = null; }
                int layer = P2Hud.HudLayer();
                foreach (Transform t in Go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                m_scale0 = Go.transform.localScale;
                if (m_scale0.x <= 0f) m_scale0 = Vector3.one;
                Go.SetActive(false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("game text '" + name + "': " + e.Message + " - using the plain font");
                Destroy();
            }
        }

        public string Text { get { return m_text ?? ""; } }

        public void SetText(string text)
        {
            if (!Ok || text == m_text) return;
            m_text = text;
            bool was = Go.activeSelf;
            if (!was) Go.SetActive(true);
            Go.transform.localScale = m_scale0;
            try { m_box.SetMessage(new MessageDescriptor(text)); m_box.RemoveMessageFade(); }
            catch (Exception e) { Plugin.Log.LogWarning("game text: " + e.Message); }
            // Measure the letters only: the hint prefab also has a dark backing strip that is much taller.
            System.Collections.Generic.List<Renderer> rs = new System.Collections.Generic.List<Renderer>();
            try
            {
                if (m_box.TextBox != null && m_box.TextBox.textRenderers != null)
                    foreach (var tr in m_box.TextBox.textRenderers) { Renderer r = tr != null ? tr.GetComponent<Renderer>() : null; if (r != null) rs.Add(r); }
            }
            catch (Exception) { }
            m_renderers = rs.Count > 0 ? rs.ToArray() : Go.GetComponentsInChildren<Renderer>(true);
            if (NoBacking && rs.Count > 0)
                foreach (Renderer r in Go.GetComponentsInChildren<Renderer>(true)) if (!rs.Contains(r)) r.enabled = false;
            Bounds b; m_baseH = m_baseW = 0f;
            if (Measure(out b)) { m_baseH = b.size.y / m_scale0.y; m_baseW = b.size.x / m_scale0.x; }
            if (!was) Go.SetActive(false);
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
            Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = P2Hud.GuiCamera();
            if (game == null || gui == null || alpha < 0.5f) { Hide(); return false; }
            Vector3 vp = game.WorldToViewportPoint(world);
            if (vp.z < 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { Hide(); return false; }
            if (!Go.activeSelf) Go.SetActive(true);
            Go.transform.position = gui.ViewportToWorldPoint(new Vector3(vp.x, vp.y, 8f));
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

        public void Hide() { if (Go != null && Go.activeSelf) Go.SetActive(false); }
        public void Destroy() { if (Go != null) UnityEngine.Object.Destroy(Go); Go = null; m_box = null; }
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
