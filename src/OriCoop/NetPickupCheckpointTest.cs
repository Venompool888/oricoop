using System;
using System.Collections;
using Game;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    public static partial class NetTestHarness
    {
        // A collected prefab copy and the registered sibling share a GUID in a scene absent on the host.
        static IEnumerator CloneCheckpoint(TestHarness h)
        {
            bool host = NetPlay.IsHost;
            MoonGuid scene = new MoonGuid(223, 17, 0, 1), id = new MoonGuid(223, 17, 0, 2);
            if (host)
            {
                SaveObject sibling = new SaveObject(id);
                sibling.Data.MemoryStream.Write(new byte[5], 0, 5);
                Game.Checkpoint.SaveGameData.InsertScene(scene).SaveObjects.Add(sibling);
            }
            int initial = Own.Inventory.Keystones;
            ProgressAction clone = new ProgressAction { Kind = ActionKind.Keystone, Amount = 1, Guid = id.ToByteArray(),
                Scene = scene.ToByteArray(), HasSpot = true, X = 101f, Y = 223f, Sequence = 0x72230001 };
            PublishWorldBarrier(h, 22301, false); yield return WorldBarrier(h, 22301);
            if (!host) NetPlay.Send(21, true, clone.Write);
            float by = Time.realtimeSinceStartup + 8f;
            while (Own.Inventory.Keystones != initial + 1 && Time.realtimeSinceStartup < by) yield return null;
            h.Check(Own.Inventory.Keystones == initial + 1, "W clone: the first guest pickup pays once");
            PublishWorldBarrier(h, 22302, false); yield return WorldBarrier(h, 22302);
            bool siblingAvailable = true;
            if (host)
            {
                GameController.Instance.CreateCheckpoint();
                SaveObject sibling = Game.Checkpoint.SaveGameData.GetScene(scene).SaveObjects.Find(s => s.Id == id);
                siblingAvailable = sibling.Data != null && sibling.Data.MemoryStream.ToArray()[4] == 0;
                SaveGameController save = GameController.Instance.SaveGameController;
                byte[] disk = save.SaveToBytes();
                h.Check(save.LoadFromBytes(disk), "W clone: reload the saved room bytes");
                GameController.Instance.RestoreCheckpoint();
                by = Time.realtimeSinceStartup + 15f;
                while ((GameController.Instance.IsLoadingGame || !ProgressSync.Ready) && Time.realtimeSinceStartup < by) yield return null;
            }
            PublishWorldBarrier(h, 22303, false); yield return WorldBarrier(h, 22303);
            int applied = ProgressSync.ActionsApplied, rejected = ProgressSync.ActionsRejected;
            if (!host) { clone.Sequence++; NetPlay.Send(21, true, clone.Write); }
            if (host)
            {
                by = Time.realtimeSinceStartup + 8f;
                while (ProgressSync.ActionsApplied == applied && ProgressSync.ActionsRejected == rejected && Time.realtimeSinceStartup < by) yield return null;
                h.Note("W clone: sibling available=" + siblingAvailable + ", balance after reload/retouch=" + Own.Inventory.Keystones + ", expected " + (initial + 1));
                h.Check(ProgressSync.ActionsRejected > rejected && Own.Inventory.Keystones == initial + 1,
                    "W clone: saved unregistered copy cannot pay again after a native reload");
                h.Check(siblingAvailable, "W clone: the unloaded registered sibling stays available");
            }
            PublishWorldBarrier(h, 22304, false); yield return WorldBarrier(h, 22304);
        }
    }
}
