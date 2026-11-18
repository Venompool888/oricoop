// T-085: test games must never touch the owner's real devices. They used to send rumble to the owner's controller and
// read its buttons, because XInput works no matter which desktop a game is on (the hidden test desktop only keeps
// keyboard and mouse events away). Test games are driven by the bots alone (P2Input.Bot*, NetTestHarness.Bot*), which
// never go through these APIs.
// Every managed method in the game, the XInput wrapper and the mod that calls XInput (get state / set state =
// vibration / capabilities) or a reading method of UnityEngine.Input is rewritten to call a stub instead: no pad is
// connected, vibration is dropped, no key, button, axis or mouse button is pressed, and the mouse never moves.
// The extern XInput functions and Unity's internal calls cannot be patched themselves, so their callers are.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
        public static bool U_GetKeyDown(KeyCode k) { return false; }
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
        public static bool U_get_anyKey() { return false; }
        public static bool U_get_anyKeyDown() { return false; }
        public static Vector3 U_get_mousePosition() { return Vector3.zero; }
        public static Vector2 U_get_mouseScrollDelta() { return Vector2.zero; }
        public static string U_get_inputString() { return ""; }
        public static string[] U_GetJoystickNames() { return new string[0]; }
        public static int U_get_touchCount() { return 0; }

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

        // Scans the target assemblies loaded so far (call again later for assemblies loaded after the first call).
        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            BuildMap();
            HarmonyMethod transpiler = new HarmonyMethod(typeof(InputBlock).GetMethod("Transpiler", BindingFlags.NonPublic | BindingFlags.Static));
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (Array.IndexOf(Targets, name) < 0 || s_scanned.Contains(name)) continue;
                s_scanned.Add(name);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                foreach (Type t in types)
                {
                    List<MethodBase> methods = new List<MethodBase>();
                    try { methods.AddRange(t.GetMethods(all)); methods.AddRange(t.GetConstructors(all)); } catch { continue; }
                    foreach (MethodBase m in methods)
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || s_patched.Contains(m) || !Calls(m)) continue;
                        try { harmony.Patch(m, null, null, transpiler); s_patched.Add(m); }
                        catch (Exception e) { log.LogWarning("TestQuiet: input block could not patch " + t.FullName + "::" + m.Name + ": " + e.Message); }
                    }
                }
            }
        }

        // Does the method's IL call one of the mapped methods? (call / callvirt with a method token)
        static bool Calls(MethodBase m)
        {
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
                if (callee != null && s_map.ContainsKey(callee)) return true;
            }
            return false;
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
            return null;
        }
    }
}
