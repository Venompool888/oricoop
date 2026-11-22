using System;
using OriCoop.Story;

static partial class StoryTests
{
    // T-175: mirror cleanup undoes only what the mirror added. Expected values come from the ticket: a takeover flag
    // (letterbox on, animation playing, a lock) is put back only when a story of this PC owned it at mirror start.
    static string Show(bool? v) { return v.HasValue ? v.Value.ToString() : "leave"; }
    static void MirrorBaselineCases()
    {
        Check("mirror baseline: playtest — host's leftover Kuro letterbox is not put back after the guest's 03", delegate {
            // Captured on (left by the host's own spirit-tree story), no story of the host running then or now.
            Equal("False", Show(MirrorBaseline.Restore(true, false, false)));
        });
        Check("mirror baseline: a flag the mirror added is removed", delegate {
            Equal("False", Show(MirrorBaseline.Restore(false, false, false)));
            Equal("False", Show(MirrorBaseline.Restore(false, true, false)));
            Equal("False", Show(MirrorBaseline.Restore(false, true, true)));
        });
        Check("mirror baseline: our own running story's flag is kept", delegate {
            Equal("True", Show(MirrorBaseline.Restore(true, true, true)));
            Equal("True", Show(MirrorBaseline.Restore(true, true, false)));   // unchanged from before T-175
        });
        Check("mirror baseline: a story of ours that started during the mirror owns the flag now", delegate {
            Equal("leave", Show(MirrorBaseline.Restore(false, false, true)));
            Equal("leave", Show(MirrorBaseline.Restore(true, false, true)));
        });
    }
}
