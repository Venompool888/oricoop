using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace OriCoop
{
    // Tool for making a P2 skin: exports every sprite atlas Ori's prefab uses (PNG + frame layout JSON).
    // Run the game with env ORICOOP_DUMP_SKIN=<output folder>; it dumps at the title screen and quits.
    // Ori is a flipbook: each animation frame is a rectangle inside an atlas texture; the game binds the atlas
    // to Ori's material in AtlasSpriteTextureBinder.BindTo. A repainted atlas with the same layout is a skin.
    public class SkinDump : MonoBehaviour
    {
        public static string OutDir { get { return Environment.GetEnvironmentVariable("ORICOOP_DUMP_SKIN"); } }
        public static bool Enabled { get { return !string.IsNullOrEmpty(OutDir); } }

        bool m_done;

        void Update()
        {
            if (m_done) return;
            if (Time.frameCount % 120 == 0) Trace("waiting t=" + Time.realtimeSinceStartup.ToString("F0"));
            if (Time.realtimeSinceStartup < 20f) return;
            m_done = true;
            Trace("dump start");
            try { Dump(OutDir); }
            catch (Exception e) { Plugin.Log.LogError("SKINDUMP failed: " + e); }
            Application.Quit();
        }

        class Found { public Atlas Atlas; public string Path; public List<string> Anims = new List<string>(); }

        readonly Dictionary<int, bool> m_seen = new Dictionary<int, bool>();
        readonly Dictionary<int, Found> m_atlases = new Dictionary<int, Found>();
        readonly Dictionary<int, string> m_materials = new Dictionary<int, string>();
        readonly Dictionary<int, TextureAnimation> m_anims = new Dictionary<int, TextureAnimation>();
        GameObject m_root;

        void Dump(string dir)
        {
            Directory.CreateDirectory(dir);
            m_root = Resources.Load<GameObject>("characters/seinCharacter");
            if (m_root == null) { Plugin.Log.LogError("SKINDUMP: characters/seinCharacter not found"); return; }
            VisitObject(m_root, m_root.name, 0, null);
            Trace("crawl done: " + m_atlases.Count + " atlases, " + m_anims.Count + " animations");

            var sb = new StringBuilder();
            sb.Append("{\n  \"source\": \"Resources/characters/seinCharacter\",\n  \"colorSpace\": \"").Append(QualitySettings.activeColorSpace).Append("\",\n  \"atlases\": [\n");
            bool first = true;
            var index = new StringBuilder();
            foreach (Found f in m_atlases.Values)
            {
                Atlas a = f.Atlas;
                Texture2D t = a.Texture;
                string file = Safe(t != null ? t.name : a.name) + ".png";
                if (t != null) SavePng(t, Path.Combine(dir, file));
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("    {\"atlas\": ").Append(Q(a.name)).Append(", \"texture\": ").Append(Q(t != null ? t.name : null))
                  .Append(", \"file\": ").Append(Q(file))
                  .Append(", \"width\": ").Append(t != null ? t.width : 0).Append(", \"height\": ").Append(t != null ? t.height : 0)
                  .Append(", \"format\": ").Append(Q(t != null ? t.format.ToString() : null))
                  .Append(", \"screenMode\": ").Append(Q(a.ScreenMode.ToString()))
                  .Append(", \"foundAt\": ").Append(Q(f.Path))
                  .Append(", \"animations\": [");
                for (int i = 0; i < f.Anims.Count; i++) sb.Append(i > 0 ? ", " : "").Append(Q(f.Anims[i]));
                sb.Append("],\n     \"frames\": [");
                int w = t != null ? t.width : 1, h = t != null ? t.height : 1;
                for (int i = 0; i < a.SpriteTextures.Count; i++)
                {
                    AtlasSpriteTexture s = a.SpriteTextures[i];
                    Rect r = s.NormalizedRect;
                    int px = Mathf.RoundToInt(r.x * w), pw = Mathf.RoundToInt(r.width * w), ph = Mathf.RoundToInt(r.height * h);
                    int pyTop = h - Mathf.RoundToInt((r.y + r.height) * h); // top-left origin, as in image editors
                    sb.Append(i > 0 ? ",\n       " : "\n       ")
                      .Append("{\"name\": ").Append(Q(s.Name)).Append(", \"x\": ").Append(px).Append(", \"y\": ").Append(pyTop)
                      .Append(", \"w\": ").Append(pw).Append(", \"h\": ").Append(ph).Append(", \"rotated\": ").Append(s.Flipped ? "true" : "false")
                      .Append(", \"ox\": ").Append(F(s.OriginalSize.x)).Append(", \"oy\": ").Append(F(s.OriginalSize.y))
                      .Append(", \"cx\": ").Append(F(s.CenterOffset.x)).Append(", \"cy\": ").Append(F(s.CenterOffset.y)).Append("}");
                }
                sb.Append("]}");
                index.Append(file).Append('\t').Append(w).Append('x').Append(h).Append('\t').Append(a.SpriteTextures.Count).Append(" frames\t").Append(f.Path).Append('\n');
            }
            sb.Append("\n  ],\n  \"otherMaterialTextures\": [");
            first = true;
            foreach (string m in m_materials.Values) { sb.Append(first ? "\n    " : ",\n    ").Append(Q(m)); first = false; }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(Path.Combine(dir, "atlases.json"), sb.ToString());
            File.WriteAllText(Path.Combine(dir, "index.txt"), index.ToString());
            Trace("atlases written");

            // Animations: each frame as [atlas texture name, frame index in that atlas's "frames"].
            var an = new StringBuilder("[\n");
            first = true;
            var names = new List<TextureAnimation>(m_anims.Values);
            names.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            foreach (TextureAnimation ta in names)
            {
                var fr = new StringBuilder();
                int n = 0;
                foreach (MoonGuid g in ta.FrameGuids)
                {
                    Atlas at;
                    AtlasSpriteTexture s = ta.GetFrameForId(g, out at);
                    if (s == null || at == null || at.Texture == null) continue;
                    fr.Append(n++ > 0 ? "," : "").Append("[").Append(Q(at.Texture.name)).Append(",").Append(at.SpriteTextures.IndexOf(s)).Append("]");
                }
                if (n == 0) continue;
                an.Append(first ? "  " : ",\n  ").Append("{\"name\": ").Append(Q(ta.name)).Append(", \"loop\": ").Append(ta.Loop ? "true" : "false")
                  .Append(", \"pingPong\": ").Append(ta.PingPong ? "true" : "false").Append(", \"frames\": [").Append(fr).Append("]}");
                first = false;
            }
            an.Append("\n]\n");
            File.WriteAllText(Path.Combine(dir, "animations.json"), an.ToString());
            Plugin.Log.LogInfo("SKINDUMP wrote " + m_anims.Count + " animations");
            Plugin.Log.LogInfo("SKINDUMP wrote " + m_atlases.Count + " atlases to " + dir);
        }

        // Walks serialized fields from the Ori prefab: components, ScriptableObjects (animations), nested prefabs.
        void VisitObject(UnityEngine.Object o, string path, int goHops, string anim)
        {
            if (o == null) return;
            int id = o.GetInstanceID();
            Atlas atlas = o as Atlas;
            if (atlas != null)
            {
                Found f;
                if (!m_atlases.TryGetValue(id, out f)) { f = new Found { Atlas = atlas, Path = path }; m_atlases[id] = f; }
                if (anim != null && !f.Anims.Contains(anim)) f.Anims.Add(anim);
                return;
            }
            if (o is TextureAnimation)
            {
                if (!m_anims.ContainsKey(id)) m_anims[id] = (TextureAnimation)o;
                // Visit each animation even if seen, so each atlas lists every animation that uses it (cheap: atlases only).
                foreach (Atlas a in ((TextureAnimation)o).Atlases) VisitObject(a, path, goHops, o.name);
                return;
            }
            if (m_seen.ContainsKey(id)) return;
            m_seen[id] = true;

            Material mat = o as Material;
            if (mat != null)
            {
                if (mat.mainTexture != null) m_materials[mat.mainTexture.GetInstanceID()] = mat.mainTexture.name + " (" + mat.name + ", shader " + (mat.shader != null ? mat.shader.name : "?") + ") at " + path;
                return;
            }
            GameObject go = o as GameObject;
            if (go != null)
            {
                if (goHops > 2) return;
                foreach (Component c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string cp = path + "/" + RelPath(go.transform, c.transform) + ":" + c.GetType().Name;
                    Renderer r = c as Renderer;
                    if (r != null) { foreach (Material m in r.sharedMaterials) VisitObject(m, cp, goHops, anim); continue; }
                    if (c is MonoBehaviour) VisitFields(c, cp, goHops, 0, anim);
                }
                return;
            }
            Component comp = o as Component;
            if (comp != null)
            {
                // A reference to a component elsewhere: walk its prefab root only if it lives outside our hierarchy.
                if (!comp.transform.IsChildOf(m_root.transform)) VisitObject(comp.transform.root.gameObject, path + "->" + comp.transform.root.name, goHops + 1, anim);
                return;
            }
            if (o is ScriptableObject) VisitFields(o, path + "." + o.name, goHops, 0, anim);
        }

        void VisitFields(object o, string path, int goHops, int depth, string anim)
        {
            if (o == null || depth > 8) return;
            for (Type t = o.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(ScriptableObject) && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsNotSerialized) continue;
                    if (!f.IsPublic && f.GetCustomAttributes(typeof(SerializeField), true).Length == 0) continue;
                    Type ft = f.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    VisitValue(v, path + "." + f.Name, goHops, depth, anim);
                }
            }
        }

        void VisitValue(object v, string path, int goHops, int depth, string anim)
        {
            if (v == null) return;
            UnityEngine.Object uo = v as UnityEngine.Object;
            if (uo != null)
            {
                bool isGo = uo is GameObject;
                VisitObject(uo, path, isGo ? goHops + 1 : goHops, anim);
                return;
            }
            if (v is UnityEngine.Object) return; // destroyed / missing reference
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string || t.IsValueType && t.Namespace == "UnityEngine") return;
            IList list = v as IList;
            if (list != null) { int i = 0; foreach (object e in list) VisitValue(e, path + "[" + (i++) + "]", goHops, depth + 1, anim); return; }
            if (t.IsSerializable) VisitFields(v, path, goHops, depth + 1, anim);
        }

        static string RelPath(Transform root, Transform t)
        {
            if (t == root) return root.name;
            string p = t.name;
            while (t.parent != null && t.parent != root) { t = t.parent; p = t.name + "/" + p; }
            return root.name + "/" + p;
        }

        static void SavePng(Texture2D src, string file)
        {
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            Texture2D t = new Texture2D(src.width, src.height, TextureFormat.ARGB32, false);
            t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(file, t.EncodeToPNG());
            UnityEngine.Object.Destroy(t);
        }

        static void Trace(string s)
        {
            try { Directory.CreateDirectory(OutDir); File.AppendAllText(Path.Combine(OutDir, "trace.txt"), DateTime.Now.ToString("HH:mm:ss ") + s + "\n"); } catch { }
        }

        static string F(float f) { return f.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture); }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) s = "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
    }
}
