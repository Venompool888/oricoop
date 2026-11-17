using System;
using System.IO;

namespace OriCoop.Seams
{
    // In-game implementations of the seams. Plugin.Awake calls GameServices.Install().
    internal static class GameServices
    {
        internal static void Install()
        {
            Services.Net = new NetPlayBus();
            Services.Clock = new UnityClock();
            Services.Log = new PluginLog();
            Services.Session = new GameSession();
        }

        sealed class NetPlayBus : INetBus
        {
            public bool Enabled { get { return NetPlay.Enabled; } }
            public bool Connected { get { return NetPlay.Connected; } }
            public bool IsHost { get { return NetPlay.IsHost; } }
            public int Sender { get { return NetPlay.Sender; } }
            public void On(byte type, Action<BinaryReader> handler) { NetPlay.On(type, handler); }
            public void Send(byte type, bool reliable, Action<BinaryWriter> body) { NetPlay.Send(type, reliable, body); }
            public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body) { NetPlay.SendTo(seat, type, reliable, body); }
            public void Forward(params byte[] types) { NetPlay.Forward(types); }
            public event Action SessionStarted { add { NetPlay.SessionStarted += value; } remove { NetPlay.SessionStarted -= value; } }
            public event Action SessionEnded { add { NetPlay.SessionEnded += value; } remove { NetPlay.SessionEnded -= value; } }
            public event Action<int> PlayerJoined { add { NetPlay.PlayerJoined += value; } remove { NetPlay.PlayerJoined -= value; } }
            public event Action<int> PlayerLeft { add { NetPlay.PlayerLeft += value; } remove { NetPlay.PlayerLeft -= value; } }
        }

        sealed class UnityClock : IClock
        {
            public float Time { get { return UnityEngine.Time.time; } }
            public float RealTime { get { return UnityEngine.Time.realtimeSinceStartup; } }
            public int Frame { get { return UnityEngine.Time.frameCount; } }
        }

        sealed class PluginLog : ILog
        {
            public void Info(string msg) { Plugin.Log.LogInfo(msg); }
            public void Warn(string msg) { Plugin.Log.LogWarning(msg); }
            public void Error(string msg) { Plugin.Log.LogError(msg); }
        }

        sealed class GameSession : ISessionState
        {
            public bool Ready { get { return SaveSync.Ready; } }
            public bool LocalInGameplay { get { return NetPlay.LocalInGameplay(); } }
        }
    }
}
