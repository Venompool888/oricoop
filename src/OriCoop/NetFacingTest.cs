using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace OriCoop
{
    // Net phase MOVE, scenario FACE (T-199): a partner's facing during and after wall jumps must match its own screen.
    // The performer wall-jumps at the left wall of the wall-jump tutorial chimney with real input (stick + Jump).
    // 1) Single jumps: run into the wall, jump, slide, jump away; every second time it turns the stick back in the
    //    air. After it has landed and stands still, both PCs compare the performer's own facing (the truth) with the
    //    facing of its copy on the other PC.
    // 2) Climb: about 7 s of wall jumps without a pause (up the wall, away and back). The watcher compares, in every
    //    rendered frame, its copy's facing with the facing the performer sent for the frame being played; a
    //    difference that lasts 0.15 s or longer is one a player sees ("the character does not turn").
    // Host and guest take turns. Signals use NetPlay's test string message ("face|step|data").
    // ORICOOP_MOVE_ONLY=FACE with ORICOOP_PHASES=MOVE runs only this.
    public static class NetFacingTest
    {
        const string SceneName = "sunkenGladesSpiritCavernWalljumpB";
        const int Rounds = 4;
        const float Visible = 0.15f;
        // The ledge at the foot of the chimney's left wall. From the playtest 4 trace: an Ori stands at
        // (-199.55,-281.14) and slides down the wall at x = -199.8; right of x = -197 the floor drops into a pit.
        static readonly Vector3 Ledge = new Vector3(-198.9f, -280.9f, 0f);
        static readonly Dictionary<string, string> s_got = new Dictionary<string, string>();

        static SeinCharacter Own { get { return P2Context.P1; } }
        static SeinCharacter Puppet { get { return P2Context.P2; } }
        static string V(Vector3 v) { return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + ")"; }

        static void OnSignal(int sender, string text)
        {
            if (text == null || !text.StartsWith("face|", StringComparison.Ordinal)) return;
            string[] parts = text.Split('|');
            if (parts.Length >= 3) s_got[parts[1]] = parts[2];
        }

        static void KeepAlive()
        {
            if (Own == null || Revive.IsDowned(Own)) return;
            Own.Mortality.DamageReciever.MakeInvincibleToEnemies(0.25f);
            if (Own.Mortality.Health.Amount < Own.Mortality.Health.MaxHealth) Own.Mortality.Health.RestoreAllHealth();
        }

        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) { KeepAlive(); yield return null; }
        }

        static IEnumerator Steps(int fixedSteps)
        {
            float until = Time.fixedTime + fixedSteps * Time.fixedDeltaTime, cap = Time.realtimeSinceStartup + 2f;
            while (Time.fixedTime < until && Time.realtimeSinceStartup < cap) { KeepAlive(); yield return null; }
        }

        static void Bot(float x, int buttons) { NetTestHarness.BotLX = x; NetTestHarness.BotButtons = buttons; }

        // Send our data for this step and wait for the partner's. false = the partner never answered.
        static IEnumerator Sync(TestHarness h, string step, string data, bool[] ok)
        {
            NetPlay.SendTestSignal("face|" + step + "|" + data);
            float end = Time.realtimeSinceStartup + 30f;
            while (NetPlay.Connected && !s_got.ContainsKey(step) && Time.realtimeSinceStartup < end) { KeepAlive(); yield return null; }
            ok[0] = s_got.ContainsKey(step);
            if (!ok[0]) h.Check(false, "FACE step " + step + " reached by both");
        }

        static void ToLedge(bool host)
        {
            Plugin.Teleport(Own, new Vector3(Ledge.x + (host ? 0f : 0.5f), Ledge.y, Own.Position.z));
            SessionRules.PositionChanged();
        }

        // One wall jump with real input. onWall[0]: the Ori reached the wall slide.
        static IEnumerator WallJump(bool turnBack, bool[] onWall)
        {
            PlatformMovement pm = Own.PlatformBehaviour.PlatformMovement;
            Bot(-1f, 0);                                  // run into the left wall
            yield return Steps(10);
            Bot(-1f, P2Input.BTN_A);                      // jump, still pressing into the wall
            float until = Time.realtimeSinceStartup + 1.2f;
            while (Time.realtimeSinceStartup < until)
            {
                KeepAlive();
                if (!pm.IsOnGround && pm.HasWallLeft && Own.Abilities.WallSlide.IsOnWall) { onWall[0] = true; break; }
                yield return null;
            }
            Bot(-1f, 0);                                  // let go of Jump on the wall
            yield return Steps(3);
            if (!onWall[0]) { Bot(0f, 0); yield break; }
            Bot(1f, P2Input.BTN_A);                       // wall jump away: stick right + Jump
            yield return Steps(4);
            Bot(1f, 0);
            yield return Steps(8);
            if (turnBack) { Bot(-1f, 0); yield return Steps(10); }   // turn the stick back in the air
            Bot(0f, 0);
            until = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < until && !pm.IsOnGround) { KeepAlive(); yield return null; }
            yield return Wait(0.6f);                      // standing still: the facing no longer changes
        }

        // About 7 s of wall jumps: twice up the wall (stick into it), then away and back to the wall, six times over.
        static IEnumerator Climb()
        {
            Bot(-1f, 0);
            yield return Steps(10);
            for (int i = 0; i < 6; i++)
            {
                for (int k = 0; k < 2; k++) { Bot(-1f, P2Input.BTN_A); yield return Steps(5); Bot(-1f, 0); yield return Steps(16); }
                Bot(1f, P2Input.BTN_A); yield return Steps(5);
                Bot(1f, 0); yield return Steps(7);
                Bot(-1f, 0); yield return Steps(20);
            }
            Bot(0f, 0);
        }

        // Watch the partner's copy for some seconds. seen: [0] frames judged, [1] frames whose facing differed from the
        // owner's, [2] the longest difference in seconds, [3] differences of Visible seconds or longer.
        static IEnumerator Watch(float seconds, float[] seen)
        {
            WaitForEndOfFrame rendered = new WaitForEndOfFrame();
            float end = Time.realtimeSinceStartup + seconds, since = -1f; bool counted = false;
            while (Time.realtimeSinceStartup < end)
            {
                yield return rendered;
                KeepAlive();
                bool left;
                if (Puppet == null || !NetPlay.PartnerFacing(out left)) continue;
                seen[0]++;
                if (Puppet.FaceLeft == left) { since = -1f; counted = false; continue; }
                seen[1]++;
                if (since < 0f) since = Time.realtimeSinceStartup;
                float lasted = Time.realtimeSinceStartup - since;
                if (lasted > seen[2]) seen[2] = lasted;
                if (!counted && lasted >= Visible) { seen[3]++; counted = true; }
            }
        }

        public static IEnumerator Run(TestHarness h)
        {
            bool host = NetPlay.IsHost;
            h.Note("--- FACE: partner facing after wall jumps (" + (host ? "host" : "guest") + ")");
            if (Own == null || Puppet == null) { h.Check(false, "FACE needs both Oris"); yield break; }
            s_got.Clear();
            NetPlay.TestSignalReceived += OnSignal;
            Vector3 back = Own.Position;
            bool hadAbility = Own.PlayerAbilities.WallJump.HasAbility;
            int ownJumps = 0, puppetJumps = 0;
            Action<Vector2> countOwn = delegate { ownJumps++; }, countPuppet = delegate { puppetJumps++; };
            SeinWallJump ownWallJump = Own.Abilities.WallJump, puppetWallJump = Puppet.Abilities.WallJump;
            try
            {
                RuntimeSceneMetaData scene = Core.Scenes.Manager.GetSceneInformation(SceneName);
                h.Check(scene != null, "FACE wall-jump tutorial scene found");
                if (scene == null) yield break;
                Plugin.Teleport(Own, new Vector3(scene.PlaceholderPosition.x, scene.PlaceholderPosition.y, Own.Position.z));
                SessionRules.PositionChanged();
                float deadline = Time.realtimeSinceStartup + 25f;
                while (Time.realtimeSinceStartup < deadline && !Core.Scenes.Manager.IsInsideActiveSceneBoundary(Ledge)) { KeepAlive(); yield return null; }
                yield return Wait(0.5f);
                ToLedge(host);
                yield return Wait(1.2f);
                bool ready = Own != null && Own.PlatformBehaviour.PlatformMovement.IsOnGround && Vector3.Distance(Own.Position, Ledge) < 2.5f;
                bool[] ok = new bool[1];
                yield return Sync(h, "ready", ready ? "1" : "0", ok);
                if (!ok[0]) yield break;
                h.Check(ready, "FACE own Ori stands on the ledge at the chimney's left wall (Ori=" + V(Own.Position) + ")");
                if (!ready || s_got["ready"] != "1") yield break;
                if (!hadAbility) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, true);
                if (ownWallJump != null) ownWallJump.OnWallJumpEvent += countOwn;
                if (puppetWallJump != null) puppetWallJump.OnWallJumpEvent += countPuppet;
                yield return Wait(1.0f);
                NetPlay.SetSim(60, 15, 0, 0);   // about the playtest's link: 120 ms RTT, some jitter

                int performed = 0, judged = 0, differ = 0, fixes0 = NetPlay.FacingFixes;
                string differing = "";
                for (int round = 0; round < Rounds; round++)
                {
                    bool performer = (round % 2 == 0) == host, turnBack = round / 2 % 2 == 1;
                    string step = "r" + round;
                    Bot(0f, 0);
                    ToLedge(host);
                    yield return Wait(0.8f);
                    yield return Sync(h, step + "a", "", ok);
                    if (!ok[0]) yield break;
                    string mine = "";
                    if (performer)
                    {
                        int before = ownJumps; bool[] onWall = new bool[1];
                        yield return WallJump(turnBack, onWall);
                        bool jumped = ownJumps > before;
                        if (jumped) performed++;
                        else h.Note("FACE round " + round + ": no wall jump (reached the wall slide=" + onWall[0] + ", Ori=" + V(Own.Position) + ")");
                        mine = (jumped ? "1" : "0") + (Own.FaceLeft ? "L" : "R");
                    }
                    yield return Sync(h, step + "b", mine, ok);
                    if (!ok[0]) yield break;
                    if (!performer)
                    {
                        string truth = s_got[step + "b"];
                        yield return Wait(0.5f);   // the copy has played everything up to the partner's signal
                        if (truth.Length == 2 && truth[0] == '1')
                        {
                            bool left = truth[1] == 'L';
                            judged++;
                            if (Puppet.FaceLeft != left)
                            {
                                differ++;
                                differing += " r" + round + (turnBack ? "(turn-back)" : "") + ":own=" + truth[1] + ",here=" + (Puppet.FaceLeft ? "L" : "R");
                            }
                        }
                    }
                    yield return Sync(h, step + "c", "", ok);   // the performer stood still until the watcher looked
                    if (!ok[0]) yield break;
                }
                Bot(0f, 0);
                h.Check(performed >= 1, "FACE own Ori wall-jumped with stick + Jump (" + performed + " of " + (Rounds / 2) + " tries)");
                if (judged < 1) h.Check(false, "FACE cannot judge: the partner wall-jumped only " + judged + " times");
                else h.Check(differ == 0, "FACE after each wall jump the partner faces the same way here as on its own PC (" + differ + " of " + judged
                    + " differ:" + (differing.Length == 0 ? " none" : differing) + "; its copy wall-jumped " + puppetJumps + " times here, facing set from the network "
                    + (NetPlay.FacingFixes - fixes0) + " times)");

                // Part 2: the climb. First the host climbs and the guest watches, then the other way round.
                for (int turn = 0; turn < 2; turn++)
                {
                    bool climber = (turn == 0) == host; string step = "c" + turn;
                    Bot(0f, 0);
                    ToLedge(host);
                    yield return Wait(0.8f);
                    yield return Sync(h, step + "a", "", ok);
                    if (!ok[0]) yield break;
                    float[] seen = new float[4]; int before = ownJumps, copyBefore = puppetJumps, fixesBefore = NetPlay.FacingFixes;
                    if (climber) yield return Climb();
                    else yield return Watch(8.5f, seen);
                    yield return Wait(climber ? 1.5f : 0.3f);
                    yield return Sync(h, step + "b", climber ? (ownJumps - before).ToString() : "", ok);
                    if (!ok[0]) yield break;
                    if (climber) h.Check(ownJumps - before >= 4, "FACE own Ori kept wall-jumping in the climb (" + (ownJumps - before) + " wall jumps)");
                    else
                    {
                        int theirs; int.TryParse(s_got[step + "b"], out theirs);
                        string numbers = "longest difference " + seen[2].ToString("F2") + " s, " + seen[1].ToString("F0") + " of " + seen[0].ToString("F0")
                            + " frames differ; the partner wall-jumped " + theirs + " times, its copy " + (puppetJumps - copyBefore) + " times here; facing set from the network "
                            + (NetPlay.FacingFixes - fixesBefore) + " times";
                        if (theirs < 4 || seen[0] < 60f) h.Check(false, "FACE cannot judge the climb (" + numbers + ")");
                        else h.Check(seen[3] == 0f, "FACE while the partner wall-jumps, its copy never faces the other way for " + Visible.ToString("F2")
                            + " s or longer (" + seen[3].ToString("F0") + " times; " + numbers + ")");
                    }
                }
            }
            finally
            {
                NetPlay.TestSignalReceived -= OnSignal;
                Bot(0f, 0); NetPlay.SetSim(0, 0, 0, 0);
                if (ownWallJump != null) ownWallJump.OnWallJumpEvent -= countOwn;
                if (puppetWallJump != null) puppetWallJump.OnWallJumpEvent -= countPuppet;
                if (Own != null && !hadAbility) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, false);
            }
            // Back to where this phase found us, for the phases after it.
            if (Own != null) { Plugin.Teleport(Own, back - new Vector3(0f, 0.5f, 0f)); SessionRules.PositionChanged(); }
            yield return Wait(1.5f);
        }
    }
}
