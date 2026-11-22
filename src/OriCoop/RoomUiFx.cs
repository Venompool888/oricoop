using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // T-184: the animated layer on the room pages' added textures (RoomUiDraw), so they move like the game's own menus:
    // ink smoke drifting off the banners' edges, sparks gliding along the blue frames and the banner ribbon, motes
    // rising off the save plate, and a breathing glow with a shimmer sweep on the selected button.
    //
    // The game's menus get this from authored prefabs (TransparencyAnimator / UberColorAnimator curves, legacy particle
    // emitters, SpiritLantern-style spark renderers), none of which is loaded on the title screen in a reusable form, so
    // the particles here are stateless sprites (Ui/RoomFxLogic: place = f(index, time)) batched into one mesh per layer
    // with the game's built-in Particles/Additive and Particles/Alpha Blended shaders. The sprite textures are generated
    // at start (a soft glow dot, a noisy smoke puff, a soft streak): no art files, so the public build has them too.
    // Times are unscaled (menus run with the game paused); sizes are fractions of the target rect, so every resolution
    // looks the same. Config UI.AnimatedEffects = false turns all of it off.
    internal static class RoomUiFx
    {
        static ConfigEntry<bool> s_enabled;
        static readonly List<NativeGui.Sprite> s_buffer = new List<NativeGui.Sprite>(32);
        public static int SpritesLastFrame { get; private set; }   // for tests: sprites drawn in the last repaint
        static int s_countFrame = -1, s_count;

        static readonly Color SmokeInk = new Color(0.13f, 0.14f, 0.32f, 0.38f);
        static readonly Color SmokeMist = new Color(0.55f, 0.70f, 1f, 0.10f);
        static readonly Color SparkCyan = new Color(0.7f, 0.94f, 1f, 1f);
        static readonly Color SparkWhite = new Color(0.85f, 0.95f, 1f, 1f);

        public static bool Enabled
        {
            get
            {
                if (s_enabled == null && !object.ReferenceEquals(Plugin.Instance, null))
                    s_enabled = Plugin.Instance.Config.Bind("UI", "AnimatedEffects", true, "Animated smoke, sparks and glow on the room pages. false = static.");
                return s_enabled == null || s_enabled.Value;
            }
        }

        static float Now { get { return Time.unscaledTime; } }

        // Every effect runs only in the repaint pass (OnGUI also runs layout / input passes) and is timed for the probe.
        static readonly System.Diagnostics.Stopwatch s_watch = new System.Diagnostics.Stopwatch();
        static int s_depth;
        static bool Begin()
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return false;
            Roll();
            Probe();
            if (!Enabled || s_abOff) return false;
            if (s_depth++ == 0) s_watch.Start();
            return true;
        }
        static void End() { if (--s_depth <= 0) { s_depth = 0; s_watch.Stop(); } }

        // ---- test probe (env-gated, off in normal play) -----------------------------------------------------
        // ORICOOP_FX_CAPTURE=<dir>: 1.5 s after the effects first draw, save 30 screenshots 0.1 s apart (the motion).
        // ORICOOP_FX_AB=1: then switch the effects off and on every 2 s and log the mean frame time and the effects'
        // CPU time per frame for each state (fx-ab.txt in the capture dir, and the BepInEx log).
        static int s_probeFrame = -1, s_shots;
        static float s_firstAt = -1f, s_nextShot, s_abSwitchAt;
        static bool s_abOff, s_abStarted;
        static double s_abDt, s_abCpu; static int s_abFrames, s_abSprites; static long s_abWatchAt;

        static string ProbeRole()
        {
            string r = Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN");
            if (string.IsNullOrEmpty(r)) r = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_TEST");
            return string.IsNullOrEmpty(r) ? "game" : r;
        }

        static void Probe()
        {
            if (Time.frameCount == s_probeFrame) return;
            s_probeFrame = Time.frameCount;
            try
            {
                string dir = Environment.GetEnvironmentVariable("ORICOOP_FX_CAPTURE");
                bool ab = Environment.GetEnvironmentVariable("ORICOOP_FX_AB") == "1";
                if (string.IsNullOrEmpty(dir) && !ab) return;
                float now = Time.realtimeSinceStartup;
                if (s_firstAt < 0f) { s_firstAt = now; s_nextShot = now + 1.5f; }
                if (!string.IsNullOrEmpty(dir) && s_shots < 30)
                {
                    if (now >= s_nextShot)
                    {
                        System.IO.Directory.CreateDirectory(dir);
                        Application.CaptureScreenshot(System.IO.Path.Combine(dir, "fx_" + ProbeRole() + "_" + Screen.width + "x" + Screen.height + "_" + s_shots.ToString("00") + ".png"));
                        s_shots++; s_nextShot = now + 0.1f;
                    }
                    return;
                }
                if (!ab) return;
                if (!s_abStarted) { s_abStarted = true; s_abSwitchAt = now + 2f; s_abWatchAt = s_watch.ElapsedTicks; return; }
                s_abDt += Time.unscaledDeltaTime; s_abFrames++; s_abSprites += SpritesLastFrame;
                if (now < s_abSwitchAt) return;
                double cpuMs = (s_watch.ElapsedTicks - s_abWatchAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                string line = "RoomUiFx AB " + (s_abOff ? "OFF" : "ON ") + " " + Screen.width + "x" + Screen.height + ": " + s_abFrames + " frames, mean frame "
                    + (s_abDt * 1000.0 / Math.Max(1, s_abFrames)).ToString("0.00") + " ms (" + (s_abFrames / Math.Max(0.001, s_abDt)).ToString("0.0") + " fps), fx cpu "
                    + (cpuMs / Math.Max(1, s_abFrames)).ToString("0.000") + " ms/frame, sprites/frame " + (s_abSprites / Math.Max(1, s_abFrames));
                if (Plugin.Log != null) Plugin.Log.LogInfo(line);
                if (!string.IsNullOrEmpty(dir)) System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "fx-ab-" + ProbeRole() + ".txt"), line + Environment.NewLine);
                s_abOff = !s_abOff; s_abSwitchAt = now + 2f; s_abDt = 0; s_abFrames = 0; s_abSprites = 0; s_abWatchAt = s_watch.ElapsedTicks;
            }
            catch (Exception e) { NativeGui.WarnOnce("roomfx.probe", "*", e); }
        }

        static void Roll()
        {
            if (Time.frameCount != s_countFrame) { SpritesLastFrame = s_count; s_count = 0; s_countFrame = Time.frameCount; }
        }
        static void Count(int n) { Roll(); s_count += n; }

        static void Flush(string id, Texture tex, bool additive)
        {
            Count(s_buffer.Count);
            NativeGui.Sprites(id, tex, additive, s_buffer);
            s_buffer.Clear();
        }

        static void Add(Rect r, FxParticle f, float sizeScale, Color color, float stretch)
        {
            if (f.Alpha <= 0.003f) return;
            float s = f.Size * r.height * sizeScale;
            NativeGui.Sprite sp = new NativeGui.Sprite();
            sp.Center = new Vector2(r.x + f.U * r.width, r.y + f.V * r.height);
            sp.Size = new Vector2(s * stretch, s);
            sp.Angle = f.Angle;
            sp.Color = new Color(color.r, color.g, color.b, color.a * f.Alpha);
            s_buffer.Add(sp);
        }

        // ---- the effects, called by RoomUiDraw around each textured piece ------------------------------------

        // Smoke curling off an ink banner's edges. Call BEFORE drawing the banner (it shows only outside the ink).
        public static void SmokeBehind(string id, Rect r, float amount)
        {
            if (!Begin()) return;
            try
            {
                int seed = RoomFxLogic.Seed(id);
                int n = Mathf.Max(1, Mathf.RoundToInt(RoomFxLogic.SmokeCount * amount));
                for (int i = 0; i < n; i++) Add(r, RoomFxLogic.Smoke(seed, i, Now), 0.6f, SmokeInk, 2.1f);
                Flush(id + ".fx.smoke", Puff(), false);
            }
            catch (Exception e) { NativeGui.WarnOnce("roomfx.smoke", id, e); }
            finally { End(); }
        }

        // A faint light mist and sparks running along a banner's glowing ribbon (its wavy line at ~72% height). Call
        // AFTER the banner.
        public static void Ribbon(string id, Rect r)
        {
            if (!Begin()) return;
            try
            {
                int seed = RoomFxLogic.Seed(id) ^ 0x5bd1e995;
                float t = Now;
                for (int i = 0; i < 4; i++)   // the mist breathing over the ink
                {
                    FxParticle f = RoomFxLogic.Smoke(seed, i * 3 + 2, t * 0.6f);
                    f.U = 0.3f + 0.4f * RoomFxLogic.Hash(seed, i) + (f.U - 0.5f) * 0.2f;
                    f.V = 0.45f + 0.15f * RoomFxLogic.Hash(seed, i + 9);
                    Add(r, f, 0.9f, SmokeMist, 1.8f);
                }
                Flush(id + ".fx.mist", Puff(), true);
                for (int i = 0; i < 7; i++)
                {
                    float h1 = RoomFxLogic.Hash(seed, i * 3 + 40), h2 = RoomFxLogic.Hash(seed, i * 3 + 41);
                    float life = 2.4f + 1.8f * h1;
                    float p = RoomFxLogic.Phase(t, life, h2);
                    FxParticle f = new FxParticle();
                    f.U = 0.2f + 0.6f * p * (i % 2 == 0 ? 1f : -1f) + (i % 2 == 0 ? 0f : 0.6f);
                    f.V = 0.72f + 0.035f * Mathf.Sin(f.U * Mathf.PI * 3f + h1 * 6f);
                    f.Size = 0.07f + 0.05f * h2;
                    f.Alpha = RoomFxLogic.Fade(p) * (0.6f + 0.4f * Mathf.Sin(t * 7f + i));
                    f.Angle = 0f;
                    Add(r, f, 1f, SparkWhite, 1f);
                }
                Flush(id + ".fx.ribbon", Glow(), true);
            }
            catch (Exception e) { NativeGui.WarnOnce("roomfx.ribbon", id, e); }
            finally { End(); }
        }

        // Sparks along a blue frame / row band's edges and motes rising off it. Call AFTER the band.
        public static void FrameSparks(string id, Rect r, bool selected)
        {
            if (!Begin()) return;
            try
            {
                int seed = RoomFxLogic.Seed(id), n = RoomFxLogic.SparkCount(selected);
                float t = Now;
                for (int i = 0; i < n; i++) Add(r, RoomFxLogic.Spark(seed, i, t, selected), 1f, i % 4 == 3 ? SparkWhite : SparkCyan, 1f);
                Flush(id + ".fx.sparks", Glow(), true);
            }
            catch (Exception e) { NativeGui.WarnOnce("roomfx.sparks", id, e); }
            finally { End(); }
        }

        // The save plate: a little smoke at its ends (BEFORE the plate) ...
        public static void PlateBehind(string id, Rect r) { SmokeBehind(id, r, 0.6f); }

        // ... and motes rising inside it (AFTER the plate).
        public static void PlateMotes(string id, Rect r)
        {
            if (!Begin()) return;
            try
            {
                int seed = RoomFxLogic.Seed(id);
                float t = Now;
                for (int i = 0; i < 6; i++)
                {
                    float h1 = RoomFxLogic.Hash(seed, i * 4 + 70), h2 = RoomFxLogic.Hash(seed, i * 4 + 71), h3 = RoomFxLogic.Hash(seed, i * 4 + 72);
                    float p = RoomFxLogic.Phase(t, 3.5f + 2.5f * h1, h2);
                    FxParticle f = new FxParticle();
                    f.U = 0.12f + 0.76f * h3 + 0.02f * Mathf.Sin(t * 1.3f + i);
                    f.V = 0.95f - 1.0f * p;
                    f.Size = 0.05f + 0.035f * h1;
                    f.Alpha = RoomFxLogic.Fade(p) * 0.7f;
                    Add(r, f, 1f, SparkCyan, 1f);
                }
                Flush(id + ".fx.motes", Glow(), true);
            }
            catch (Exception e) { NativeGui.WarnOnce("roomfx.motes", id, e); }
            finally { End(); }
        }

        // The selected button: its glow texture breathing over it, a shimmer streak sweeping across, sparks on its
        // edges. Call AFTER the selected band, with that band's rect and texture.
        public static void Selected(string id, Rect r, Texture glow)
        {
            if (!Begin()) return;
            try
            {
                float t = Now;
                if (glow != null) NativeGui.Texture(id + ".fx.pulse", r, glow, new Color(0.7f, 0.95f, 1f, 0.04f + 0.16f * RoomFxLogic.Pulse(t)));
                float strength;
                float u = RoomFxLogic.ShimmerU(t, out strength);
                if (strength > 0f)
                {
                    FxParticle f = new FxParticle();
                    f.U = u; f.V = 0.5f; f.Size = 0.75f; f.Angle = 0f;
                    f.Alpha = strength * Mathf.Clamp01(1f - Mathf.Abs(u - 0.5f) * 1.4f);   // fade out towards the pointed ends
                    Add(r, f, 1f, new Color(0.75f, 0.95f, 1f, 0.55f), 0.9f);
                    Flush(id + ".fx.shimmer", Streak(), true);
                }
                FrameSparks(id, r, true);
            }
            catch (Exception e) { NativeGui.WarnOnce("roomfx.selected", id, e); }
            finally { End(); }
        }

        // ---- generated sprite textures ---------------------------------------------------------------------

        static Texture2D s_glow, s_puff, s_streak;

        static Texture2D New(int w, int h)
        {
            Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
            t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        // A soft glow dot: bright core, wide halo (white; tinted per sprite).
        static Texture2D Glow()
        {
            if (s_glow != null) return s_glow;
            const int N = 32;
            s_glow = New(N, N);
            Color[] px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f, d2 = dx * dx + dy * dy;
                    float a = Mathf.Exp(-d2 * 18f) + 0.35f * Mathf.Exp(-d2 * 4f);
                    a *= Mathf.Clamp01((1f - d2) * 3f);
                    px[y * N + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
                }
            s_glow.SetPixels(px); s_glow.Apply();
            return s_glow;
        }

        // A smoke puff: fractal value noise in a soft round falloff, with wispy holes (white; tinted per sprite).
        static Texture2D Puff()
        {
            if (s_puff != null) return s_puff;
            const int N = 64;
            s_puff = New(N, N);
            Color[] px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N, v = (y + 0.5f) / N;
                    float dx = u * 2f - 1f, dy = v * 2f - 1f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float n = 0f, amp = 0.5f, freq = 3f;
                    for (int o = 0; o < 4; o++) { n += amp * Noise(u * freq, v * freq, o); amp *= 0.5f; freq *= 2f; }
                    float fall = Mathf.Clamp01(1f - d);
                    fall = fall * fall * (3f - 2f * fall);
                    float a = Mathf.Clamp01((n - 0.38f) * 2.2f) * fall * fall;
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
                }
            s_puff.SetPixels(px); s_puff.Apply();
            return s_puff;
        }

        // A soft vertical streak (the shimmer): narrow across, fading at top and bottom.
        static Texture2D Streak()
        {
            if (s_streak != null) return s_streak;
            const int W = 32, H = 32;
            s_streak = New(W, H);
            Color[] px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float dx = (x + 0.5f) / W * 2f - 1f, dy = (y + 0.5f) / H * 2f - 1f;
                    float a = Mathf.Exp(-dx * dx * 9f) * Mathf.Clamp01(1f - dy * dy);
                    px[y * W + x] = new Color(1f, 1f, 1f, a);
                }
            s_streak.SetPixels(px); s_streak.Apply();
            return s_streak;
        }

        // Smooth value noise on a 2D lattice, tiling-free, seeded per octave.
        static float Noise(float x, float y, int octave)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float a = RoomFxLogic.Hash(xi * 73 + octave * 9973, yi), b = RoomFxLogic.Hash((xi + 1) * 73 + octave * 9973, yi);
            float c = RoomFxLogic.Hash(xi * 73 + octave * 9973, yi + 1), d = RoomFxLogic.Hash((xi + 1) * 73 + octave * 9973, yi + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }
    }
}
