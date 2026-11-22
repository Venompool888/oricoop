// T-178: the game polls its four XInput slots through J2i's XboxController.UpdateState (on its own polling
// thread). A PlayStation pad that nobody shows as XInput (no Steam Input) is given an empty slot by
// Pads.VirtualPads; this patch hands that slot's state to the game, so the game, P1 routing and P2 all see a
// normal pad. Every other slot runs the game's own code.
using HarmonyLib;
using J2i.Net.XInputWrapper;
using OriCoop.Pads;

namespace OriCoop.Glyphs
{
    [HarmonyPatch(typeof(XboxController), "UpdateState")]
    static class VirtualPadPatch
    {
        static readonly System.Reflection.MethodInfo s_onStateChanged = AccessTools.Method(typeof(XboxController), "OnStateChanged");


        static bool Prefix(XboxController __instance, int ____playerIndex, ref XInputState ___gamepadStateCurrent,
            ref XInputState ___gamepadStatePrev, ref bool ____isConnected)
        {
            XInputState s = new XInputState();
            if (!VirtualPads.TryGetVirtual(____playerIndex, ref s)) return true;
            ___gamepadStateCurrent = s;
            ____isConnected = true;
            if (___gamepadStateCurrent.PacketNumber != ___gamepadStatePrev.PacketNumber && s_onStateChanged != null)
                s_onStateChanged.Invoke(__instance, null);
            ___gamepadStatePrev.Copy(___gamepadStateCurrent);
            return false;
        }
    }
}
