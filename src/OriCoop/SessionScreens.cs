using System;
using Game;
using HarmonyLib;
using UnityEngine;

using OriCoop.Seams;
using OriCoop.Rules;

namespace OriCoop
{
    // Online-only screens and focus rules:
    //  * losing window focus never stops the game or the network (runInBackground, no resume screen);
    //  * the "partner paused the game" banner (the pause itself lives in SessionRules);
    //  * the guest's full-screen "receiving the host's save" screen, which blocks input until the
    //    host's checkpoint is loaded, or shows a clear failure message.
    public static class SessionScreens
    {
        static IRulesHost Host { get { return SessionRules.Host; } }
        public enum SavePhase { None, Waiting, Receiving, Loading, Failed }

        public static SavePhase Phase { get; private set; }
        public static float Progress { get; private set; }      // 0..1 while receiving, -1 when unknown
        public static int FocusEvents, SuppressedResumeScreens, BackgroundFixes, SaveScreensShown;
        public static bool Blocking { get { return Phase != SavePhase.None; } }
        static bool s_failureShown, s_reflectionWarned;
        static float s_failedAt, s_phaseSince;

        static void WarnReflection()
        {
            if (s_reflectionWarned) return;
            s_reflectionWarned = true;
            Services.Log.Warn("net: SessionScreens cannot read SaveSync state (fields renamed?); save screen disabled");
        }

        public static void Tick()
        {
            // Keep the player loop (and with it NetPlay.Update and keepalives) running unfocused.
            if (Services.Net.Enabled && !Application.runInBackground) { Application.runInBackground = true; BackgroundFixes++; }
            SavePhase next = ComputeSavePhase();
            if (next != Phase)
            {
                if (Phase == SavePhase.None && next != SavePhase.None) SaveScreensShown++;
                Services.Log.Info("net: save screen " + Phase + " -> " + next);
                Phase = next; s_phaseSince = Time.realtimeSinceStartup;
            }
        }

        static SavePhase ComputeSavePhase()
        {
            Progress = -1f;
            if (!Services.Net.Enabled || Services.Net.IsHost) { s_failureShown = false; return SavePhase.None; }
            if (!Host.SaveFieldsAvailable) { WarnReflection(); return SavePhase.None; }
            bool disabled = Host.SaveDisabled, loading = Host.SaveLoading, started = Host.SaveStarted;
            bool pending = Host.SavePending;
            if (Phase == SavePhase.Failed)
            {
                // Dismissed by any key after a moment, or after 15 s.
                float shown = Time.realtimeSinceStartup - s_failedAt;
                if ((shown > 1.5f && Input.anyKeyDown) || shown > 15f || !Services.Net.Connected) return SavePhase.None;
                return SavePhase.Failed;
            }
            if (!Services.Net.Connected) return SavePhase.None;
            if (disabled)
            {
                if (Phase != SavePhase.None && !s_failureShown)
                { s_failureShown = true; s_failedAt = Time.realtimeSinceStartup; return SavePhase.Failed; }
                return SavePhase.None;
            }
            s_failureShown = false;
            if (loading) return SavePhase.Loading;
            if (!started || Services.Session.Ready) return SavePhase.None;
            // A guest still on the title / slot screen picks a slot first; SaveSync loads the host's
            // save once they are in gameplay. Only cover the screen from then on.
            // Exception: a guest that picked an empty slot waits on the title for the host's save (GuestJoin.cs).
            // (Not while it asks which slot to overwrite: that slot list must stay visible and usable.)
            if (!Services.Session.LocalInGameplay && !(Host.GuestWaiting && !Host.GuestNeedsSlot)) return SavePhase.None;
            if (pending) return SavePhase.Loading;
            int have, total;
            if (Host.IncomingProgress(out have, out total) && total > 4)
            { Progress = Mathf.Clamp01(have / (float)total); return SavePhase.Receiving; }
            return Host.RemoteInGameplay ? SavePhase.Receiving : SavePhase.Waiting;
        }

        // ---------------------------------------------------------------- focus

        [HarmonyPatch(typeof(GameController), "OnApplicationFocus")]
        static class KeepRunningUnfocused
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(bool focusStatus)
            {
                if (!Services.Net.Enabled) return true;
                // Vanilla shows the resume screen (SuspendAll) and turns runInBackground off two
                // frames later, which freezes Unity and times the partner out. Online: neither.
                Application.runInBackground = true;
                GameController.IsFocused = focusStatus;
                FocusEvents++;
                Services.Log.Info("net: window focus " + (focusStatus ? "gained" : "lost") + "; game keeps running");
                return false;
            }
        }

        // Also raised by a resolution change (alt-tab out of fullscreen) in GameController.FixedUpdate.
        [HarmonyPatch(typeof(MenuScreenManager), "ShowResumeScreen")]
        static class NoFocusPauseOnline
        {
            static bool Prefix()
            {
                if (!Services.Net.Enabled) return true;
                SuppressedResumeScreens++;
                return false;
            }
        }

        // ---------------------------------------------------------------- input block while loading

        [HarmonyPatch(typeof(GameController), "get_InputLocked")]
        static class BlockWhileLoading
        {
            // Never lock the partner's puppet: it replays the host's input inside P2Context.
            static void Postfix(ref bool __result) { if (Blocking && !Host.Inside) __result = true; }
        }
        [HarmonyPatch(typeof(SeinController), "get_InputLocked")]
        static class BlockOwnWhileLoading
        {
            static void Postfix(SeinController __instance, ref bool __result)
            {
                if (!Blocking || Host.Inside) return;
                SeinCharacter own = Host.P1 != null ? Host.P1 : Characters.Sein;
                if (own != null && __instance == own.Controller) __result = true;
            }
        }

        // ---------------------------------------------------------------- drawing

        internal static string TestPage = "";
        static string s_page;
        static int s_element;
        static void Begin(string page) { s_page = page; s_element = 0; Host.BeginPage(page); }
        static Texture2D s_solid;
        static GUIStyle s_titleZh, s_titleEn, s_bodyZh, s_bodyEn, s_small;
        static float s_styleScale;
        static readonly Color Ink = new Color(0.867f, 0.918f, 0.898f);       // #DDEAE5
        static readonly Color InkDim = new Color(0.541f, 0.655f, 0.671f);    // #8AA7AB
        static readonly Color Glow = new Color(0.39f, 0.87f, 0.96f);
        static readonly Color Warn = new Color(0.95f, 0.65f, 0.55f);

        static void EnsureStyles(float scale)
        {
            if (s_solid == null)
            {
                s_solid = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                s_solid.SetPixel(0, 0, Color.white); s_solid.Apply(); s_solid.hideFlags = HideFlags.HideAndDontSave;
            }
            if (s_titleZh != null && Mathf.Abs(s_styleScale - scale) < 0.01f) return;
            s_styleScale = scale;
            s_titleZh = Style(52f * scale); s_titleEn = Style(26f * scale);
            s_bodyZh = Style(26f * scale); s_bodyEn = Style(20f * scale); s_small = Style(20f * scale);
        }
        static GUIStyle Style(float size)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.font = GUI.skin.font; s.alignment = TextAnchor.MiddleCenter; s.wordWrap = false;
            s.clipping = TextClipping.Overflow; s.fontSize = Mathf.Max(8, Mathf.RoundToInt(size));
            return s;
        }
        static void Text(Rect r, string text, GUIStyle style, Color c)
        {
            string id = s_page + ".text." + s_element++;
            if (Host.NativeGuiAvailable) { Host.Label(id, r, text, style.fontSize, style.alignment, c); return; }
            Color prev = style.normal.textColor;
            style.normal.textColor = new Color(0.01f, 0.03f, 0.05f, 0.75f * c.a);
            GUI.Label(new Rect(r.x, r.y + 2f, r.width, r.height), text, style);
            style.normal.textColor = c; GUI.Label(r, text, style);
            style.normal.textColor = prev;
        }
        static void Fill(Rect r, Color c)
        {
            string id = s_page + ".panel." + s_element++;
            if (Host.NativeGuiAvailable) { Host.Panel(id, r, c); return; }
            Color prev = GUI.color; GUI.color = c; GUI.DrawTexture(r, s_solid); GUI.color = prev;
        }

        public static void Draw()
        {
            if (!Services.Net.Enabled && !(Host.L10nProbeOn && TestPage.Length > 0)) return;
            try
            {
                float scale = Screen.height / 1080f;
                EnsureStyles(scale);
                if (Host.L10nProbeOn && TestPage.Length > 0)
                {
                    Begin(TestPage);
                    try { if (TestPage == "session.save") DrawSaveScreen(scale); else DrawPartnerPaused(scale); }
                    finally { Host.EndPage(); }
                    return;
                }
                if (Blocking) { Begin("session.save"); try { DrawSaveScreen(scale); } finally { Host.EndPage(); } return; }
                if (!SessionRules.PausedByPartner && !(SessionRules.LocalPaused && Host.PartnerHere)) return;
                Begin("session.paused");
                try
                {
                    if (SessionRules.PausedByPartner) DrawPartnerPaused(scale);
                    else if (SessionRules.LocalPaused && Host.PartnerHere) DrawBothPausedHint(scale);
                }
                finally { Host.EndPage(); }
            }
            catch (Exception e) { Services.Log.Error("SessionScreens Draw error: " + e); }
        }

        static void DrawPartnerPaused(float scale)
        {
            // A soft dark band across the upper middle; the world stays visible behind it.
            float h = 150f * scale, y = Screen.height * 0.30f;
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.01f, 0.03f, 0.06f, 0.35f));
            Fill(new Rect(0, y, Screen.width, h), new Color(0.01f, 0.03f, 0.06f, 0.55f));
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.realtimeSinceStartup * 2.2f);
            float lineW = 520f * scale;
            Fill(new Rect((Screen.width - lineW) * 0.5f, y + h - 3f * scale, lineW, 2f * scale), new Color(Glow.r, Glow.g, Glow.b, 0.55f * pulse));
            Text(new Rect(0, y + 22f * scale, Screen.width, 60f * scale), Host.Text("session.paused"), s_titleZh, Ink);
            Text(new Rect(0, y + h + 14f * scale, Screen.width, 26f * scale), Host.Text("session.resume_help"), s_small, new Color(InkDim.r, InkDim.g, InkDim.b, 0.9f));
        }

        static void DrawBothPausedHint(float scale)
        {
            Text(new Rect(0, 24f * scale, Screen.width, 28f * scale), Host.Text("session.both_paused"), s_small, new Color(Ink.r, Ink.g, Ink.b, 0.85f));
        }

        static void DrawSaveScreen(float scale)
        {
            float t = Time.realtimeSinceStartup;
            float fade = Host.L10nProbeOn && TestPage == "session.save" ? 1f : Mathf.Clamp01((t - s_phaseSince) / 0.3f);
            if (Phase == SavePhase.Failed) fade = 1f;
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.012f, 0.03f, 0.055f, 0.93f));
            float cy = Screen.height * 0.42f;
            if (Phase == SavePhase.Failed)
            {
                Text(new Rect(0, cy - 70f * scale, Screen.width, 60f * scale), Host.Text("session.failed"), s_titleZh, Warn);
                Text(new Rect(0, cy + 50f * scale, Screen.width, 30f * scale), Host.Text("session.failed_help"), s_bodyZh, Ink);
                if (t - s_failedAt > 1.5f)
                    Text(new Rect(0, Screen.height - 90f * scale, Screen.width, 28f * scale), Host.Text("session.continue"), s_small, Ink);
                return;
            }
            string text;
            if (Phase == SavePhase.Waiting) { text = Host.Text("session.waiting"); }
            else if (Phase == SavePhase.Receiving) { text = Host.Text("session.receiving"); }
            else { text = Host.Text("session.loading"); }
            Color ink = new Color(Ink.r, Ink.g, Ink.b, fade), dim = new Color(InkDim.r, InkDim.g, InkDim.b, fade);
            DrawSpinner(Screen.width * 0.5f, cy - 110f * scale, scale, fade);
            Text(new Rect(0, cy - 60f * scale, Screen.width, 60f * scale), text, s_titleZh, ink);
            // Progress line: real fraction while fragments arrive, otherwise a travelling glow.
            float w = 560f * scale, x = (Screen.width - w) * 0.5f, y = cy + 64f * scale, bh = 3f * scale;
            Fill(new Rect(x, y, w, bh), new Color(1f, 1f, 1f, 0.12f * fade));
            if (Phase == SavePhase.Receiving && Progress >= 0f)
            {
                Fill(new Rect(x, y, w * Progress, bh), new Color(Glow.r, Glow.g, Glow.b, 0.9f * fade));
                Text(new Rect(0, y + 12f * scale, Screen.width, 26f * scale), Host.Format("session.progress", Mathf.RoundToInt(Progress * 100f)), s_small, dim);
            }
            else
            {
                float seg = w * 0.22f, p = Mathf.Repeat(t * 0.6f, 1f) * (w + seg) - seg;
                float a = Mathf.Max(x, x + p), b = Mathf.Min(x + w, x + p + seg);
                if (b > a) Fill(new Rect(a, y, b - a, bh), new Color(Glow.r, Glow.g, Glow.b, 0.8f * fade));
            }
            string help = Phase == SavePhase.Waiting ? Host.Text("session.waiting_help") : Host.Text("session.loading_help");
            Text(new Rect(0, Screen.height - 110f * scale, Screen.width, 28f * scale), help, s_bodyZh, ink);
        }

        static void DrawSpinner(float cx, float cy, float scale, float alpha)
        {
            float radius = 22f * scale, baseAngle = Time.realtimeSinceStartup * 4f;
            for (int i = 0; i < 12; i++)
            {
                float frac = i / 12f, angle = baseAngle + frac * Mathf.PI * 2f;
                float size = (3f + frac * 5f) * scale;
                Fill(new Rect(cx + Mathf.Cos(angle) * radius - size * 0.5f, cy + Mathf.Sin(angle) * radius - size * 0.5f, size, size),
                    new Color(Glow.r, Glow.g, Glow.b, Mathf.Pow(frac, 1.8f) * alpha));
            }
        }
    }
}
