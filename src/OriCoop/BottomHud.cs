using System;
using System.Collections.Generic;
using System.Reflection;
using CatlikeCoding.TextBox;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // T-061: a status bar for every teammate along the bottom of the screen, next to the game's own HUD.
    //
    // Each bar is a copy of the game's own HUD strip (seinHUD/ui: energy cells, the spirit-light orb with the ability
    // point count, health cells, soul-flame ring), so it uses the game's sprites, font and animations. The copy's value
    // providers (SeinHealthVisualMinProvider, ...) read Characters.Sein, i.e. P1; a Harmony prefix answers the per-player
    // ones (health, energy, soul flame) from that teammate's VitalsModel instead. Shared progress (spirit light, ability
    // points) keeps showing the team's value. Values come from the teammate's own Ori in local co-op and from the
    // Vitals message (79) online. Downed = drawn at 35 % opacity, gone (left / no Ori) = hidden. The bars fade with the
    // game's own HUD (pause, cutscenes, fades).
    public static class BottomHud
    {
        public const byte MsgVitals = 79;
        const float DownedOpacity = 0.35f;
        const string StatusChild = "status";   // seinHUD/ui/status: energy, orb, health, soul flame (not the keystone / map stone corners: team values)

        sealed class Copy
        {
            public int Seat;
            public GameObject Root;
            public TransparencyAnimator Fade;
            public GameObject Label;   // the game's hint text (letters and digits), placed beside the copy each frame
            public TextBox LabelBox;
            public float LabelAlpha = -1f;
            public readonly List<FloatValueProvider> Providers = new List<FloatValueProvider>();
            public GameObject EnergyBar, SoulFlameFull, SoulFlameFlame;
            public readonly VitalsModel Model = new VitalsModel();
            public float Opacity;
            public SlotRect Slot;
        }

        static readonly Dictionary<int, Copy> s_copies = new Dictionary<int, Copy>();
        static readonly Dictionary<FloatValueProvider, Copy> s_byProvider = new Dictionary<FloatValueProvider, Copy>();
        static readonly Dictionary<int, VitalsModel> s_net = new Dictionary<int, VitalsModel>();   // online: seat -> last received
        static readonly Dictionary<Type, StatKind> s_kinds = new Dictionary<Type, StatKind>();
        static readonly Dictionary<Type, FieldInfo> s_divide = new Dictionary<Type, FieldInfo>();
        static readonly VitalsSender s_sender = new VitalsSender();
        static readonly HashSet<string> s_failures = new HashSet<string>();
        static bool s_patched, s_registered;
        static float s_lastTick;

        public static bool TestHide;                       // test: hide every copy (pixel check)
        public static readonly List<int> TestFakeSeats = new List<int>(); // test: extra copies for layout checks (values set by the test)
        public static int VitalsSent, VitalsReceived;

        // ---------------------------------------------------------------- per frame (Plugin.DoLateUpdate)
        public static void Update()
        {
            EnsurePatched();
            if (Services.Net != null && Services.Net.Enabled) { EnsureRegistered(); SendOwn(); }
            float now = Time.unscaledTime, dt = Mathf.Clamp(now - s_lastTick, 0f, 0.25f);
            s_lastTick = now;

            // Who gets a bar: every teammate with an Ori on this screen.
            List<int> seats = new List<int>();
            Dictionary<int, Puppet> puppets = new Dictionary<int, Puppet>();
            foreach (Puppet p in Players.Remotes)
                if (p != null && p.Sein != null && p.Seat >= 1 && p.Seat <= Players.MaxSeats && !puppets.ContainsKey(p.Seat))
                { puppets[p.Seat] = p; seats.Add(p.Seat); }
            foreach (int s in TestFakeSeats) if (!puppets.ContainsKey(s) && !seats.Contains(s)) seats.Add(s);
            seats.Sort();

            List<int> gone = new List<int>();
            foreach (int seat in s_copies.Keys) if (!seats.Contains(seat)) gone.Add(seat);
            foreach (int seat in gone) DestroyCopy(seat);

            SeinUI ui = UI.SeinUI;
            float hud = P2Hud.GameHudOpacity();
            if (ui == null || ui.UI == null) { DestroyAll(); return; }

            Rect native;
            bool haveNative = NativeFrame(ui, out native);
            for (int i = 0; i < seats.Count; i++)
            {
                int seat = seats[i];
                Copy c;
                s_copies.TryGetValue(seat, out c);
                try
                {
                    if (c == null || c.Root == null)
                    {
                        // Copy only while the game's HUD is fully shown: the copy's fade caches the materials' alpha now.
                        if (hud < 0.999f || !haveNative) continue;
                        c = Create(ui, seat);
                        if (c == null) continue;
                    }
                    Puppet puppet;
                    bool have = Feed(c, puppets.TryGetValue(seat, out puppet) ? puppet : null, dt);
                    float a = have && !TestHide ? hud * (c.Model.Now.Downed ? DownedOpacity : 1f) : 0f;
                    Show(c, a);
                    if (a > 0f && haveNative) Place(ui, c, i, seats.Count, native);
                }
                catch (Exception e) { Warn("bar P" + seat, e); if (c != null) Show(c, 0f); }
            }
        }

        // ---------------------------------------------------------------- values
        static bool Feed(Copy c, Puppet puppet, float dt)
        {
            if (TestFakeSeats.Contains(c.Seat) && puppet == null) { c.Model.Tick(dt); return c.Model.Have; }
            if (puppet == null || puppet.Sein == null) return false;
            // A downed Ori is switched off (Revive) but keeps its (dimmed) bar; any other inactive Ori has none.
            if (!puppet.Sein.gameObject.activeInHierarchy && !Revive.IsDowned(puppet.Sein)) return false;
            if (NetPlay.Enabled)
            {
                VitalsModel m;
                if (!s_net.TryGetValue(c.Seat, out m) || !m.Have) return false;   // no numbers from that player yet
                VitalsSample s = m.Now;
                s.Downed = s.Downed || Revive.IsDowned(puppet.Sein);
                c.Model.Set(s);
            }
            else
            {
                VitalsSample s = Sample(puppet.Sein);
                c.Model.Set(s);
            }
            c.Model.Tick(dt);
            return true;
        }

        public static VitalsSample Sample(SeinCharacter s)
        {
            VitalsSample v = new VitalsSample();
            if (s == null) return v;
            if (s.Mortality != null && s.Mortality.Health != null) { v.Health = s.Mortality.Health.Amount; v.MaxHealth = s.Mortality.Health.MaxHealth; }
            if (s.Energy != null) { v.Energy = s.Energy.Current; v.MaxEnergy = s.Energy.Max; }
            v.SoulFlame = s.SoulFlame != null ? s.SoulFlame.BarValue : 1f;
            v.Downed = Revive.IsDowned(s);
            return v;
        }

        // What the bar of this seat shows right now, read back through its own (patched) providers: the real path.
        public static bool TryShown(int seat, out float health, out float maxHealth, out float energy, out float maxEnergy)
        {
            health = maxHealth = energy = maxEnergy = -1f;
            Copy c;
            if (!s_copies.TryGetValue(seat, out c) || c.Root == null) return false;
            return Read(c.Providers, out health, out maxHealth, out energy, out maxEnergy);
        }

        // The same for the game's own HUD (must keep showing this PC's own Ori).
        public static bool TryNativeShown(out float health, out float maxHealth, out float energy, out float maxEnergy)
        {
            health = maxHealth = energy = maxEnergy = -1f;
            if (UI.SeinUI == null || UI.SeinUI.UI == null) return false;
            return Read(new List<FloatValueProvider>(UI.SeinUI.UI.GetComponentsInChildren<FloatValueProvider>(true)), out health, out maxHealth, out energy, out maxEnergy);
        }

        static bool Read(List<FloatValueProvider> providers, out float health, out float maxHealth, out float energy, out float maxEnergy)
        {
            health = maxHealth = energy = maxEnergy = -1f;
            foreach (FloatValueProvider p in providers)
            {
                if (p == null) continue;
                switch (KindOf(p.GetType()))
                {
                    case StatKind.HealthVisualMin: health = p.GetFloatValue() * DivideBy(p); break;
                    case StatKind.MaxHealth: maxHealth = p.GetFloatValue() * DivideBy(p); break;
                    case StatKind.EnergyVisualMin: energy = p.GetFloatValue() * DivideBy(p); break;
                    case StatKind.MaxEnergy: maxEnergy = p.GetFloatValue() * DivideBy(p); break;
                }
            }
            return health >= 0f && maxHealth >= 0f && energy >= 0f && maxEnergy >= 0f;
        }

        public static void SetTestVitals(int seat, VitalsSample s) { Copy c; if (s_copies.TryGetValue(seat, out c)) c.Model.Set(s); }
        public static int Count { get { int n = 0; foreach (Copy c in s_copies.Values) if (c.Root != null) n++; return n; } }
        public static bool Visible(int seat) { Copy c; return s_copies.TryGetValue(seat, out c) && c.Root != null && c.Root.activeSelf && c.Opacity > 0.01f; }
        public static float Opacity(int seat) { Copy c; return s_copies.TryGetValue(seat, out c) && c.Root != null && c.Root.activeSelf ? c.Opacity : 0f; }
        public static string LabelText(int seat) { Copy c; return s_copies.TryGetValue(seat, out c) && c.Label != null ? Roles.Tag(seat) : ""; }

        // Screen rectangle (pixels, origin bottom-left) the copy takes; false when it has none.
        public static bool ScreenRect(int seat, out Rect r)
        {
            r = new Rect();
            Copy c; Camera gui = P2Hud.GuiCamera();
            if (gui == null || !s_copies.TryGetValue(seat, out c) || c.Root == null || !c.Root.activeSelf) return false;
            Rect b; if (!Frame(c.Root.transform, out b)) return false;
            Vector3 lo = gui.WorldToScreenPoint(new Vector3(b.xMin, b.yMin, 0f)), hi = gui.WorldToScreenPoint(new Vector3(b.xMax, b.yMax, 0f));
            r = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y);
            return true;
        }

        public static bool NativeScreenRect(out Rect r)
        {
            r = new Rect();
            Camera gui = P2Hud.GuiCamera(); Rect b;
            if (gui == null || UI.SeinUI == null || !NativeFrame(UI.SeinUI, out b)) return false;
            Vector3 lo = gui.WorldToScreenPoint(new Vector3(b.xMin, b.yMin, 0f)), hi = gui.WorldToScreenPoint(new Vector3(b.xMax, b.yMax, 0f));
            r = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y);
            return true;
        }

        // ---------------------------------------------------------------- the copy
        static Copy Create(SeinUI ui, int seat)
        {
            // Only the "status" strip is copied, under a root of our own with its own fade. (Copying the whole "ui" and
            // dropping the map stone / keystone corners logged "Cannot remove delegate!": their
            // ScalePositionForAspectRatio unregisters in OnDestroy what it only registers in Start.)
            Transform status = ui.UI.transform.Find(StatusChild);
            TransparencyAnimator nativeFade = ui.UI.GetComponent<TransparencyAnimator>();
            if (status == null) { Warn("create", new MissingReferenceException("seinHUD/ui/" + StatusChild)); return null; }
            GameObject root = new GameObject("OriCoop_P" + seat + "Status");
            root.layer = ui.UI.layer;
            root.transform.SetParent(ui.UI.transform.parent, false);
            root.transform.localPosition = ui.UI.transform.localPosition;
            root.transform.localRotation = ui.UI.transform.localRotation;
            root.transform.localScale = ui.UI.transform.localScale;
            TransparencyAnimator fade = root.AddComponent<TransparencyAnimator>();
            fade.AnimateChildren = true;
            if (nativeFade != null) { fade.Mode = nativeFade.Mode; fade.AnimationCurve = nativeFade.AnimationCurve; }
            GameObject strip = (GameObject)UnityEngine.Object.Instantiate(status.gameObject);
            strip.name = status.name;
            strip.transform.SetParent(root.transform, false);
            strip.transform.localPosition = status.localPosition;
            strip.transform.localRotation = status.localRotation;
            strip.transform.localScale = status.localScale;
            Copy c = new Copy { Seat = seat, Root = root, Fade = fade };
            foreach (FloatValueProvider p in root.GetComponentsInChildren<FloatValueProvider>(true))
            {
                c.Providers.Add(p); s_byProvider[p] = c;
            }
            c.EnergyBar = Twin(ui.UI.transform, root.transform, ui.EnergyBar);
            c.SoulFlameFull = Twin(ui.UI.transform, root.transform, ui.SoulFlameFull);
            c.SoulFlameFlame = Twin(ui.UI.transform, root.transform, ui.SoulFlameUIFlame);
            MakeLabel(c, ui.UI.transform.parent);
            if (c.Fade != null) c.Fade.IsInScene = false;   // write the copy's own materials, never the shared ones of the game's HUD
            s_copies[seat] = c;
            Show(c, 0f);
            Log("bottom status bar for P" + seat + " created (" + c.Providers.Count + " value providers)");
            return c;
        }

        // The same object in the copy as `original` is in the game's HUD (by path), or null.
        static GameObject Twin(Transform nativeRoot, Transform copyRoot, GameObject original)
        {
            if (original == null) return null;
            string path = "";
            Transform t = original.transform;
            while (t != null && t != nativeRoot) { path = path.Length == 0 ? t.name : t.name + "/" + path; t = t.parent; }
            if (t == null) return null;
            Transform found = copyRoot.Find(path);
            return found != null ? found.gameObject : null;
        }

        // "P2" in the game's own text font (its hint message, as the tags over the teammates' heads use; the orb's
        // counter font has digits only), in the player's colour, above the orb. Driven like P2Hud's tag: its own
        // object, colour and opacity set on the TextBox (under the copy's fade it lost its colour and was not drawn).
        static void MakeLabel(Copy c, Transform parent)
        {
            GameText text = new GameText("OriCoop_P" + c.Seat + "Label") { NoBacking = true };
            if (!text.Ok) return;
            text.SetText(Roles.Tag(c.Seat));
            MessageBox box = text.Go.GetComponentInChildren<MessageBox>(true);
            if (box == null || box.TextBox == null) { text.Destroy(); return; }
            text.Go.transform.SetParent(parent, true);
            box.TextBox.horizontalAnchor = HorizontalAnchorMode.Center;
            box.TextBox.verticalAnchor = VerticalAnchorMode.Middle;
            box.TextBox.alignment = AlignmentMode.Center;
            c.Label = text.Go; c.LabelBox = box.TextBox;
        }

        static void ShowLabel(Copy c, float a)
        {
            if (c.Label == null || c.LabelBox == null) return;
            bool on = a > 0.01f;
            if (c.Label.activeSelf != on) { c.Label.SetActive(on); c.LabelAlpha = -1f; }
            if (!on || Mathf.Abs(c.LabelAlpha - a) < 0.004f) return;
            c.LabelAlpha = a;
            Color color = Roles.ColorOf(c.Seat);
            color.a = a;
            c.LabelBox.color = color;
            c.LabelBox.RenderText();
            TextRenderer[] renderers = c.LabelBox.textRenderers;
            if (renderers != null) foreach (TextRenderer tr in renderers)
            {
                Renderer r = tr != null ? tr.GetComponent<Renderer>() : null;
                if (r == null || r.material == null) continue;
                r.sortingOrder = 32000;
                r.material.renderQueue = 3000;
            }
        }

        static void Show(Copy c, float a)
        {
            if (c.Root == null) return;
            bool on = a > 0.01f;
            if (c.Root.activeSelf != on) c.Root.SetActive(on);
            if (on && c.Fade != null && Mathf.Abs(c.Opacity - a) > 0.004f) c.Fade.SampleValue(a, true);
            c.Opacity = on ? a : 0f;
            ShowLabel(c, a);
            if (!on) return;
            // What SeinUI.FixedUpdate does for the game's HUD, for this player.
            if (c.EnergyBar != null && c.EnergyBar.activeSelf != c.Model.Now.MaxEnergy > 0f) c.EnergyBar.SetActive(c.Model.Now.MaxEnergy > 0f);
            bool full = c.Model.Now.SoulFlame >= 0.999f;
            if (c.SoulFlameFull != null && c.SoulFlameFull.activeSelf != full) c.SoulFlameFull.SetActive(full);
            if (c.SoulFlameFlame != null && c.SoulFlameFlame.activeSelf != full) c.SoulFlameFlame.SetActive(full);
        }

        // ---------------------------------------------------------------- layout
        // The game's HUD strip in HUD-camera world units: the energy, orb and health backgrounds (their meshes span the
        // full 15-cell width whatever the current maximum is, so the frame does not jump on an upgrade).
        static bool NativeFrame(SeinUI ui, out Rect r) { return Frame(ui.UI.transform, out r); }

        static readonly string[] s_frameParts = { "energyBackground", "healthBackground", "experienceBackground", "soulFlameBackground" };
        static bool Frame(Transform root, out Rect r)
        {
            r = new Rect();
            bool any = false;
            float x0 = 0f, y0 = 0f, x1 = 0f, y1 = 0f;
            foreach (Renderer rd in root.GetComponentsInChildren<Renderer>(true))
            {
                if (Array.IndexOf(s_frameParts, rd.gameObject.name) < 0) continue;
                Bounds b = rd.bounds;
                if (b.size.x <= 0f) continue;
                if (!any) { x0 = b.min.x; y0 = b.min.y; x1 = b.max.x; y1 = b.max.y; any = true; }
                else { x0 = Mathf.Min(x0, b.min.x); y0 = Mathf.Min(y0, b.min.y); x1 = Mathf.Max(x1, b.max.x); y1 = Mathf.Max(y1, b.max.y); }
            }
            if (any) r = Rect.MinMaxRect(x0, y0, x1, y1);
            return any;
        }

        static void Place(SeinUI ui, Copy c, int order, int count, Rect native)
        {
            Camera gui = P2Hud.GuiCamera();
            if (gui == null || !gui.orthographic) return;
            float halfH = gui.orthographicSize, halfW = halfH * gui.aspect;
            Vector3 cam = gui.transform.position;
            float left = cam.x - halfW, bottom = cam.y - halfH, w = 2f * halfW, h = 2f * halfH;
            SlotRect s = StatusBarLayout.Place(order, count, (native.xMin - left) / w, (native.xMax - left) / w,
                (native.yMin - bottom) / h, native.height / h);
            c.Slot = s;
            // The copy scales about its own origin (the game's HUD origin): frame' = origin + s * (frame - nativeOrigin).
            Vector3 o = ui.UI.transform.position;
            float targetCx = left + s.CenterX * w, targetBottom = bottom + s.Bottom * h;
            Vector3 p = new Vector3(targetCx - s.Scale * (native.center.x - o.x), targetBottom - s.Scale * (native.yMin - o.y), o.z);
            Transform t = c.Root.transform;
            Vector3 parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(s.Scale * ui.UI.transform.localScale.x, s.Scale * ui.UI.transform.localScale.y, ui.UI.transform.localScale.z);
            if (parentScale.x != 0f) t.position = p;
            PlaceLabel(c, h);
        }

        // The label keeps a fixed share of the screen height whatever the copy's scale, centred above the orb. Its size
        // and offset are read from what the text renderer really draws (the TextBox lays the glyphs out itself).
        public const float LabelScreenHeight = 0.024f;
        static void PlaceLabel(Copy c, float screenWorldH)
        {
            if (c.Label == null) return;
            Rect frame, text;
            if (!Frame(c.Root.transform, out frame) || !LabelBounds(c, out text) || text.height <= 1e-5f) return;
            Transform lt = c.Label.transform;
            Camera gui = P2Hud.GuiCamera();
            if (gui != null) lt.rotation = gui.transform.rotation;
            float want = LabelScreenHeight * screenWorldH;
            lt.localScale *= Mathf.Clamp(want / text.height, 0.25f, 4f);
            if (!LabelBounds(c, out text)) return;
            Vector3 target = new Vector3(frame.center.x, frame.yMax + want * 0.35f + text.height / 2f, c.Root.transform.position.z);
            lt.position = new Vector3(lt.position.x + target.x - text.center.x, lt.position.y + target.y - text.center.y, target.z);
        }

        static bool LabelBounds(Copy c, out Rect r)
        {
            r = new Rect();
            bool any = false;
            foreach (Renderer rd in c.Label.GetComponentsInChildren<Renderer>(false))
            {
                if (!rd.enabled) continue;
                Bounds b = rd.bounds;
                if (b.size.y <= 0f) continue;
                Rect br = Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y);
                r = any ? Rect.MinMaxRect(Mathf.Min(r.xMin, br.xMin), Mathf.Min(r.yMin, br.yMin), Mathf.Max(r.xMax, br.xMax), Mathf.Max(r.yMax, br.yMax)) : br;
                any = true;
            }
            return any;
        }

        public static bool LabelScreenRect(int seat, out Rect r)
        {
            r = new Rect();
            Copy c; Camera gui = P2Hud.GuiCamera(); Rect b;
            if (gui == null || !s_copies.TryGetValue(seat, out c) || c.Label == null || c.Root == null || !c.Root.activeSelf || !LabelBounds(c, out b)) return false;
            Vector3 lo = gui.WorldToScreenPoint(new Vector3(b.xMin, b.yMin, 0f)), hi = gui.WorldToScreenPoint(new Vector3(b.xMax, b.yMax, 0f));
            r = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y);
            return true;
        }

        // ---------------------------------------------------------------- value providers
        static void EnsurePatched()
        {
            if (s_patched || Plugin.H == null) return;
            s_patched = true;
            HarmonyMethod prefix = new HarmonyMethod(AccessTools.Method(typeof(BottomHud), "ProviderPrefix"));
            foreach (string name in new[] { "SeinMaxHealthValueProvider", "SeinHealthVisualMinProvider", "SeinHealthVisualMaxProvider",
                "SeinHealthVisualMinNormalizedProvider", "SeinHealthVisualMaxNormalizedProvider", "SeinMaxEnergyValueProvider",
                "SeinEnergyMinVisualProvider", "SeinEnergyMaxVisualProvider", "SeinEnergyMinVisualNormalizedProvider",
                "SeinEnergyMaxVisualNormalizedProvider", "SeinSoulFlameCooldownValueProvider", "SeinBreathRemainingValueProvider",
                "ChargeJumpChargeFloatProvider" })
            {
                try
                {
                    Type type = AccessTools.TypeByName(name);
                    MethodInfo m = type == null ? null : AccessTools.Method(type, "GetFloatValue");
                    if (m == null) { Warn("patch " + name, new MissingMethodException(name)); continue; }
                    Plugin.H.Patch(m, prefix);
                }
                catch (Exception e) { Warn("patch " + name, e); }
            }
        }

        // Mutation check: the copies read P1's values (the bottomhud value checks must then fail).
        static readonly bool s_mutateNoValues = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_BOTTOMHUD_P1VALUES") == "1";

        static bool ProviderPrefix(FloatValueProvider __instance, ref float __result)
        {
            if (s_byProvider.Count == 0 || s_mutateNoValues) return true;
            Copy c;
            if (!s_byProvider.TryGetValue(__instance, out c)) return true;
            float? v = c.Model.Provide(KindOf(__instance.GetType()), DivideBy(__instance));
            if (!v.HasValue) return true;
            __result = v.Value;
            return false;
        }

        static StatKind KindOf(Type t)
        {
            StatKind k;
            if (!s_kinds.TryGetValue(t, out k)) { k = VitalsModel.KindOf(t.Name); s_kinds[t] = k; }
            return k;
        }

        static float DivideBy(FloatValueProvider p)
        {
            Type t = p.GetType();
            FieldInfo f;
            if (!s_divide.TryGetValue(t, out f)) { f = AccessTools.Field(t, "DivideBy"); s_divide[t] = f; }
            if (f == null) return 1f;
            float d = (float)f.GetValue(p);
            return d == 0f ? 1f : d;
        }

        // ---------------------------------------------------------------- online
        static void EnsureRegistered()
        {
            if (s_registered) return;
            s_registered = true;
            Services.Net.On(MsgVitals, OnVitals);
            Services.Net.Forward(MsgVitals);
            Services.Net.PlayerLeft += delegate(int seat) { s_net.Remove(seat); DestroyCopy(seat); };
            Services.Net.SessionEnded += delegate { s_net.Clear(); s_sender.Reset(); DestroyAll(); };
            Services.Net.SessionStarted += delegate { s_net.Clear(); s_sender.Reset(); };
        }

        static void SendOwn()
        {
            if (!Services.Net.Connected) return;
            SeinCharacter own = P2Context.P1;
            if (own == null || P2Context.Inside) return;
            VitalsSample s = Sample(own);
            float now = Time.unscaledTime;
            if (!s_sender.Due(s, now)) return;
            int seat = Players.LocalSeat;
            Services.Net.Send(MsgVitals, true, w => VitalsModel.Write(w, seat, s));
            s_sender.MarkSent(s, now);
            VitalsSent++;
        }

        static void OnVitals(System.IO.BinaryReader r)
        {
            int seat;
            VitalsSample s = VitalsModel.Read(r, out seat);
            if (seat < 1 || seat > Players.MaxSeats || seat == Players.LocalSeat) return;
            VitalsModel m;
            if (!s_net.TryGetValue(seat, out m)) { m = new VitalsModel(); s_net[seat] = m; }
            m.Set(s);
            VitalsReceived++;
        }

        // ---------------------------------------------------------------- cleanup
        static void DestroyCopy(int seat)
        {
            Copy c;
            if (!s_copies.TryGetValue(seat, out c)) return;
            foreach (FloatValueProvider p in c.Providers) if ((object)p != null) s_byProvider.Remove(p);
            if (c.Root != null) UnityEngine.Object.Destroy(c.Root);
            if (c.Label != null) UnityEngine.Object.Destroy(c.Label);
            s_copies.Remove(seat);
            Log("bottom status bar for P" + seat + " removed");
        }

        public static void DestroyAll()
        {
            List<int> seats = new List<int>(s_copies.Keys);
            foreach (int seat in seats) DestroyCopy(seat);
            s_byProvider.Clear();
        }

        static void Log(string msg) { try { if (Plugin.Log != null) Plugin.Log.LogInfo(msg); } catch (Exception) { } }
        static void Warn(string operation, Exception error)
        {
            string failure = operation + ": " + error.Message;
            if (!s_failures.Add(failure)) return;
            try { if (Plugin.Log != null) Plugin.Log.LogWarning("BottomHud " + failure + "\n" + error); } catch (Exception) { }
        }
    }
}
