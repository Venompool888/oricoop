using Game;
using UnityEngine;

namespace OriCoop
{
    // Single player or 2 players, chosen each time a save is entered (the mode-select screen in CoopMenu).
    //   Single:  no P2, no join by controller button; P1 gets keyboard/mouse AND the controller.
    //   Coop:    P2 joins (from the 2-player setup screen, or later with a button / F9); P1 keeps its own input.
    // Back to Undecided whenever the game leaves gameplay (title screen, One Life restart, ending).
    public static class PlayMode
    {
        public const int Undecided = 0, Single = 1, Coop = 2;
        public static int Current;
        public static bool AskOnLoad = true;   // false: always co-op without asking (old behaviour)
        public static int LastChoice = Coop;   // default focus on the mode-select screen
        static float s_readyFor;

        public static string Name(int m) { return m == Single ? "single player" : m == Coop ? "2 players" : "undecided"; }

        public static void SetMode(int m)
        {
            if (Current == m) return;
            Current = m;
            if (m != Undecided) LastChoice = m;
            Plugin.Log.LogInfo("play mode: " + Name(m));
        }

        public static void Reset() { if (Current != Undecided) { Current = Undecided; Plugin.Log.LogInfo("play mode: undecided (left gameplay)"); } s_readyFor = 0f; }

        // Called every frame from Plugin.Tick: once gameplay is really running after a load, ask.
        public static void Tick()
        {
            if (Current != Undecided) return;
            // Online: this PC has one player, who gets keyboard/mouse and the controller (like single player).
            if (NetPlay.Enabled) { NativeModeMenu.Pending = Undecided; SetMode(Single); return; }
            if (TestHarness.Enabled || !AskOnLoad) { NativeModeMenu.Pending = Undecided; SetMode(Coop); return; }
            // Already chosen in the game-style menu on the save-slot screen (NativeModeMenu).
            if (NativeModeMenu.Pending == Single) { NativeModeMenu.Pending = Undecided; SetMode(Single); return; }
            if (CoopMenu.IsOpen || !CanAskNow()) { s_readyFor = 0f; return; }
            s_readyFor += Time.unscaledDeltaTime;
            if (s_readyFor < 0.5f) return;
            s_readyFor = 0f;
            if (NativeModeMenu.Pending == Coop) { NativeModeMenu.Pending = Undecided; CoopMenu.OpenSetup(); return; }
            CoopMenu.OpenModeSelect();
        }

        static bool CanAskNow()
        {
            SeinCharacter s = Characters.Sein;
            if (s == null || !s.gameObject.activeInHierarchy) return false;
            if (UI.MainMenuVisible) return false;
            if (GameStateMachine.Instance != null && GameStateMachine.Instance.IsInExtendedTitleScreen()) return false;
            GameController gc = GameController.Instance;
            if (gc == null) return false;
            if (gc.GameplaySuspended || gc.GameplaySuspendedForUI || gc.InputLocked || gc.IsLoadingGame) return false;
            if (Core.Scenes.Manager != null && Core.Scenes.Manager.IsLoadingScenes) return false;
            return true;
        }
    }
}
