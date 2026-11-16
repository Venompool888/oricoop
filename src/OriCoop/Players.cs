using System.Collections.Generic;
using Game;
using UnityEngine;

namespace OriCoop
{
    public sealed class Puppet
    {
        public int Seat;
        public SeinCharacter Sein;
        public readonly InputState Input = new InputState();
        public readonly HashSet<int> Ids = new HashSet<int>();
        public readonly List<GameObject> Owned = new List<GameObject>();
        public Ori Companion;
    }

    public static class Players
    {
        public const int MaxSeats = 4;
        public static int AssignedSeat;
        public static int LocalSeat { get { return Roles.Mine; } }
        public static int RemoteCount { get { return NetPlay.RemoteCount; } }
        public static readonly List<Puppet> Remotes = new List<Puppet>();
        public static Puppet First { get { return Remotes.Count == 0 ? null : Remotes[0]; } }

        public static Puppet OfInstance(int instanceId)
        {
            foreach (Puppet p in Remotes) if (p.Sein != null && p.Ids.Contains(instanceId)) return p;
            return null;
        }

        public static Puppet Of(Component c)
        {
            if (c == null) return null;
            foreach (Puppet p in Remotes)
            {
                if (p.Sein == null) continue;
                if (c.transform.IsChildOf(p.Sein.transform)) return p;
                foreach (GameObject g in p.Owned)
                    if (g != null && c.transform.IsChildOf(g.transform)) return p;
            }
            return null;
        }

        public static Puppet OfSein(SeinCharacter s)
        {
            if (s == null) return null;
            foreach (Puppet p in Remotes) if (p.Sein == s) return p;
            return null;
        }

        public static int SeatOf(SeinCharacter s)
        {
            if (s == null) return 0;
            Puppet p = OfSein(s);
            if (p != null) return p.Seat;
            return s == P2Context.P1 || (!P2Context.Inside && s == Characters.Sein) ? LocalSeat : 0;
        }

        public static Puppet NearestPuppet(Vector3 pos)
        {
            SeinCharacter local = P2Context.P1;
            if (local == null) return null;
            float distance = Mathf.Sqrt((local.Position - pos).sqrMagnitude) - 1f;
            bool localDown = !NetPlay.Enabled && Revive.IsDowned(local);
            Puppet nearest = null;
            foreach (Puppet p in Remotes)
            {
                if (p.Sein == null || !p.Sein.gameObject.activeInHierarchy) continue;
                float d = Mathf.Sqrt((p.Sein.Position - pos).sqrMagnitude);
                if ((localDown && nearest == null) || d < distance) { nearest = p; distance = d; }
            }
            return nearest;
        }
    }
}
