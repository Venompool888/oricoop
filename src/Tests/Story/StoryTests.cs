using System;
using OriCoop.Story;

static partial class StoryTests
{
    static int failures;
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Equal<T>(T expected, T actual)
    { if (!object.Equals(expected, actual)) throw new Exception("expected " + expected + ", got " + actual); }

    static int Main()
    {
        Check("shared initial copy", delegate {
            SharedResources r = new SharedResources();
            SharedValues a = r.Merge(3, 4, 2f, 5f, 9, 9, 9f);
            Equal(3, a.Keys); Equal(4, a.Maps); Equal(2f, a.Energy);
        });
        IntroCases();
        StoryCases();
        ProgressCases();
        ObjectCases();
        GateCases();
        AnimCases();
        SkillCases();
        return failures == 0 ? 0 : 1;
    }
}
