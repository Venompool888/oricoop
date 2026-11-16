using System;
using System.IO;
using BepInEx;
using Game;
using UnityEngine;

namespace OriCoop
{
    // The downed-teammate marker picture: the game's own world-map Ori head with X eyes (src\assets\dead-marker).
    // Drawn as a textured quad by the game's HUD camera, like ReviveHud's labels.
    //   - Default picture: dead_marker.png embedded in OriCoop.dll.
    //   - Override: BepInEx\plugins\OriCoop_Marks\dead_marker.png (any square RGBA PNG). It is re-read whenever the
    //     file changes, so a new design can be dropped in while the game runs (the next downed marker uses it).
    //   - If neither loads, or no usable shader exists, Available is false and ReviveHud falls back to the text skull.
    public static class DeadMarker
    {
        const string ResourceName = "OriCoop.dead_marker.png";
        static Texture2D s_tex;
        static Material s_mat;
        static bool s_hasTint, s_failed;
        static DateTime s_overrideStamp = DateTime.MinValue;
        public static string Source = "";

        public static string OverridePath { get { return Path.Combine(Path.Combine(Paths.PluginPath, "OriCoop_Marks"), "dead_marker.png"); } }

        static byte[] LoadBytes(out string source)
        {
            source = "";
            try
            {
                string p = OverridePath;
                if (File.Exists(p))
                {
                    s_overrideStamp = File.GetLastWriteTimeUtc(p);
                    source = p;
                    return File.ReadAllBytes(p);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("dead marker: could not read override: " + e.Message); }
            s_overrideStamp = DateTime.MinValue;
            using (Stream s = typeof(DeadMarker).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) return null;
                byte[] b = new byte[s.Length];
                int n = 0; while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                source = "embedded";
                return b;
            }
        }

        static bool OverrideChanged()
        {
            try
            {
                string p = OverridePath;
                DateTime t = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
                return t != s_overrideStamp;
            }
            catch (Exception) { return false; }
        }

        // The shared material, (re)loaded on first use and whenever the override file changes. Null = not available.
        public static Material Material()
        {
            if (s_mat != null && !OverrideChanged()) return s_mat;
            if (s_failed && !OverrideChanged()) return null;
            s_failed = false;
            try
            {
                string src;
                byte[] png = LoadBytes(out src);
                if (png == null) { Fail("no picture (embedded resource missing)"); return null; }
                // LoadImage into a fresh small texture (in-place on big game textures crashes; this is our own).
                Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!t.LoadImage(png)) { UnityEngine.Object.Destroy(t); Fail("not a valid PNG: " + src); return null; }
                t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.hideFlags = HideFlags.HideAndDontSave;
                if (s_mat == null)
                {
                    // Particles/Alpha Blended takes a tint (smooth fades with the HUD); Unlit/Transparent ships in
                    // resources.assets and is always there, but has no tint (then the marker just hides when the HUD fades).
                    Shader sh = Shader.Find("Particles/Alpha Blended");
                    if (sh == null) sh = Shader.Find("Unlit/Transparent");
                    if (sh == null) { UnityEngine.Object.Destroy(t); Fail("no transparent shader in this build"); return null; }
                    s_mat = new Material(sh); s_mat.hideFlags = HideFlags.HideAndDontSave;
                    s_mat.renderQueue = 3000;
                    s_hasTint = s_mat.HasProperty("_TintColor");
                    Plugin.Log.LogInfo("dead marker: shader " + sh.name);
                }
                if (s_tex != null) UnityEngine.Object.Destroy(s_tex);
                s_tex = t;
                s_mat.mainTexture = t;
                Source = src;
                Plugin.Log.LogInfo("dead marker: picture " + t.width + "x" + t.height + " from " + src);
                return s_mat;
            }
            catch (Exception e) { Fail(e.Message); return null; }
        }

        static void Fail(string why) { s_failed = true; Plugin.Log.LogWarning("dead marker: " + why + " - using the text skull"); }

        public static bool Available { get { return Material() != null; } }

        static Mesh s_quad;
        static Mesh Quad()
        {
            if (s_quad != null) return s_quad;
            s_quad = new Mesh();
            s_quad.vertices = new Vector3[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            s_quad.uv = new Vector2[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            s_quad.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            s_quad.RecalculateBounds();
            s_quad.hideFlags = HideFlags.HideAndDontSave;
            return s_quad;
        }

        // One marker instance (one per downed player).
        public class Icon
        {
            public GameObject Go;
            MeshRenderer m_renderer;
            MaterialPropertyBlock m_block;

            public Icon(string name)
            {
                Go = new GameObject(name);
                UnityEngine.Object.DontDestroyOnLoad(Go);
                Go.AddComponent<MeshFilter>().sharedMesh = Quad();
                m_renderer = Go.AddComponent<MeshRenderer>();
                m_renderer.sharedMaterial = Material();
                m_renderer.sortingOrder = 32000;
                m_block = new MaterialPropertyBlock();
                Go.layer = P2Hud.HudLayer();
                Go.SetActive(false);
            }

            // Place at a world point (as seen by the gameplay camera), `screenFraction` of the screen height tall.
            public bool Place(Vector3 world, float screenFraction, float alpha)
            {
                Material mat = Material();
                Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = P2Hud.GuiCamera();
                bool show = mat != null && game != null && gui != null && (s_hasTint ? alpha > 0.01f : alpha > 0.5f);
                Vector3 vp = show ? game.WorldToViewportPoint(world) : Vector3.zero;
                if (!show || vp.z < 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { Go.SetActive(false); return false; }
                if (m_renderer.sharedMaterial != mat) m_renderer.sharedMaterial = mat;
                Go.SetActive(true);
                Go.transform.position = gui.ViewportToWorldPoint(new Vector3(vp.x, vp.y, 8f));
                Go.transform.rotation = gui.transform.rotation;
                float worldH = gui.orthographic ? gui.orthographicSize * 2f * screenFraction : 0.3f;
                Go.transform.localScale = new Vector3(worldH, worldH, 1f);
                if (s_hasTint)
                {
                    // Particles/Alpha Blended doubles the colour: 0.5 grey = picture as drawn.
                    m_block.Clear();
                    m_block.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f * alpha));
                    m_renderer.SetPropertyBlock(m_block);
                }
                return true;
            }
            public void Hide() { if (Go != null && Go.activeSelf) Go.SetActive(false); }
            public void Destroy() { if (Go != null) UnityEngine.Object.Destroy(Go); Go = null; }
        }
    }
}
