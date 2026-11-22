// T-178: PlayStation (DualSense) button icons. Ported from the stand-alone OriPSGlyphs plugin (2026-11-14).
// The game draws every button prompt (<icon>e</> markup, menus, tutorials, the save page) from a few named
// Xbox textures (buttonA ... xboxButtonRB). When the pad used last is a PlayStation pad, those textures get
// the PlayStation pixels in place, so every material, prefab and clone that uses them follows; when an Xbox
// pad is used again they get the game's own pixels back. Keyboard prompts are the game's own business (it
// switches to the keyboard textures itself), so they are never touched.
//   - PlayStation images: embedded OriCoop.ps_<texture>.png (src\assets\ps-glyphs, optional) or
//     BepInEx\plugins\OriCoop_Icons\<texture>.png (wins). Without either, nothing changes.
//   - Switching back needs the game's own pixels: embedded OriCoop.xbox_<texture>.png (src\assets\xbox-glyphs,
//     taken from the game's files, private like the PlayStation set). Reading them back from the live texture
//     (Blit + ReadPixels) made the next LoadImage on that texture block the game for good (T-178 probe,
//     2026-11-22), so that is not done.
//   - controller / controllerLines (Options > Controls picture, 1024x1024): LoadImage in place on these
//     crashes the game (OriPSGlyphs, 7 of 7 tries), so they get a separate texture and the materials that use
//     them are pointed at it. Only used when a picture for them exists.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using OriCoop.Pads;
using UnityEngine;

namespace OriCoop.Glyphs
{
    public static class PadGlyphs
    {
        // texture name -> its size in the game (the name alone is not unique enough)
        static readonly Dictionary<string, Vector2> Targets = new Dictionary<string, Vector2>
        {
            { "buttonA", new Vector2(64, 64) },        // Cross
            { "buttonB", new Vector2(64, 64) },        // Circle
            { "buttonX", new Vector2(64, 64) },        // Square
            { "buttonY", new Vector2(64, 64) },        // Triangle
            { "buttonMenu", new Vector2(64, 64) },     // Options
            { "buttonSelect", new Vector2(64, 64) },   // Create
            { "buttonLT", new Vector2(64, 64) },       // L2
            { "buttonRT", new Vector2(64, 64) },       // R2
            { "xboxButtonLB", new Vector2(256, 128) }, // L1
            { "xboxButtonRB", new Vector2(256, 128) }, // R1
            { "xboxButtonA", new Vector2(128, 128) },  // Cross (one level's in-world prompt)
            { "controller", new Vector2(1024, 1024) },
            { "controllerLines", new Vector2(1024, 1024) },
        };

        public static string IconSet = "Auto";   // Auto | PlayStation | Xbox (config [Controllers] ButtonIcons)

        static readonly Dictionary<string, byte[]> s_ps = new Dictionary<string, byte[]>();
        static readonly Dictionary<string, byte[]> s_xbox = new Dictionary<string, byte[]>();   // the game's own icons (to switch back)
        static readonly List<Texture2D> s_icons = new List<Texture2D>();
        static readonly Dictionary<int, PadFamily> s_shows = new Dictionary<int, PadFamily>();
        static readonly HashSet<int> s_seen = new HashSet<int>();
        static readonly Dictionary<int, Texture2D> s_bigToPs = new Dictionary<int, Texture2D>();
        static readonly Dictionary<int, Texture2D> s_bigToXbox = new Dictionary<int, Texture2D>();
        static PadFamily s_wanted = PadFamily.None;
        static float s_nextScan;
        static bool s_loaded;

        public static PadFamily Showing { get { return s_wanted; } }
        public static string OverrideDir { get { return Path.Combine(Paths.PluginPath, "OriCoop_Icons"); } }

        static void Load()
        {
            s_loaded = true;
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("local.oripsglyphs"))
            {
                // the old stand-alone plugin rewrites the same textures; two writers would fight every switch
                Plugin.Log.LogWarning("icons: OriPSGlyphs.dll is installed; OriCoop leaves the button icons to it. Delete BepInEx\\plugins\\OriPSGlyphs.dll to use OriCoop's own PlayStation icons.");
                return;
            }
            var asm = typeof(PadGlyphs).Assembly;
            foreach (var name in Targets.Keys)
            {
                byte[] data = null;
                string f = Path.Combine(OverrideDir, name + ".png");
                try { if (File.Exists(f)) data = File.ReadAllBytes(f); }
                catch (Exception e) { Plugin.Log.LogWarning("icons: could not read " + f + ": " + e.Message); }
                if (data == null) data = Embedded(asm, "OriCoop.ps_" + name + ".png");
                if (data != null) s_ps[name] = data;
                byte[] x = Embedded(asm, "OriCoop.xbox_" + name + ".png");
                if (x != null) s_xbox[name] = x;
            }
            Plugin.Log.LogInfo("icons: " + s_ps.Count + " PlayStation images, icon set " + IconSet);
        }

        internal static PadFamily TestForce = PadFamily.None;   // GlyphProbe only
        internal static bool Trace;                              // GlyphProbe only: log each swap

        static byte[] Embedded(System.Reflection.Assembly asm, string res)
        {
            using (var st = asm.GetManifestResourceStream(res))
            {
                if (st == null) return null;
                var data = new byte[st.Length];
                int read = 0;
                while (read < data.Length) read += st.Read(data, read, data.Length - read);
                return data;
            }
        }

        static PadFamily Wanted()
        {
            if (TestForce != PadFamily.None) return TestForce;
            if (string.Equals(IconSet, "PlayStation", StringComparison.OrdinalIgnoreCase)) return PadFamily.PlayStation;
            if (string.Equals(IconSet, "Xbox", StringComparison.OrdinalIgnoreCase)) return PadFamily.Xbox;
            if (PadDetector.Last != PadFamily.None) return PadDetector.Last;
            // nothing pressed yet: PlayStation icons when a PlayStation pad is connected
            return PadDetector.PlayStationConnected ? PadFamily.PlayStation : PadFamily.Xbox;
        }

        // Main thread, every frame (after VirtualPads.Tick).
        public static void Tick()
        {
            if (!s_loaded) Load();
            if (s_ps.Count == 0) return;
            var wanted = Wanted();
            if (wanted != s_wanted)
            {
                Plugin.Log.LogInfo("icons -> " + wanted + (s_wanted == PadFamily.None ? "" : " (was " + s_wanted + ")"));
                s_wanted = wanted;
                ApplyAll();
            }
            if (Time.realtimeSinceStartup < s_nextScan) return;
            // Ori streams scenes in the background, so new copies of the icon textures can appear at any time;
            // a scan every second is a name check only and catches them before they are on screen.
            s_nextScan = Time.realtimeSinceStartup + 1f;
            Scan();
        }

        public static void RescanSoon() { s_nextScan = 0f; }

        // "PlayStation 11/11" = how many live icon textures show the wanted set (GlyphProbe's check).
        internal static string Describe()
        {
            s_icons.RemoveAll(x => x == null);
            int ok = 0;
            foreach (var t in s_icons) { PadFamily f; if (s_shows.TryGetValue(t.GetInstanceID(), out f) && f == s_wanted) ok++; }
            return s_wanted + " " + ok + "/" + s_icons.Count + (s_bigToPs.Count > 0 ? " big=" + s_bigToPs.Count : "");
        }

        static void Scan()
        {
            Texture2D[] all = Resources.FindObjectsOfTypeAll<Texture2D>();
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                Vector2 size;
                if (!Targets.TryGetValue(t.name, out size)) continue;
                int id = t.GetInstanceID();
                if (!s_seen.Add(id)) continue;
                if (t.width != (int)size.x || t.height != (int)size.y) continue; // same name, different texture
                byte[] png;
                if (!s_ps.TryGetValue(t.name, out png)) continue;
                if (t.name.StartsWith("controller", StringComparison.Ordinal)) { AddBig(t, id, png); continue; }
                if (!s_xbox.ContainsKey(t.name)) continue; // no way back to the Xbox look: leave it alone
                s_icons.Add(t);
                s_shows[id] = PadFamily.Xbox; // a fresh texture has the game's pixels
                Apply(t);
            }
            s_icons.RemoveAll(x => x == null);
            if (s_bigToPs.Count > 0) PointMaterials();
        }

        static void AddBig(Texture2D t, int id, byte[] png)
        {
            var nt = new Texture2D(2, 2, TextureFormat.ARGB32, true);
            if (!nt.LoadImage(png)) { Plugin.Log.LogWarning("icons: " + t.name + ".png is not a valid image"); return; }
            nt.name = t.name + "_ps5";
            nt.wrapMode = TextureWrapMode.Clamp;
            UnityEngine.Object.DontDestroyOnLoad(nt);
            s_bigToPs[id] = nt;
            s_bigToXbox[nt.GetInstanceID()] = t;
            Plugin.Log.LogInfo("icons: PlayStation " + t.name + " picture ready (" + nt.width + "x" + nt.height + ")");
        }

        static void ApplyAll()
        {
            s_icons.RemoveAll(x => x == null);
            for (int i = 0; i < s_icons.Count; i++) Apply(s_icons[i]);
            if (s_bigToPs.Count > 0) PointMaterials();
        }

        static void Apply(Texture2D t)
        {
            int id = t.GetInstanceID();
            PadFamily shows;
            s_shows.TryGetValue(id, out shows);
            if (shows == s_wanted || s_wanted == PadFamily.None) return;
            byte[] png;
            if (!(s_wanted == PadFamily.PlayStation ? s_ps : s_xbox).TryGetValue(t.name, out png)) return;
            Load(t, png, s_wanted);
        }

        static void Load(Texture2D t, byte[] png, PadFamily family)
        {
            int id = t.GetInstanceID();
            // LoadImage keeps these textures DXT5, and a LoadImage that does not change the size blocked the game for
            // good (T-178 probe 2026-11-22, every time; size changes always worked). So the two sets have different
            // sizes (PlayStation = 2x the game's, Xbox = the game's own) and every switch changes the size; a picture
            // with the texture's current size is refused instead of risking the hang.
            int w, h;
            if (!PngSize(png, out w, out h) || (w == t.width && h == t.height))
            {
                Plugin.Log.LogWarning("icons: " + t.name + " " + family + " picture is " + w + "x" + h + ", same as the texture now; not loaded");
                return;
            }
            try
            {
                // LoadImage replaces the pixels (and size) in place, so everything that references the texture follows.
                if (Trace) Plugin.Log.LogInfo("icons: LoadImage " + t.name + " " + t.width + "x" + t.height + " fmt=" + t.format + " mips=" + t.mipmapCount + " -> " + family);
                if (t.LoadImage(png))
                {
                    t.wrapMode = TextureWrapMode.Clamp;
                    t.filterMode = FilterMode.Trilinear;
                    s_shows[id] = family;
                }
                else Plugin.Log.LogWarning("icons: LoadImage failed for " + t.name);
            }
            catch (Exception e) { Plugin.Log.LogWarning("icons: could not replace " + t.name + ": " + e.Message); }
        }

        internal static bool PngSize(byte[] png, out int w, out int h)
        {
            w = h = 0;
            if (png == null || png.Length < 24 || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return false;
            w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return true;
        }

        static void PointMaterials()
        {
            var map = s_wanted == PadFamily.PlayStation ? s_bigToPs : s_bigToXbox;
            var mats = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || !m.HasProperty("_MainTex")) continue;
                var cur = m.mainTexture;
                if (cur == null) continue;
                Texture2D nt;
                if (map.TryGetValue(cur.GetInstanceID(), out nt) && nt != null) m.mainTexture = nt;
            }
        }
    }
}
