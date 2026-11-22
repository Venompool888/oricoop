using OriCoop.Story;

static partial class StoryTests
{
    // T-183: on the mirror PC, the mirror's start of 03 / 04 stands in for one firing of the string trigger that would
    // start it there (the mirrored Kuro onFinish / world-map sequence registered the string after the mirror started the
    // sequence). Expected values from the ticket: the leftover firing is consumed once; a sequence the mirror never
    // started, a second firing, or a firing long after (a new story) runs normally.
    static void MirrorStartsCases()
    {
        Check("mirror starts: KURO=1 guest — 03 mirrored at 122 s, its leftover trigger fires back from the world map at 139 s: consumed", delegate {
            MirrorStarts m = new MirrorStarts();
            m.MirrorPerformed(3, 122.15f);
            Equal(true, m.SkipTrigger(3, 138.90f));
            Equal(1, m.Skipped);
        });
        Check("mirror starts: one mirror start consumes one firing only", delegate {
            MirrorStarts m = new MirrorStarts();
            m.MirrorPerformed(3, 10f);
            Equal(true, m.SkipTrigger(3, 11f));
            Equal(false, m.SkipTrigger(3, 12f));
        });
        Check("mirror starts: a sequence the mirror never started runs (the host's own chain, another sequence)", delegate {
            MirrorStarts m = new MirrorStarts();
            Equal(false, m.SkipTrigger(4, 5f));
            m.MirrorPerformed(3, 5f);
            Equal(false, m.SkipTrigger(4, 6f));
        });
        Check("mirror starts: window boundary — 300 s consumed, later runs (a new story)", delegate {
            MirrorStarts m = new MirrorStarts();
            m.MirrorPerformed(3, 100f);
            Equal(true, m.SkipTrigger(3, 400f));
            m.MirrorPerformed(3, 100f);
            Equal(false, m.SkipTrigger(3, 400.5f));
        });
        Check("mirror starts: a partner's second start is mirrored again and consumes again", delegate {
            MirrorStarts m = new MirrorStarts();
            m.MirrorPerformed(3, 10f); Equal(true, m.SkipTrigger(3, 20f));
            m.MirrorPerformed(3, 30f); Equal(true, m.SkipTrigger(3, 40f));
            m.Clear(); Equal(false, m.SkipTrigger(3, 41f));
        });
        Check("overtaken: chain B-4 — mirrored 03 still before its Register spiritTreeWorldMap when the world-map sequence (that string) is mirrored", delegate {
            Equal(true, MirrorStarts.Overtaken(new[] { "spiritTreeWorldMap" }, new[] { "spiritTreeWorldMap" }));
        });
        Check("overtaken: a running mirror that registers something else, or nothing, is left alone", delegate {
            Equal(false, MirrorStarts.Overtaken(new[] { "returnFromWorldMap" }, new[] { "spiritTreeWorldMap" }));
            Equal(false, MirrorStarts.Overtaken(new string[0], new[] { "spiritTreeWorldMap" }));
            Equal(false, MirrorStarts.Overtaken(new[] { "" }, new[] { "" }));
        });
        Check("overtaken: a started sequence no string trigger starts never retires anything", delegate {
            Equal(false, MirrorStarts.Overtaken(new[] { "spiritTreeWorldMap" }, new string[0]));
            Equal(false, MirrorStarts.Overtaken(new[] { "spiritTreeWorldMap" }, null));
        });
    }
}
