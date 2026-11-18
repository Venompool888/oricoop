using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop.Enemies
{
    internal sealed class GameEnemyHost : IEnemyHost
    {
        public bool RemoteHello { get { return NetPlay.RemoteHello; } }
        public int RemoteCount { get { return NetPlay.RemoteCount; } }
        public int LocalSeat { get { return Players.LocalSeat; } }
        public uint LocalTick { get { return NetPlay.LocalTick; } }
        public IEnumerable<KeyValuePair<int, Vector3>> RemotePositions { get { return NetPlay.RemotePositions; } }
        public bool RemotePos(int seat, out Vector3 position) { return NetPlay.RemotePos(seat, out position); }
        public SeinCharacter Local { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        public SeinCharacter LocalAt(int seat) { return Players.IsLocal(seat) ? Players.OriOf(seat) : null; }
        public int VictimSeat(Component component)
        { Puppet p = Players.Of(component); return p == null ? Players.LocalSeat : p.Seat; }
        public bool Inside { get { return Players.RemoteContext; } }
        public bool P2IsNearer(Vector3 position) { return P2Context.P2IsNearer(position); }
        public object NearestPuppet(Vector3 position) { return Players.NearestPuppet(position); }
        public SeinCharacter PuppetSein(object puppet) { return ((Puppet)puppet).Sein; }
        public void Enter(object puppet) { P2Context.Enter((Puppet)puppet); }
        public void Exit() { P2Context.Exit(); }
        public bool BelongsToP2(Component component) { return Players.IsRemote(component); }
        public bool IsRemoteSein(SeinCharacter sein) { return Players.IsRemote(sein); }
        public int SeatOf(Component component) { return Players.Of(component).Seat; }
        public void Patch(MethodBase method, HarmonyMethod prefix, HarmonyMethod finalizer) { Plugin.H.Patch(method, prefix, null, null, finalizer, null); }
        public bool TraceSampleDue() { return CoopTrace.SampleDue("enemy"); }
        public void TraceEvent(string name, params object[] values) { CoopTrace.Event("enemy", name, values); }
    }
}
