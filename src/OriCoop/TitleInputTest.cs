using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Playtest 2026-11-16, P5: after an online session the title could not be used until alt-tab. Extra phase of the
    // real two-client title test (src\run-titlejoin.ps1 with $env:ORICOOP_TITLEJOIN_LEAVE set):
    //   host   the host quits from its pause menu (the playtest path); the guest sees the drop, waits through the
    //          reconnect tries, then quits from its own pause menu;
    //   guest  the guest quits from its pause menu; the host stays in game, then quits too;
    //   drop   the host quits the game outright; the guest picks 回到标题 in the disconnect dialog;
    //   alone  the host quits; the guest picks 继续单人, then quits from its pause menu.
    // Each client that reaches the title checks that "press any key" and the title menu react to input at once.
    public static partial class NativeModeMenu
    {
        public static int FakeMenuDownSteps;

        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class FakeMenuDown
        {
            static void Postfix()
            {
                if (FakeMenuDownSteps <= 0) return;
                FakeMenuDownSteps--;
                if (FakeMenuDownSteps == 0) Core.Input.MenuDown.Update(true);   // one pressed step after released ones
            }
        }

        static readonly FieldInfo s_suspendedCounts = AccessTools.Field(typeof(SuspensionManager), "s_suspended");
        static int SuspendCount(ISuspendable s)
        {
            if (s == null || s_suspendedCounts == null) return -1;
            Dictionary<ISuspendable, int> d = s_suspendedCounts.GetValue(null) as Dictionary<ISuspendable, int>;
            int n; return d != null && d.TryGetValue(s, out n) ? n : -1;
        }

        public static string TitleInputDiag()
        {
            CleverMenuItemSelectionManager m = s_tMain;
            return "screen=" + TitleScreenManager.CurrentScreen + " focused=" + GameController.IsFocused
                + " runInBg=" + Application.runInBackground + " timeScale=" + Time.timeScale
                + (m == null ? " main=null" : " main.susp=" + m.IsSuspended + "/" + SuspendCount(m) + " active=" + m.IsActive + " locked=" + m.IsLocked
                    + " visible=" + m.IsVisible + " idx=" + m.Index)
                + " inputLocked=" + (GameController.Instance != null && GameController.Instance.InputLocked)
                + " lockInput=" + (GameController.Instance != null && GameController.Instance.LockInput)
                + " menuVisible=" + UI.MainMenuVisible + " gameSusp=" + ResumeGameController.IsGameSuspended
                + " modal=" + NetHud.ModalOpen + " net=" + NetPlay.Enabled + " orb=" + IsOpen;
        }

        partial class TitleJoinTest
        {
            static string LeaveMode { get { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_LEAVE") ?? ""; } }

            IEnumerator Leave()
            {
                string mode = LeaveMode; bool host = m_role == "host";
                Note("--- leave: " + mode + " (" + m_role + ")");
                if (host)
                {
                    if (mode == "guest")
                    {
                        yield return StartCoroutine(WaitFor(() => !NetPlay.PartnerHere, 40f));
                        yield return StartCoroutine(TWait(2f));
                        Check(!NetPlay.PartnerHere && NetPlay.LocalInGameplay(), "leave guest: host stays in game after the guest left");
                        yield return StartCoroutine(QuitFromPause());
                        yield return StartCoroutine(CheckTitleInput("host after the guest left"));
                    }
                    else if (mode == "drop")
                    {
                        Note("leave drop: host quits the game without leaving the session");
                        yield return StartCoroutine(TWait(0.5f));
                        Finish(); yield break;
                    }
                    else
                    {
                        yield return StartCoroutine(QuitFromPause());
                        yield return StartCoroutine(CheckTitleInput("host quit"));
                    }
                }
                else
                {
                    if (mode == "guest")
                    {
                        yield return StartCoroutine(TWait(2f));
                        yield return StartCoroutine(QuitFromPause());
                        yield return StartCoroutine(CheckTitleInput("guest quit"));
                        yield break;
                    }
                    yield return StartCoroutine(WaitFor(() => !NetPlay.Connected, 40f));
                    yield return StartCoroutine(WaitFor(() => NetHud.ModalOpen, 10f));
                    Check(NetHud.ModalOpen, "leave " + mode + ": guest shows the disconnect dialog (net=" + NetPlay.CurrentPhase + ")");
                    Shot("leave_dialog");
                    yield return StartCoroutine(TWait(6f));                       // reconnect tries run meanwhile
                    if (m_tr == "steam") Check(NetHud.SteamHostLeft && NetPlay.RetryHeld, "steam: host departure is shown and reconnects have stopped");
                    MethodInfo pick = AccessTools.Method(typeof(NetHud), "ActivateModalOption");
                    if (mode == "drop")
                    {
                        pick.Invoke(null, new object[] { 1 });                    // 回到标题
                        yield return StartCoroutine(CheckTitleInput("guest picked 回到标题"));
                        yield break;
                    }
                    if (mode == "alone")
                    {
                        pick.Invoke(null, new object[] { 0 }); // 继续单人
                        yield return StartCoroutine(TWait(2f));
                        Check(!NetHud.ModalOpen && !NetPlay.Connected && (m_tr != "steam" || !NetPlay.Enabled) && NetPlay.LocalInGameplay(), "guest continued playing alone after the host left");
                    }
                    yield return StartCoroutine(QuitFromPause());
                    yield return StartCoroutine(CheckTitleInput("guest quit after the host left (" + mode + ")"));
                }
            }

            // ---- online guide stone (ORICOOP_TITLEJOIN_SIGN=1, before the leave phase) -------------------------
            static bool SignMode { get { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_SIGN") == "1"; } }

            string Flag(string name, string role) { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(m_out)), "titlejoin-" + name + "-" + role + ".flag"); }
            IEnumerator Barrier(string name)
            {
                try { System.IO.File.WriteAllText(Flag(name, m_role), "1"); } catch (Exception e) { Note("barrier " + name + ": " + e.Message); }
                string other = Flag(name, m_role == "host" ? "join" : "host");
                yield return StartCoroutine(WaitFor(() => System.IO.File.Exists(other), 90f));
                if (!System.IO.File.Exists(other)) Note("barrier " + name + ": partner never arrived");
            }

            IEnumerator HoldOwn(Vector3 pos, float s)
            {
                float end = Time.realtimeSinceStartup + s;
                while (Time.realtimeSinceStartup < end)
                {
                    SeinCharacter me = Characters.Sein;
                    if (me != null) { me.Position = pos; me.Speed = Vector3.zero; }
                    yield return new WaitForFixedUpdate();
                }
            }

            IEnumerator SignOnline()
            {
                Note("--- online guide stone");
                Vector3 side = m_role == "host" ? Vector3.zero : Vector3.right;
                yield return StartCoroutine(Barrier("sign0"));
                if (Characters.Sein != null) Plugin.Teleport(Characters.Sein, SignpostTest.AwayPos + side);
                yield return StartCoroutine(WaitFor(() => SignpostTest.Found, 20f));
                yield return StartCoroutine(HoldOwn(SignpostTest.AwayPos + side, 3f));
                Check(SignpostTest.Found && SignpostTest.Hidden, "sign: guide stone loaded, hidden while nobody is near (" + SignpostTest.Diag() + ")");
                Note("sign: actions " + SignpostTest.Actions());
                // T-024: ANY player at the stone shows the text on EVERY screen (server mode showed it only for P1).
                foreach (string who in new[] { "host", "join" })
                {
                    yield return StartCoroutine(Barrier("sign-" + who + "-near"));
                    yield return StartCoroutine(HoldOwn(m_role == who ? SignpostTest.StonePos : SignpostTest.AwayPos + side, 4f));
                    Check(SignpostTest.Shown, "sign: text shows on the " + m_role + "'s screen with only the " + who + " at the stone (" + SignpostTest.Diag() + ")");
                    if (!SignpostTest.Shown)
                    {
                        SignpostTest.PerformEnter();
                        yield return StartCoroutine(HoldOwn(m_role == who ? SignpostTest.StonePos : SignpostTest.AwayPos + side, 1f));
                        Note("sign: experiment: enter action run directly -> " + SignpostTest.Diag());
                    }
                    yield return StartCoroutine(Barrier("sign-" + who + "-away"));
                    yield return StartCoroutine(HoldOwn(SignpostTest.AwayPos + side, 4f));
                    Check(SignpostTest.Hidden, "sign: text hides on the " + m_role + "'s screen once the " + who + " left (" + SignpostTest.Diag() + ")");
                }
                yield return StartCoroutine(Barrier("sign1"));
                yield return StartCoroutine(HoldOwn(SignpostTest.StonePos, 4f));
                Check(SignpostTest.Shown, "sign: text shows with both at the stone (" + SignpostTest.Diag() + ")");
                Shot("sign_near");
                // The partner's puppet loses its collider inside the zone (as when it dies, revives or is hidden),
                // then both walk away: the text must still go away on this screen.
                Puppet partner = Players.First;
                List<Collider> off = new List<Collider>();
                if (partner != null && partner.Sein != null)
                    foreach (Collider c in partner.Sein.GetComponentsInChildren<Collider>()) if (c.enabled && c.CompareTag("Player")) { c.enabled = false; off.Add(c); }
                string before = SignpostTest.Diag();
                yield return StartCoroutine(Barrier("sign2"));
                yield return StartCoroutine(HoldOwn(SignpostTest.AwayPos + side, 4f));
                Check(off.Count > 0 && SignpostTest.Hidden, "sign: text hides after both walk away, the partner's collider gone inside (" + off.Count + "; before: " + before + "; now: " + SignpostTest.Diag() + ")");
                Shot("sign_away");
                foreach (Collider c in off) if (c != null) c.enabled = true;
                yield return StartCoroutine(Barrier("sign3"));
            }

            static IEnumerator WaitFor(Func<bool> cond, float timeout)
            {
                float end = Time.realtimeSinceStartup + timeout;
                while (Time.realtimeSinceStartup < end && !cond()) yield return null;
            }

            // Pause menu, then "quit to the title" (GameController.RestartGame), as a player does.
            IEnumerator QuitFromPause()
            {
                UI.Menu.ShowMenuScreen(true);
                yield return StartCoroutine(TWait(1.5f));
                Note("quit from the pause menu (" + TitleInputDiag() + ")");
                GameController.Instance.RestartGame();
            }

            IEnumerator CheckTitleInput(string what)
            {
                float end = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < end && !(Characters.Sein == null
                    && (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart || TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu)))
                    yield return null;
                yield return StartCoroutine(TWait(3f));
                Shot("title_" + LeaveMode);
                Note("title reached: " + TitleInputDiag());
                Check(!NetPlay.Enabled, what + ": the online session ended at the title");
                if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart)
                {
                    TestHarness.FakeAnyKeySteps = 3;
                    yield return StartCoroutine(WaitFor(() => TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.PressStart, 5f));
                    Check(TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.PressStart, what + ": \"press any key\" reacts (" + TitleInputDiag() + ")");
                }
                yield return StartCoroutine(WaitFor(() => TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu && TitleItemsReady && s_tMain.IsVisible, 15f));
                yield return StartCoroutine(TWait(1.5f));
                int idx0 = s_tMain != null ? s_tMain.Index : -1;
                FakeMenuDownSteps = 3;
                yield return StartCoroutine(WaitFor(() => s_tMain != null && s_tMain.Index != idx0, 3f));
                Check(s_tMain != null && s_tMain.Index != idx0, what + ": the title menu moves on a Down press at once (from " + idx0 + "; " + TitleInputDiag() + ")");
                // The guest's disconnect dialog must not survive on the title: invisible there, it still took
                // Down + A (or Up/Down + Enter) as "回到标题" and restarted the game under the player.
                Check(!NetHud.ModalOpen, what + ": no disconnect dialog left on the title (" + TitleInputDiag() + ")");
                int restarts0 = Restarts;
                const int DDOWN = 0x0002, PAD_A = 0x1000;
                CoopMenu.TestPadButtons = new int[4];
                yield return StartCoroutine(TWait(0.3f));
                CoopMenu.TestPadButtons[0] = DDOWN; yield return StartCoroutine(TWait(0.3f));
                CoopMenu.TestPadButtons[0] = 0; yield return StartCoroutine(TWait(0.3f));
                CoopMenu.TestPadButtons[0] = PAD_A; yield return StartCoroutine(TWait(0.3f));
                CoopMenu.TestPadButtons[0] = 0; yield return StartCoroutine(TWait(2f));
                CoopMenu.TestPadButtons = null;
                Check(Restarts == restarts0 && Characters.Sein == null, what + ": pad Down + A on the title doesn't restart the game ("
                    + (Restarts - restarts0) + " restart(s); " + TitleInputDiag() + ")");
            }

            static int Restarts;
            [HarmonyPatch(typeof(GameController), "RestartGame")]
            static class CountRestarts { static void Prefix() { Restarts++; } }
        }
    }
}
