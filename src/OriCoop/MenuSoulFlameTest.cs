using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    // T-179 regression check, run by the guest inside NetStoryTest's S4 (menus do not pause online there).
    // Owner's report 2026-11-21: in their soul link, the skill tree closed on a short B and reopened ~0.1 s later
    // (the closing B was also a Soul Flame "tap"); only a long hold closed it.
    // Oracle (vanilla behaviour): a short B closes the skill tree and it stays closed.
    public static class MenuSoulFlameTest
    {
        static float Now { get { return Time.realtimeSinceStartup; } }
        static IEnumerator Wait(float seconds) { float end = Now + seconds; while (Now < end) yield return null; }

        public static IEnumerator SkillTreeShortB(TestHarness h, SeinCharacter own)
        {
            SeinSoulFlame sf = own != null ? own.SoulFlame : null;
            if (sf == null || UI.Menu == null) { h.Check(false, "S4 skill tree B: own Ori has no Soul Flame ability / no menu (cannot judge)"); yield break; }
            bool spawned = false; int level0 = own.Level.Current;
            try
            {
                if (!sf.InsideCheckpointMarker) { sf.SpawnSoulFlame(own.Position); spawned = true; }
                if (own.Level.Current < 1) own.Level.Current = 1;   // AllowedToAccessSkillTree needs level > 0
                yield return Wait(0.3f);
                bool ready = sf.InsideCheckpointMarker && sf.AllowedToAccessSkillTree && !UI.Fader.IsFadingInOrStay();
                h.Note("S4 skill tree B: inside=" + sf.InsideCheckpointMarker + " allowed=" + sf.AllowedToAccessSkillTree
                    + " safety=" + sf.IsSafeToCastSoulFlame + " fading=" + UI.Fader.IsFadingInOrStay());
                h.Check(ready, "S4 skill tree B: own Ori stands in its soul link, where a B tap opens the skill tree (precondition)");
                if (!ready) yield break;

                UI.Menu.ShowSkillTree();
                yield return Wait(1.2f);   // fade in; the menu ignores input while it animates
                bool open = UI.Menu.MainMenuVisible && UI.Menu.CurrentScreen == MenuScreenManager.Screens.SkillTree;
                h.Check(open, "S4 skill tree B: the skill tree is open (" + UI.Menu.CurrentScreen + ")");
                if (!open) yield break;

                int blocked0 = MenuSoulFlameGuard.Blocked;
                NetTestHarness.BotButtons = P2Input.BTN_B;           // a short press, like a quick tap on the pad
                yield return Wait(0.12f);
                NetTestHarness.BotButtons = 0;
                bool closed = false, reopened = false; float end = Now + 1.5f;
                while (Now < end)
                {
                    bool visible = UI.Menu.MainMenuVisible;
                    if (!visible) closed = true;
                    else if (closed) reopened = true;
                    yield return null;
                }
                h.Note("S4 skill tree B: closed=" + closed + " reopened=" + reopened + " visibleNow=" + UI.Menu.MainMenuVisible
                    + " screen=" + UI.Menu.CurrentScreen + " guardBlocked=" + (MenuSoulFlameGuard.Blocked - blocked0));
                h.Check(closed && !reopened && !UI.Menu.MainMenuVisible, "S4 skill tree B: a short B closes the skill tree and it stays closed (T-179)");
            }
            finally
            {
                NetTestHarness.BotButtons = 0;
                if (UI.Menu != null && UI.Menu.MainMenuVisible) UI.Menu.HideMenuScreen(true);
                if (spawned) sf.DestroySoulFlame();
                own.Level.Current = level0;
            }
            yield return Wait(0.5f);
        }
    }
}
