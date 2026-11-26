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
        // T-041: the health of the soul link made while this player was down (0 = none) and the hold-Y timer.
        public float LinkHealth, SelfHold;
        // T-215 #5: the health this PC's rescuer has drained into the current hold (refunded if it yields).
        public float LocalGiven;
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
        // T-215 #5 (#301): two teammates on different PCs held at the same skull at once; each PC simulated its own
        // hold, so both lost health and only the first revive counted. The lower seat keeps the revive. A higher seat
        // that hears a lower seat's progress gives up its hold, gets back what it had drained, and stays out while
        // that progress keeps coming. A teammate already reviving keeps it: a newcomer does not start beside it.
        public const float RivalFresh = 0.3f;
        public static bool Outranks(int rival, int rescuer) { return rival > 0 && rescuer > 0 && rival < rescuer; }
        // activeRescuer: whose progress this PC last took (d.Rescuer); activeRemote: that seat plays on another PC.
        public static bool StandAside(bool wasHolding, int localRescuer, int activeRescuer, bool activeRemote, float sinceActive)
        {
            if (!activeRemote || activeRescuer <= 0 || activeRescuer == localRescuer || sinceActive >= RivalFresh) return false;
            return !wasHolding || Outranks(activeRescuer, localRescuer);
        }
        // Returns what the yielding rescuer gets back; the rival's own progress replaces this PC's.
        public static float Yield(DownedState d, int rival)
        {
            float refund = d.LocalGiven;
            d.LocalGiven = 0f; d.Holding = false; d.NeedRelease = false;
            d.Progress = 0f; d.Given = 0f; d.Rescuer = rival;
            return refund;
        }
        public static void Advance(DownedState d, float dt, float drain, float duration)
        {
            d.Given += drain;
            d.LocalGiven += drain;
            float progress = d.Progress + dt / duration;
            d.Progress = 1f < progress ? 1f : progress;
        }
        // T-215 (#301, 6th playtest "看队友复活的转圈一卡一卡"): a teammate's revive progress arrives every 0.1 s on an
        // unreliable channel, so a watcher's ring jumped a step per message (two when one was lost). Between messages
        // the shown ring keeps filling at the revive rate, at most ProgressLead seconds' worth past the last value, and
        // only while messages keep coming; a value well below the shown one (a new revive) is taken at once.
        public const float ProgressLead = 0.12f, ProgressFresh = 0.25f;
        public static float DisplayProgress(float shown, float target, float rate, float dt, float sinceUpdate)
        {
            if (target <= 0f) return 0f;
            float lead = rate * ProgressLead;
            if (shown > target + lead) shown = target;   // a new revive started below what was shown
            float next = sinceUpdate < ProgressFresh ? shown + rate * dt : shown;
            float high = target + lead < 1f ? target + lead : 1f;
            if (next > high) next = high;
            return next < target ? target : next;
        }

        public static bool ShouldSend(DownedState d, int localSeat, bool multi, float now)
        {
            if (d.Progress <= 0f || d.Rescuer != localSeat || multi && !d.Holding || now < d.ProgressSendAt) return false;
            d.ProgressSendAt = now + 0.1f;
            return true;
        }

        // T-215 #1: where a revived Ori appears. Beside its rescuer; when the rescuer's Ori is not on this PC any more
        // (it left or is loading while its "revived you" was on the way), at the revived Ori's own skull, where the
        // rescuer stood. Either way the revive is completed: position, health, invincibility.
        public static void ReviveSpot(bool rescuerHere, float rescuerX, float rescuerY, bool rescuerFacesLeft,
            float skullX, float skullY, out float x, out float y)
        {
            if (rescuerHere) { x = rescuerX + (rescuerFacesLeft ? 1f : -1f); y = rescuerY; }
            else { x = skullX; y = skullY; }
        }

        // The health a revived Ori gets: what the rescuer gave, at least 1, at most its maximum.
        public static float RevivedHealth(float given, float max)
        {
            float h = given < 1f ? 1f : given;
            return h > max ? max : h;
        }
    }

    // T-215 #4: the last solid ground an Ori stood on is where its skull goes, but only while the Ori is still around
    // there. An Ori that moved further than any movement allows between two looks (a door, a teleport, a reload, a
    // pedestal) left that ground behind: its skull goes where it died.
    public static class SafeGroundRules
    {
        // Looks are 0.2 s apart; this is 60 units a second, faster than any fall or dash.
        public const float RelocatedDistance = 12f;

        public static bool Relocated(float dx, float dy)
        {
            return dx * dx + dy * dy > RelocatedDistance * RelocatedDistance;
        }

        // haveSeen / dx, dy: the Ori's move since it was last looked at.
        public static bool UseSafeGround(bool haveSafe, bool haveSeen, float dx, float dy)
        {
            return haveSafe && !(haveSeen && Relocated(dx, dy));
        }
    }

    // T-215 #2: "everyone is down" must stay true for a moment before the host declares the wipe. A teammate's
    // "I'm back" (62) that left before another teammate's "I'm down" (60) can arrive after it; the host would see
    // everyone down for that instant and roll back a player who is already up.
    public sealed class WipeGrace
    {
        public const float Seconds = 0.8f;   // real seconds; covers a 400 ms one-way lag with room to spare
        float m_due = -1f;
        public bool Armed { get { return m_due >= 0f; } }

        // Every frame on the host. true = everyone was down for the whole grace: declare the wipe now.
        public bool Tick(bool allDown, float now, float seconds)
        {
            if (!allDown) { m_due = -1f; return false; }
            if (m_due < 0f) m_due = now + seconds;
            if (now < m_due) return false;
            m_due = -1f;
            return true;
        }

        public void Reset() { m_due = -1f; }
    }
}
