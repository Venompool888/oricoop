using System;
using System.Collections;
using System.Collections.Generic;
using Game;
using UnityEngine;
using OriCoop.Authority;

namespace OriCoop
{
    // T-208: leftovers of enemy sync (checks "E5k"; the numbers are the ticket's items). Two real clients, on the
    // Swarm family the Swarm scenario used first. Conditions a two-player session cannot produce by playing (an
    // ownership change, a lost handoff, a scene that unloads under live pieces, a game started anew in a seat, the
    // end of a session) are arranged with EnemySync's Test* hooks; what is checked is the enemy on both PCs.
    // Each check has a switch that brings the old behaviour back and must fail it (phase-map.psd1).
    public static partial class EnemySyncTest
    {
        static Dictionary<int, PieceSample> FamilyNow(string key) { return ParseSwarm(SwarmReport(key)); }
        static string Piece(Dictionary<int, PieceSample> family, int node) { PieceSample p; return family.TryGetValue(node, out p) ? p.Key : ""; }
        static bool Alive(Dictionary<int, PieceSample> family, int node) { PieceSample p; return family.TryGetValue(node, out p) && p.Alive; }

        static IEnumerator Leftovers(TestHarness h)
        {
            const int step = 30; string tag = s_tag + "k"; Packet chosen = new Packet(); Vector3 saved = Own.Position;
            bool immortal = Own.Mortality.DamageReciever.IsImmortal; Own.Mortality.DamageReciever.IsImmortal = true;
            GameObject blast = Own.Level.OnLevelUpGameObject; Own.Level.OnLevelUpGameObject = null;   // as in Swarm: no level-up blast in here
            try
            {
                string key = "";
                if (NetPlay.IsHost && Core.Scenes.Manager != null)
                    foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                    {
                        if (key != "" || Array.IndexOf(SwarmScenes, scene.Scene) < 0) continue;
                        Vector3 point = scene.PlaceholderPosition; float until = Now + 10f;
                        while (key == "" && Now < until)
                        {
                            EnemySync.LoadTestArea(point); Plugin.Teleport(Own, point);
                            yield return Wait(0.5f);
                            key = EnemySync.FindTestSwarm(point, null);
                        }
                    }
                yield return Barrier(h, step, key);
                if (!Has(step)) yield break;
                chosen.Key = NetPlay.IsHost ? key : Part[step].Key;
                h.Check(!string.IsNullOrEmpty(chosen.Key), tag + " a Swarm family is there for the leftover checks");
                if (string.IsNullOrEmpty(chosen.Key)) yield break;
                chosen.Origin = NetPlay.IsHost ? EnemySync.Inspect(chosen.Key).Origin : Part[step].Origin;
                float side = NetPlay.IsHost ? -6f : 6f;
                Place(chosen.Origin, side);
                yield return FamilyPresence(chosen);   // its owner brings the first one back if the Swarm scenario killed it
                yield return Ready(h, chosen, step + 1, side);
                if (!chosen.Ready) yield break;
                bool direct = !NetRoute.ServerMode;

                // (6) A game started anew in a seat begins its id counter anew; its hits on an enemy the seat's earlier
                // game had hit must still count.
                if (!LocalReport(step + 1).Owner)
                {
                    float health = EnemySync.Inspect(chosen.Key).Health;
                    for (int hit = 1; hit <= 2; hit++)
                    {
                        EnemySync.TestNewIds();
                        EnemySync.TestAttack(chosen.Key, 0.5f, false);
                        float by = Now + 2f;
                        while (Now < by && EnemySync.Inspect(chosen.Key).Health > health - hit * 0.5f + 0.01f) yield return null;
                    }
                    float lost = health - EnemySync.Inspect(chosen.Key).Health;
                    h.Check(Mathf.Abs(lost - 1f) < 0.01f, tag + " (6) a hit made after the id counter started anew still counts (two hits of 0.5 took " + F(lost) + ")");
                    // (7) The freeze an ownership message applies must leave an enemy of a class that is not mirrored alone.
                    int frozen = EnemySync.TestFreezeUnmirrored(chosen.Key);
                    if (frozen < 0) h.Note(tag + " (7) not judged: this enemy has no rigidbody");
                    else h.Check(frozen == 0, tag + " (7) an ownership freeze leaves the rigidbodies of a class that is not mirrored alone (" + frozen + " frozen)");
                }
                // (8) An enemy looks sideways for a player while this PC has none.
                int sight = EnemySync.TestSightNoPlayer(chosen.Key);
                if (sight < 0) h.Note(tag + " (8) not judged on this PC: it does not simulate an enemy here (the other PC judges it)");
                else h.Check(sight == 0, tag + " (8) an enemy looking for a player while this PC has none does not throw");
                yield return Barrier(h, step + 2, chosen.Key);
                if (!Has(step + 2)) yield break;

                if (!direct) h.Note(tag + " (5) (9) (1) need the host's grants and handoffs: not run in server mode (leases)");
                if (direct)
                {
                    // (5) Only the guest has the enemy loaded and the host never saw it: the host grants it to the guest
                    // with a state nobody captured.
                    // The guest forgets it first (its new record has no owner yet), then the host: the guest's next
                    // presence report is then the first the host hears of the enemy.
                    // (9) and review 2 below: the guest loses its first handoff, and its enemy dies just before that freeze.
                    if (!NetPlay.IsHost) { EnemySync.TestSwallowHandoff = chosen.Key; EnemySync.TestKillAtFreeze = chosen.Key; EnemySync.TestForget(chosen.Key, false); }
                    yield return Wait(0.5f);
                    yield return Barrier(h, step + 14, chosen.Key);
                    if (!Has(step + 14)) yield break;
                    if (NetPlay.IsHost) EnemySync.TestForget(chosen.Key, true);
                    float granted = Now + 6f;
                    while (Now < granted && !(NetPlay.IsHost ? EnemySync.Inspect(chosen.Key).OwnerSeat == 2 : EnemySync.Inspect(chosen.Key).Owner)) yield return null;
                    yield return Wait(0.5f);
                    if (!NetPlay.IsHost)
                    {
                        EnemySync.Probe seeded = EnemySync.Inspect(chosen.Key);
                        h.Note(tag + " (5) " + EnemySync.Describe(chosen.Key));
                        h.Check(seeded.Owner && seeded.Present && seeded.Health > 0f && Vector3.Distance(seeded.Position, chosen.Origin) < 20f,
                            tag + " (5) an enemy the host granted without ever seeing it stays where it is, alive, on its new owner");
                    }
                    yield return Barrier(h, step + 3, chosen.Key);
                    if (!Has(step + 3)) yield break;
                    if (NetPlay.IsHost) h.Check(Part[step + 3].Owner && !Part[step + 3].Dead && Part[step + 3].Health > 0f, tag + " (5) the guest reports it alive and its own");

                    // (9) The host loads the scene and wants the enemy back; the guest's first handoff is lost.
                    float asked = Now;
                    if (NetPlay.IsHost) EnemySync.TestHide(chosen.Key, false);
                    float back = Now + 10f;
                    while (Now < back)
                    {
                        EnemySync.Probe p = EnemySync.Inspect(chosen.Key);
                        if (NetPlay.IsHost ? p.Owner : p.OwnerSeat == 1 && !p.Owner && EnemySync.TestSwallowHandoff == "") break;
                        yield return null;
                    }
                    EnemySync.Probe returned = EnemySync.Inspect(chosen.Key);
                    if (NetPlay.IsHost) h.Check(returned.Owner, tag + " (9) a handoff that got lost is given up and asked for again: the host simulates the enemy (" + F(Now - asked) + " s)");
                    else h.Check(EnemySync.TestSwallowHandoff == "" && returned.OwnerSeat == 1 && !returned.Owner, tag + " (9) the first handoff was lost as arranged and the enemy went back to the host");
                    EnemySync.TestSwallowHandoff = "";
                    yield return Wait(0.7f);
                    yield return Barrier(h, step + 4, chosen.Key);
                    if (!Has(step + 4)) yield break;
                    h.Check(Agreement(chosen.Key, step + 4), tag + " (9) peers agree on one owner/epoch afterwards");
                    // Review 2: the death reached the host while it was frozen for the handoff it then gave up.
                    h.Check(LocalReport(step + 4).Dead && Part[step + 4].Dead, tag + " (9) a death just before the freeze stands on both PCs when the handoff is given up (here "
                        + (LocalReport(step + 4).Dead ? "dead" : "alive") + ", there " + (Part[step + 4].Dead ? "dead" : "alive") + ")");
                    EnemySync.TestKillAtFreeze = "";
                    yield return FamilyPresence(chosen);   // its owner brings it back for the checks below
                }

                // The family's first one dies on its simulator: two pieces for the checks below.
                if (EnemySync.Inspect(chosen.Key).Owner) EnemySync.TestAttack(chosen.Key, 1000f, false);
                float split = Now + 6f;
                while (Now < split && SwarmCount(FamilyNow(chosen.Key), 1, false) < 2) yield return null;
                yield return Wait(0.7f);
                yield return Barrier(h, step + 5, chosen.Key, false, 30f, SwarmReport(chosen.Key));
                if (!Has(step + 5)) yield break;
                Dictionary<int, PieceSample> mine = ParseSwarm(LocalReport(step + 5).Extra), theirs = ParseSwarm(Part[step + 5].Extra);
                bool two = SwarmCount(mine, 1, false) == 2 && SwarmCount(theirs, 1, false) == 2;
                h.Check(two, tag + " the family split into two pieces on both PCs");
                if (!two) yield break;
                string second = Piece(mine, 2);

                if (direct)
                {
                    // (1) The family goes to the guest and back to the host; a death the guest reports with the epoch
                    // of its time as the simulator arrives after that.
                    if (NetPlay.IsHost) { EnemySync.TestOwnerKey = chosen.Key; EnemySync.TestOwnerSeat = 2; }
                    float moved = Now + 5f;
                    while (Now < moved && EnemySync.Inspect(second).OwnerSeat != 2) yield return null;
                    yield return Wait(0.7f);
                    yield return Barrier(h, step + 6, second);
                    if (!Has(step + 6)) yield break;
                    bool there = LocalReport(step + 6).OwnerSeat == 2 && Part[step + 6].OwnerSeat == 2 && (NetPlay.IsHost ? Part[step + 6].Owner : LocalReport(step + 6).Owner);
                    h.Check(there, tag + " (1) the family, its pieces included, went to the guest");
                    if (NetPlay.IsHost) { EnemySync.TestOwnerSeat = 1; EnemySync.TestNoLocalPlayer = true; }   // review 3: the late death finds no local Ori
                    moved = Now + 6f;
                    while (Now < moved)
                    {
                        EnemySync.Probe p = EnemySync.Inspect(second);
                        if (p.OwnerSeat == 1 && p.Owner == NetPlay.IsHost) break;
                        yield return null;
                    }
                    yield return Wait(0.7f);
                    yield return Barrier(h, step + 7, second);
                    if (!Has(step + 7)) yield break;
                    bool returnedHome = LocalReport(step + 7).OwnerSeat == 1 && Part[step + 7].OwnerSeat == 1 && (NetPlay.IsHost ? LocalReport(step + 7).Owner : Part[step + 7].Owner);
                    h.Check(returnedHome, tag + " (1) and came back to the host");
                    if (NetPlay.IsHost) { EnemySync.TestOwnerSeat = 0; EnemySync.TestOwnerKey = ""; }
                    if (there && returnedHome)
                    {
                        if (!NetPlay.IsHost) h.Check(EnemySync.TestLateDeath(second, LocalReport(step + 6).Epoch), tag + " (1) the guest reports a death from its last moment as the piece's simulator, with that epoch");
                        float settled = Now + 5f;
                        while (Now < settled)
                        {
                            Dictionary<int, PieceSample> now = FamilyNow(chosen.Key);
                            if (!Alive(now, 2) && Alive(now, 4) && Alive(now, 5)) break;
                            yield return null;
                        }
                        yield return Wait(0.7f);
                        yield return Barrier(h, step + 8, chosen.Key, false, 30f, SwarmReport(chosen.Key));
                        if (!Has(step + 8)) yield break;
                        mine = ParseSwarm(LocalReport(step + 8).Extra); theirs = ParseSwarm(Part[step + 8].Extra);
                        h.Check(!Alive(mine, 2) && !Alive(theirs, 2), tag + " (1) a piece its former simulator killed in its last moment is dead on both PCs");
                        h.Check(Alive(mine, 4) && Alive(mine, 5) && Alive(theirs, 4) && Alive(theirs, 5) && Piece(mine, 4) == Piece(theirs, 4),
                            tag + " (1) and the two pieces it split into live on both PCs");
                        h.Check(!EnemySync.Inspect(second).Present, tag + " (1) no body of that piece is left on this PC, even with no local Ori on the new simulator");
                        EnemySync.TestNoLocalPlayer = false;
                        // Review 1: in its last moment as the simulator the guest also split off a piece nobody else heard of.
                        string late = EnemySync.SwarmPieceKey(chosen.Key, 6);
                        if (!NetPlay.IsHost) h.Check(EnemySync.TestLateSpawn(late, LocalReport(step + 6).Epoch, Piece(mine, 3)), tag + " (1) the guest reports a piece it split off in its last moment as the simulator");
                        float born = Now + 5f;
                        while (Now < born && !Alive(FamilyNow(chosen.Key), 6)) yield return null;
                        yield return Wait(0.5f);
                        yield return Barrier(h, step + 15, chosen.Key, false, 30f, SwarmReport(chosen.Key));
                        if (!Has(step + 15)) yield break;
                        h.Check(Alive(ParseSwarm(LocalReport(step + 15).Extra), 6) && Alive(ParseSwarm(Part[step + 15].Extra), 6),
                            tag + " (1) a piece its former simulator split off, unknown to the new one, lives on both PCs");
                    }
                    EnemySync.TestNoLocalPlayer = false;
                }

                // (3) The family's scene unloads on both PCs while its pieces live on (they followed the players).
                EnemySync.TestHide(chosen.Key, true);
                yield return Wait(2f);
                float oldest = 0f; int copies = 0, own = 0;
                foreach (KeyValuePair<int, PieceSample> piece in FamilyNow(chosen.Key))
                {
                    if (!piece.Value.Alive) continue;
                    if (EnemySync.Inspect(piece.Value.Key).Owner) own++;
                    else { copies++; oldest = Mathf.Max(oldest, EnemySync.TestStateAge(piece.Value.Key)); }
                }
                yield return Barrier(h, step + 9, chosen.Key, false, 30f, own.ToString());
                if (!Has(step + 9)) yield break;
                int ownThere; int.TryParse(Part[step + 9].Extra, out ownThere);
                h.Check((own > 0) != (ownThere > 0), tag + " (3) with the family's scene gone on both PCs one PC still simulates the live pieces (" + own + " here, " + ownThere + " there)");
                if (own == 0) h.Check(copies > 0 && oldest < 0.6f, tag + " (3) and their copies here still follow it (" + copies + " copies, newest state " + F(oldest) + " s old)");
                EnemySync.TestHide(chosen.Key, false);
                yield return Wait(1f);

                // (2) What the end of a session does to the pieces: the ones this PC only shows go, its own stay.
                Dictionary<int, PieceSample> shown = FamilyNow(chosen.Key); int showing = 0, owning = 0;
                foreach (KeyValuePair<int, PieceSample> piece in shown)
                {
                    EnemySync.Probe p = EnemySync.Inspect(piece.Value.Key);
                    if (piece.Value.Alive && p.Present && !p.Owner) showing++;
                    if (piece.Value.Alive && p.Present && p.Owner) owning++;
                }
                // Review 4: the session ends while this PC, the simulator, is frozen for a handoff.
                if (owning > 0)
                {
                    int lost = EnemySync.TestDropCopies(true);
                    h.Check(lost == 0, tag + " (2) the end of a session keeps the pieces of a simulator frozen for a handoff (" + lost + " of " + owning + " removed)");
                }
                int dropped = EnemySync.TestDropCopies();
                if (showing > 0) h.Check(dropped == showing, tag + " (2) the end of a session removes the pieces this PC only showed (" + dropped + " of " + showing + ")");
                else h.Check(dropped == 0, tag + " (2) the end of a session leaves the pieces this PC simulates alone (" + dropped + " removed)");
                if (showing > 0)
                {
                    // The session did not end here: the simulator's next states bring the copies back.
                    float rebuilt = Now + 4f; int again = 0;
                    while (Now < rebuilt && again < showing)
                    {
                        again = 0;
                        foreach (KeyValuePair<int, PieceSample> piece in shown) if (piece.Value.Alive && EnemySync.Inspect(piece.Value.Key).Present) again++;
                        yield return null;
                    }
                    h.Check(again >= showing, tag + " (2) the copies come back from the simulator's next states (" + again + " of " + showing + ")");
                }

                // Nothing of the family is left behind for the scenarios that follow.
                float done = Now + 12f, strike = 0f, quiet = -1f;
                while (Now < done)
                {
                    Dictionary<int, PieceSample> now = FamilyNow(chosen.Key);
                    if (SwarmCount(now, 0, false) > 0) quiet = -1f;
                    else if (quiet < 0f) quiet = Now;
                    else if (Now - quiet > 1f) break;
                    if (Now >= strike)
                    {
                        strike = Now + 0.4f;
                        foreach (KeyValuePair<int, PieceSample> piece in now)
                        {
                            EnemySync.Probe p = EnemySync.Inspect(piece.Value.Key);
                            if (!piece.Value.Alive || !p.Owner || !p.Present) continue;
                            try { EnemySync.TestAttack(piece.Value.Key, 1000f, false); } catch (InvalidOperationException) { }
                        }
                    }
                    yield return null;
                }
                yield return Barrier(h, step + 10, chosen.Key, false, 30f, SwarmReport(chosen.Key));
                if (!Has(step + 10)) yield break;
                h.Check(SwarmCount(ParseSwarm(LocalReport(step + 10).Extra), 0, false) == 0 && SwarmCount(ParseSwarm(Part[step + 10].Extra), 0, false) == 0,
                    tag + " no piece of the family is left on either PC when the checks end");

            }
            finally
            {
                EnemySync.TestOwnerSeat = 0; EnemySync.TestOwnerKey = ""; EnemySync.TestSwallowHandoff = ""; EnemySync.TestKillAtFreeze = ""; EnemySync.TestNoLocalPlayer = false;
                if (!string.IsNullOrEmpty(chosen.Key)) EnemySync.TestHide(chosen.Key, false);
                Own.Mortality.DamageReciever.IsImmortal = immortal; Own.Level.OnLevelUpGameObject = blast; Plugin.Teleport(Own, saved);
            }
        }
    }
}
