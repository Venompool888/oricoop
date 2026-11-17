using UnityEngine;

namespace OriCoop.Authority
{
    internal static class LeaseMath
    {
        public static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        public static bool Finite(Vector3 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z); }
        public static bool Finite(Quaternion value) { return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w); }
    }
}
