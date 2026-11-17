using System.IO;
using System.Reflection;
using Game;
using UnityEngine;

namespace OriCoop.Story
{
    internal interface IStoryHost
    {
        SeinCharacter P1 { get; }
        SeinCharacter P2 { get; }
        bool Inside { get; }
        int LocalSeat { get; }
        bool IsPuppet(SeinCharacter sein);
        bool IsPuppetComponent(Component component);
        bool WorldApplying { get; }
        object FindAny(MoonGuid id);
        void MarkCollected(MoonGuid id);
        void MarkPedestal(MoonGuid id);
        void Rebroadcast(BinaryReader reader);
        bool NewestRemoteFrame(int seat, uint tick);
        void PositionChanged();
        void Teleport(SeinCharacter sein, Vector3 position);
        void Trace(string subsystem, string name, params object[] values);
        void PatchPickupShortcut(MethodInfo original);
        bool IsPluginCameraTarget(Transform target);
    }
    internal static class StoryHost
    {
        internal static IStoryHost Current;
    }
}
