using System;
using System.IO;
using OriCoop.Enemies;

static class EnemyVisualTests
{
    sealed class Prefab
    {
        public readonly string Name;
        public Prefab(string name) { Name = name; }
    }

    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }

    public static void Register(Action<string, Action> run)
    {
        run("MissingEnemyEffectDoesNotSelectDifferentVariant", delegate {
            Prefab fire = new Prefab("fireSlugEnemyShoot");
            Check(EnemyVisuals.FindByName("acidSlugEnemyShoot", new[] { fire }, p => p.Name) == null, "fire is not acid");
        });
        run("ArmourSwapKeepsHitsInFlightOnTheSameLife", delegate {
            var enemy = new EnemyAuthority { Generation = 3 };
            uint sentGeneration = enemy.Generation;
            Check(!enemy.BeginEntityLife(true, 0), "replacing the shell is not a new life");
            Check(EnemyOwnership.HitDecision(enemy.Generation, sentGeneration, false, false, true, 5, 5, true) == 2,
                "hit sent before the swap still reaches the owner");
            Check(EnemyOwnership.HitDecision(enemy.Generation, sentGeneration, true, false, true, 5, 5, true) == 0,
                "already applied hit is still a duplicate");
            Check(enemy.BeginEntityLife(false, 0) && enemy.Generation == sentGeneration + 1, "respawn starts a new life");
            Check(EnemyOwnership.HitDecision(enemy.Generation, sentGeneration, false, false, true, 5, 5, true) == 0,
                "a hit from a previous life is still dropped");
        });
        run("ArmouredRammingEnemyIsOnMirrorAllowlist", delegate {
            Check(EnemyVisuals.MirroredClass("ArmouredRammingEnemy"), "armoured ram mirrors");
            Check(EnemyVisuals.MirroredClass("RammingEnemy"), "bare ram mirrors");
            Check(!EnemyVisuals.MirroredClass("ShootingSpider"), "owner-only enemy stays owner-only");
            Check(!EnemyVisuals.MirroredClass("ArmouredRammingEnemyPlaceholder"), "requires exact class");
        });
        run("BrokenArmourSurvivesEveryStateFormat", delegate {
            foreach (bool full in new[] { false, true })
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    BinaryWriter w = new BinaryWriter(stream);
                    var state = new EnemyState { ArmourBroken = true, Alive = true, Generation = 3 };
                    EnemyWire.WriteState(w, new EnemyVector(), state, full, s => 0);
                    w.Flush(); stream.Position = 0;
                    var copy = EnemyWire.ReadState(new BinaryReader(stream), new EnemyVector(), full, id => "");
                    Check(copy.ArmourBroken && copy.Alive && copy.Generation == 3 && stream.Position == stream.Length,
                        "broken armour in " + (full ? "full state" : "snapshot"));
                    Check(EnemyWire.Flags(state) == 65 && EnemyWire.Flags(new EnemyState { Alive = true }) == 1,
                        "armour uses a spare bit; normal enemies stay wire-compatible");
                }
            }
        });
    }
}
