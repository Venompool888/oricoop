using System;

namespace OriCoop.Ui
{
    // T-093: size of the 「怎么玩？单人 / 双人」 chooser for a screen. Everything scales with the screen height (1080 = reference):
    // the old chooser capped its panel at 680 px and picked its fonts once at the first draw, so at 2K / 4K it was a small
    // box with wrapped text (and kept the small fonts if the window grew later). No Unity types: unit-tested without the game.
    public struct ModeSelectLayout
    {
        public float PanelW, PanelH, RowH, FontPx, TitlePx, SmallPx;

        public static ModeSelectLayout Compute(int screenW, int screenH)
        {
            ModeSelectLayout l = new ModeSelectLayout();
            if (Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1" && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_MODE_FIXED_SIZE") == "1")
            {
                // The old fixed-size chooser (test-only mutation: the scaling checks must fail).
                l.FontPx = Math.Max(14, screenH / 42); l.TitlePx = (int)(l.FontPx * 1.35f); l.SmallPx = (int)(l.FontPx * 0.8f);
                l.PanelW = Math.Min(screenW - 40f, 680f); l.RowH = l.FontPx * 1.9f * 2.1f; l.PanelH = l.FontPx * 1.9f * 9.4f;
                return l;
            }
            float scale = screenH / 1080f;
            l.FontPx = Math.Max(14, (int)Math.Round(28 * scale));
            l.TitlePx = (int)Math.Round(l.FontPx * 1.35f);
            l.SmallPx = (int)Math.Round(l.FontPx * 0.8f);
            float lineH = l.FontPx * 1.9f;
            l.RowH = lineH * 2.1f;
            l.PanelH = Math.Min(lineH * 9.4f, screenH - 20f);
            l.PanelW = Math.Min(screenW - 40f * scale, 960f * scale);
            return l;
        }
    }
}
