using Game;
using HarmonyLib;

namespace OriCoop
{
    // T-179: B is both "Cancel" (close a menu) and "Soul Flame". Vanilla pauses the whole game while a menu is open,
    // so SeinSoulFlame never sees the B that closes it. Online nothing pauses (T-044, SessionRules.MenuSuspend): the
    // closing press also started a Soul Flame cast, and inside the soul link its quick release counts as the "tap"
    // that opens the skill tree (SeinSoulFlame.UpdateCharacterState: casting starts on SoulFlame.OnPressed when
    // SoulFlame/Cancel are not Used; on release within 0.3 s it calls UI.Menu.ShowSkillTree). The tree closed and
    // reopened ~0.1 s later; only a hold longer than the 0.3 s tap window closed it.
    // Fix: while a menu is open, and after it closes until B is released, the game's Soul Flame button counts as
    // used, exactly as if the game had been paused. Runs after PlayerInput (and the net test bot) filled Core.Input;
    // Used stays set until the next PlayerInput.FixedUpdate clears it, so the order of other FixedUpdates is irrelevant.
    public static class MenuSoulFlameGuard
    {
        public static bool Latched { get; private set; }
        public static int Blocked;   // fixed steps in which a held / pressed B was kept away from Soul Flame (tests)
        // Test-only mutation switch: proves the S4 skill tree check fails without this guard.
        static readonly bool s_mutate = System.Environment.GetEnvironmentVariable("ORICOOP_MUTATE_MENU_SOULFLAME") == "1";

        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class AfterInput
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix() { Step(UI.MainMenuVisible, Core.Input.SoulFlame); }
        }

        // menuOpen: a native menu (pause, inventory, map, skill tree) is on screen this step.
        public static void Step(bool menuOpen, Core.Input.InputButtonProcessor soulFlame)
        {
            if (s_mutate || soulFlame == null) { Latched = false; return; }
            if (menuOpen) Latched = true;
            else if (!soulFlame.IsPressed) Latched = false;
            if (!Latched) return;
            if (soulFlame.IsPressed) Blocked++;
            soulFlame.Used = true;
        }
    }
}
