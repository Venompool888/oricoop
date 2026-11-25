using System;
using System.IO;
using HarmonyLib;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    // Native saves have one object per prefab GUID in a scene. Additional copies need their
    // scene and position as well. Keep those claims in an OriCoop-owned SaveScene so that a
    // checkpoint commits (or rolls back) both the reward and its claim in the same operation.
    internal static class SavedPickupCopies
    {
        static readonly MoonGuid Ledger = new MoonGuid(new Guid("75779a9e-c231-41b5-b901-76342827b9e4"));
        const byte Format = 1;
        const int PlaceSize = 24; // native scene GUID + two floats
        internal static readonly bool MutateAlias = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PICKUP_CLONE_ALIAS") == "1";
        static readonly bool s_forget = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PICKUP_CLONE_FORGOTTEN") == "1";
        static SaveScene s_visibleLedger;
        static bool s_sceneChanged = true;

        static SaveScene Current()
        {
            SaveGameData data = Game.Checkpoint.SaveGameData;
            if (data == null) return null;
            SaveScene pending;
            return data.PendingScenes.TryGetValue(Ledger, out pending) ? pending : data.GetScene(Ledger);
        }

        static bool Located(ProgressAction a)
        {
            return a.HasSpot && a.Scene != null && a.Scene.Length == 16 && new MoonGuid(a.Scene) != MoonGuid.Empty
                && !float.IsNaN(a.X) && !float.IsInfinity(a.X) && !float.IsNaN(a.Y) && !float.IsInfinity(a.Y);
        }

        static byte[] Places(SaveScene ledger, MoonGuid id)
        {
            if (ledger == null) return null;
            SaveObject entry = ledger.SaveObjects.Find(item => item.Id == id);
            return entry.Data == null ? null : entry.Data.MemoryStream.ToArray();
        }

        static bool Known(byte[] places) { return places != null && places.Length > 0 && places[0] == Format && (places.Length - 1) % PlaceSize == 0; }

        internal static bool Contains(MoonGuid id, ProgressAction a)
        {
            if (s_forget || !Located(a)) return false;
            byte[] places = Places(Current(), id);
            if (!Known(places)) return false;
            MoonGuid scene = new MoonGuid(a.Scene);
            using (BinaryReader reader = new BinaryReader(new MemoryStream(places, 1, places.Length - 1)))
            {
                for (int i = 1; i < places.Length; i += PlaceSize)
                {
                    MoonGuid savedScene = new MoonGuid(reader);
                    float dx = reader.ReadSingle() - a.X, dy = reader.ReadSingle() - a.Y;
                    if (savedScene == scene && dx * dx + dy * dy <= PickupPlace.SameSpot * PickupPlace.SameSpot) return true;
                }
            }
            return false;
        }

        internal static void Remember(MoonGuid id, ProgressAction a)
        {
            if (s_forget || !Located(a) || Game.Checkpoint.SaveGameData == null || Contains(id, a)) return;
            byte[] places = Places(Current(), id);
            if (places != null && !Known(places)) return;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                if (places == null) writer.Write(Format);
                else writer.Write(places);
                writer.Write(a.Scene); writer.Write(a.X); writer.Write(a.Y);
                WorldSync.StoreSceneProgress(Ledger, id, stream.ToArray());
            }
        }

        internal static void SceneChanged() { s_sceneChanged = true; }

        internal static void HideLoaded()
        {
            SaveScene ledger = Current();
            if (s_forget || (!s_sceneChanged && ReferenceEquals(ledger, s_visibleLedger))) return;
            s_visibleLedger = ledger; s_sceneChanged = false;
            if (ledger == null || ledger.SaveObjects.Count == 0) return;
            foreach (PickupBase pickup in UnityEngine.Object.FindObjectsOfType<PickupBase>())
            {
                if (pickup == null || pickup.IsCollected || !pickup.IsGuidInitialized()) continue;
                ExpOrbPickup pooled = pickup as ExpOrbPickup;
                if (pooled != null && pooled.MessageType == ExpOrbPickup.ExpOrbMessageType.None) continue;
                Vector3 at = pickup.transform.position;
                ProgressAction place = new ProgressAction { Scene = WorldSync.SceneOf(pickup).ToByteArray(), HasSpot = true, X = at.x, Y = at.y };
                if (Contains(pickup.MoonGuid, place)) { pickup.IsCollected = true; pickup.gameObject.SetActive(false); }
            }
        }

        [HarmonyPatch(typeof(SceneRoot), "LateStart")]
        static class LoadedScene { static void Postfix() { SceneChanged(); } }
    }
}
