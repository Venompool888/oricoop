using System;

namespace OriCoop.Ui
{
    // #406 (2): NativeGui.LabelCore runs for every native label on every repaint and skips the expensive text rebuild
    // when its inputs are the same as last time. It used to compare a string built from them each call (text, the
    // Rect, the font size, the anchor, the Color, the screen size and the language), about ten allocations per label
    // per frame. This keeps the same inputs as fields. The Rect and Color compare at the precision their ToString
    // printed (2 and 3 decimals), so a sub-pixel jitter is still "the same" and does not start a rebuild.
    public sealed class NativeLabelKey
    {
        bool set;
        string text;
        long x, y, width, height, red, green, blue, alpha;
        float fontPx;
        int anchor, screenWidth, screenHeight;
        bool zh;

        static long Q(float value, double scale) { return (long)Math.Round(value * scale, MidpointRounding.AwayFromZero); }

        public bool Same(string text, float x, float y, float width, float height, float fontPx, int anchor,
            float red, float green, float blue, float alpha, int screenWidth, int screenHeight, bool zh)
        {
            return set && string.Equals(this.text, text, StringComparison.Ordinal)
                && this.x == Q(x, 100) && this.y == Q(y, 100) && this.width == Q(width, 100) && this.height == Q(height, 100)
                && this.fontPx.Equals(fontPx) && this.anchor == anchor
                && this.red == Q(red, 1000) && this.green == Q(green, 1000) && this.blue == Q(blue, 1000) && this.alpha == Q(alpha, 1000)
                && this.screenWidth == screenWidth && this.screenHeight == screenHeight && this.zh == zh;
        }

        public void Store(string text, float x, float y, float width, float height, float fontPx, int anchor,
            float red, float green, float blue, float alpha, int screenWidth, int screenHeight, bool zh)
        {
            this.text = text;
            this.x = Q(x, 100); this.y = Q(y, 100); this.width = Q(width, 100); this.height = Q(height, 100);
            this.fontPx = fontPx; this.anchor = anchor;
            this.red = Q(red, 1000); this.green = Q(green, 1000); this.blue = Q(blue, 1000); this.alpha = Q(alpha, 1000);
            this.screenWidth = screenWidth; this.screenHeight = screenHeight; this.zh = zh;
            set = true;
        }

        // A failed render, a hidden label or a removed element must be rebuilt next time.
        public void Clear() { set = false; text = null; }
    }
}
