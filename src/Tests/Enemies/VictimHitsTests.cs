using System;
using OriCoop.Enemies;

static class VictimHitsTests
{
    internal static void Run(Action<string, Action> run)
    {
        run("same projectile can hit two local seats once each", delegate {
            VictimHits hits = new VictimHits();
            if (!hits.Add(2, 59) || !hits.Add(3, 59) || hits.Add(2, 59) || hits.Add(3, 59))
                throw new Exception("hit receipts must be per victim seat, not per device");
            hits.Clear();
            if (!hits.Add(2, 59) || hits.Add(0, 59) || hits.Add(5, 59)) throw new Exception("reset and seat bounds");
        });
    }
}
