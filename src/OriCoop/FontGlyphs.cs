using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CatlikeCoding.TextBox;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Installs the embedded Chinese SDF atlas when a MessageBox wakes up. A '# mode=full' table
    // replaces the entire Heiti atlas with WenKai and rewrites every character. Legacy/headerless
    // or mode=append tables retain the original atlas and add only missing glyphs above it.
    // The generator preserves the game's line metrics and original advances in full mode.
    static class FontGlyphs
    {
        public static int Added;              // glyphs added (for tests)
        public static string Status = "not run";

        static readonly HashSet<BitmapFont> s_done = new HashSet<BitmapFont>();
        static List<Glyph> s_glyphs;
        static Color32[] s_strip; static int s_stripW, s_stripH;
        static bool s_full;
        static bool s_loadFailed;
        static int s_noCollectionLogged;

        class Glyph { public int id, x, y, w, h; public float xOffset, yOffset, width, height, advance; public bool legacy; }
        class FullAtlasData
        {
            public int width, height;
            public Color32[] pixels;
            public Dictionary<int, Glyph> glyphs;
            public List<int> missingIds;
        }
        class OldGlyph { public Glyph glyph; public int x, y, w, h; }

        [HarmonyPatch(typeof(MessageBox), "Awake")]
        static class MessageBoxAwakePatch
        {
            static void Postfix(MessageBox __instance)
            {
                try { Ensure(__instance); }
                catch (Exception e) { Status = "error: " + e.Message; Plugin.Log.LogWarning("zh glyphs: " + e); }
            }
        }

        public static void Ensure(MessageBox box)
        {
            if (box == null || box.LanguageStyles == null) return;
#if !ZHPATCH
            if (s_forceZh == 0) ForceChineseForTests();
#endif
            TextStyleCollection col = box.LanguageStyles.Chinese;
            if (col == null || col.styles == null)
            {
                if (s_noCollectionLogged++ == 0) Plugin.Log.LogInfo("zh glyphs: MessageBox '" + box.name + "' has no Chinese style collection");
                return;
            }
            foreach (TextStyle st in col.styles)
                if (st != null && st.font != null && !s_done.Contains(st.font)) Patch(st);
            RetargetLateMaterials();
        }

        // Old atlas -> its replacement. The swap above only reaches materials loaded at that moment (the first
        // MessageBox, on the title). Text styles in area scenes bring their own material for the same font, e.g. the
        // Sunken Glades guide stone (guidestoneTextStyles, its own UberShaderMaterial): it still sampled the old
        // 2048x2048 atlas through the rewritten glyph table, so its Chinese came out as wrong pieces of other
        // characters (playtest 2026-11-16, P5). New MessageBoxes wake as their scene loads: once per frame, point
        // every material (and per-renderer material instance) still using a replaced atlas at the new one.
        static readonly Dictionary<Texture, Texture2D> s_swaps = new Dictionary<Texture, Texture2D>();
        static int s_retargetFrame = -1;
        public static int LateMaterialsFixed;   // for tests / logs

        public static int RetargetLateMaterials()
        {
            if (s_swaps.Count == 0 || s_retargetFrame == Time.frameCount) return 0;
#if !ZHPATCH
            if (P2Extras.MutateSignpost) return 0;
#endif
            s_retargetFrame = Time.frameCount;
            int n = 0;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                foreach (string p in TexProps)
                {
                    if (!m.HasProperty(p)) continue;
                    Texture t = m.GetTexture(p); Texture2D repl;
                    if (t != null && s_swaps.TryGetValue(t, out repl) && repl != null) { m.SetTexture(p, repl); n++; }
                }
            }
            if (n > 0)
            {
                LateMaterialsFixed += n;
#if !ZHPATCH
                CoopTrace.Event("hud", "glyph.retarget", "materials", n);
#endif
                Plugin.Log.LogInfo("zh glyphs: " + n + " late-loaded material(s) moved to the new atlas");
            }
            return n;
        }

        static void Patch(TextStyle st)
        {
            BitmapFont font = st.font;
            s_done.Add(font);
            // Our glyphs are fitted to this font's metrics; other fonts keep their own characters.
            if (!font.name.StartsWith("adobeHeiti", StringComparison.OrdinalIgnoreCase) || font.otherChars == null)
            {
                Plugin.Log.LogInfo("zh glyphs: skipping font '" + font.name + "'");
                return;
            }
            Plugin.Log.LogInfo("zh glyphs: patching '" + font.name + "' (" + font.otherChars.Length + " glyphs), renderer " + DescribeRenderer(st));
            if (!Load()) return;

            if (s_full)
            {
                Texture2D original = FindAtlas(st);
                if (original == null) { Status = "atlas not found"; Plugin.Log.LogWarning("zh glyphs: atlas texture of " + font.name + " not found"); return; }
                ReplaceAtlas(original, font);
                return;
            }

            var have = new HashSet<int>();
            if (font.asciiChars != null) foreach (BitmapFontChar c in font.asciiChars) have.Add(c.id);
            foreach (BitmapFontChar c in font.otherChars) have.Add(c.id);
            var todo = new List<Glyph>();
            foreach (Glyph g in s_glyphs) if (!have.Contains(g.id)) todo.Add(g);
            if (todo.Count == 0) { Status = "nothing missing"; Plugin.Log.LogInfo("zh glyphs: nothing missing"); return; }

            Texture2D atlas = FindAtlas(st);
            if (atlas == null) { Status = "atlas not found"; Plugin.Log.LogWarning("zh glyphs: atlas texture of " + font.name + " not found"); return; }
            int W = atlas.width, H0 = atlas.height;

            // Pack the new glyphs in rows above the old atlas.
            const int gap = 2;
            int rowH = 0; foreach (Glyph g in todo) rowH = Math.Max(rowH, g.h);
            rowH += gap;
            var pos = new int[todo.Count * 2];
            int cx = 0, row = 0;
            for (int i = 0; i < todo.Count; i++)
            {
                if (cx + todo[i].w > W) { cx = 0; row++; }
                pos[i * 2] = cx; pos[i * 2 + 1] = H0 + gap + row * rowH;
                cx += todo[i].w + gap;
            }
            int H = H0 + gap + (row + 1) * rowH;
            H = (H + 63) / 64 * 64;

            // Old atlas pixels: the texture is not readable, so draw it into a render texture and read it back.
            byte[] old = ReadAlpha(atlas);
            if (old == null) { Status = "atlas readback failed"; return; }

            var px = new Color32[W * H];
            for (int y = 0; y < H0; y++)
                for (int x = 0; x < W; x++) px[y * W + x].a = old[y * W + x];
            for (int i = 0; i < todo.Count; i++)
            {
                Glyph g = todo[i];
                int dx = pos[i * 2], dy = pos[i * 2 + 1];
                // Strip rows are bottom-up (Unity's PNG import); the glyph sits at the top of the strip.
                for (int r = 0; r < g.h; r++)
                {
                    int sy = s_stripH - g.h + r - g.y;
                    for (int c = 0; c < g.w; c++)
                        px[(dy + r) * W + dx + c].a = s_strip[sy * s_stripW + g.x + c].a;
                }
            }
            Texture2D tex = new Texture2D(W, H, TextureFormat.Alpha8, false);
            tex.name = atlas.name + " +OriCoop";
            tex.filterMode = atlas.filterMode; tex.wrapMode = atlas.wrapMode; tex.anisoLevel = atlas.anisoLevel;
            tex.SetPixels32(px);
            tex.Apply(false, true);

            // Swap the texture in every material using the old atlas.
            int mats = 0;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                bool hit = false;
                foreach (string p in TexProps)
                    if (m.HasProperty(p) && m.GetTexture(p) == atlas) { m.SetTexture(p, tex); hit = true; }
                if (hit) mats++;
            }
            if (mats > 0) s_swaps[atlas] = tex;
            if (mats == 0)
            {
                UnityEngine.Object.Destroy(tex); Status = "no material uses the atlas";
                Plugin.Log.LogWarning("zh glyphs: no material uses atlas '" + atlas.name + "' as main texture; renderer material: " + DescribeRenderer(st));
                return;
            }

            // Old characters now cover the bottom H0/H of the texture.
            float k = (float)H0 / H;
            foreach (BitmapFont f in FontsOn(atlas, font))
            {
                foreach (BitmapFontChar[] arr in new[] { f.asciiChars, f.otherChars })
                    if (arr != null) foreach (BitmapFontChar c in arr) { c.vMin *= k; c.vMax *= k; }
            }

            var list = new List<BitmapFontChar>(font.otherChars);
            for (int i = 0; i < todo.Count; i++)
            {
                Glyph g = todo[i];
                int dx = pos[i * 2], dy = pos[i * 2 + 1];
                list.Add(new BitmapFontChar
                {
                    id = g.id,
                    uMin = (float)dx / W, uMax = (float)(dx + g.w) / W,
                    vMin = (float)dy / H, vMax = (float)(dy + g.h) / H,
                    xOffset = g.xOffset, yOffset = g.yOffset, width = g.width, height = g.height, advance = g.advance,
                    kerningIds = new int[0], kernings = new float[0],
                });
            }
            list.Sort((a, b) => a.id.CompareTo(b.id));   // BitmapFont looks characters up by binary search
            font.otherChars = list.ToArray();
            Added += todo.Count;

            var added = new System.Text.StringBuilder();
            foreach (Glyph g in todo) added.Append((char)g.id);
            Status = "added " + todo.Count;
            Plugin.Log.LogInfo("zh glyphs: added " + todo.Count + " to " + font.name + " (" + added + "), atlas " + W + "x" + H0 + " -> " + W + "x" + H + ", " + mats + " material(s)");
            try { font.UpdateAllTextBoxes(); } catch (Exception) { }
        }

        static List<int> MissingIds(IEnumerable<BitmapFontChar> chars, Dictionary<int, Glyph> table)
        {
            var ids = new HashSet<int>();
            foreach (BitmapFontChar c in chars)
                if (c != null && !table.ContainsKey(c.id)) ids.Add(c.id);
            var list = new List<int>(ids); list.Sort();
            return list;
        }

        static Dictionary<int, Glyph> GlyphTable()
        {
            var table = new Dictionary<int, Glyph>();
            foreach (Glyph g in s_glyphs) table.Add(g.id, g);
            return table;
        }

        static void ApplyGlyph(BitmapFontChar c, Glyph g, int width, int height, bool keepMetrics)
        {
            c.uMin = (float)g.x / width; c.uMax = (float)(g.x + g.w) / width;
            // Table/PNG coordinates start at the top, Unity texture UVs at the bottom.
            c.vMin = 1f - (float)(g.y + g.h) / height; c.vMax = 1f - (float)g.y / height;
            if (keepMetrics) return;
            c.xOffset = g.xOffset; c.yOffset = g.yOffset;
            c.width = g.width; c.height = g.height; c.advance = g.advance;
        }

        // Preserve ASCII's indexed order, existing object references and kerning data.
        // New IDs belong in otherChars, whose lookup uses binary search.
        static BitmapFontChar[] RewritePreparedCharacters(BitmapFontChar[] ascii, BitmapFontChar[] other, FullAtlasData data)
        {
            var have = new HashSet<int>();
            foreach (BitmapFontChar[] arr in new[] { ascii, other })
                if (arr != null) foreach (BitmapFontChar c in arr)
                {
                    if (c == null) continue;
                    Glyph g = data.glyphs[c.id];
                    ApplyGlyph(c, g, data.width, data.height, g.legacy); have.Add(c.id);
                }
            var list = other == null ? new List<BitmapFontChar>() : new List<BitmapFontChar>(other);
            list.RemoveAll(c => c == null);
            foreach (Glyph g in data.glyphs.Values)
                if (!have.Contains(g.id))
                {
                    var c = new BitmapFontChar { id = g.id, kerningIds = new int[0], kernings = new float[0] };
                    ApplyGlyph(c, g, data.width, data.height, false); list.Add(c);
                }
            list.Sort((a, b) => a.id.CompareTo(b.id));
            return list.ToArray();
        }

        // CPU-only preparation, before changing any live font/material. Missing glyphs are
        // copied from their original UV rectangles into unused rows, retaining old metrics.
        static FullAtlasData BuildFullAtlas(BitmapFontChar[] originals, byte[] oldAlpha, int oldWidth, int oldHeight)
        {
            var data = new FullAtlasData { width = s_stripW, height = s_stripH, pixels = s_strip,
                glyphs = GlyphTable() };
            data.missingIds = MissingIds(originals, data.glyphs);
            if (data.missingIds.Count == 0) return data;
            var originalsById = new Dictionary<int, BitmapFontChar>();
            foreach (BitmapFontChar c in originals)
                if (c != null && !originalsById.ContainsKey(c.id)) originalsById.Add(c.id, c);
            int x = 0, y = 0, rowHeight = 0;
            foreach (Glyph g in s_glyphs) y = Math.Max(y, g.y + g.h);
            y += 2;
            var oldGlyphs = new List<OldGlyph>();
            foreach (int id in data.missingIds)
            {
                BitmapFontChar c = originalsById[id];
                int sx = Math.Max(0, Math.Min(oldWidth, (int)Math.Round(c.uMin * oldWidth)));
                int sy = Math.Max(0, Math.Min(oldHeight, (int)Math.Round(c.vMin * oldHeight)));
                int ex = Math.Max(sx, Math.Min(oldWidth, (int)Math.Round(c.uMax * oldWidth)));
                int ey = Math.Max(sy, Math.Min(oldHeight, (int)Math.Round(c.vMax * oldHeight)));
                int w = Math.Max(1, ex-sx), h = Math.Max(1, ey-sy);
                if (w > data.width) throw new InvalidDataException("original glyph exceeds atlas width");
                if (x + w > data.width) { x = 0; y += rowHeight + 2; rowHeight = 0; }
                var g = new Glyph { id = id, x = x, y = y, w = w, h = h, legacy = true,
                    xOffset = c.xOffset, yOffset = c.yOffset, width = c.width, height = c.height, advance = c.advance };
                data.glyphs.Add(id, g);
                oldGlyphs.Add(new OldGlyph { glyph = g, x = sx, y = sy, w = ex-sx, h = ey-sy });
                x += w + 2; rowHeight = Math.Max(rowHeight, h);
            }
            while (data.height < y + rowHeight + 2) data.height *= 2;
            data.pixels = new Color32[data.width * data.height];
            // Keep top-down table coordinates stable even when the final height grows.
            Array.Copy(s_strip, 0, data.pixels, (data.height-s_stripH)*data.width, s_strip.Length);
            foreach (OldGlyph old in oldGlyphs)
            {
                if (old.w == 0 || old.h == 0) continue; // blank original glyph retains its advance
                if (oldAlpha == null || oldAlpha.Length != oldWidth*oldHeight)
                    throw new IOException("original atlas readback needed to preserve missing glyphs");
                Glyph g = old.glyph;
                for (int row = 0; row < old.h; row++)
                    for (int col = 0; col < old.w; col++)
                        data.pixels[(data.height-g.y-g.h+row)*data.width+g.x+col].a = oldAlpha[(old.y+row)*oldWidth+old.x+col];
            }
            return data;
        }

        static void ReplaceAtlas(Texture2D atlas, BitmapFont font)
        {
            // Include both arrays of every loaded duplicate in the coverage audit.
            var fonts = new List<BitmapFont>(FontsOn(atlas, font));
            var originals = new List<BitmapFontChar>();
            foreach (BitmapFont f in fonts)
            {
                if (f.asciiChars != null) originals.AddRange(f.asciiChars);
                if (f.otherChars != null) originals.AddRange(f.otherChars);
            }
            List<int> missing = MissingIds(originals, GlyphTable());
            Plugin.Log.LogInfo("zh glyphs: coverage ascii=" + (font.asciiChars == null ? 0 : font.asciiChars.Length) +
                ", other=" + (font.otherChars == null ? 0 : font.otherChars.Length) + ", embedded=" + s_glyphs.Count);
            if (missing.Count > 0)
            {
                var ids = new System.Text.StringBuilder();
                foreach (int id in missing) { if (ids.Length > 0) ids.Append(", "); ids.Append(id); }
                Plugin.Log.LogWarning("zh glyphs: full atlas missing ids [" + ids + "]; preserving " + missing.Count + " original glyph(s)");
            }
            var materials = new List<Material>();
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                foreach (string p in TexProps)
                    if (m.HasProperty(p) && m.GetTexture(p) == atlas) { materials.Add(m); break; }
            }
            if (materials.Count == 0) { Status = "no material uses the atlas"; Plugin.Log.LogWarning("zh glyphs: " + Status); return; }
            byte[] oldAlpha = missing.Count == 0 ? null : ReadAlpha(atlas);
            FullAtlasData data = BuildFullAtlas(originals.ToArray(), oldAlpha, atlas.width, atlas.height);
            if (data.width > SystemInfo.maxTextureSize || data.height > SystemInfo.maxTextureSize)
                throw new InvalidDataException("full atlas exceeds GPU texture limit");

            // Normal full replacement uses only embedded pixels; mismatches add old crops.
            var tex = new Texture2D(data.width, data.height, TextureFormat.Alpha8, false);
            tex.name = atlas.name + " +OriCoop WenKai";
            tex.filterMode = atlas.filterMode; tex.wrapMode = atlas.wrapMode; tex.anisoLevel = atlas.anisoLevel;
            tex.SetPixels32(data.pixels); tex.Apply(false, true);
            int added = 0;
            foreach (BitmapFont f in fonts)
            {
                int before = (f.asciiChars == null ? 0 : f.asciiChars.Length) + (f.otherChars == null ? 0 : f.otherChars.Length);
                f.otherChars = RewritePreparedCharacters(f.asciiChars, f.otherChars, data);
                added += (f.asciiChars == null ? 0 : f.asciiChars.Length) + f.otherChars.Length - before;
            }
            foreach (Material m in materials)
                foreach (string p in TexProps)
                    if (m.HasProperty(p) && m.GetTexture(p) == atlas) m.SetTexture(p, tex);
            s_swaps[atlas] = tex;
            Added += added;
            Status = "replaced " + s_glyphs.Count + " glyphs; added " + added + "; preserved " + missing.Count;
            Plugin.Log.LogInfo("zh glyphs: " + Status + ", atlas " + data.width + "x" + data.height + ", " + fonts.Count + " font(s), " + materials.Count + " material(s)");
            foreach (BitmapFont f in fonts) try { f.UpdateAllTextBoxes(); } catch (Exception) { }
        }

        // Fonts whose characters live on this atlas (normally just the one).
        static IEnumerable<BitmapFont> FontsOn(Texture2D atlas, BitmapFont font)
        {
            yield return font;
            foreach (BitmapFont f in Resources.FindObjectsOfTypeAll<BitmapFont>())
                if (f != font && f != null && f.name.StartsWith(font.name, StringComparison.Ordinal) && f.name.Length == font.name.Length && !s_done.Contains(f))
                {
                    s_done.Add(f);   // same-named duplicate asset: shares the atlas, rescale it too
                    yield return f;
                }
        }

        // The game's text goes through its UberShader, which samples the distance map from _TxtTexture
        // (TextifyModifier); plain TextMeshRenderer materials would use _MainTex.
        static readonly string[] TexProps = { "_TxtTexture", "_MainTex" };

        static Texture TexOf(Material m)
        {
            foreach (string p in TexProps)
                if (m.HasProperty(p) && m.GetTexture(p) != null) return m.GetTexture(p);
            return null;
        }

        static string DescribeRenderer(TextStyle st)
        {
            if (st.renderer == null) return "(no renderer)";
            Renderer r = st.renderer.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) return "(no material)";
            Material m = r.sharedMaterial;
            Texture t = TexOf(m);
            return m.name + " shader=" + (m.shader != null ? m.shader.name : "?") + " texture=" + (t != null ? t.name : "null");
        }

        static Texture2D FindAtlas(TextStyle st)
        {
            if (st.renderer != null)
            {
                Renderer r = st.renderer.GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != null && TexOf(r.sharedMaterial) is Texture2D)
                    return (Texture2D)TexOf(r.sharedMaterial);
            }
            foreach (Texture2D t in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (t != null && t.name.StartsWith(st.font.name + "_0", StringComparison.Ordinal)) return t;
            return null;
        }

        static byte[] ReadAlpha(Texture2D atlas)
        {
            int W = atlas.width, H = atlas.height;
            RenderTexture rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            Texture2D tmp = null;
            try
            {
                Graphics.Blit(atlas, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(W, H, TextureFormat.ARGB32, false);
                tmp.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
                Color32[] c = tmp.GetPixels32();
                var a = new byte[W * H];
                int nonzero = 0;
                for (int i = 0; i < a.Length; i++) { a[i] = c[i].a; if (a[i] != 0) nonzero++; }
                if (nonzero == 0) { Plugin.Log.LogWarning("zh glyphs: atlas readback is empty"); return null; }
                return a;
            }
            catch (Exception e) { Plugin.Log.LogWarning("zh glyphs: readback failed: " + e.Message); return null; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (tmp != null) UnityEngine.Object.Destroy(tmp);
            }
        }

        static bool Load()
        {
            if (s_glyphs != null) return true;
            if (s_loadFailed) return false;
            s_loadFailed = true;
            Assembly asm = typeof(FontGlyphs).Assembly;
            byte[] png = ReadResource(asm, "OriCoop.zh_glyphs.png");
            byte[] txt = ReadResource(asm, "OriCoop.zh_glyphs.txt");
            if (png == null || txt == null) { Status = "resources missing"; Plugin.Log.LogWarning("zh glyphs: embedded glyph files missing"); return false; }
            // LoadImage into a fresh small texture of our own (in place on big game textures crashes).
            Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (!t.LoadImage(png)) { UnityEngine.Object.Destroy(t); Status = "bad png"; return false; }
            s_stripW = t.width; s_stripH = t.height;
            s_strip = t.GetPixels32();
            UnityEngine.Object.Destroy(t);
            // Grayscale PNG -> RGB channels hold the value; copy it to alpha.
            for (int i = 0; i < s_strip.Length; i++) s_strip[i].a = s_strip[i].r;

            ParseGlyphs(System.Text.Encoding.UTF8.GetString(txt));
            s_loadFailed = false;
            return true;
        }

        static void ParseGlyphs(string text)
        {
            var list = new List<Glyph>();
            var ids = new HashSet<int>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int declaredCount = -1;
            s_full = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("# mode=", StringComparison.Ordinal))
                {
                    string[] header = line.Substring(2).Split(' ');
                    if (header[0] == "mode=full") s_full = true;
                    else if (header[0] != "mode=append") throw new InvalidDataException("unknown glyph mode");
                    foreach (string field in header)
                    {
                        if (field.StartsWith("width=", StringComparison.Ordinal) && int.Parse(field.Substring(6), ci) != s_stripW)
                            throw new InvalidDataException("glyph PNG width mismatch");
                        if (field.StartsWith("height=", StringComparison.Ordinal) && int.Parse(field.Substring(7), ci) != s_stripH)
                            throw new InvalidDataException("glyph PNG height mismatch");
                        if (field.StartsWith("count=", StringComparison.Ordinal)) declaredCount = int.Parse(field.Substring(6), ci);
                    }
                    continue;
                }
                if (line[0] == '#') continue;
                string[] p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 10) throw new InvalidDataException("bad glyph row");
                var g = new Glyph
                {
                    id = int.Parse(p[0], ci), x = int.Parse(p[1], ci), y = int.Parse(p[2], ci), w = int.Parse(p[3], ci), h = int.Parse(p[4], ci),
                    xOffset = float.Parse(p[5], ci), yOffset = float.Parse(p[6], ci), width = float.Parse(p[7], ci),
                    height = float.Parse(p[8], ci), advance = float.Parse(p[9], ci),
                };
                if (!ids.Add(g.id) || g.x < 0 || g.y < 0 || g.w <= 0 || g.h <= 0 ||
                    g.w > s_stripW || g.h > s_stripH || g.x > s_stripW - g.w || g.y > s_stripH - g.h)
                    throw new InvalidDataException("duplicate or out-of-bounds glyph " + g.id);
                foreach (float value in new[] { g.xOffset, g.yOffset, g.width, g.height, g.advance })
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("non-finite glyph metrics");
                if (g.width < 0 || g.height < 0 || g.advance < 0) throw new InvalidDataException("negative glyph dimensions");
                list.Add(g);
            }
            if (declaredCount >= 0 && declaredCount != list.Count) throw new InvalidDataException("glyph count mismatch");
            s_glyphs = list;
        }

        static byte[] ReadResource(Assembly asm, string name)
        {
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                var buf = new byte[16384]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

#if !ZHPATCH
        // ORICOOP_FORCE_ZH=1 (tests): run the game in Simplified Chinese. This build's Language setter turns
        // Chinese into English (the translation and font ship but are locked), so override the getter.
        static readonly bool s_forceZhEnv = Environment.GetEnvironmentVariable("ORICOOP_FORCE_ZH") == "1";
        static int s_forceZh;
        static void ForceChineseForTests()
        {
            s_forceZh = 1;
            if (s_forceZhEnv) Plugin.Log.LogInfo("zh glyphs: ORICOOP_FORCE_ZH=1, game language reads as Chinese");
        }

        [HarmonyPatch(typeof(GameSettings), "get_Language")]
        static class ForceZhPatch
        {
            static void Postfix(ref Language __result) { if (s_forceZhEnv) __result = Language.Chinese; }
        }
#endif
    }
}
