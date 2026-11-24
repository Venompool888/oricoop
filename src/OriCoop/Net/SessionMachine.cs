using System;
using System.Collections.Generic;

namespace OriCoop.Net
{
    public interface ISessionContext
    {
        bool Enabled { get; }
        bool IsHost { get; }
        bool FactorySession { get; }
        bool UsesSteam { get; }
        bool HasHub { get; }
        bool HasPeer { get; }
        bool Connected { get; }
        bool PrimaryHello { get; }
        bool SteamReady { get; }
        string Problem { get; }
        int AssignedSeat { get; }
        double PeerLastReceiveAgeMs { get; }
    }

    public sealed class SessionMachine
    {
        public enum Phase { Off, Starting, Waiting, Connecting, Connected, Retrying }

        // T-201: a side that leaves tells its partner PartnerGoodbye, and the connection keeps that text as its
        // disconnect reason on both sides. In this side's own trace and status it must not read as "the partner quit":
        // for a connection this side closed itself the reason is "left: <why>". Any other reason is kept as it is.
        public const string PartnerGoodbye = "partner quit the game";
        public const string LocalLeavePrefix = "left: ";
        public static string OwnDisconnectReason(string connectionReason, bool leftHere, string why)
        {
            if (leftHere && connectionReason == PartnerGoodbye) return LocalLeavePrefix + (why ?? "");
            return connectionReason ?? "";
        }

        public class Remote
        {
            public int Seat;
            public IMessagePeer Peer;
            public bool Hello, InGameplay;
            public double LastHeardMs;
        }

        readonly ISessionContext context;
        readonly Func<double> clock;
        readonly Action<int> assignSeat;
        readonly Action<Remote> beforeLeave;
        readonly Action<int> afterRemove;
        readonly Action<Exception> endError;
        readonly Dictionary<int, Remote> remotes = new Dictionary<int, Remote>();
        bool started;

        public SessionMachine(ISessionContext context, Func<double> clock, Action<int> assignSeat,
            Action<Remote> beforeLeave, Action<int> afterRemove, Action<Exception> endError)
        {
            this.context = context; this.clock = clock; this.assignSeat = assignSeat;
            this.beforeLeave = beforeLeave; this.afterRemove = afterRemove; this.endError = endError;
        }

        // The selected transport survives peer resets and SessionEnded while online play is still active.
        internal string ActiveTransport { get; set; }
        internal string TransportName(Func<string> configured) { return ActiveTransport ?? NormalizeTransport(configured()); }
        internal static string NormalizeTransport(string transport)
        {
            string name = (transport ?? "direct").Trim().ToLowerInvariant();
            return name == "relay" ? "direct" : name;
        }

        public Dictionary<int, Remote> Remotes { get { return remotes; } }
        public bool Started { get { return started; } }
        public event Action SessionStarted, SessionEnded;
        public event Action<int> PlayerJoined, PlayerLeft;

        public bool WaitingForSeat
        {
            get { return (context.FactorySession || !context.IsHost && !context.UsesSteam) && context.AssignedSeat == 0; }
        }

        public Phase CurrentPhase
        {
            get
            {
                if (!context.Enabled) return Phase.Off;
                if (context.Connected && context.FactorySession && !context.PrimaryHello && context.AssignedSeat != 0) return Phase.Waiting;
                if (context.Connected) return context.PrimaryHello ? Phase.Connected : Phase.Connecting;
                if (context.Problem.Length > 0) return Phase.Retrying;
                if (context.IsHost)
                {
                    if (context.UsesSteam) return context.SteamReady ? Phase.Waiting : Phase.Starting;
                    return context.HasHub || context.HasPeer ? Phase.Waiting : Phase.Starting;
                }
                return context.HasPeer ? Phase.Connecting : Phase.Starting;
            }
        }

        public float SilenceMs
        {
            get
            {
                double worst = 0;
                foreach (Remote remote in remotes.Values)
                {
                    if (remote.Peer != null && remote.Peer.Connected)
                        worst = Math.Max(worst, remote.Peer.LastReceiveAgeMs);
                    else if (remote.Peer == null && context.FactorySession && remote.InGameplay && remote.LastHeardMs > 0 && context.Connected)
                        worst = Math.Max(worst, clock() - remote.LastHeardMs);
                }
                if (worst == 0 && context.Connected) worst = context.PeerLastReceiveAgeMs;
                return (float)worst;
            }
        }
        public bool SignalLost { get { return context.Connected && SilenceMs >= 1000f; } }

        public bool RemoteInSession(int seat)
        {
            Remote remote;
            return remotes.TryGetValue(seat, out remote) && remote.Hello;
        }

        public bool IsFactoryMessage(byte type) { return context.FactorySession && type >= 176 && type <= 186; }
        public void AssignSeat(int seat) { assignSeat(seat); }
        public void Heard(Remote remote) { remote.LastHeardMs = clock(); }

        public bool MarkHello(Remote remote)
        {
            bool joined = !remote.Hello;
            remote.Hello = true;
            return joined;
        }

        public void AnnounceHello(Remote remote, bool joined)
        {
            if (!started)
            {
                started = true;
                if (SessionStarted != null) SessionStarted();
            }
            if (joined && PlayerJoined != null) PlayerJoined(remote.Seat);
        }

        public void RemoteLeft(int seat)
        {
            Remote remote;
            if (remotes.TryGetValue(seat, out remote)) Leave(remote);
        }

        public void Leave(Remote remote)
        {
            beforeLeave(remote);
            remotes.Remove(remote.Seat);
            afterRemove(remote.Seat);
            if (PlayerLeft != null) PlayerLeft(remote.Seat);
            if (remotes.Count == 0) EndSession();
        }

        public void EndSession()
        {
            if (!started) return;
            started = false;
            if (SessionEnded != null) { try { SessionEnded(); } catch (Exception e) { endError(e); } }
        }

        public void Reset()
        {
            remotes.Clear(); assignSeat(0); started = false;
        }
    }
}
