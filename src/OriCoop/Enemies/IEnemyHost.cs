using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OriCoop.Enemies
{
    internal interface IEnemyHost
    {
        bool RemoteHello { get; }
        int RemoteCount { get; }
        int LocalSeat { get; }
        uint LocalTick { get; }
        IEnumerable<KeyValuePair<int, Vector3>> RemotePositions { get; }
        bool RemotePos(int seat, out Vector3 position);
        SeinCharacter Local { get; }
        SeinCharacter LocalAt(int seat);
        int VictimSeat(Component component);
        bool Inside { get; }
        bool P2IsNearer(Vector3 position);
        object NearestPuppet(Vector3 position);
        SeinCharacter PuppetSein(object puppet);
        void Enter(object puppet);
        void Exit();
        bool BelongsToP2(Component component);
        bool IsRemoteSein(SeinCharacter sein);
        int SeatOf(Component component);
        void Patch(MethodBase method, HarmonyMethod prefix, HarmonyMethod finalizer);
        bool TraceSampleDue();
        void TraceEvent(string name, params object[] values);
    }
}
