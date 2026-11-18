using System.Collections.Generic;

namespace OriCoop.Rules
{
    public class DownedState
    {
        public int Player;
        public float Progress, DrainRate, Given;
        public bool NeedRelease, Holding;
        public int Rescuer;
        public float ProgressSendAt;
        public float RespawnLeft = 30f;
    }

    public sealed class DownedRoster<T> where T : DownedState
    {
        public readonly T[] Seats;
        public DownedRoster(int maxSeats) { Seats = new T[maxSeats + 1]; }
        public bool AnyDowned
        {
            get { foreach (T d in Seats) if (d != null) return true; return false; }
        }
        public bool TryAdd(int seat, T down)
        {
            if (Seats[seat] != null) return false;
            Seats[seat] = down;
            return true;
        }
        public T Take(int seat)
        {
            T down = Seats[seat]; Seats[seat] = null; return down;
        }
        public T TakeRevived(int seat, int sender)
        {
            if (seat != sender) return null;
            return Take(seat);
        }
        public bool AllDown(IEnumerable<int> present, int dyingSeat)
        {
            foreach (int seat in present) if (seat != dyingSeat && Seats[seat] == null) return false;
            return true;
        }
        public void MergeProgress(int seat, int rescuer, int sender, int localSeat, float progress, float given)
        {
            if (seat < 0 || seat >= Seats.Length) return;   // seat comes off the wire
            T d = Seats[seat];
            if (d == null || rescuer != sender || d.Holding && d.Rescuer == localSeat) return;
            d.Progress = d.Progress > progress ? d.Progress : progress;
            d.Given = d.Given > given ? d.Given : given;
            d.Rescuer = rescuer;
        }
    }

    public enum DeathOutcome { Downed, Checkpoint }

    public static class ReviveLogic
    {
        public const int MaxSeats = 4;
        public static bool CanSimulateHold(bool online, bool saverLocal) { return !online || saverLocal; }
        public static DeathOutcome DecideLocalDeath(bool allDown, bool partnerAlive, bool menuOpen)
        {
            return !allDown && partnerAlive && !menuOpen ? DeathOutcome.Downed : DeathOutcome.Checkpoint;
        }
        public static DeathOutcome DecideOnlineDeath(bool allDown, bool isHost)
        {
            return allDown && isHost ? DeathOutcome.Checkpoint : DeathOutcome.Downed;
        }
        public static bool RespawnDue(DownedState d, float dt)
        {
            d.RespawnLeft -= dt;
            return d.RespawnLeft <= 0f;
        }
        public static void Release(DownedState d) { d.NeedRelease = false; }
        public static bool Interrupt(DownedState d)
        {
            if (!d.Holding) return false;
            d.NeedRelease = true; d.Holding = false; return true;
        }
        public static void BeginHold(DownedState d, int rescuer)
        {
            d.Rescuer = rescuer;
            d.Holding = true;
        }
        public static void SetDrainRate(DownedState d, float health, float duration)
        {
            d.DrainRate = health / 2f / duration;
        }
        public static float Drain(DownedState d, float health, float dt)
        {
            float remaining = health - 0.25f;
            float cap = 0f > remaining ? 0f : remaining;
            float amount = d.DrainRate * dt;
            return amount < cap ? amount : cap;
        }
        public static void Advance(DownedState d, float dt, float drain, float duration)
        {
            d.Given += drain;
            float progress = d.Progress + dt / duration;
            d.Progress = 1f < progress ? 1f : progress;
        }
        public static bool ShouldSend(DownedState d, int localSeat, bool multi, float now)
        {
            if (d.Progress <= 0f || d.Rescuer != localSeat || multi && !d.Holding || now < d.ProgressSendAt) return false;
            d.ProgressSendAt = now + 0.1f;
            return true;
        }
    }
}
