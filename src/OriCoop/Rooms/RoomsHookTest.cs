using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Game;
using UnityEngine;

namespace OriCoop
{
    public static partial class NativeModeMenu
    {
        internal static IEnumerator RoomsTestMenu(MonoBehaviour host, bool start)
        {
            s_tHost = host;
            if (start) yield return host.StartCoroutine(Row(1, 0.5f));
            else
            {
                yield return host.StartCoroutine(PressCoop());
                yield return host.StartCoroutine(Row(Array.IndexOf(TitleTransports, "relay"), 0.5f));
                // Since the C-server menu (950e23c) the relay entry first asks equal / relay mode (row 1 = relay); the
                // walk stopped on that page and never saw My rooms (T-082).
                if (LastPage == "servermode") yield return host.StartCoroutine(Row(1, 0.5f));
            }
        }
    }
}

namespace OriCoop.Rooms
{
    // Real title/menu + session + save hooks. No synthetic enter/upload/leave calls to the server.
    public sealed class RoomsHookTest : MonoBehaviour
    {
        internal static bool Enabled
        {
            get
            {
                string role = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_TEST");
                return TestHarness.Enabled && (role == "host" || role == "join");
            }
        }

        [HarmonyPatch(typeof(TestHarness), "Awake")]
        static class Boot
        {
            static bool Prefix(TestHarness __instance)
            {
                if (!Enabled) return true;
                __instance.enabled = false;
                __instance.gameObject.AddComponent<RoomsHookTest>();
                return false;
            }
        }

        static string joinedTarget;
        static volatile string postedLive;

        [HarmonyPatch(typeof(RoomClient), "Live")]
        static class ObserveLive
        {
            static void Postfix(string liveCode, RoomReply __result)
            {
                if (Enabled && __result.Ok) postedLive = liveCode;
            }
        }

        [HarmonyPatch(typeof(RoomGame), "Join")]
        static class ObserveJoin
        {
            static void Prefix(string target) { if (Enabled) joinedTarget = target; }
        }

        int checks;
        bool done;
        float began;
        string role, share, room, save;
        RoomsPanel panel;
        RoomReply observed;
        RoomAuth auth;
        RoomClient client;
        RoomJobs reads;
        long upload;

        static object Get(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        void Set(string name, object value)
        {
            typeof(RoomsPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, value);
        }

        bool Check(bool ok, string text)
        {
            Plugin.Log.LogInfo("ROOMS " + (ok ? "PASS " : "FAIL ") + (++checks) + " " + text);
            if (!ok) Finish(false);
            return ok;
        }

        void Finish(bool ok)
        {
            if (done) return;
            done = true;
            Plugin.Log.LogInfo("ROOMS DONE " + (ok ? "ok" : "fail"));
        }

        void Start()
        {
            began = Time.realtimeSinceStartup;
            role = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_TEST");
            share = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_SHARE");
            reads = new RoomJobs();
            StartCoroutine(Guard(Run()));
        }

        // Catch exceptions from nested iterators as well as the outer test, always producing a final result.
        IEnumerator Guard(IEnumerator run)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(run);
            while (stack.Count > 0 && !done)
            {
                object next = null;
                bool moved = false;
                try { moved = stack.Peek().MoveNext(); if (moved) next = stack.Peek().Current; }
                catch (Exception e) { Check(false, e.ToString()); }
                if (!moved) stack.Pop();
                else if (next is IEnumerator) stack.Push((IEnumerator)next);
                else yield return next;
            }
        }

        void Update()
        {
            Application.runInBackground = true;
            if (reads != null) reads.Pump();
            if (done) return;
            if (Time.realtimeSinceStartup - began > 345) Check(false, "overall timeout");
            if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart)
                TestHarness.PulseMenu = true;
            if (UI.MainMenuVisible && GameController.Instance != null && GameController.Instance.IsLoadingGame)
            {
                UI.Menu.HideResumeScreen();
                UI.Menu.HideMenuScreen(true);
            }
        }

        void OnDestroy() { if (reads != null) reads.Dispose(); }

        IEnumerator Wait(Func<bool> ready, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!done && !ready() && Time.realtimeSinceStartup < end) yield return null;
        }

        IEnumerator ReadInfo()
        {
            observed = null;
            reads.Add(delegate { return client.Info(auth); }, delegate(RoomReply r) { observed = r; });
            yield return Wait(() => observed != null, 18);
            if (!Check(observed != null && observed.Ok, "info readable")) yield break;
        }

        // The two clients replace share.dat atomically, but a read that lands inside the other client's replace can find
        // no file for a moment (host: FileNotFoundException at step 14, T-082). Retry briefly instead of failing the run.
        RoomWire.Fields Shared()
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return SharedOnce(); }
                catch (IOException) { if (attempt >= 40) throw; System.Threading.Thread.Sleep(25); }
            }
        }

        RoomWire.Fields SharedOnce()
        {
            if (!File.Exists(share)) return new RoomWire.Fields();
            using (var file = new FileStream(share, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var bytes = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) > 0) bytes.Write(buffer, 0, count);
                return RoomWire.Fields.Decode(bytes.ToArray());
            }
        }

        void Publish(string key, string value)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    RoomWire.Fields fields = Shared();
                    fields.Set(key, value);
                    RoomWire.AtomicWrite(share, fields.Encode());
                    return;
                }
                catch (IOException) { if (attempt >= 40) throw; System.Threading.Thread.Sleep(25); }
            }
        }

        IEnumerator Click(string screen, string id, string label)
        {
            bool clicked = false;
            yield return Wait(() => clicked = panel.TestClick(screen, id), 15);
            if (done) yield break;
            string detail = "screen '" + screen + "' button '" + label + "' (" + id + ")";
            if (!clicked)
            {
                detail = "timeout waiting for " + detail + "; " + panel.TestDrawState;
                if (id == "create-slot-0")
                    detail += "; slot 0 path=" + RoomGame.PathFor(0)
                        + ", exists=" + File.Exists(RoomGame.PathFor(0)) + ", slots=" + SaveSlotsManager.SaveSlotCount;
            }
            Check(clicked, detail);
        }

        bool Idle { get { return !(bool)Get(panel, "busy"); } }
        bool RoomReady { get { return Idle && Get(panel, "info") != null && panel.Session != null; } }

        IEnumerator Run()
        {
            string relay = Environment.GetEnvironmentVariable("ORICOOP_NET_RELAY") ?? "";
            Uri url;
            string server = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_SERVER");
            if (!Check(Uri.TryCreate(server, UriKind.Absolute, out url) && url.Scheme == "http"
                && url.Host == "127.0.0.1" && relay.StartsWith("127.0.0.1:"), "localhost services")) yield break;
            NetPlay.SetRelayServer(relay);
            yield return Wait(() => NativeModeMenu.TitleItemsReady
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, 100);
            if (!Check(NativeModeMenu.TitleItemsReady
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, "title ready")) yield break;
            yield return NativeModeMenu.RoomsTestMenu(this, false);
            if (!Check(NativeModeMenu.LastLabels.Exists(x => x.Contains("我的房间")
                || x.IndexOf("My rooms", StringComparison.OrdinalIgnoreCase) >= 0), "自建服务器 My rooms row"))
                yield break;
            // Dispatch the actual My rooms row, not an independent panel initialization.
            typeof(NativeModeMenu).GetMethod("TitlePressed", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { 1 });   // the 自建服务器 page's My rooms row (T-045)
            panel = RoomsPanel.Instance;
            if (!Check(panel != null, "My rooms opens panel")) yield break;
            yield return Wait(() => Idle, 25);
            if (!Check(Idle && (bool)Get(panel, "online"), "room service hello")) yield break;
            if (role == "host")
            {
                yield return Click("list", "create-room", "???? / Create room");
                if (done) yield break;
                Set("roomName", "Rooms hookup test");
                Set("password", "test-pass");
                yield return Click("create", "create-slot-0", "?? / Create ? slot 0");
                if (done) yield break;
            }
            else
            {
                yield return Wait(() => Shared().GetString("live").Length > 0, 150);
                if (!Check(Shared().GetString("live").Length > 0, "host published shared live code")) yield break;
                yield return Click("list", "join-code", "?????? / Join by code");
                if (done) yield break;
                Set("code", Shared().GetString("code"));
                Set("password", "test-pass");
                yield return Click("join", "join-room", "?? / Join");
                if (done) yield break;
            }
            yield return Wait(() => RoomReady, 25);
            if (!Check(RoomReady, role + " authenticated room")) yield break;
            auth = (RoomAuth)Get(panel, "auth");
            client = (RoomClient)Get(panel, "client");
            room = auth.Room;
            var info = (RoomWire.Fields)Get(panel, "info");
            save = info.GetString("save.0.id");
            if (role == "host")
            {
                Publish("room", room);
                Publish("code", info.GetString("code"));
            }
            else if (!Check(room == Shared().GetString("room"), "joined shared room id")) yield break;
            yield return Click("room", "continue-" + save, "?? / Continue");
            if (done) yield break;
            if (role == "host") yield return Host();
            else yield return Join();
            if (!done) Finish(true);
        }

        IEnumerator Host()
        {
            yield return Wait(() => panel.Game.Slot >= 0 && NativeModeMenu.LastPage == "hosting"
                && !string.IsNullOrEmpty(NetPlay.RoomCode), 40);
            if (!Check(panel.Session.Holder && panel.Game.Slot >= 0
                && NativeModeMenu.LastPage == "hosting" && !string.IsNullOrEmpty(NetPlay.RoomCode),
                "enter host opens title host slot")) yield break;
            yield return NativeModeMenu.RoomsTestMenu(this, true);
            yield return Wait(() => NetPlay.LocalInGameplay(), 90);
            if (!Check(NetPlay.LocalInGameplay() && SaveSlotsManager.CurrentSlotIndex == panel.Game.Slot,
                "room slot loaded and gameplay running")) yield break;
            // live() is queued independently of game loading; allow the job to finish before asserting.
            float liveEnd = Time.realtimeSinceStartup + 20;
            do
            {
                yield return ReadInfo();
                if (done) yield break;
                if (postedLive == NetPlay.RoomCode) break;
                yield return new WaitForSeconds(0.25f);
            } while (Time.realtimeSinceStartup < liveEnd);
            if (!Check(observed.Fields.GetString("save.0.holder") == auth.Steam
                && postedLive == NetPlay.RoomCode,
                "live code published, holder is me")) yield break;
            Publish("live", NetPlay.RoomCode);
            yield return Wait(() => NetPlay.PartnerHere && Shared().GetString("playing") == "1", 100);
            if (!Check(NetPlay.PartnerHere && Shared().GetString("playing") == "1",
                "partner connected and in gameplay")) yield break;
            yield return ReadInfo();
            if (done) yield break;
            long before = observed.Fields.GetLong("save.0.version", 0);
            yield return Wait(() => GameController.Instance.SaveGameController.CanPerformSave(), 15);
            if (!Check(GameController.Instance.SaveGameController.CanPerformSave(), "game ready to save")) yield break;
            GameController.Instance.SaveGameController.PerformSave();
            yield return Wait(() => panel.Session.Version > before, 25);
            yield return ReadInfo();
            if (done) yield break;
            upload = observed.Fields.GetLong("save.0.version", 0);
            if (!Check(upload == before + 1 && panel.Session.Version == upload,
                "PerformSave upload version +1: " + upload)) yield break;
            Publish("upload", upload.ToString());
            yield return Wait(() => Shared().GetString("backup") == upload.ToString(), 90);
            if (!Check(Shared().GetString("backup") == upload.ToString(), "guest acknowledged upload backup"))
                yield break;
            GameController.Instance.RestartGame();
            yield return Wait(() => !panel.Session.Active
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, 45);
            yield return ReadInfo();
            if (done) yield break;
            Check(!panel.Session.Active
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu
                && observed.Fields.GetLong("save.0.version", 0) == upload + 1
                && observed.Fields.GetString("save.0.holder").Length == 0,
                "return title snapshot version +1 and lease free");
        }

        IEnumerator Join()
        {
            yield return Wait(() => NetPlay.PartnerHere && NetPlay.LocalInGameplay(), 100);
            string target = Environment.GetEnvironmentVariable("ORICOOP_NET_RELAY") + "|"
                + Shared().GetString("live");
            if (!Check(joinedTarget == target && panel.Session.Active && !panel.Session.Holder && !NetPlay.IsHost
                && NetPlay.PartnerHere && NetPlay.LocalInGameplay() && NetPlay.RoomCode == Shared().GetString("live"),
                "enter join with host live code, connected in gameplay")) yield break;
            Publish("playing", "1");
            var store = (RoomLocalStore)Get(panel, "store");
            long backup = 0;
            yield return Wait(delegate
            {
                upload = Shared().GetLong("upload", 0);
                string summary;
                byte[] data = store.LoadLatest(room, save, out backup, out summary);
                return upload > 0 && data != null && data.Length > 0 && backup == upload;
            }, 90);
            if (!Check(upload > 0 && backup == upload, "local backup has host upload version " + backup)) yield break;
            Publish("backup", backup.ToString());
        }
    }
}
