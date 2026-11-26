// T-085: test games must never touch the owner's real devices. They used to send rumble to the owner's controller and
// read its buttons, because XInput works no matter which desktop a game is on (the hidden test desktop only keeps
// keyboard and mouse events away). Test games are driven by the bots alone (P2Input.Bot*, NetTestHarness.Bot*), which
// never go through these APIs.
// Every managed method in the game, the XInput wrapper and the mod that calls XInput (get state / set state =
// vibration / capabilities) or a reading method of UnityEngine.Input is rewritten to call a stub instead: no pad is
// connected, vibration is dropped, no key, button, axis or mouse button is pressed, and the mouse never moves.
// The extern XInput functions and Unity's internal calls cannot be patched themselves, so their callers are.
// T-227: the same holds for every native DEVICE function the mod (or the game) imports on its own (hid.dll,
// setupapi.dll, an XInput dll, DirectInput, the joystick functions of winmm). The mod's PlayStation pad support
// (T-178) reads the pad over HID, asks XInput through an import of its own and hands the pad to the game as XInput
// slot 0: with a DualSense connected, a test game read the owner's controller, and the self-test below said so
// ("pad 0 still reads as connected"). Now an import with the XInput wrapper's signature goes to the same stub, and
// a method that calls any other device import does not run at all: no device is found, opened or read.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using J2i.Net.XInputWrapper;
using UnityEngine;

namespace OriCoop.TestQuiet
{
    public static class InputBlock
    {
        const int ERROR_DEVICE_NOT_CONNECTED = 1167;
        public static int SetStateCalls, GetStateCalls;

        // Stubs: same parameters as the original. X_<name> replaces XInput.<name>, U_<name> UnityEngine.Input.<name>.
        public static int X_XInputGetState(int pad, ref XInputState state) { GetStateCalls++; state = new XInputState(); return ERROR_DEVICE_NOT_CONNECTED; }
        public static int X_XInputSetState(int pad, ref XInputVibration vibration) { SetStateCalls++; return 0; }
        public static int X_XInputGetCapabilities(int pad, int flags, ref XInputCapabilities caps) { caps = new XInputCapabilities(); return ERROR_DEVICE_NOT_CONNECTED; }
        public static bool U_GetKey(KeyCode k) { return false; }
        public static bool U_GetKey(string k) { return false; }
        public static KeyCode FakeKeyDown;
        public static bool U_GetKeyDown(KeyCode k) { return k != KeyCode.None && k == FakeKeyDown; }
        public static bool U_GetKeyDown(string k) { return false; }
        public static bool U_GetKeyUp(KeyCode k) { return false; }
        public static bool U_GetKeyUp(string k) { return false; }
        public static bool U_GetButton(string b) { return false; }
        public static bool U_GetButtonDown(string b) { return false; }
        public static bool U_GetButtonUp(string b) { return false; }
        public static float U_GetAxis(string a) { return 0f; }
        public static float U_GetAxisRaw(string a) { return 0f; }
        public static bool U_GetMouseButton(int b) { return false; }
        public static bool U_GetMouseButtonDown(int b) { return false; }
        public static bool U_GetMouseButtonUp(int b) { return false; }
        // T-180: a test may fake Unity's keyboard "any key" state (never a real device): FakeAnyKey = Unity believes a key
        // is held (a lost key-up), FakeAnyKeyDown = a key went down this frame. Both false unless a test sets them.
        public static bool FakeAnyKey, FakeAnyKeyDown;
        public static bool U_get_anyKey() { return FakeAnyKey || FakeAnyKeyDown; }
        public static bool U_get_anyKeyDown() { return FakeAnyKeyDown; }
        public static Vector3 U_get_mousePosition() { return Vector3.zero; }
        public static Vector2 U_get_mouseScrollDelta() { return Vector2.zero; }
        public static string U_get_inputString() { return ""; }
        public static string[] U_GetJoystickNames() { return new string[0]; }
        public static int U_get_touchCount() { return 0; }

        // Test-only mutation ORICOOP_MUTATE_NO_NATIVE_PAD_BLOCK=1: as before T-227 (only the XInput wrapper's and Unity's
        // calls are rewritten). The self-test must then fail on the mod's own device imports, pad connected or not.
        public static bool MutateNoNativeBlock;
        public static int DeviceImports, SkippedMethods;
        static readonly Dictionary<MethodBase, string> s_imports = new Dictionary<MethodBase, string>();   // device import -> "dll!function"
        static readonly HashSet<MethodBase> s_noStub = new HashSet<MethodBase>();    // device imports without a stub: their callers do not run
        static readonly List<string> s_unblocked = new List<string>();                // callers of a device import that still run
        public static bool SkipDeviceCaller() { return false; }

        static readonly Dictionary<MethodBase, MethodInfo> s_map = new Dictionary<MethodBase, MethodInfo>();
        static readonly HashSet<MethodBase> s_patched = new HashSet<MethodBase>();
        static readonly HashSet<string> s_scanned = new HashSet<string>();
        static readonly string[] Targets = { "Assembly-CSharp", "Assembly-CSharp-firstpass", "J2i.Net.XInputWrapper", "OriCoop" };
        public static int Sites { get { return s_patched.Count; } }
        public static bool ScannedMod { get { return s_scanned.Contains("OriCoop"); } }

        static void BuildMap()
        {
            if (s_map.Count > 0) return;
            foreach (MethodInfo stub in typeof(InputBlock).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                Type owner = stub.Name.StartsWith("X_") ? typeof(XInput) : stub.Name.StartsWith("U_") ? typeof(Input) : null;
                if (owner == null) continue;
                ParameterInfo[] pi = stub.GetParameters();
                Type[] ps = new Type[pi.Length];
                for (int i = 0; i < pi.Length; i++) ps[i] = pi[i].ParameterType;
                MethodInfo orig = owner.GetMethod(stub.Name.Substring(2), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, ps, null);
                if (orig != null && orig.ReturnType == stub.ReturnType) s_map[orig] = stub;
            }
        }

        // The dll of a native device function this method imports ("hid", "xinput9_1_0", ...), or null.
        static string DeviceImport(MethodBase m, out string function)
        {
            function = null;
            if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0) return null;
            object[] found;
            try { found = m.GetCustomAttributes(typeof(DllImportAttribute), false); } catch { return null; }
            if (found == null || found.Length == 0) return null;
            DllImportAttribute import = (DllImportAttribute)found[0];
            string dll = (import.Value ?? "").ToLowerInvariant();
            int slash = Math.Max(dll.LastIndexOf('/'), dll.LastIndexOf('\\'));
            if (slash >= 0) dll = dll.Substring(slash + 1);
            if (dll.EndsWith(".dll")) dll = dll.Substring(0, dll.Length - 4);
            function = string.IsNullOrEmpty(import.EntryPoint) ? m.Name : import.EntryPoint;
            bool device = dll.StartsWith("xinput") || dll == "hid" || dll == "setupapi" || dll.StartsWith("dinput")
                || (dll == "winmm" && function.StartsWith("joy", StringComparison.OrdinalIgnoreCase));
            return device ? dll : null;
        }

        // An XInput import with the wrapper's own signature (the mod's VirtualPads.GetRealState) gets the wrapper's stub.
        static MethodInfo StubFor(MethodBase import, string dll, string function)
        {
            if (!dll.StartsWith("xinput")) return null;
            MethodInfo method = import as MethodInfo;
            if (method == null) return null;
            foreach (MethodInfo stub in typeof(InputBlock).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (stub.Name != "X_" + function || stub.ReturnType != method.ReturnType) continue;
                ParameterInfo[] a = stub.GetParameters(), b = method.GetParameters();
                if (a.Length != b.Length) continue;
                bool same = true;
                for (int i = 0; i < a.Length && same; i++) same = a[i].ParameterType == b[i].ParameterType;
                if (same) return stub;
            }
            return null;
        }

        static List<MethodBase> MethodsOf(Type t)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            List<MethodBase> methods = new List<MethodBase>();
            try { methods.AddRange(t.GetMethods(all)); methods.AddRange(t.GetConstructors(all)); } catch { }
            return methods;
        }

        // Scans the target assemblies loaded so far (call again later for assemblies loaded after the first call).
        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            BuildMap();
            HarmonyMethod transpiler = new HarmonyMethod(typeof(InputBlock).GetMethod("Transpiler", BindingFlags.NonPublic | BindingFlags.Static));
            HarmonyMethod skip = new HarmonyMethod(typeof(InputBlock).GetMethod("SkipDeviceCaller", BindingFlags.Public | BindingFlags.Static));
            List<Type> fresh = new List<Type>();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (Array.IndexOf(Targets, name) < 0 || s_scanned.Contains(name)) continue;
                s_scanned.Add(name);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
                fresh.AddRange(types);
            }
            // First every device import of the new assemblies (T-227), then the methods that call into a device.
            foreach (Type t in fresh)
                foreach (MethodBase m in MethodsOf(t))
                {
                    string function, dll = DeviceImport(m, out function);
                    if (dll == null || s_imports.ContainsKey(m)) continue;
                    s_imports[m] = dll + "!" + function; DeviceImports++;
                    if (s_map.ContainsKey(m) || MutateNoNativeBlock) continue;     // the wrapper's own imports have their stubs already
                    MethodInfo stub = StubFor(m, dll, function);
                    if (stub != null) s_map[m] = stub; else s_noStub.Add(m);
                }
            foreach (Type t in fresh)
                foreach (MethodBase m in MethodsOf(t))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || s_patched.Contains(m)) continue;
                    bool mapped; MethodBase noStub, import;
                    if (!Calls(m, out mapped, out noStub, out import)) continue;
                    try
                    {
                        if (noStub != null) { harmony.Patch(m, skip); s_patched.Add(m); SkippedMethods++; }
                        else if (mapped) { harmony.Patch(m, null, null, transpiler); s_patched.Add(m); }
                    }
                    catch (Exception e) { log.LogWarning("TestQuiet: input block could not patch " + t.FullName + "::" + m.Name + ": " + e.Message); }
                    // The self-test's rule: no method that calls a device import still runs it.
                    if (import != null && !s_patched.Contains(m)) s_unblocked.Add(t.FullName + "::" + m.Name + " calls " + s_imports[import]);
                }
        }

        // What of ours the method's IL calls (call / callvirt with a method token): a mapped method (rewritten to its
        // stub), a device import without a stub (the method must not run), any device import at all.
        static bool Calls(MethodBase m, out bool mapped, out MethodBase noStub, out MethodBase import)
        {
            mapped = false; noStub = null; import = null;
            byte[] il;
            try { MethodBody body = m.GetMethodBody(); if (body == null) return false; il = body.GetILAsByteArray(); }
            catch { return false; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                int table = (int)((uint)token >> 24);
                if (table != 0x0A && table != 0x06) continue;
                MethodBase callee;
                try { callee = m.Module.ResolveMethod(token); } catch { continue; }
                if (callee == null) continue;
                if (s_map.ContainsKey(callee)) mapped = true;
                if (s_noStub.Contains(callee)) noStub = callee;
                if (s_imports.ContainsKey(callee)) import = callee;
            }
            return mapped || noStub != null || import != null;
        }
        static bool Calls(MethodBase m)
        {
            bool mapped; MethodBase noStub, import;
            return Calls(m, out mapped, out noStub, out import) && mapped;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ins in instructions)
            {
                MethodInfo stub;
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand is MethodBase && s_map.TryGetValue((MethodBase)ins.operand, out stub))
                { ins.opcode = OpCodes.Call; ins.operand = stub; }
                yield return ins;
            }
        }

        // Self-test: the wrapper's own polling and vibration paths must be patched, a vibration request must reach the
        // stub (not the device), and pad 0 must read as not connected. Returns null when all hold.
        public static string Verify()
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            // T-227: no method that calls a native device function may still run it (named first: it says where the leak is).
            if (s_unblocked.Count > 0)
                return "a native device function is not blocked: " + s_unblocked[0] + (s_unblocked.Count > 1 ? " (and " + (s_unblocked.Count - 1) + " more)" : "");
            Type xc = typeof(XboxController);
            MethodInfo update = xc.GetMethod("UpdateState", any);
            if (update == null || !s_patched.Contains(update)) return "XboxController.UpdateState not patched";
            foreach (MethodInfo v in xc.GetMethods(any))
                if (v.Name == "Vibrate" && Calls(v) && !s_patched.Contains(v)) return "an XboxController.Vibrate overload is not patched";
            if (!s_map.ContainsKey(typeof(XInput).GetMethod("XInputSetState"))) return "no stub for XInputSetState";
            if (!s_map.ContainsKey(typeof(Input).GetMethod("GetKey", new Type[] { typeof(KeyCode) }))) return "no stub for Input.GetKey";
            XboxController pad = XboxController.RetrieveController(0);
            int before = SetStateCalls;
            try { pad.Vibrate(0.5, 0.5); }
            catch (Exception e) { return "vibration self-test threw " + e.GetType().Name + ": " + e.Message; }
            if (SetStateCalls == before) return "a vibration request did not reach the stub";
            try { update.Invoke(update.IsStatic ? null : pad, new object[update.GetParameters().Length]); } catch { }
            if (pad.IsConnected) return "pad 0 still reads as connected";
            return VerifyNative();
        }

        // T-227: the mod's PlayStation pad reader finds and maps nothing.
        static string VerifyNative()
        {
            Assembly mod = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) if (asm.GetName().Name == "OriCoop") mod = asm;
            Type hid = mod == null ? null : mod.GetType("OriCoop.Pads.PsHid"), pads = mod == null ? null : mod.GetType("OriCoop.Pads.VirtualPads");
            if (hid == null || pads == null) return null;      // a build without PlayStation pad support
            // The reader imports hid.dll: finding none of its imports means the scan above cannot see imports at all.
            bool seen = false;
            foreach (KeyValuePair<MethodBase, string> import in s_imports) if (import.Key.DeclaringType == hid) seen = true;
            if (!seen) return "the mod's PlayStation pad reader is there but its device imports were not found";
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                hid.GetMethod("Tick", any).Invoke(null, null);                 // a scan right now: blocked = it finds nothing
                int open = (int)hid.GetProperty("Count", any).GetValue(null, null);
                if (open != 0) return "the mod's PlayStation pad reader opened " + open + " device(s)";
                MethodInfo virtualState = pads.GetMethod("TryGetVirtual", any);
                for (int slot = 0; slot < 4; slot++)
                {
                    object[] args = { slot, new XInputState() };
                    if ((bool)virtualState.Invoke(null, args)) return "the mod hands a pad to the game on XInput slot " + slot;
                }
            }
            catch (Exception e) { return "native pad self-test threw " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            return null;
        }
    }
}
