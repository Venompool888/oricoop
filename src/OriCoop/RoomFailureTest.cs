using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace OriCoop.Rooms
{
    // A real RoomsPanel, RoomClient and worker with a private local store and a scripted room service.
    internal static class RoomFailureTest
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        sealed class Service : IRoomHttp
        {
            public readonly ManualResetEvent InfoStarted = new ManualResetEvent(false);
            public readonly ManualResetEvent ReleaseInfo = new ManualResetEvent(false);
            public int Uploads, Enters, Leaves;
            public byte[] Post(string op, RoomWire.Fields fields, int timeoutMs)
            {
                string room = fields.GetString("room");
                RoomWire.Fields reply = new RoomWire.Fields();
                string error = null;
                if (op == "info" && room == "stale")
                { InfoStarted.Set(); ReleaseInfo.WaitOne(5000, false); error = "bad_token"; }
                else if (op == "info" && room.StartsWith("gone")) error = "no_room";
                else if (op == "info" && room == "offline") error = "network";
                else if (op == "upload") { Interlocked.Increment(ref Uploads); error = "network"; }
                else if (op == "enter")
                {
                    if (Interlocked.Increment(ref Enters) == 1) reply.Set("mode", "host"); else error = "not_member";
                }
                else if (op == "download") error = "network";
                else if (op == "leave") Interlocked.Increment(ref Leaves);
                else if (op == "info") { reply.Set("name", room); reply.Set("room", room); }
                else error = "bad_request";
                reply.Set("ok", error == null ? "1" : "0");
                if (error != null) reply.Set("error", error);
                return reply.Encode();
            }
        }
        static void Set(RoomsPanel panel, string name, object value)
        { typeof(RoomsPanel).GetField(name, Private).SetValue(panel, value); }
        static void Call(RoomsPanel panel, string name, params object[] args)
        { typeof(RoomsPanel).GetMethod(name, Private).Invoke(panel, args); }
        static IEnumerator Pump(RoomJobs jobs, Func<bool> finished)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!finished() && Time.realtimeSinceStartup < deadline) { jobs.Pump(); yield return null; }
        }
        public static IEnumerator Run(TestHarness h)
        {
            string path = Path.Combine(Path.GetTempPath(), "oricoop-room-ui-" + Guid.NewGuid().ToString("N"));
            RoomLocalStore store = new RoomLocalStore(path);
            Service service = new Service(); RoomJobs jobs = new RoomJobs();
            GameObject go = new GameObject("OriCoop test rooms panel");
            RoomsPanel previous = RoomsPanel.Instance;
            RoomsPanel panel = go.AddComponent<RoomsPanel>(); panel.enabled = false;
            int toasts = NetHud.QueuedToasts;
            bool wasCoop = Saves.CoopSaveFolder.Active, rendered = false;
            string expectedNotice = null;
            Action<string> onToast = delegate(string text) { if (text == expectedNotice) rendered = true; };
            try
            {
                RoomsPanel.Instance = panel; panel.Game = new RoomGame();
                Set(panel, "store", store); Set(panel, "jobs", jobs); Set(panel, "client", new RoomClient(service));
                Set(panel, "online", true); Set(panel, "server", "fixture"); Set(panel, "steam", "fixture");
                panel.OpenRoom("stale");
                yield return Pump(jobs, delegate { return service.InfoStarted.WaitOne(0, false); });
                h.Check(service.InfoStarted.WaitOne(0, false), "ROOMFAIL info is in flight before the page deadline");
                Call(panel, "RefreshGaveUp"); service.ReleaseInfo.Set();
                bool drained = false;
                jobs.Add(delegate { return new RoomReply(); }, delegate { drained = true; });
                yield return Pump(jobs, delegate { return drained; });
                h.Check(drained && panel.Screen != "password" && !panel.Busy, "ROOMFAIL expired detail reply leaves the password page closed");

                foreach (string room in new string[] { "gone1", "gone2", "offline", "live", "other" })
                    store.SaveRoom(new RoomBackupInfo { Id = room, Name = room, Server = room == "other" ? "elsewhere" : "fixture" });
                store.StoreVersion("gone1", "s", 4, new byte[] { 3, 2 }, "backup");
                Set(panel, "rooms", store.ListRooms()); Call(panel, "RefreshNext", panel.Rooms, 0);
                yield return Pump(jobs, delegate { return !panel.Busy; });
                List<RoomBackupInfo> rooms = store.ListRooms();
                h.Check(rooms.Count == 3 && !rooms.Exists(r => r.Id.StartsWith("gone")) && panel.Refreshed.ContainsKey("live"),
                    "ROOMFAIL refreshing removes both deleted rooms and still refreshes the live one");
                long version; string summary;
                h.Check(rooms.Exists(r => r.Id == "offline") && rooms.Exists(r => r.Id == "other")
                    && store.LoadLatest("gone1", "s", out version, out summary) != null && version == 4,
                    "ROOMFAIL temporary and other-service records and the local backup survive");

                Call(panel, "SetRoom", "live"); panel.Enter("s");
                yield return Pump(jobs, delegate { return panel.Message == RoomsPanel.ErrorText("network") && !panel.Busy; });
                bool free = !panel.Session.Active && service.Leaves == 1;
                panel.Enter("s");
                yield return Pump(jobs, delegate { return !panel.Busy; });
                h.Check(free && service.Enters == 2, "ROOMFAIL failed download releases the lease before the next Continue");

                // Start at an existing holder's checkpoint. The real upload and its error callback run normally.
                Call(panel, "SetRoom", "live"); Set(panel, "visible", false);
                RoomSession holder = panel.Session;
                foreach (string flag in new string[] { "Active", "Holder" })
                    typeof(RoomSession).GetProperty(flag).GetSetMethod(true).Invoke(holder, new object[] { true });
                typeof(RoomSession).GetProperty("Save").GetSetMethod(true).Invoke(holder, new object[] { "s" });
                h.Check(panel.Game.InGameplay, "ROOMFAIL actual gameplay is active at the checkpoint");
                // The normal room flow enables the co-op save folder, which makes the real HUD available.
                if (!wasCoop) Saves.CoopSaveFolder.Enter();
                NetHud.ToastShown += onToast;
                holder.Saved(new byte[] { 7 }, "checkpoint");
                drained = false; jobs.Add(delegate { return new RoomReply(); }, delegate { drained = true; });
                // Inspect the GUI queue during the same Pump that delivers the failed upload.
                float end = Time.realtimeSinceStartup + 5f;
                while (!drained && Time.realtimeSinceStartup < end) { jobs.Pump(); if (!drained) yield return null; }
                expectedNotice = NetHud.LastQueuedToast;
                bool queued = expectedNotice != null && expectedNotice.Contains(RoomsPanel.ErrorText("network"));
                end = Time.realtimeSinceStartup + 15f;
                while (queued && !rendered && Time.realtimeSinceStartup < end) yield return null;
                h.Check(service.Uploads == 1 && !panel.Visible && queued && rendered,
                    "ROOMFAIL failed checkpoint upload shows its reason on the gameplay HUD");
            }
            finally
            {
                service.ReleaseInfo.Set(); jobs.Dispose();
                RoomsPanel.Instance = previous; UnityEngine.Object.Destroy(go);
                NetHud.ToastShown -= onToast;
                NetHud.TestDropQueuedToasts(toasts);
                if (!wasCoop && Saves.CoopSaveFolder.Active) Saves.CoopSaveFolder.Leave();
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        }
    }
}
