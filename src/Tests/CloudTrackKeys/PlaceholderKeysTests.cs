using System;
using OriCoop.Enemies;

// #406 (4): EnemySync.Track keeps each placeholder's scene root and actor key here instead of finding the root and
// building the key again on every call. A kept entry must only ever answer for the object it was stored for.
static class PlaceholderKeysTests
{
    static int failures;

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    sealed class Placeholder { }
    sealed class Root { }

    static int Main()
    {
        Run("miss before anything is kept", delegate
        {
            PlaceholderKeys<Placeholder, Root> keys = new PlaceholderKeys<Placeholder, Root>();
            Root root; string key;
            Check(!keys.TryGet(7, new Placeholder(), out root, out key), "empty table answered");
            Check(root == null && key == null, "miss left values");
        });
        Run("hit returns the kept root and key", delegate
        {
            PlaceholderKeys<Placeholder, Root> keys = new PlaceholderKeys<Placeholder, Root>();
            Placeholder p = new Placeholder(); Root r = new Root();
            keys.Set(7, p, r, "scene:enemy");
            Root root; string key;
            Check(keys.TryGet(7, p, out root, out key), "kept entry missed");
            Check(ReferenceEquals(root, r) && key == "scene:enemy", "wrong root or key");
        });
        Run("another object under the same id is a miss", delegate
        {
            PlaceholderKeys<Placeholder, Root> keys = new PlaceholderKeys<Placeholder, Root>();
            keys.Set(7, new Placeholder(), new Root(), "old");
            Root root; string key;
            Check(!keys.TryGet(7, new Placeholder(), out root, out key), "answered for a different object");
        });
        Run("null placeholder is never kept or found", delegate
        {
            PlaceholderKeys<Placeholder, Root> keys = new PlaceholderKeys<Placeholder, Root>();
            keys.Set(7, null, new Root(), "k");
            Check(keys.Count == 0, "kept a null placeholder");
            Root root; string key;
            Check(!keys.TryGet(7, null, out root, out key), "found a null placeholder");
        });
        Run("set again replaces the entry", delegate
        {
            PlaceholderKeys<Placeholder, Root> keys = new PlaceholderKeys<Placeholder, Root>();
            Placeholder a = new Placeholder(), b = new Placeholder(); Root r = new Root();
            keys.Set(7, a, r, "a"); keys.Set(7, b, r, "b");
            Root root; string key;
            Check(!keys.TryGet(7, a, out root, out key), "old object still answered");
            Check(keys.TryGet(7, b, out root, out key) && key == "b", "new object missed");
            Check(keys.Count == 1, "entry duplicated");
        });
        Run("remove and clear forget entries", delegate
        {
            PlaceholderKeys<Placeholder, Root> keys = new PlaceholderKeys<Placeholder, Root>();
            Placeholder a = new Placeholder(), b = new Placeholder(); Root r = new Root();
            keys.Set(1, a, r, "a"); keys.Set(2, b, r, "b");
            keys.Remove(1);
            Root root; string key;
            Check(!keys.TryGet(1, a, out root, out key), "removed entry answered");
            Check(keys.TryGet(2, b, out root, out key), "other entry lost on remove");
            keys.Clear();
            Check(keys.Count == 0 && !keys.TryGet(2, b, out root, out key), "clear kept entries");
        });
        Run("a hit does not allocate", delegate
        {
            System.Reflection.MethodInfo allocated = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (allocated == null) { Console.WriteLine("  (skipped: no allocation counter on this runtime)"); return; }
            PlaceholderKeys<Placeholder, Root> keys = new PlaceholderKeys<Placeholder, Root>();
            Placeholder p = new Placeholder(); keys.Set(7, p, new Root(), "k");
            Root root; string key;
            keys.TryGet(7, p, out root, out key);
            long before = (long)allocated.Invoke(null, null);
            for (int i = 0; i < 1000; i++) keys.TryGet(7, p, out root, out key);
            long after = (long)allocated.Invoke(null, null);
            Check(after - before < 1000, "1000 hits allocated " + (after - before) + " bytes");
        });
        Console.WriteLine(failures == 0 ? "PASS" : "FAILED " + failures);
        return failures == 0 ? 0 : 1;
    }
}
