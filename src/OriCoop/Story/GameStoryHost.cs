using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop.Story
{
    internal partial class GameStoryHost : IStoryHost
    {
        static readonly FieldInfo CameraTarget = AccessTools.Field(typeof(Plugin), "m_camTarget");
        internal static MoonGuid ReadGuid(byte[] bytes) { return new MoonGuid(new BinaryReader(new MemoryStream(bytes))); }
        public SeinCharacter P1 { get { return P2Context.P1; } }
        public SeinCharacter P2 { get { return P2Context.P2; } }
        public bool Inside { get { return P2Context.Inside; } }
        public int LocalSeat { get { return Players.LocalSeat; } }
        public bool IsPuppet(SeinCharacter sein) { return Players.OfSein(sein) != null; }
        public bool IsPuppetComponent(Component component) { return Players.Of(component) != null; }
        public bool WorldApplying { get { return WorldSync.Applying; } }
        public object FindAny(MoonGuid id) { return WorldSync.FindAny(id); }
        public void MarkCollected(MoonGuid id) { WorldSync.MarkCollected(id); }
        public void MarkPedestal(MoonGuid id) { WorldSync.MarkPedestal(id); }
        public void Rebroadcast(BinaryReader reader) { NetPlay.Rebroadcast(reader); }
        public bool NewestRemoteFrame(int seat, uint tick) { return NetPlay.NewestRemoteFrame(seat, tick); }
        public void PositionChanged() { SessionRules.PositionChanged(); }
        public void Teleport(SeinCharacter sein, Vector3 position) { Plugin.Teleport(sein, position); }
        public void Trace(string subsystem, string name, params object[] values) { CoopTrace.Event(subsystem, name, values); }
        public void PatchPickupShortcut(MethodInfo original)
        { Plugin.H.Patch(original, new HarmonyMethod(typeof(ProgressSync), "LocalPickupShortcut"), null, null, null, null); }
        public bool IsPluginCameraTarget(Transform target)
        { return Plugin.Instance != null && target == (CameraTarget.GetValue(Plugin.Instance) as Transform); }
    }
}
