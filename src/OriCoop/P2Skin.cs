using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Player 2 skin: repainted copies of Ori's sprite atlases, used only on P2.
    //   Put the artist's PNGs (or their zip, as is) in BepInEx\plugins\OriCoop_P2Skin\. Each PNG is named after the
    //   atlas texture it replaces (seinPlatformingAtlas0.png ...), same size, same frame layout (see SkinDump).
    //   A transparent PNG is accepted: its background is filled with the atlas's chroma-key color (the game keys
    //   Ori out of a red or green background; the atlases have no alpha).
    // How: every sprite frame goes through AtlasSpriteTextureBinder.BindTo, which sets the atlas on the material.
    // For P2's renderers we give the renderer its own material (P2 and P1 share the prefab's material otherwise)
    // and put the replacement texture on it. Atlases with no replacement show the original.
    public static class P2Skin
    {
        static ConfigEntry<bool> s_enabled;
        static ConfigEntry<string> s_folder;
        static ConfigEntry<KeyCode> s_reloadKey;
        static readonly int s_mainTex = Shader.PropertyToID("_MainTex");

        static Dictionary<string, byte[]> s_sources;                          // texture name -> PNG bytes
        static readonly Dictionary<string, Texture2D> s_ready = new Dictionary<string, Texture2D>();
        static readonly HashSet<string> s_failed = new HashSet<string>();
        static readonly HashSet<int> s_ownMaterial = new HashSet<int>();      // P2 renderers given their own material
        static readonly Queue<Atlas> s_preload = new Queue<Atlas>();

        public static string Folder { get { string f = s_folder.Value; return Path.IsPathRooted(f) ? f : Path.Combine(Paths.PluginPath, f); } }
        public static int LoadedCount { get { return s_ready.Count; } }
        public static bool HasSkin { get { return s_enabled.Value && s_sources != null && s_sources.Count > 0; } }

        public static void Init(ConfigFile cfg)
        {
            s_enabled = cfg.Bind("Skin", "Enabled", true, "Use the player 2 skin found in Folder (if any).");
            s_folder = cfg.Bind("Skin", "Folder", "OriCoop_P2Skin", "Folder with the P2 skin: repainted atlas PNGs and/or the artist's zip. Relative to BepInEx\\plugins.");
            s_reloadKey = cfg.Bind("Skin", "ReloadKey", KeyCode.F7, "Reload the P2 skin from Folder while playing (for testing new art).");
            ReadSources();
        }

        static void ReadSources()
        {
            s_sources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string dir = Folder;
            if (!Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch { } return; }
            Predicate<string> png = n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            // Zips first, loose PNGs override them (so a single fixed file can be dropped next to the zip).
            foreach (string zip in Directory.GetFiles(dir, "*.zip"))
            {
                try { foreach (var kv in MiniZip.Read(zip, png)) s_sources[Path.GetFileNameWithoutExtension(kv.Key)] = kv.Value; }
                catch (Exception e) { Plugin.Log.LogError("P2 skin: can't read " + Path.GetFileName(zip) + ": " + e.Message); }
            }
            foreach (string f in Directory.GetFiles(dir, "*.png"))
            {
                try { s_sources[Path.GetFileNameWithoutExtension(f)] = File.ReadAllBytes(f); }
                catch (Exception e) { Plugin.Log.LogError("P2 skin: can't read " + Path.GetFileName(f) + ": " + e.Message); }
            }
            if (s_sources.Count > 0) Plugin.Log.LogInfo("P2 skin: " + s_sources.Count + " images in " + dir + " (" + string.Join(", ", new List<string>(s_sources.Keys).ToArray()) + ")");
        }

        // Called when P2 joins: prepare every replacement whose atlas is loaded, one per frame.
        public static void Preload()
        {
            if (!HasSkin) return;
            foreach (Atlas a in Resources.FindObjectsOfTypeAll<Atlas>())
                if (a != null && a.Texture != null && s_sources.ContainsKey(a.Texture.name) && !s_ready.ContainsKey(a.Texture.name)) s_preload.Enqueue(a);
        }

        public static void Tick()
        {
            if (Input.GetKeyDown(s_reloadKey.Value)) Reload();
            if (s_preload.Count > 0) { try { Get(s_preload.Dequeue()); } catch (Exception e) { Plugin.Log.LogError(e); } }
        }

        public static void Reload()
        {
            foreach (Texture2D t in s_ready.Values) if (t != null) UnityEngine.Object.Destroy(t);
            s_ready.Clear(); s_failed.Clear(); s_preload.Clear();
            ReadSources();
            Plugin.Log.LogInfo("P2 skin reloaded: " + s_sources.Count + " images");
            if (P2Context.P2 != null) Preload();
        }

        static Texture2D Get(Atlas atlas)
        {
            Texture2D orig = atlas.Texture;
            if (orig == null) return null;
            string name = orig.name;
            Texture2D t;
            if (s_ready.TryGetValue(name, out t)) return t;
            byte[] png;
            if (s_failed.Contains(name) || !s_sources.TryGetValue(name, out png)) return null;
            t = Prepare(name, png, orig, atlas.ScreenMode);
            if (t == null) s_failed.Add(name); else s_ready[name] = t;
            return t;
        }

        static Texture2D Prepare(string name, byte[] png, Texture2D orig, UberScreenMode mode)
        {
            float t0 = Time.realtimeSinceStartup;
            Texture2D img = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            try
            {
                if (!img.LoadImage(png)) { Plugin.Log.LogError("P2 skin: " + name + ".png is not a valid PNG"); return null; }
                if (img.width != orig.width || img.height != orig.height)
                {
                    Plugin.Log.LogError("P2 skin: " + name + ".png is " + img.width + "x" + img.height + ", must be " + orig.width + "x" + orig.height + " like the original; skipped");
                    return null;
                }
                Color32[] px = img.GetPixels32();
                Color32 key = mode == UberScreenMode.Red ? new Color32(255, 0, 0, 255) : mode == UberScreenMode.Green ? new Color32(0, 255, 0, 255) : mode == UberScreenMode.Blue ? new Color32(0, 0, 255, 255) : new Color32(0, 0, 0, 255);
                int transparent = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 c = px[i];
                    if (c.a == 255) continue;
                    transparent++;
                    int a = c.a, ia = 255 - a;
                    px[i] = new Color32((byte)((c.r * a + key.r * ia) / 255), (byte)((c.g * a + key.g * ia) / 255), (byte)((c.b * a + key.b * ia) / 255), 255);
                }
                Texture2D t = new Texture2D(orig.width, orig.height, TextureFormat.RGB24, orig.mipmapCount > 1);
                t.name = name + "_P2";
                t.SetPixels32(px);
                t.Apply(true);
                t.Compress(true);                  // DXT1 like the original: 2048x2048 is ~2.7 MB instead of ~16 MB
                t.filterMode = orig.filterMode;
                t.wrapMode = orig.wrapMode;
                t.anisoLevel = orig.anisoLevel;
                t.Apply(false, true);              // upload and free the CPU copy
                Plugin.Log.LogInfo("P2 skin: " + name + " ready (" + (transparent > 0 ? "transparent background filled with " + mode + " key" : "key-color background") + ", " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms)");
                return t;
            }
            finally { UnityEngine.Object.Destroy(img); }
        }

        [HarmonyPatch(typeof(AtlasSpriteTextureBinder), "BindTo")]
        static class BindToPatch
        {
            static void Postfix(MeshFilter filter, Atlas atlas)
            {
                if (filter == null || atlas == null || s_sources == null) return;
                Puppet puppet = Players.Of(filter);
                if (puppet == null || !filter.transform.IsChildOf(puppet.Sein.transform)) return;
                Renderer r = filter.GetComponent<Renderer>();
                if (r == null) return;
                Texture2D skin = s_enabled.Value ? Get(atlas) : null;
                int id = r.GetInstanceID();
                if (skin == null)
                {
                    // No replacement for this atlas (or the skin was reloaded without it): make sure the original shows.
                    if (s_ownMaterial.Contains(id) && r.sharedMaterial != null && r.sharedMaterial.GetTexture(s_mainTex) != atlas.Texture) r.sharedMaterial.SetTexture(s_mainTex, atlas.Texture);
                    return;
                }
                if (s_ownMaterial.Add(id)) { Material own = r.material; own.name += " (P2)"; } // r.material clones once; sharedMaterial is then the clone
                Material m = r.sharedMaterial;
                if (m.GetTexture(s_mainTex) != skin) m.SetTexture(s_mainTex, skin);
            }
        }
    }
}
