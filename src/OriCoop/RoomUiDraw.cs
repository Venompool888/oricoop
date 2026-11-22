using System;
using System.Collections.Generic;
using System.IO;
using SmartInput;
using UnityEngine;

namespace OriCoop
{
    // T-171 / T-173: shared drawing for the room pages (房间管理页, 我的房间 join / list), with NativeGui (the game's font
    // and GUI camera). The misty pieces of the designs (title banner, row bands, input frames, description box, keycaps)
    // are transparent textures embedded from src/assets/room-ui/*.png (AI-made for this mod, not taken from the game);
    // a build without them (e.g. the public sync) falls back to a generated soft-edged band.
    internal static class RoomUiDraw
    {
        public static readonly Color Text = new Color(0.93f, 0.96f, 1f, 1f);
        public static readonly Color Dim = new Color(0.75f, 0.82f, 0.92f, 0.85f);
        public static readonly Color Gold = new Color(1f, 0.86f, 0.5f, 1f);
        public static readonly Color Cyan = new Color(0.55f, 0.9f, 1f, 1f);
        static readonly Color BandTint = new Color(0.03f, 0.09f, 0.2f, 0.62f);
        static readonly Color SelectedTint = new Color(0.25f, 0.75f, 0.95f, 0.42f);

        static readonly Dictionary<string, Texture2D> s_textures = new Dictionary<string, Texture2D>();

        // An embedded texture (OriCoop.roomui.<name>.png) or null.
        public static Texture2D Embedded(string name)
        {
            Texture2D t;
            if (s_textures.TryGetValue(name, out t)) return t;
            t = null;
            try
            {
                using (Stream s = typeof(RoomUiDraw).Assembly.GetManifestResourceStream("OriCoop.roomui." + name + ".png"))
                {
                    if (s != null)
                    {
                        byte[] b = new byte[s.Length];
                        int n = 0; while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                        t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                        if (!t.LoadImage(b)) { UnityEngine.Object.Destroy(t); t = null; }
                        else { t.wrapMode = TextureWrapMode.Clamp; t.hideFlags = HideFlags.HideAndDontSave; }
                    }
                }
            }
            catch (Exception e) { NativeGui.WarnOnce("roomui.texture", name, e); t = null; }
            s_textures[name] = t;
            return t;
        }

        static Texture2D s_soft;
        public static Texture2D Soft()
        {
            if (s_soft != null) return s_soft;
            const int W = 128, H = 32;
            s_soft = new Texture2D(W, H, TextureFormat.ARGB32, false);
            s_soft.wrapMode = TextureWrapMode.Clamp;
            s_soft.hideFlags = HideFlags.HideAndDontSave;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W, v = (y + 0.5f) / H;
                    float ex = Mathf.Clamp01(Mathf.Min(u, 1f - u) / 0.12f), ey = Mathf.Clamp01(Mathf.Min(v, 1f - v) / 0.35f);
                    s_soft.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, ex) * Mathf.SmoothStep(0f, 1f, ey)));
                }
            s_soft.Apply();
            return s_soft;
        }

        // A texture piece drawn white (its own colours), or the soft band tinted when the texture is missing.
        static void Piece(string id, Rect r, string texture, Color fallback)
        {
            Texture2D t = Embedded(texture);
            if (t != null) NativeGui.Texture(id, r, t, Color.white);
            else NativeGui.Texture(id, r, Soft(), fallback);
        }

        // The page title on its smoky banner (房间管理 / 加入房间 / 房间列表).
        public static void Title(string id, string text)
        {
            float w = Screen.width, h = Screen.height;
            Rect banner = new Rect(w * 0.27f, h * 0.035f, w * 0.46f, h * 0.17f);
            RoomUiFx.SmokeBehind(id + ".banner", banner, 1f);   // T-184
            Piece(id + ".banner", banner, "banner", new Color(0.02f, 0.05f, 0.14f, 0.8f));
            RoomUiFx.Ribbon(id + ".banner", banner);
            NativeGui.Label(id, new Rect(0, h * 0.05f, w, h * 0.12f), text, h * 0.06f, TextAnchor.MiddleCenter, Text);
        }

        // A list row band (player row, room row, dropdown entry); selected = the bright cyan one.
        public static void Band(string id, Rect r, bool selected)
        {
            Rect wide = new Rect(r.x - r.width * 0.03f, r.y - r.height * 0.12f, r.width * 1.06f, r.height * 1.24f);
            Piece(id, wide, selected ? "band_selected" : "band", selected ? SelectedTint : BandTint);
            RoomUiFx.FrameSparks(id, wide, selected);   // T-184
        }

        // An input field frame (服务器 / 邀请码).
        public static void Frame(string id, Rect r, bool selected)
        {
            Piece(id, r, selected ? "frame_selected" : "frame", selected ? SelectedTint : BandTint);
            RoomUiFx.FrameSparks(id, r, selected);   // T-184
        }

        // The misty description box under a menu.
        public static void Description(string id, Rect r, string text)
        {
            RoomUiFx.PlateBehind(id + ".box", r);   // T-184
            Piece(id + ".box", r, "desc", new Color(0.02f, 0.06f, 0.16f, 0.75f));
            RoomUiFx.PlateMotes(id + ".box", r);
            NativeGui.Label(id, r, text, Screen.height * 0.03f, TextAnchor.MiddleCenter, Text);
        }

        public static void Arrow(string id, Rect r, Color tint)
        {
            Texture2D t = Embedded("arrow");
            if (t != null) NativeGui.Texture(id, r, t, tint);
            else NativeGui.Label(id, r, ">", r.height * 0.9f, TextAnchor.MiddleCenter, tint);
        }

        public struct Key
        {
            public string Name, Label;
            public string Icon;   // the game's button icon markup (KeyIcon), or null: a drawn keycap with Name
            public bool Enabled, Selected;
        }

        // A soft, faint glow behind the selected bar item (owner 2026-11-22: keep the selection, lighter than the band).
        static readonly Color BarGlow = new Color(0.5f, 0.85f, 1f, 0.3f);
        static readonly Color BarDisabled = new Color(Text.r, Text.g, Text.b, 0.4f);

        // The bottom key bar, drawn like the game's own legends (the save slot screen's "[SPACE] 选择 ... [ESC] 返回"):
        // the game's button icon and the label in one game text, so the icon is the game's own glyph and follows the
        // keyboard / controller (and the PlayStation icons, T-178) like the game's legends do. Items are spread evenly
        // across the width at the legend's height; the selected one (the bar can be stepped through with left / right)
        // has a soft glow behind it, one that can't be used now is greyed. A key the game has no icon for gets a drawn
        // keycap with its name. Returns each item's rect (for clicks).
        public static List<Rect> KeyBar(string id, IList<Key> keys)
        {
            List<Rect> rects = new List<Rect>();
            float w = Screen.width, h = Screen.height;
            float itemH = h * 0.07f, y = h * 0.918f - itemH / 2f, span = w * 0.8f, x0 = w * 0.1f, font = h * 0.032f;
            float itemW = span / Math.Max(1, keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                Key k = keys[i];
                string kid = id + "." + i;
                Rect r = new Rect(x0 + i * itemW, y, itemW, itemH);
                rects.Add(r);
                if (k.Selected)
                {
                    Rect sel = new Rect(r.x + itemW * 0.06f, r.y - itemH * 0.08f, itemW * 0.88f, itemH * 1.16f);
                    NativeGui.Texture(kid + ".sel", sel, Soft(), BarGlow);
                    RoomUiFx.Selected(kid + ".sel", sel, Soft());   // T-184
                }
                Color color = k.Enabled ? Text : BarDisabled;
                if (!string.IsNullOrEmpty(k.Icon))
                {
                    NativeGui.Label(kid, r, k.Icon + " " + k.Label, font, TextAnchor.MiddleCenter, color);
                    continue;
                }
                if (string.IsNullOrEmpty(k.Name))   // T-189: an item with no key (in-game 离开房间): the label alone, no empty cap
                {
                    NativeGui.Label(kid, r, k.Label, font, TextAnchor.MiddleCenter, color);
                    continue;
                }
                float capH = font * 1.15f, capW = CapWidth(capH, k.Name);
                float cx = r.center.x - itemW * 0.05f;
                Keycap(kid, new Rect(cx - capW, r.center.y - capH / 2f, capW, capH), k.Name, color);
                NativeGui.Label(kid, new Rect(cx + itemW * 0.04f, r.y, itemW * 0.55f, itemH), k.Label, font, TextAnchor.MiddleLeft, color);
            }
            return rects;
        }

        // The game's own button icon for a keyboard key / controller button, picked the way the game's legends pick
        // theirs (ButtonIconUtility: the key when the keyboard was used last, else the controller button): "<icon>C</>"
        // markup for a game text, or null when the game has no icon for it (a key like O, or no controller button).
        public static string KeyIcon(KeyCode keyboard, KeyCode pad)
        {
            try
            {
                // The IMGUI fallback ([UI] NativeText = false) would print the markup: keycaps there.
                if (PlayerInput.Instance == null || GameSettings.Instance == null || !NativeGui.Available) return null;
                List<IButtonInput> inputs = new List<IButtonInput>();
                // 7 / 8 are the game's stand-ins for its keyboard-navigation / arrow-keys icons, not keycaps of their own.
                if (keyboard != KeyCode.None && keyboard != KeyCode.Alpha7 && keyboard != KeyCode.Alpha8) inputs.Add(new KeyCodeButtonInput(keyboard));
                XboxControllerInput.Button button;
                if (PadButton(pad, out button)) inputs.Add(new ControllerButtonInput(button));
                string icon = ButtonIconUtility.GetButtonString(new CompoundButtonInput(inputs.ToArray()));
                return icon != null && icon.StartsWith("<icon>", StringComparison.Ordinal) ? icon : null;
            }
            catch (Exception e) { NativeGui.WarnOnce("roomui.keyicon", keyboard + "/" + pad, e); return null; }
        }

        // A configured key / controller button held now. Controller buttons are read through the game's own input
        // (XboxControllerInput, where the bars' icons come from), not Unity's raw JoystickButtonN: a pad Unity numbers in
        // another order (a DualSense without Steam Input, which the game reads through T-178's XInput hook: Unity's
        // button 2 is Circle) then still does what its icon says.
        public static bool KeyHeld(KeyCode key)
        {
            XboxControllerInput.Button button;
            if (!PadButton(key, out button)) return key != KeyCode.None && Input.GetKey(key);
            try { return XboxControllerInput.GetButton(button); }
            catch (Exception e) { NativeGui.WarnOnce("roomui.padbutton", key.ToString(), e); return false; }
        }

        // Unity's JoystickButtonN (any joystick) as the game's controller button (XInput order: A B X Y LB RB View Menu).
        static bool PadButton(KeyCode pad, out XboxControllerInput.Button button)
        {
            button = XboxControllerInput.Button.ButtonA;
            int code = (int)pad - (int)KeyCode.JoystickButton0;
            if (pad == KeyCode.None || code < 0 || code >= 20 * 9) return false;
            switch (code % 20)
            {
                case 0: button = XboxControllerInput.Button.ButtonA; return true;
                case 1: button = XboxControllerInput.Button.ButtonB; return true;
                case 2: button = XboxControllerInput.Button.ButtonX; return true;
                case 3: button = XboxControllerInput.Button.ButtonY; return true;
                case 4: button = XboxControllerInput.Button.LeftShoulder; return true;
                case 5: button = XboxControllerInput.Button.RightShoulder; return true;
                case 6: button = XboxControllerInput.Button.Select; return true;
                case 7: button = XboxControllerInput.Button.Start; return true;
                case 8: button = XboxControllerInput.Button.LeftStick; return true;
                case 9: button = XboxControllerInput.Button.RightStick; return true;
            }
            return false;
        }

        static float CapWidth(float capH, string name) { return Mathf.Max(capH * 1.1f, capH * 0.42f * name.Length + capH * 0.5f); }

        // A drawn keycap with the key's name, for a key the game has no button icon for.
        static void Keycap(string id, Rect cap, string name, Color color)
        {
            Texture2D keycap = Embedded("keycap");
            if (keycap != null) NativeGui.Texture(id + ".cap", cap, keycap, new Color(1f, 1f, 1f, color.a));
            else
            {
                NativeGui.Panel(id + ".cap", cap, new Color(0.9f, 0.93f, 1f, 0.9f * color.a));
                NativeGui.Panel(id + ".capin", new Rect(cap.x + 2f, cap.y + 2f, cap.width - 4f, cap.height - 4f), new Color(0.06f, 0.09f, 0.16f, 0.92f));
            }
            NativeGui.Label(id + ".key", cap, name, cap.height * 0.5f, TextAnchor.MiddleCenter, color);
        }

        // T-173 (room-v2-a): a small key hint "[E] 改名" starting at x, centred on y, keycap height capH: the game's icon
        // (icon markup from KeyIcon) and the label in one game text, or a drawn keycap with the key's name when icon is
        // null. Returns the rect the hint covers (for a click).
        public static Rect KeyHint(string id, float x, float y, float capH, string icon, string key, string label, Color color)
        {
            float capW = CapWidth(capH, key), fontPx = capH * 0.85f, labelW = fontPx * 0.3f;
            foreach (char c in label ?? "") labelW += fontPx * (c > '~' ? 1.05f : 0.55f);   // CJK about 1 em, Latin about half
            if (!string.IsNullOrEmpty(icon))
                NativeGui.Label(id, new Rect(x, y - capH, capW + capH * 0.3f + labelW, capH * 2f), icon + " " + label, fontPx, TextAnchor.MiddleLeft, color);
            else
            {
                Keycap(id, new Rect(x, y - capH / 2f, capW, capH), key, color);
                NativeGui.Label(id, new Rect(x + capW + capH * 0.3f, y - capH, labelW, capH * 2f), label, fontPx, TextAnchor.MiddleLeft, color);
            }
            return new Rect(x - capH * 0.2f, y - capH * 0.8f, capW + capH * 0.5f + labelW, capH * 1.6f);
        }

        // T-173 (room-v2-a): the title's smoky banner, smaller, behind the room name and the 玩家列表 header.
        public static void Banner(string id, Rect r)
        {
            RoomUiFx.SmokeBehind(id, r, 0.7f);   // T-184
            Piece(id, r, "banner", new Color(0.02f, 0.05f, 0.14f, 0.8f));
            RoomUiFx.Ribbon(id, r);
        }

        // Keyboard key names for the keycaps.
        public static string KeyName(KeyCode code)
        {
            switch (code)
            {
                case KeyCode.Space: return "SPACE";
                case KeyCode.Delete: return "DEL";
                case KeyCode.Escape: return "ESC";
                case KeyCode.Return: return "ENTER";
                case KeyCode.Backspace: return "BKSP";
                case KeyCode.Tab: return "TAB";
                case KeyCode.None: return "-";
            }
            string n = code.ToString();
            if (n.StartsWith("Alpha")) n = n.Substring(5);
            return n.ToUpperInvariant();
        }
    }
}
