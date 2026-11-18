using System;
using System.Collections.Generic;
using OriCoop.World;

static class WorldTests
{
    static int cases, failures;
    static void Run(string name, Action body)
    {
        cases++;
        try { body(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    sealed class Ev { public string Name; public string Scene; public bool Gone; }

    static int Main()
    {
        // T-074 review: two held events for the same object must replay as the partner sent them.
        Run("held events for one object come out in arrival order", delegate {
            HeldEvents<Ev> q = new HeldEvents<Ev>(); int dropped;
            q.Add(new Ev { Name = "damage", Scene = "a" }); q.Add(new Ev { Name = "collapse", Scene = "a" });
            List<Ev> got = q.TakeReady(e => e.Gone, e => e.Scene == "a", out dropped);
            Check(got.Count == 2 && got[0].Name == "damage" && got[1].Name == "collapse", "order " + string.Join(",", got.ConvertAll(e => e.Name).ToArray()));
            Check(q.Count == 0 && dropped == 0, "left " + q.Count + " dropped " + dropped);
        });
        Run("events of a scene still loading stay held, in order", delegate {
            HeldEvents<Ev> q = new HeldEvents<Ev>(); int dropped;
            q.Add(new Ev { Name = "trigger", Scene = "b" }); q.Add(new Ev { Name = "rope", Scene = "a" }); q.Add(new Ev { Name = "stomper", Scene = "b" });
            List<Ev> first = q.TakeReady(e => e.Gone, e => e.Scene == "a", out dropped);
            Check(first.Count == 1 && first[0].Name == "rope" && q.Count == 2, "first pass");
            List<Ev> second = q.TakeReady(e => e.Gone, e => true, out dropped);
            Check(second.Count == 2 && second[0].Name == "trigger" && second[1].Name == "stomper", "second pass order");
        });
        Run("events whose object is gone are dropped and counted", delegate {
            HeldEvents<Ev> q = new HeldEvents<Ev>(); int dropped;
            q.Add(new Ev { Name = "x", Scene = "a", Gone = true }); q.Add(new Ev { Name = "y", Scene = "a" });
            List<Ev> got = q.TakeReady(e => e.Gone, e => true, out dropped);
            Check(got.Count == 1 && got[0].Name == "y" && dropped == 1, "dropped " + dropped);
        });
        Console.WriteLine("World: " + cases + " cases, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
