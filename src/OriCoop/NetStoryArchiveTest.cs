using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace OriCoop
{
    public static partial class WorldSync
    {
        // Use the same live capture, chunking and receiver as a native world snapshot.
        internal static void TestSendUnseenScripts(MoonGuid scene, ActionSequenceSerializer story, ActionSequenceSerializer platform)
        {
            List<Blob> captured = new List<Blob> { CaptureRecord(scene, story.MoonGuid, story), CaptureRecord(scene, platform.MoonGuid, platform) };
            foreach (Blob blob in captured) s_blobs[Key(blob.Scene, blob.Id)] = blob;
            SendBlobs(captured);
        }
    }

    public static partial class NetTestHarness
    {
        static ActionSequenceSerializer UnseenScript(GameObject go, MoonGuid id, bool story)
        {
            ActionSequence sequence = go.AddComponent<ActionSequence>();
            sequence.MoonGuid = id;
            if (story) sequence.Actions.Add(go.AddComponent<LockPlayerInputAction>());
            else sequence.Actions.Add(go.AddComponent<WaitAction>());
            sequence.Index = 3;
            ActionSequenceSerializer progress = go.AddComponent<ActionSequenceSerializer>();
            progress.MoonGuid = id; progress.OnValidate();
            return progress;
        }

        static IEnumerator UnseenStoryArchive(TestHarness h)
        {
            MoonGuid scene = new MoonGuid(222, 4, 1, 0), story = new MoonGuid(222, 4, 2, 0), platform = new MoonGuid(222, 4, 3, 0);
            SaveGameData data = Game.Checkpoint.SaveGameData;
            if (!NetPlay.IsHost)
            {
                SaveObject own = new SaveObject(story);
                byte[] previous = new byte[] { 1, 0, 0, 0, 0 };
                own.Data.MemoryStream.Write(previous, 0, previous.Length);
                data.InsertScene(scene).SaveObjects.Add(own);
            }
            PublishWorldBarrier(h, 22241, false); yield return WorldBarrier(h, 22241);
            if (NetPlay.IsHost)
            {
                GameObject a = new GameObject("W saved story sequence"), b = new GameObject("W saved platform sequence");
                try
                {
                    WorldSync.TestSendUnseenScripts(scene, UnseenScript(a, story, true), UnseenScript(b, platform, false));
                }
                finally { UnityEngine.Object.Destroy(a); UnityEngine.Object.Destroy(b); }
            }
            else
            {
                SaveScene archived = data.GetScene(scene);
                float until = Time.realtimeSinceStartup + 8f;
                while (!archived.SaveObjects.Exists(o => o.Id == platform) && Time.realtimeSinceStartup < until) yield return null;
                SaveObject ordinary = archived.SaveObjects.Find(o => o.Id == platform), local = archived.SaveObjects.Find(o => o.Id == story);
                h.Check(ordinary.Data != null && BitConverter.ToInt32(ordinary.Data.MemoryStream.ToArray(), 0) == 3,
                    "W unseen story: an ordinary mechanism in the same remote batch saves its step");
                h.Check(local.Data != null && BitConverter.ToInt32(local.Data.MemoryStream.ToArray(), 0) == 1,
                    "W unseen story: local checkpoint keeps its own step before this scene ever loads");
            }
            PublishWorldBarrier(h, 22242, false); yield return WorldBarrier(h, 22242);
        }
    }
}
