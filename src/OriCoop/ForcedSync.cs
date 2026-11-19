using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    public static class ForcedSync
    {
        const byte StateMessage = 83, CountMessage = 84, ForceMessage = 85, FireMessage = 86;
        public static readonly bool Off = Environment.GetEnvironmentVariable("ORICOOP_GATE_OFF") == "1";
        static GateTable s_table = new GateTable();
        sealed class Gate
        {
            public string Key;
            public Component Object;
            public Rect Rect;
            public bool Local, Inside;   // Inside: any of this PC's seats is in the area
            // T-110: presence per seat (the primary and each extra local player of this PC).
            public readonly bool[] SeatIn = new bool[GateSeats.MaxSeat + 1], SeatSent = new bool[GateSeats.MaxSeat + 1];
            public readonly float[] SeatRefresh = new float[GateSeats.MaxSeat + 1];
            public float LeftAt = -1f, CountAt;
            public int Count, Total, SentCount = -1, SentTotal = -1;
            public GatePillars Pillars;
        }
        sealed class PendingFire
        {
            public Gate Gate;
            public float Requested, At;
            public bool Sent;
        }
        static readonly Dictionary<string, Gate> Gates = new Dictionary<string, Gate>();
        static readonly List<PendingFire> Pending = new List<PendingFire>();
        static readonly HoldMeter Meter = new HoldMeter();
        static readonly FieldInfo WasTriggered = AccessTools.Field(typeof(CutsceneTrigger), "m_wasTriggered");
        static bool s_firing;
        static GameText s_top, s_bottom;
        static GlowRing s_ring;
        public static string LocalGateKey { get; private set; }
        public static int LastCount, LastTotal;
        public static float HoldProgress { get { return Meter.Progress; } }
        public static int Fires, Forces, Blocked;
        public static bool PillarsShown { get; private set; }
        public static string TopText = "", BottomText = "";
        // Test diagnostics: where the ward and the ring actually are.
        public static string VisualDebug()
        {
            string pillars = "none";
            foreach (Gate gate in Gates.Values) if (gate.Pillars != null) pillars = gate.Pillars.Debug(gate.Rect);
            return "pillars=" + pillars + " ring=" + (s_ring == null || s_ring.Go == null ? "none" : s_ring.Go.activeSelf + "@" + s_ring.Go.transform.position + " layer=" + s_ring.Go.layer);
        }
        static float Now { get { return Time.realtimeSinceStartup; } }
        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        static bool Online { get { return !Off && Services.Net.Enabled && Services.Net.Connected && SaveSync.Ready && !P2Context.Inside; } }
        // Every player in the session: this PC's seats (primary + extra local players) and every remote seat that
        // said Hello (a guest's extra seats carry its device's Hello). T-110: was 1 + RemoteCount, which counted a
        // guest's extra seat but not the host's own, while presence was only ever reported for each PC's primary.
        static int Total { get { return GateSeats.Total(Players.LocalSeats.Length, Players.RemoteCount); } }
        static void Log(string action, string key, int seat, int n, int total)
        {
            Plugin.Log.LogInfo("gate " + action + " key=" + key + " seat=" + seat + " n=" + n + " N=" + total);
            CoopTrace.Event("story", "gate." + action, "key", key, "seat", seat, "n", n, "N", total);
        }
        public static void Init()
        {
            Services.Net.On(StateMessage, ReceiveState); Services.Net.On(CountMessage, ReceiveCount);
            Services.Net.On(ForceMessage, ReceiveForce); Services.Net.On(FireMessage, ReceiveFire);
            Services.Net.PlayerLeft += Forget;
            Services.Net.SessionEnded += Clear;
        }
        static void Forget(int seat) { s_table.Forget(seat); }
        static void Clear()
        {
            foreach (Gate gate in Gates.Values) if (gate.Pillars != null) gate.Pillars.Destroy();
            Gates.Clear(); Pending.Clear(); s_table = new GateTable();
            Select(null); Hide(); PillarsShown = false;
        }
        static Gate Get(string key)
        {
            Gate gate;
            if (!Gates.TryGetValue(key, out gate)) { gate = new Gate(); gate.Key = key; Gates[key] = gate; }
            return gate;
        }
        static void Attach(Gate gate, Component component)
        {
            gate.Object = component;
            CutsceneTrigger cutscene = component as CutsceneTrigger;
            gate.Rect = cutscene != null ? Utility.RectFromBounds(cutscene.GetComponent<Collider>().bounds)
                : Utility.RectFromBounds(Utility.BoundsFromTransform(component.transform));
        }
        static void Resolve(Gate gate)
        {
            if (gate.Object != null) return;
            foreach (Trigger trigger in Resources.FindObjectsOfTypeAll<Trigger>())
                if (trigger != null && StorySync.ComponentKey(trigger) == gate.Key) { Attach(gate, trigger); return; }
            foreach (CutsceneTrigger trigger in Resources.FindObjectsOfTypeAll<CutsceneTrigger>())
                if (trigger != null && StorySync.ComponentKey(trigger) == gate.Key) { Attach(gate, trigger); return; }
        }
        static bool Inside(Gate gate, Vector3 position)
        {
            Rect rect = gate.Rect; rect.xMin -= 0.5f; rect.xMax += 0.5f; rect.yMin -= 0.5f; rect.yMax += 0.5f;
            return gate.Object != null && rect.Contains(new Vector2(position.x, position.y));
        }
        static void Remember(Component component)
        {
            string key = StorySync.ComponentKey(component);
            if (s_table.RecentlyFired(key, Now)) return;
            Gate gate = Get(key); Attach(gate, component); gate.Local = true;
            // The native trigger only sees this PC's primary Ori (Characters.Current): it is inside.
            LocalPresence(gate, true);
            Select(key);
        }
        // Reports each of this PC's seats; returns whether any of them is inside.
        static bool LocalPresence(Gate gate, bool primaryInside)
        {
            bool any = false;
            int[] seats = Players.LocalSeats;
            foreach (int seat in seats)
            {
                if (seat < 1 || seat > GateSeats.MaxSeat) continue;
                SeinCharacter ori = Players.OriOf(seat);
                bool inside = ori != null && (seat == Players.LocalSeat && primaryInside || Inside(gate, ori.Position));
                if (gate.SeatIn[seat] != inside) { gate.SeatIn[seat] = inside; Log(inside ? "enter" : "leave", gate.Key, seat, gate.Count, Total); }
                Presence(gate, seat, inside);
                any |= inside;
            }
            // A seat this PC no longer has (released extra player): forget it here; the host's entry times out.
            for (int seat = 1; seat <= GateSeats.MaxSeat; seat++)
                if (Array.IndexOf(seats, seat) < 0) gate.SeatIn[seat] = gate.SeatSent[seat] = false;
            if (gate.Inside != any) { gate.Inside = any; gate.LeftAt = any ? -1f : Now; }
            return any;
        }
        static void Presence(Gate gate, int seat, bool inside)
        {
            if (inside == gate.SeatSent[seat] && (!inside || Now < gate.SeatRefresh[seat])) return;
            gate.SeatSent[seat] = inside; gate.SeatRefresh[seat] = Now + 0.5f;
            if (Services.Net.IsHost) s_table.SetInside(gate.Key, seat, inside, Now);
            else Services.Net.Send(StateMessage, true, delegate(BinaryWriter w) { GateWire.WriteState(w, gate.Key, seat, inside); });
        }
        static IEnumerable<int> SeatsOf(int device)
        { for (int seat = 1; seat <= GateSeats.MaxSeat; seat++) if (NetPlay.DeviceSeat(seat) == device) yield return seat; }
        static void ReceiveState(BinaryReader r)
        {
            bool inside; int seat; string key = GateWire.ReadState(r, out seat, out inside);
            if (!Online || !Services.Net.IsHost || s_table.RecentlyFired(key, Now)) return;
            if (!GateSeats.MayReport(Services.Net.Sender, seat, NetPlay.DeviceSeat(seat))) return;
            bool before = s_table.IsInside(key, seat, Now);
            s_table.SetInside(key, seat, inside, Now); Get(key);
            if (before != inside) Log(inside ? "enter" : "leave", key, seat, s_table.Count(key, Now), Total);
        }
        static void ReceiveCount(BinaryReader r)
        {
            byte n, total; string key = GateWire.ReadCount(r, out n, out total);
            if (!Online || Services.Net.IsHost || Services.Net.Sender != 1 || s_table.RecentlyFired(key, Now)) return;
            Gate gate = Get(key); gate.Count = n; gate.Total = total;
            if (LocalGateKey == key) { LastCount = n; LastTotal = total; }
            Log("count", key, Services.Net.Sender, n, total);
        }
        static void ReceiveForce(BinaryReader r)
        {
            string key = GateWire.ReadForce(r);
            if (Online && Services.Net.IsHost) Force(key, Services.Net.Sender);
        }
        // device: the forcing PC's primary seat. Its force counts when any of that PC's seats is inside.
        static void Force(string key, int device)
        {
            if (s_table.RecentlyFired(key, Now) || device != Players.LocalSeat && !s_table.AnyInside(key, SeatsOf(device), Now)) return;
            Log("force", key, device, s_table.Count(key, Now), Total); Fire(key);
        }
        static void Fire(string key)
        {
            if (s_table.RecentlyFired(key, Now)) return;
            s_table.MarkFired(key, Now);
            Pending.Add(new PendingFire { Gate = Get(key), Requested = Now });
        }
        static void ReceiveFire(BinaryReader r)
        {
            float x, y, z; string key = GateWire.ReadFire(r, out x, out y, out z);
            if (!Online || Services.Net.IsHost || Services.Net.Sender != 1 || s_table.RecentlyFired(key, Now)) return;
            s_table.MarkFired(key, Now); Apply(Get(key), new Vector3(x, y, z));
        }
        static void Apply(Gate gate, Vector3 position)
        {
            Resolve(gate);
            // Every player of this PC that is not in the area joins the trigger (the extra local players too).
            foreach (int seat in Players.LocalSeats)
            {
                SeinCharacter ori = Players.OriOf(seat);
                if (ori == null || Inside(gate, ori.Position)) continue;
                Plugin.Teleport(ori, position + new Vector3((seat - 1) * 0.7f - 1f, 0.5f, 0));
                if (seat == Players.LocalSeat) CutsceneGuard.SnapSpirit("gate");
            }
            Fires++; Log("fire", gate.Key, Players.LocalSeat, gate.Count, gate.Total);
            gate.Local = gate.Inside = false; gate.Count = 0;
            Array.Clear(gate.SeatIn, 0, gate.SeatIn.Length); Array.Clear(gate.SeatSent, 0, gate.SeatSent.Length);
            if (gate.Pillars != null) { gate.Pillars.Destroy(); gate.Pillars = null; }
            if (LocalGateKey == gate.Key) { Select(null); Hide(); }
        }
        static void FirePending()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingFire pending = Pending[i]; Gate gate = pending.Gate;
                Resolve(gate);
                if (gate.Object == null)
                {
                    if (Now - pending.Requested >= 3f)
                    { Plugin.Log.LogInfo("gate fire: trigger not found key=" + gate.Key); Pending.RemoveAt(i); }
                    continue;
                }
                if (!pending.Sent)
                {
                    Vector3 position = gate.Object.transform.position;
                    Services.Net.Send(FireMessage, true, delegate(BinaryWriter w) { GateWire.WriteFire(w, gate.Key, position.x, position.y, position.z); });
                    Apply(gate, position); pending.Sent = true; pending.At = Now + 0.6f;
                }
                if (Now < pending.At) continue;
                s_firing = true;
                try
                {
                    Trigger trigger = gate.Object as Trigger;
                    if (trigger != null) trigger.DoTrigger(true);
                    else
                    {
                        CutsceneTrigger cutscene = (CutsceneTrigger)gate.Object;
                        WasTriggered.SetValue(cutscene, true); cutscene.Cutscene.ChangeState(cutscene.State);
                    }
                }
                finally { s_firing = false; Pending.RemoveAt(i); }
            }
        }
        static void Select(string key)
        {
            if (LocalGateKey == key) return;
            LocalGateKey = key; Meter.Reset();
            if (key == null) { LastCount = LastTotal = 0; return; }
            Gate gate = Gates[key]; LastCount = gate.Count; LastTotal = gate.Total;
        }
        public static void Tick()
        {
            if (!Online)
            {
                Hide(); PillarsShown = false;
                foreach (Gate gate in Gates.Values)
                    if (gate.Pillars != null) { gate.Pillars.Destroy(); gate.Pillars = null; }
                return;
            }
            string selected = null; List<string> gone = new List<string>();
            foreach (Gate gate in Gates.Values)
            {
                if (gate.Local)
                {
                    bool inside = LocalPresence(gate, false);
                    if (inside && (selected == null || gate.Key == LocalGateKey)) selected = gate.Key;
                    if (!inside && gate.LeftAt < 0f) gate.LeftAt = Now;
                    if (!inside && Now - gate.LeftAt >= 2f) gate.Local = false;
                }
                if (Services.Net.IsHost && !s_table.RecentlyFired(gate.Key, Now))
                {
                    gate.Count = s_table.Count(gate.Key, Now); gate.Total = Total;
                    if (gate.Count != gate.SentCount || gate.Total != gate.SentTotal || gate.Count > 0 && Now >= gate.CountAt)
                    {
                        gate.SentCount = gate.Count; gate.SentTotal = gate.Total; gate.CountAt = Now + 1f;
                        Gate send = gate;
                        Services.Net.Send(CountMessage, true, delegate(BinaryWriter w) { GateWire.WriteCount(w, send.Key, (byte)send.Count, (byte)send.Total); });
                        Log("count", gate.Key, Players.LocalSeat, gate.Count, gate.Total);
                    }
                    if (s_table.AllHere(gate.Key, Total, Now)) Fire(gate.Key);
                }
                if (!gate.Local && gate.Count == 0 && !s_table.RecentlyFired(gate.Key, Now)) gone.Add(gate.Key);
            }
            Select(selected);
            if (Services.Net.IsHost) FirePending();
            Gate local = LocalGateKey == null ? null : Gates[LocalGateKey];
            if (local != null)
            {
                LastCount = local.Count; LastTotal = local.Total;
                if (Meter.Update(Core.Input.Bash.IsPressed, Time.deltaTime))
                {
                    Forces++; Log("force", local.Key, Players.LocalSeat, LastCount, LastTotal);
                    if (Services.Net.IsHost) Force(local.Key, Players.LocalSeat);
                    else Services.Net.Send(ForceMessage, true, delegate(BinaryWriter w) { GateWire.WriteForce(w, local.Key); });
                }
                Show();
            }
            else Hide();
            PillarsShown = false;
            foreach (Gate gate in Gates.Values)
            {
                bool show = !s_table.RecentlyFired(gate.Key, Now) && (gate.Local && gate.Inside || gate.Count > 0);
                if (show)
                {
                    Resolve(gate);
                    if (gate.Object != null)
                    {
                        if (gate.Pillars == null) gate.Pillars = new GatePillars(gate.Rect, gate.Object.transform.position.z);
                        gate.Pillars.Tick(); PillarsShown = true;
                    }
                }
                else if (gate.Pillars != null) { gate.Pillars.Destroy(); gate.Pillars = null; }
            }
            foreach (string key in gone) Gates.Remove(key);
        }
        static void Show()
        {
            if (s_top == null) s_top = new GameText("OriCoop_GateForce");
            if (s_bottom == null) s_bottom = new GameText("OriCoop_GateWait");
            if (s_ring == null) s_ring = new GlowRing("OriCoop_GateHold");
            TopText = L10n.F("gate.force", "[Bash]"); BottomText = L10n.F("gate.wait", LastCount, LastTotal);
            s_top.SetText(TopText); s_bottom.SetText(BottomText);
            Vector3 point = Own.Position;
            s_top.Place(point + new Vector3(0, 2.4f, 0), 0.028f, 0.36f, 1f);
            s_ring.Place(point + new Vector3(3.3f, 2.2f, 0), 0.08f, Meter.Progress, 1f);
            s_bottom.Place(point + new Vector3(0, 1.75f, 0), 0.03f, 0.6f, 1f);
        }
        static void Hide()
        {
            if (s_top != null) s_top.Hide(); if (s_bottom != null) s_bottom.Hide(); if (s_ring != null) s_ring.Hide();
        }
        // Stay triggers call DoTrigger every frame: classify each trigger's action once (reflection walk).
        static readonly Dictionary<int, bool> StoryTriggers = new Dictionary<int, bool>();
        static bool IsStoryTrigger(Trigger trigger)
        {
            bool story; int id = trigger.GetInstanceID();
            if (!StoryTriggers.TryGetValue(id, out story)) StoryTriggers[id] = story = StorySync.IsTakeoverAction(trigger.ActionToRun);
            return story;
        }
        [HarmonyPatch(typeof(Trigger), "DoTrigger", new Type[] { typeof(bool) })]
        static class AreaTrigger
        {
            static bool Prefix(Trigger __instance, bool shouldCheckCondition)
            {
                if (!Online || s_firing || !(__instance is PlayerCollisionTrigger || __instance is PlayerCollisionStayTrigger)
                    || __instance.ActionToRun == null || !IsStoryTrigger(__instance)) return true;
                ActionWithDuration duration = __instance.ActionToRun as ActionWithDuration;
                if (!__instance.Active || __instance.DontTriggerWhileRunning && duration != null && duration.IsPerforming
                    || shouldCheckCondition && __instance.Condition != null && !__instance.Condition.Validate(null)) return true;
                Remember(__instance); Blocked++; return false;
            }
        }
        [HarmonyPatch(typeof(CutsceneTrigger), "OnTriggerEnter")]
        static class CutsceneArea
        {
            [HarmonyPriority(Priority.Last)]
            static bool Prefix(CutsceneTrigger __instance, Collider collider)
            {
                if (!Online || s_firing || Characters.Current == null || collider == null || collider.gameObject != Characters.Current.GameObject
                    || (bool)WasTriggered.GetValue(__instance) || !StorySync.IsStoryCutscene(__instance.Cutscene)) return true;
                Remember(__instance); Blocked++; return false;
            }
        }
        // Pillars are drawn on the HUD camera like GlowRing: the game camera does not draw our additive meshes.
        // Each frame every pillar's ground point and top are projected through the game camera.
        sealed class GatePillars
        {
            readonly List<GameObject> objects = new List<GameObject>();
            readonly List<Mesh> meshes = new List<Mesh>();
            readonly List<Vector3> bases = new List<Vector3>();
            readonly List<float> heights = new List<float>();
            readonly Material material;
            public GatePillars(Rect rect, float z)
            {
                Shader shader = Shader.Find("Particles/Additive");
                if (shader == null) shader = Shader.Find("Particles/Alpha Blended");
                material = new Material(shader); material.hideFlags = HideFlags.HideAndDontSave; material.renderQueue = 3000;
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                int count = Mathf.Max(1, Mathf.FloorToInt(rect.width / 1.2f) + 1);
                for (int i = 0; i < count; i++)
                {
                    float x = rect.xMin + i * 1.2f; RaycastHit hit;
                    float y = Physics.Raycast(new Vector3(x, rect.yMax + 0.5f, z), Vector3.down, out hit, rect.height + 3f, -1, QueryTriggerInteraction.Ignore) ? hit.point.y : rect.yMin;
                    bases.Add(new Vector3(x, y, z)); heights.Add(1.2f + 0.6f * (Mathf.Sin(i * 17.17f + rect.xMin) * 0.5f + 0.5f));
                    GameObject go = new GameObject("OriCoop_GatePillar"); go.layer = P2Hud.HudLayer();
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    Mesh mesh = new Mesh(); mesh.hideFlags = HideFlags.HideAndDontSave; mesh.MarkDynamic();
                    mesh.vertices = new Vector3[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0),
                        new Vector3(-0.5f, 0.3f, 0), new Vector3(0.5f, 0.3f, 0), new Vector3(-0.5f, 1f, 0), new Vector3(0.5f, 1f, 0) };
                    mesh.triangles = new int[] { 0, 2, 3, 0, 3, 1, 2, 4, 5, 2, 5, 3 }; mesh.RecalculateBounds();
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.sortingOrder = 31990;
                    objects.Add(go); meshes.Add(mesh);
                }
                Tick();
            }
            public void Tick()
            {
                Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = P2Hud.GuiCamera();
                for (int i = 0; i < meshes.Count; i++)
                {
                    GameObject go = objects[i];
                    if (game == null || gui == null) { go.SetActive(false); continue; }
                    Vector3 vb = game.WorldToViewportPoint(bases[i]), vt = game.WorldToViewportPoint(bases[i] + new Vector3(0, heights[i], 0));
                    if (vb.z < 0f || vb.x < -0.1f || vb.x > 1.1f || vb.y < -0.2f || vb.y > 1.1f) { go.SetActive(false); continue; }
                    if (!go.activeSelf) go.SetActive(true);
                    Vector3 b = gui.ViewportToWorldPoint(new Vector3(vb.x, vb.y, 8f)), t = gui.ViewportToWorldPoint(new Vector3(vt.x, vt.y, 8f));
                    float h = (t - b).magnitude;
                    go.transform.position = b; go.transform.rotation = gui.transform.rotation;
                    go.transform.localScale = new Vector3(h * 0.16f, h, 1f);
                    float alpha = 0.8f + 0.2f * Mathf.Sin(Time.time * 2.2f + i * 0.7f);
                    Color low = new Color(0.65f, 0.85f, 1f, alpha * 0.45f), core = new Color(0.75f, 0.95f, 1f, alpha), clear = new Color(0.75f, 0.95f, 1f, 0);
                    meshes[i].colors = new Color[] { low, low, core, core, clear, clear };
                }
            }
            public string Debug(Rect rect)
            {
                if (objects.Count == 0) return "0";
                GameObject go = objects[0]; Renderer r = go.GetComponent<Renderer>();
                return objects.Count + " rect=" + rect + " first=" + go.transform.position + " layer=" + go.layer
                    + " visible=" + (r != null && r.isVisible) + " cam=" + (UI.Cameras.Current != null ? UI.Cameras.Current.Camera.cullingMask.ToString("X") : "?");
            }
            public void Destroy()
            {
                foreach (GameObject go in objects) UnityEngine.Object.Destroy(go);
                foreach (Mesh mesh in meshes) UnityEngine.Object.Destroy(mesh);
                UnityEngine.Object.Destroy(material);
            }
        }
    }
}
