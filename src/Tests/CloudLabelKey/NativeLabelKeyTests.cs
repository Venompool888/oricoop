using System;
using System.Globalization;
using OriCoop.Ui;

// #406 (2): NativeGui.LabelCore decides "same label as last frame, skip the rebuild" with NativeLabelKey instead of
// a string built from Rect.ToString() (F2), Color.ToString() (F3) and the other inputs. It must decide the same way
// as that string did, and deciding must not allocate.
static class NativeLabelKeyTests
{
    static int failures;

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    // The key main built: text|Rect|fontPx|anchor|Color|WxH|zh, with Unity's Rect (F2) and Color (F3) formats.
    static string OldKey(string text, float[] v, int anchor, int sw, int sh, bool zh)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string rect = string.Format(c, "(x:{0:F2}, y:{1:F2}, width:{2:F2}, height:{3:F2})", v[0], v[1], v[2], v[3]);
        string color = string.Format(c, "RGBA({0:F3}, {1:F3}, {2:F3}, {3:F3})", v[5], v[6], v[7], v[8]);
        return text + "|" + rect + "|" + v[4].ToString(c) + "|" + anchor + "|" + color + "|" + sw + "x" + sh + "|" + zh;
    }

    static bool Same(NativeLabelKey k, string text, float[] v, int anchor, int sw, int sh, bool zh)
    { return k.Same(text, v[0], v[1], v[2], v[3], v[4], anchor, v[5], v[6], v[7], v[8], sw, sh, zh); }

    static void Store(NativeLabelKey k, string text, float[] v, int anchor, int sw, int sh, bool zh)
    { k.Store(text, v[0], v[1], v[2], v[3], v[4], anchor, v[5], v[6], v[7], v[8], sw, sh, zh); }

    static float[] Base() { return new float[] { 100f, 40f, 320f, 32f, 24f, 1f, 0.9f, 0.5f, 1f }; }

    static int Main()
    {
        Run("nothing stored is never the same", delegate
        {
            Check(!Same(new NativeLabelKey(), "a", Base(), 4, 1920, 1080, true), "fresh key matched");
        });
        Run("same inputs are the same", delegate
        {
            NativeLabelKey k = new NativeLabelKey(); Store(k, "开始", Base(), 4, 1920, 1080, true);
            Check(Same(k, "开始", Base(), 4, 1920, 1080, true), "identical inputs did not match");
        });
        Run("each input on its own makes it different", delegate
        {
            NativeLabelKey k = new NativeLabelKey(); Store(k, "a", Base(), 4, 1920, 1080, true);
            Check(!Same(k, "b", Base(), 4, 1920, 1080, true), "text");
            for (int i = 0; i < 9; i++)
            {
                float[] v = Base(); v[i] += i < 4 ? 1f : i == 4 ? 0.5f : -0.1f;
                Check(!Same(k, "a", v, 4, 1920, 1080, true), "value " + i);
            }
            Check(!Same(k, "a", Base(), 5, 1920, 1080, true), "anchor");
            Check(!Same(k, "a", Base(), 4, 1280, 1080, true), "screen width");
            Check(!Same(k, "a", Base(), 4, 1920, 720, true), "screen height");
            Check(!Same(k, "a", Base(), 4, 1920, 1080, false), "language");
        });
        Run("clear forgets the stored label", delegate
        {
            NativeLabelKey k = new NativeLabelKey(); Store(k, "a", Base(), 4, 1920, 1080, true); k.Clear();
            Check(!Same(k, "a", Base(), 4, 1920, 1080, true), "cleared key matched");
        });
        Run("sub-display jitter stays the same, as it did with the string", delegate
        {
            NativeLabelKey k = new NativeLabelKey(); Store(k, "a", Base(), 4, 1920, 1080, true);
            float[] v = Base(); v[0] += 0.001f; v[3] -= 0.002f; v[6] += 0.0001f;
            Check(Same(k, "a", v, 4, 1920, 1080, true), "jitter below the printed precision started a rebuild");
        });
        Run("decides like the old string key on random inputs", delegate
        {
            Random random = new Random(406); int compared = 0;
            for (int i = 0; i < 20000; i++)
            {
                float[] a = Base(), b = Base();
                for (int j = 0; j < 9; j++)
                {
                    float scale = j < 4 ? 800f : j == 4 ? 40f : 1f;
                    a[j] = (float)(random.NextDouble() * scale);
                    // Often equal, often a tiny or a visible change.
                    int kind = random.Next(4);
                    b[j] = kind == 0 ? a[j] : kind == 1 ? a[j] + (float)(random.NextDouble() * 0.004 - 0.002)
                        : kind == 2 ? a[j] + (float)(random.NextDouble() * 0.04 - 0.02) : (float)(random.NextDouble() * scale);
                }
                string ta = random.Next(2) == 0 ? "a" : "b", tb = random.Next(3) == 0 ? "b" : ta;
                // Values within a hair of a rounding boundary may print either way; skip those (both answers are fine).
                if (NearBoundary(a) || NearBoundary(b)) continue;
                NativeLabelKey k = new NativeLabelKey(); Store(k, ta, a, 4, 1920, 1080, true);
                bool oldSame = OldKey(ta, a, 4, 1920, 1080, true) == OldKey(tb, b, 4, 1920, 1080, true);
                Check(Same(k, tb, b, 4, 1920, 1080, true) == oldSame, "differs from the string key at case " + i);
                compared++;
            }
            Check(compared > 5000, "too few cases compared: " + compared);
        });
        Run("deciding does not allocate", delegate
        {
            System.Reflection.MethodInfo allocated = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (allocated == null) { Console.WriteLine("  (skipped: no allocation counter on this runtime)"); return; }
            NativeLabelKey k = new NativeLabelKey(); float[] v = Base(); string text = "开始游戏";
            Store(k, text, v, 4, 1920, 1080, true); Same(k, text, v, 4, 1920, 1080, true);
            long before = (long)allocated.Invoke(null, null);
            for (int i = 0; i < 1000; i++) Same(k, text, v, 4, 1920, 1080, true);
            long after = (long)allocated.Invoke(null, null);
            Check(after - before < 1000, "1000 checks allocated " + (after - before) + " bytes");
        });
        Console.WriteLine(failures == 0 ? "PASS" : "FAILED " + failures);
        return failures == 0 ? 0 : 1;
    }

    static bool NearBoundary(float[] v)
    {
        for (int j = 0; j < 9; j++)
        {
            if (j == 4) continue;
            double scaled = v[j] * (j < 4 ? 100.0 : 1000.0), frac = scaled - Math.Floor(scaled);
            if (Math.Abs(frac - 0.5) < 0.01) return true;
        }
        return false;
    }
}
