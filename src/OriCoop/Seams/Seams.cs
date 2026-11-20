using System;
using System.IO;

// Seams between modules (see docs\modules.md). Nothing in this file may reference UnityEngine,
// Assembly-CSharp or any other OriCoop type, so a module plus this file compiles alone in a unit test.
namespace OriCoop.Seams
{
    // The message bus. In game: NetPlayBus (wraps NetPlay). In unit tests: a fake.
    public interface INetBus
    {
        bool Enabled { get; }
        bool Connected { get; }
        bool IsHost { get; }
        int Sender { get; }                      // seat of the peer whose message is being handled
        int HostSeat { get; }                    // seat whose host-only messages count: 1 in P2P, the arbiter in server mode
        void On(byte type, Action<BinaryReader> handler);
        void Send(byte type, bool reliable, Action<BinaryWriter> body);
        void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body);
        void Forward(params byte[] types);       // host relays these types to the other guests
        event Action SessionStarted;
        event Action SessionEnded;
        event Action<int> PlayerJoined;
        event Action<int> PlayerLeft;
    }

    public interface IClock
    {
        float Time { get; }          // Time.time
        float RealTime { get; }      // Time.realtimeSinceStartup
        int Frame { get; }           // Time.frameCount
    }

    public interface ILog
    {
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg);
    }

    // Session gates that many modules read (owner: saves/session).
    public interface ISessionState
    {
        bool Ready { get; }              // SaveSync.Ready
        bool LocalInGameplay { get; }    // NetPlay.LocalInGameplay()
    }

    // Composition root fills these in Plugin.Awake (GameServices.Install); tests fill them with fakes.
    public static class Services
    {
        public static INetBus Net;
        public static IClock Clock;
        public static ILog Log;
        public static ISessionState Session;
    }
}
