using System;
using System.Collections.Generic;

namespace OriCoop.Pads
{
    public enum PsRoute { Unknown, Native, Mirrored }

    public sealed class MirrorJudge
    {
        const long Before = 50 * TimeSpan.TicksPerMillisecond;
        const long After = 250 * TimeSpan.TicksPerMillisecond;

        sealed class Edge
        {
            public long Ticks;
            public bool Paired;

            public bool Contains(long ticks) { return ticks >= Ticks - Before && ticks <= Ticks + After; }
        }

        readonly Queue<Edge> m_edges = new Queue<Edge>();
        long m_lastXInput = long.MinValue;
        bool m_anyConnected;
        PsRoute m_route, m_previousVote;

        public MirrorJudge(bool anyRealXInputConnectedAtStart)
        {
            m_anyConnected = anyRealXInputConnectedAtStart;
            m_route = m_anyConnected ? PsRoute.Unknown : PsRoute.Native;
        }

        public void OnPsEdge(long ticks)
        {
            var edge = new Edge { Ticks = ticks };
            edge.Paired = edge.Contains(m_lastXInput);
            m_edges.Enqueue(edge);
        }

        public void OnXInputActive(long ticks)
        {
            if (!m_anyConnected) return;
            m_lastXInput = ticks;
            foreach (Edge edge in m_edges)
                if (edge.Contains(ticks)) edge.Paired = true;
        }

        public void OnXInputConnectedChanged(bool anyConnected) { m_anyConnected = anyConnected; }

        public PsRoute Tick(long nowTicks)
        {
            // At +250 ms each edge votes Mirrored if XInput was active in [-50, +250] ms, Native otherwise.
            // Two consecutive equal votes are required for every route change, including from Unknown.
            while (m_edges.Count != 0 && nowTicks >= m_edges.Peek().Ticks + After)
            {
                PsRoute vote = m_edges.Dequeue().Paired ? PsRoute.Mirrored : PsRoute.Native;
                if (vote == m_previousVote) m_route = vote;
                m_previousVote = vote;
            }
            return m_route;
        }
    }
}
