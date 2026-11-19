using System;
using System.Collections.Generic;
using UnityEngine;

namespace OriCoop.Ui
{
    // T-153: Font.CreateDynamicFontFromOSFont makes a new Font asset each call, and Unity never frees it on its own
    // (destroying the GameObject that shows it does not). CoopMenu made one per screen-height change, the P2 name tag and
    // the revive HUD one per (re)created text object, so they piled up for the whole session.
    internal static class OsFonts
    {
        static readonly Dictionary<string, Font> s_shared = new Dictionary<string, Font>();

        // Test-only mutation: the old behaviour (a new font every call, nothing destroyed).
        internal static bool LeakMutated
        {
            get { return Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1" && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_FONT_LEAK") == "1"; }
        }

        // One font per name list and size for the whole session, shared by every text object that asks for it.
        public static Font Shared(string[] names, int size)
        {
            string key = string.Join("|", names) + "#" + size;
            Font font;
            if (!LeakMutated && s_shared.TryGetValue(key, out font) && font != null) return font;
            font = Create(names, size);
            if (font != null) s_shared[key] = font;
            return font;
        }

        // A font one owner keeps at a size that changes (CoopMenu: from the screen height): the old one is destroyed.
        public static Font Replace(Font old, string[] names, int size)
        {
            if (old != null && !LeakMutated) UnityEngine.Object.Destroy(old);
            return Create(names, size);
        }

        static Font Create(string[] names, int size)
        {
            try { return Font.CreateDynamicFontFromOSFont(names, size); } catch (Exception) { return null; }
        }
    }
}
