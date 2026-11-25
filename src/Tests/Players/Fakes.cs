namespace UnityEngine
{
    public class Transform
    {
        public Transform Parent;
        public bool IsChildOf(Transform parent) { for (Transform p = this; p != null; p = p.Parent) if (p == parent) return true; return false; }
    }
    public class Component { public Transform transform = new Transform(); }
    public class GameObject : Component { public bool activeInHierarchy = true; }
    public struct Vector2 { }
    public struct Vector3
    {
        public float x, y, z;
        public float sqrMagnitude { get { return x * x + y * y + z * z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3 { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z }; }
    }
    public static class Mathf { public static float Sqrt(float x) { return (float)System.Math.Sqrt(x); } }
}
namespace Game
{
    public class SeinCharacter : UnityEngine.Component { public UnityEngine.Vector3 Position; public UnityEngine.GameObject gameObject = new UnityEngine.GameObject(); }
    public class Ori { }
    public static class Characters { public static SeinCharacter Sein; }
}
namespace OriCoop
{
    public class InputState { }
    public static class Roles { public static int Mine = 1; }
    public static class NetPlay
    {
        public static bool Enabled = true; public static int RemoteCount; public static int[] LocalSeats = { 1, 2 };
        public static bool IsLocalSeat(int seat) { return System.Array.IndexOf(LocalSeats, seat) >= 0; }
    }
    public static class P2Context { public static bool Inside; public static Puppet Current; public static Game.SeinCharacter P1, P2; }
    public static class Revive { public static bool IsDowned(Game.SeinCharacter s) { return false; } }
    public static class OnlineLocalPlayers
    {
        public static bool LocalGroupBounds(out UnityEngine.Vector3 centre, out UnityEngine.Vector2 size)
        { centre = new UnityEngine.Vector3(); size = new UnityEngine.Vector2(); return false; }
    }
}
