using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Net;

namespace BepInEx.Configuration
{
    public class ConfigEntry<T> { public T Value; }
    public class ConfigFile
    {
        readonly Dictionary<string, object> entries = new Dictionary<string, object>();
        public ConfigEntry<T> Bind<T>(string section, string key, T initial, string description)
        {
            string path = section + "/" + key;
            if (!entries.ContainsKey(path)) entries[path] = new ConfigEntry<T> { Value = initial };
            return (ConfigEntry<T>)entries[path];
        }
    }
}

namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }

namespace OriCoop
{
    internal struct InputDevice
    {
        public enum DeviceKind { KeyboardMouse, Pad }
        public DeviceKind Kind;
        public int Pad;
    }
    internal class GameSeatView
    {
        public InputDevice InputOf(int seat) { return Players.Inputs[seat]; }
    }
    internal static class Roles { public static string Tag(int seat) { return "P" + seat; } }
    internal static class Players
    {
        public static int LocalSeat = 1;
        public static int[] LocalSeats = { 1 };
        public static InputDevice[] Inputs = new InputDevice[5];
        public static bool IsLocal(int seat) { return Array.IndexOf(LocalSeats, seat) >= 0; }
        public static event Action<int[]> SeatsRenamed;
        public static void Rename(int oldSeat, int newSeat)
        {
            int[] changes = new int[5]; changes[oldSeat] = newSeat; SeatsRenamed(changes);
        }
    }
    internal sealed class Peer : IMessagePeer
    {
        internal readonly List<byte[]> Sent = new List<byte[]>();
        public bool Connected { get { return true; } }
        public double LastReceiveAgeMs { get { return 0; } }
        public void Send(byte[] message, int length, bool reliable)
        {
            if (!reliable) throw new Exception("names should be ordered");
            byte[] copy = new byte[length]; Array.Copy(message, copy, length); Sent.Add(copy);
        }
    }
    internal class Room : IMessageContext
    {
        public bool Host = true, Online;
        public readonly SessionMachine Session;
        public readonly MessageBus Bus;
        internal readonly Peer Single = new Peer();
        internal Room()
        {
            Session = new SessionMachine(this, delegate { return 0; }, delegate(int seat) { }, delegate { }, delegate { }, delegate { });
            Bus = new MessageBus(this, Session, delegate(byte type, Exception e) { throw e; });
        }
        internal Peer Add(int seat)
        {
            Peer peer = new Peer(); Session.Remotes[seat] = new SessionMachine.Remote { Seat = seat, Peer = peer }; return peer;
        }
        public bool Enabled { get { return Online; } }
        public bool IsHost { get { return Host; } }
        public bool FactorySession { get { return false; } }
        public bool UsesSteam { get { return false; } }
        public bool HasHub { get { return Host; } }
        public bool HasPeer { get { return Online; } }
        public bool Connected { get { return Online; } }
        public bool PrimaryHello { get { return true; } }
        public bool SteamReady { get { return false; } }
        public string Problem { get { return ""; } }
        public int AssignedSeat { get { return Players.LocalSeat; } }
        public double PeerLastReceiveAgeMs { get { return 0; } }
        public int LocalSeat { get { return Players.LocalSeat; } }
        public int MaxSeats { get { return 4; } }
        public bool DisableForward { get { return false; } }
        public IMessagePeer SinglePeer { get { return Single; } }
        public bool SendToOverride(int seat, byte type, bool reliable, Action<BinaryWriter> body) { return false; }
        public void AssignSeat(int seat) { Players.LocalSeat = seat; }
        public SessionMachine.Remote CreateRemote(int seat) { return new SessionMachine.Remote { Seat = seat }; }
        public void HandleCore(byte type, BinaryReader r, byte[] packet, SessionMachine.Remote remote)
        {
            if (type == 1) Session.AnnounceHello(remote, Session.MarkHello(remote));
        }
    }
    internal static class NetPlay
    {
        internal static readonly Room Room = new Room();
        public static readonly int[] Owners = { 0, 1, 2, 3, 2 };
        public static int DeviceSeat(int seat) { return Owners[seat] != 0 ? Owners[seat] : seat; }
        public static bool IsHost { get { return Room.Host; } }
        public static bool Enabled { get { return Room.Online; } }
        public static bool Connected { get { return Room.Connected; } }
        public static int Sender { get { return Room.Bus.Sender; } }
        public static event Action<int> PlayerJoined { add { Room.Session.PlayerJoined += value; } remove { Room.Session.PlayerJoined -= value; } }
        public static event Action<int> PlayerLeft { add { Room.Session.PlayerLeft += value; } remove { Room.Session.PlayerLeft -= value; } }
        public static event Action SessionEnded { add { Room.Session.SessionEnded += value; } remove { Room.Session.SessionEnded -= value; } }
        public static void On(byte type, Action<BinaryReader> receive) { Room.Bus.On(type, receive); }
        public static void Forward(byte type) { Room.Bus.Forward(type); }
        public static void Send(byte type, bool reliable, Action<BinaryWriter> body) { Room.Bus.Send(type, reliable, body); }
    }
}
