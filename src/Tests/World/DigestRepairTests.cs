using System;
using System.Collections.Generic;
using OriCoop.World;

static class DigestRepairTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    static Dictionary<string, DigestDifference> MissingLocally(params string[] keys)
    {
        var remote = new Dictionary<string, DigestObject>();
        foreach (string key in keys) remote.Add(key, new DigestObject("EnergyDoor", 10));
        return DigestRepair.Differences(new Dictionary<string, DigestObject>(), remote);
    }

    public static void Register(Action<string, Action> run)
    {
        run("OnlyGumosCinematicMeshComparesPosePresence", delegate {
            const string scene = "-1289149174 680822595 558787450 1729667918";
            const string mesh = "-1517079076 1077808971 1966622865 680266200";
            const string transform = "TransformSaveSerialize";
            Check(DigestRepair.ComparesPosePresence(scene + ":" + mesh, transform), "Gumo mesh pose");
            Check(!DigestRepair.ComparesPosePresence(scene + ":1 2 3 4", transform), "another transform in the same scene");
            Check(!DigestRepair.ComparesPosePresence("1 2 3 4:" + mesh, transform), "same object ID in a different scene");
            Check(!DigestRepair.ComparesPosePresence(scene + ":" + mesh, "GameController"), "different component type");
            var local = new Dictionary<string, DigestObject> { { scene + ":" + mesh, new DigestObject(transform, 0) } };
            var remote = new Dictionary<string, DigestObject>();
            Check(DigestRepair.Differences(local, remote).Count == 1, "missing Gumo still differs");
        });
        run("ExcludedDigestObjectsDoNotRequestResync", delegate {
            var local = new Dictionary<string, DigestObject> {
                { "door", new DigestObject("EnergyDoor", 10) },
                { "awards", new DigestObject("AchievementsLogic", 1) },
                { "phase", new DigestObject("BaseAnimatorSerializer", 2) }
            };
            var remote = new Dictionary<string, DigestObject> {
                { "door", new DigestObject("EnergyDoor", 10) },
                { "awards", new DigestObject("AchievementsLogic", 3) },
                { "remote-phase", new DigestObject("BaseAnimatorSerializer", 4) }
            };
            var differences = DigestRepair.Differences(local, remote);
            Check(differences.Count == 0, "local-only objects are not shared");
            DigestRepair repair = new DigestRepair();
            for (int i = 0; i < 3; i++)
                Check(repair.Observe(differences, false) == DigestRepair.Decision.None, "no request in round " + i);
        });
        run("ResyncedGuidDoesNotHideNewMismatch", delegate {
            DigestRepair repair = new DigestRepair();
            var door = MissingLocally("door");
            var both = MissingLocally("door", "switch");
            Check(repair.Observe(door, false) == DigestRepair.Decision.Wait, "wait one round");
            Check(repair.Observe(door, false) == DigestRepair.Decision.Request, "request on second round");
            repair.SnapshotRequested(door);
            repair.SnapshotReceived();
            for (int i = 0; i < 3; i++)
                Check(repair.Observe(door, false) == DigestRepair.Decision.None, "same door after snapshot: no more requests");
            Check(repair.Observe(both, false) == DigestRepair.Decision.Wait, "new switch mismatch counts again");
            Check(repair.Observe(both, false) == DigestRepair.Decision.Request, "request for the new GUID even if the door still differs");
        });
        run("ResolvedGuidCanRequestAgain", delegate {
            DigestRepair repair = new DigestRepair();
            var door = MissingLocally("door");
            repair.SnapshotRequested(door);
            repair.SnapshotReceived();
            Check(repair.Observe(door, false) == DigestRepair.Decision.None, "initially suppressed");
            Check(repair.Observe(MissingLocally(), false) == DigestRepair.Decision.None, "door agrees");
            Check(repair.Observe(door, false) == DigestRepair.Decision.Wait, "door differs again");
            Check(repair.Observe(door, false) == DigestRepair.Decision.Request, "new divergence of a previously repaired door");
        });
        run("SameGuidWithNewContentCanRequestAgain", delegate {
            foreach (bool changeLocal in new[] { false, true })
            {
                var local = new Dictionary<string, DigestObject> { { "door", new DigestObject("EnergyDoor", 10) } };
                var remote = new Dictionary<string, DigestObject> { { "door", new DigestObject("EnergyDoor", 11) } };
                var repair = new DigestRepair();
                var differences = DigestRepair.Differences(local, remote);
                repair.SnapshotRequested(differences);
                repair.SnapshotReceived();
                Check(repair.Observe(differences, false) == DigestRepair.Decision.None, "unchanged content is suppressed");

                // The door matched briefly, but no manifest was compared before another change.
                var changed = changeLocal ? local : remote;
                changed["door"] = new DigestObject("EnergyDoor", changeLocal ? 11UL : 10UL);
                Check(DigestRepair.Differences(local, remote).Count == 0, "the snapshot left the door matching");
                changed["door"] = new DigestObject("EnergyDoor", 12);
                differences = DigestRepair.Differences(local, remote);
                Check(repair.Observe(differences, false) == DigestRepair.Decision.Wait, "new content counts on the same GUID");
                Check(repair.Observe(differences, false) == DigestRepair.Decision.Request, "new content can request a snapshot");
            }
        });
        run("SharedGuidWithDifferentTypeStillDiffers", delegate {
            var local = new Dictionary<string, DigestObject> { { "object", new DigestObject("EnergyDoor", 10) } };
            var remote = new Dictionary<string, DigestObject> { { "object", new DigestObject("MapStone", 10) } };
            var differences = DigestRepair.Differences(local, remote);
            Check(differences.Count == 1 && differences.ContainsKey("object"), "same hash but different shared type");
        });
        run("ProgressCanRequestWithoutWorldDifferences", delegate {
            DigestRepair repair = new DigestRepair();
            var none = MissingLocally();
            Check(repair.Observe(none, true) == DigestRepair.Decision.Wait, "progress waits one round");
            Check(repair.Observe(none, true) == DigestRepair.Decision.Request, "progress mismatch still requests");
        });
    }
}
