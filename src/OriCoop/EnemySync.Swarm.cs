using System;
using System.Collections.Generic;
using Core;
using HarmonyLib;
using UnityEngine;
using OriCoop.Enemies;
using OriCoop.Seams;

namespace OriCoop
{
    // T-195: the splitting Swarm. Before, Swarm placeholders were left out of E3 because the pieces a Swarm splits
    // into are created at run time and have no id; every PC then ran its own Swarm (one PC had killed the large one
    // and fought the small ones while the other still saw the large one standing there).
    //
    // Now the Swarm is a mirrored class like the other ground enemies, and every piece is an E3 actor of its own:
    // - id: its place in the family tree (Enemies\SwarmFamily.cs), the same on every PC, nothing new on the wire;
    // - owner: the PC that simulates the family's placeholder (its lease or grant); pieces have none of their own;
    // - split: only the owner runs the game's split; it announces each piece with the usual Life message, and the
    //   other PCs build their copy from that state (or from the next snapshot, e.g. after loading the scene late);
    // - damage and death: the usual E3 hit routing, by the piece's id; the owner's death is the death everywhere.
    public static partial class EnemySync
    {
        sealed class Piece { public Actor Root; public int Node; public GameObject Prefab; public float Loot; }
        sealed class Split { public Actor Parent; public int Index; }
        static readonly Dictionary<string, Piece> s_pieces = new Dictionary<string, Piece>();
        // One entry per entry of the game's own spawn queue (SwarmEnemyManager.m_spawnQueue), in the same order: which
        // actor split, and to which side. Filled and emptied by the game's own calls, in a session or not, so the two
        // queues never drift apart.
        static readonly Queue<Split> s_splits = new Queue<Split>();
        static Actor s_splitting; static int s_splitIndex;
        public static int SwarmPiecesSpawned, SwarmPiecesMirrored;
        static readonly bool s_mutateSwarmLocal = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SWARM_LOCAL") == "1";   // T-195 test-only: Swarms stay outside E3

        static bool PieceGone(Enemy e)
        { return e == null || (UberPoolManager.Instance != null && UberPoolManager.Instance.IsDestroyed(e.gameObject)); }

        // Name every piece this Swarm can split into, so a piece id from the wire finds its family and its prefab.
        static void IndexPieces(Actor root, SwarmEnemyPlaceholder placeholder)
        {
            if (placeholder == null || root.PiecesIndexed || placeholder.SwarmEnemy == null) return;
            root.PiecesIndexed = true;
            GameObject[] prefabs = new GameObject[SwarmFamily.MaxDepth + 1]; float[] loot = new float[SwarmFamily.MaxDepth + 1];
            SwarmEnemy parent = placeholder.SwarmEnemy.GetComponent<SwarmEnemy>();
            for (int depth = 1; depth <= SwarmFamily.MaxDepth && parent != null && parent.Settings != null && parent.Settings.Child != null; depth++)
            {
                // The game hands a piece (int)(parent loot x parent multiplier): SwarmEnemy.OnDeath.
                prefabs[depth] = parent.Settings.Child;
                loot[depth] = parent.Loot == null ? 0f : (int)((depth == 1 ? parent.Loot.LootAmount : loot[depth - 1]) * parent.Loot.LootMultiplier);
                parent = prefabs[depth].GetComponent<SwarmEnemy>();
            }
            for (int node = SwarmFamily.Root + 1; node <= SwarmFamily.MaxNode; node++)
            {
                int depth = SwarmFamily.Depth(node); if (prefabs[depth] == null) continue;
                Piece piece = new Piece(); piece.Root = root; piece.Node = node; piece.Prefab = prefabs[depth]; piece.Loot = loot[depth];
                string key = PieceKey(root, node); s_pieces[key] = piece;
                Actor known; if (s_actors.TryGetValue(key, out known)) Adopt(known, piece);   // heard of before this PC loaded the family
            }
        }
        static MoonGuid PieceId(Actor root, int node)
        { int[] id = SwarmFamily.Id(root.Id.A, root.Id.B, root.Id.C, root.Id.D, node); return new MoonGuid(id[0], id[1], id[2], id[3]); }
        static string PieceKey(Actor root, int node) { return Key(root.Scene, PieceId(root, node)); }
        static void Adopt(Actor a, Piece piece)
        {
            a.Root = piece.Root; a.Node = piece.Node; a.Prefab = piece.Prefab; a.PieceLoot = piece.Loot;
            a.Origin = piece.Root.Origin; a.SceneName = piece.Root.SceneName; a.ClassName = "SwarmEnemy";
            a.Loaded = piece.Root.Loaded && piece.Root.Placeholder != null;
        }
        // The actor of the piece `parent` throws to side `index` (0 left, 1 right); null beyond the family tree.
        static Actor PieceOf(Actor parent, int index)
        {
            Actor root = parent.Root != null ? parent.Root : parent;
            int node = SwarmFamily.Child(parent.Root != null ? parent.Node : SwarmFamily.Root, index);
            if (!SwarmFamily.Valid(node)) return null;
            MoonGuid id = PieceId(root, node); string key = Key(root.Scene, id);
            Piece piece; if (!s_pieces.TryGetValue(key, out piece)) return null;
            Actor a;
            if (!s_actors.TryGetValue(key, out a)) { a = new Actor(); a.Key = key; a.Scene = new MoonGuid(root.Scene); a.Id = id; s_actors.Add(key, a); }
            Adopt(a, piece); return a;
        }
        // A piece has no lease or grant of its own: whoever simulates the family's placeholder simulates the piece.
        static void FollowFamily(Actor a)
        {
            Actor root = a.Root; RefreshOwnership(root);
            if (a.Known == root.Known && a.Frozen == root.Frozen && a.OwnerSeat == root.OwnerSeat && a.Epoch == root.Epoch) return;
            bool was = a.Mine(s_host.LocalSeat);
            bool source = a.Known != root.Known || a.OwnerSeat != root.OwnerSeat || a.Epoch != root.Epoch;
            a.Known = root.Known; a.Frozen = root.Frozen; a.OwnerSeat = root.OwnerSeat; a.Epoch = root.Epoch;
            bool mine = a.Mine(s_host.LocalSeat);
            if (source) { a.Buffer.Clear(); a.ExpectFirstSnapshot = a.Known && !mine; }   // another PC's tick clock from here on
            if (mine && !was) { Bind(a); Resume(a); }
            else if (!mine) FreezeBodies(a);
        }
        // Another PC's state of a piece arrived (Life or snapshot): build, keep or remove this PC's copy.
        static void ReceivePiece(Actor a, State s)
        {
            if (Mine(a)) return;
            Bind(a);
            if (!s.Alive || s.Killed)
            {
                if (a.Entity == null) return;
                a.LastRemoval = s.Killed ? "death" : "gone"; Trace("despawn", a, a.LastRemoval);
                Enemy e = a.Entity;
                if (s.Killed && e.gameObject.activeInHierarchy) PlayPuppetDeath(a);
                Attach(a, null);
                if (!PieceGone(e)) InstantiateUtility.Destroy(e.gameObject);
            }
            else if (a.Entity == null && a.Loaded && Scenes.Manager != null && Scenes.Manager.SceneVisibleAtPosition(s.Position))
                SpawnPiece(a, s, false);   // not where this PC's own boundary check would remove it again at once
        }
        // This PC's body for a piece it did not split off itself, with the set-up the game's SwarmEnemyManager gives a
        // piece. own = this PC simulates the family and took the piece over without a body (same generation goes on).
        static void SpawnPiece(Actor a, State s, bool own)
        {
            SwarmEnemyPlaceholder family = a.Root.Placeholder as SwarmEnemyPlaceholder;
            if (family == null || a.Prefab == null) return;
            bool spawning = s_spawning; s_spawning = true;
            try
            {
                GameObject go = InstantiateUtility.Instantiate(a.Prefab, s.Position, Quaternion.identity) as GameObject;
                SwarmEnemy e = go == null ? null : go.GetComponent<SwarmEnemy>();
                if (e == null) { if (go != null) InstantiateUtility.Destroy(go); return; }
                e.Owner = family; family.OnChildComponentSpawned(e);   // first: SwarmEnemy.OnDestroy needs its Owner
                e.SetSceneRoot(a.Scene); e.SetModeToSpawned();
                if (e.PlatformMovement != null) e.PlatformMovement.LocalSpeed = s.Velocity;
                if (e.Loot != null) e.Loot.LootAmount = a.PieceLoot;
                if (e.DamageDealer != null) e.DamageDealer.Damage = family.Settings.DamageOnTouch;
                if (e.OrbSpawner != null) { e.OrbSpawner.SetNumberOfExpOrbs((int)a.PieceLoot); e.OrbSpawner.LimitNumberOfOrbs(1); }
                DestroyOnRestoreCheckpoint restore = go.GetComponent<DestroyOnRestoreCheckpoint>(); if (restore != null) restore.enabled = true;
                DestroyWhenOutsideActiveBoundaries boundary = go.GetComponent<DestroyWhenOutsideActiveBoundaries>(); if (boundary != null) boundary.enabled = true;
                if (s.Health > 0 && e.DamageReciever != null) e.DamageReciever.Health = s.Health;
                Attach(a, e); SwarmPiecesMirrored++;
            }
            finally { s_spawning = spawning; }
            if (own && a.Entity != null) { a.RestoreController = null; RestoreBodies(a); a.Latest = Capture(a, true); }
        }
        // Every 0.25 s (Scan). A piece is loaded where its family's placeholder is. Its owner reports a piece that
        // vanished without dying (a checkpoint restore, or it left the loaded world), and gives a body to a live piece
        // it took over without one.
        static void ScanPieces()
        {
            List<Actor> pieces = null;
            foreach (Actor a in s_actors.Values)
                if (a.Root != null) { if (pieces == null) pieces = new List<Actor>(); pieces.Add(a); }
            if (pieces == null) return;
            foreach (Actor a in pieces)
            {
                a.Loaded = a.Root.Loaded && a.Root.Placeholder != null;
                bool had = a.EntityInstanceId != 0; Bind(a);
                if (!Mine(a) || a.Entity != null || !a.Latest.Alive || a.Latest.Killed) continue;
                if (had)
                {
                    a.Latest.Alive = false; a.Latest.Tick = s_host.LocalTick; a.LastRemoval = "gone";
                    Trace("despawn", a, "gone"); SendLife(a, 4);
                }
                else if (a.Loaded) SpawnPiece(a, a.Latest, true);
            }
        }
        // The owner's piece died: tell the others (the placeholder's own enemy is reported by PlaceholderDeath).
        static void PieceDied(Actor a)
        {
            a.Latest = Capture(a, true); a.Latest.Killed = true; a.Latest.Alive = false; a.Latest.Health = 0;
            a.DeathTime = Now; a.LastRemoval = "death";
            Trace("death", a, "hit=" + a.LastHit + " node=" + a.Node);
            Services.Log.Info("net: E3 died hit=" + a.LastHit + " swarm piece node=" + a.Node + " " + Describe(a.Key));
            SendLife(a, 2);
        }
        [HarmonyPatch(typeof(SwarmEnemy), "OnDeath")]
        static class SwarmDeath
        {
            static void Prefix(SwarmEnemy __instance) { s_splitting = Active ? For(__instance) : null; s_splitIndex = 0; }
            static Exception Finalizer(Exception __exception)
            {
                Actor a = s_splitting; s_splitting = null;
                if (a != null && a.Root != null && Mine(a)) PieceDied(a);
                return __exception;
            }
        }
        [HarmonyPatch(typeof(SwarmEnemyManager), "QueueSpawn")]
        static class SwarmSplitQueued
        {
            static bool Prefix()
            {
                Actor parent = Active ? s_splitting : null;
                // Only the PC that simulates the family splits it; the others get each piece from that PC.
                if (parent != null && !Mine(parent)) { s_splitIndex++; return false; }
                Split split = new Split(); split.Parent = parent; split.Index = s_splitIndex++; s_splits.Enqueue(split);
                return true;
            }
        }
        [HarmonyPatch(typeof(SwarmEnemyManager), "OnGameSerializeLoad")]
        static class SwarmSplitQueueCleared { static void Postfix() { s_splits.Clear(); } }
        [HarmonyPatch(typeof(SwarmEnemyPlaceholder), "OnChildComponentSpawned")]
        static class SwarmPieceSpawned
        {
            static void Postfix(SwarmEnemy swarmEnemy)
            {
                if (s_spawning) return;   // SpawnPiece: this PC's copy of another PC's piece, not from the game's queue
                Split split = s_splits.Count > 0 ? s_splits.Dequeue() : null;
                if (!Active || split == null || split.Parent == null || swarmEnemy == null) return;
                Actor current;
                if (!s_actors.TryGetValue(split.Parent.Key, out current) || !object.ReferenceEquals(current, split.Parent)) return;   // queued in an earlier session
                Actor a = PieceOf(split.Parent, split.Index); if (a == null) return;
                if (!Mine(a))
                {
                    // The family changed hands between the split and this spawn; its new owner never heard of this piece.
                    Services.Log.Info("net: E3 swarm piece dropped (family changed owner during the split) node=" + a.Node + " " + Describe(a.Key));
                    InstantiateUtility.Destroy(swarmEnemy.gameObject); return;
                }
                Attach(a, swarmEnemy); SwarmPiecesSpawned++;
            }
        }

        // Tests: the nearest loaded Swarm placeholder (its own enemy may be dead or split), "" when none is tracked.
        public static string FindTestSwarm(Vector3 near, ICollection<string> rejected)
        {
            Scan(); Actor best = null;
            foreach (Actor a in s_actors.Values)
            {
                if (!a.Loaded || a.Root != null || !(a.Placeholder is SwarmEnemyPlaceholder)) continue;
                if (rejected != null && rejected.Contains(a.Key)) continue;
                if (best == null || Vector3.Distance(a.Origin, near) < Vector3.Distance(best.Origin, near)) best = a;
            }
            return best == null ? "" : best.Key;
        }
        // Tests: remove this PC's body of a piece without a death, as a checkpoint restore or the edge of the loaded
        // world does. On the owner the piece is then gone for everyone; on a mirror the next state rebuilds the copy.
        public static bool TestDropPiece(string key)
        {
            Actor a; if (string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a) || a.Root == null || PieceGone(a.Entity)) return false;
            InstantiateUtility.Destroy(a.Entity.gameObject); return true;
        }
        // Tests: the keys of the pieces of the family `rootKey` this PC has heard of (alive or not).
        public static string[] SwarmPieces(string rootKey)
        {
            List<string> keys = new List<string>(); Actor root;
            if (string.IsNullOrEmpty(rootKey) || !s_actors.TryGetValue(rootKey, out root)) return keys.ToArray();
            for (int node = SwarmFamily.Root + 1; node <= SwarmFamily.MaxNode; node++)
            { string key = PieceKey(root, node); Actor a; if (s_actors.TryGetValue(key, out a) && a.Root != null) keys.Add(key); }
            return keys.ToArray();
        }
        // Tests: how many pieces the family can have per level below the placeholder's own enemy ("2,4,8").
        public static string SwarmLevels(string rootKey)
        {
            Actor root; if (string.IsNullOrEmpty(rootKey) || !s_actors.TryGetValue(rootKey, out root)) return "";
            int[] counts = new int[SwarmFamily.MaxDepth + 1];
            for (int node = SwarmFamily.Root + 1; node <= SwarmFamily.MaxNode; node++)
                if (s_pieces.ContainsKey(PieceKey(root, node))) counts[SwarmFamily.Depth(node)]++;
            List<string> levels = new List<string>();
            for (int depth = 1; depth <= SwarmFamily.MaxDepth && counts[depth] > 0; depth++) levels.Add(counts[depth].ToString());
            return string.Join(",", levels.ToArray());
        }
    }
}
