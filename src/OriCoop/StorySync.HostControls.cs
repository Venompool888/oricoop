using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    public static partial class StorySync
    {
        const byte HostControlMessage = 104;
        static ConfigEntry<bool> hostControls;
        static readonly StoryControls controlStates = new StoryControls();
        static readonly Dictionary<MessageBox, SharedControl> boxes = new Dictionary<MessageBox, SharedControl>();
        static readonly Dictionary<ulong, Dictionary<ActionMethod, int>> runs = new Dictionary<ulong, Dictionary<ActionMethod, int>>();
        static readonly List<ulong> oldRuns = new List<ulong>();
        static readonly List<MessageBox> openBoxes = new List<MessageBox>();
        static SharedControl skipControl;
        static float nextControlTick;
        static bool applyingControl;
        internal static int RequestsShown, RequestsSent;
        internal static bool TestDropControlHeartbeats;
        static readonly FieldInfo registeredSkip = AccessTools.Field(typeof(SkipCutsceneController), "m_skipCutsceneAction");

        sealed class SharedControl
        {
            internal ActionMethod Action;
            internal ActionSequence Sequence;
            internal string Key;
            internal Story Run;
            internal float MatchedAt;
            internal int ConfirmedHost;
            internal bool Closed;
            internal int Requested = int.MinValue;
            internal int SentRequest = int.MinValue;
            internal int ShownRequest = int.MinValue;
            internal int Published = -2;
            internal float Resend;
        }

        static void InitControls()
        {
            hostControls = Plugin.Instance.Config.Bind("Story", "HostControlsSharedStory", true,
                "When hosting, members can request to advance a shared dialogue or skip a shared cutscene. The host confirms using the normal dialogue or skip button. Personal tutorials, menu messages and offline play keep their native controls.");
            Services.Net.On(HostControlMessage, ReceiveControl);
            Services.Net.SessionStarted += ResetControls;
            Services.Net.SessionEnded += ResetControls;
        }

        static void ResetControls()
        {
            controlStates.Clear(); boxes.Clear(); skipControl = null; runs.Clear(); nextControlTick = 0f;
        }

        static bool HostControlsEnabled { get { return hostControls != null && hostControls.Value && Online && Services.Net.IsHost; } }

        static void ReceiveControl(BinaryReader reader)
        {
            if (!Services.Net.Enabled || !Services.Net.Connected) return;
            int sender = Services.Net.Sender;
            if (reader.BaseStream.Position >= reader.BaseStream.Length) return;
            byte kind = reader.ReadByte();
            if (kind == 2)
            {
                if (reader.BaseStream.Length - reader.BaseStream.Position == 4 && Services.Net.IsHost &&
                    sender >= 1 && sender <= 4 && sender != Services.Net.HostSeat)
                    SharedPrologue.ReceiveSkipRequest(reader.ReadUInt32());
                return;
            }
            if (!Online) return;
            if (kind == 0 && (Services.Net.IsHost || sender != Services.Net.HostSeat)) return;
            if (kind == 1 && (!HostControlsEnabled || sender < 1 || sender > 4 || sender == Services.Net.HostSeat)) return;
            string key; int page;
            if (kind > 1 || !StoryControls.ReadAction(reader, out key, out page)) return;
            if (kind == 0) controlStates.Receive(sender, Services.Net.HostSeat, key, page, Time.realtimeSinceStartup);
            else
            {
                if (skipControl != null && CurrentSkip(skipControl) && ShowRequest(skipControl, key, page, 0, true)) return;
                foreach (var item in boxes)
                    if (item.Key != null && ShowRequest(item.Value, key, page, item.Key.MessageIndex, false)) return;
            }
        }

        static bool Belongs(SharedControl target, Story story)
        {
            if (!story.Classified || story.Watch || story.EndedEarly || story.EndAt >= 0) return false;
            if (target.Sequence != null && (target.Sequence == story.Sequence || target.Sequence == story.PlayedContinuation)) return true;
            Story owner;
            if (MirrorOwners.TryGetValue(target.Action, out owner)) return owner == story;
            return Below(target.Action.transform, story.Sequence) || Below(target.Action.transform, story.Controller);
        }

        static bool Match(SharedControl target)
        {
            if (target == null || target.Action == null) return false;
            if (target.Run != null)
            {
                ulong id = TokenKey(target.Run.Sender, target.Run.Token);
                Story live;
                return target.Key.Length <= StoryControls.MaxActionKey &&
                    (Local.TryGetValue(id, out live) || Remote.TryGetValue(id, out live)) &&
                    live == target.Run && !live.Watch && !live.EndedEarly && live.EndAt < 0;
            }
            Story match = null;
            foreach (Story story in Local.Values)
                if (story.StartSent && Belongs(target, story)) { if (match != null) return false; match = story; }
            foreach (Story story in Remote.Values)
                if (story.Started && Belongs(target, story)) { if (match != null) return false; match = story; }
            if (match == null || LocalOnlyScoped(match)) return false;
            ulong token = TokenKey(match.Sender, match.Token);
            Dictionary<ActionMethod, int> visits;
            if (!runs.TryGetValue(token, out visits)) runs.Add(token, visits = new Dictionary<ActionMethod, int>());
            int visit; visits.TryGetValue(target.Action, out visit); visits[target.Action] = ++visit;
            // Same-type components can share a transform. The component ordinal disambiguates them.
            Component[] siblings = target.Action.GetComponents(target.Action.GetType());
            string identity = target.Action.GetType().Name + "/" + ComponentKey(target.Action) + "/" + Array.IndexOf(siblings, target.Action);
            target.Run = match; target.MatchedAt = Time.realtimeSinceStartup;
            target.Key = StoryControls.Key(match.Sender, match.Token, identity, visit);
            return target.Key.Length <= StoryControls.MaxActionKey;
        }

        static void PublishControl(SharedControl target, int page, bool repeat)
        {
            if (!Services.Net.IsHost || !Match(target)) return;
            if (!HostControlsEnabled) page = StoryControls.Native;
            float now = Time.realtimeSinceStartup;
            if (page == target.Published && (!repeat || now < target.Resend || TestDropControlHeartbeats)) return;
            string key = target.Key;
            Services.Net.Send(HostControlMessage, true, delegate(BinaryWriter w) { w.Write((byte)0); w.Write(key); w.Write(page); });
            target.Published = page; target.Resend = now + 1.5f;
        }

        static bool HostOwns(SharedControl target, out int page)
        {
            page = 0;
            if (applyingControl || Services.Net.IsHost || !Online || !Match(target)) return false;
            return controlStates.ControlsAction(Services.Net.HostSeat, target.Key, Time.realtimeSinceStartup,
                target.MatchedAt, ref target.ConfirmedHost, out page);
        }

        static bool ShowRequest(SharedControl target, string key, int requested, int current, bool cutscene)
        {
            if (target.Closed || !Match(target) || !StoryControls.AcceptRequest(target.Key, current, key, requested, ref target.ShownRequest)) return false;
            RequestsShown++;
            NetHud.Toast(L10n.T(cutscene ? "story.request.cutscene" : "story.request.dialogue"));
            return true;
        }

        static void SendPending(SharedControl target)
        {
            int page = target.Requested;
            int active;
            if (page < 0 || target.SentRequest == page ||
                !controlStates.TryGet(Services.Net.HostSeat, target.Key, Time.realtimeSinceStartup, out active) || active != page) return;
            SendRequestToHost(delegate(BinaryWriter w) { w.Write((byte)1); w.Write(target.Key); w.Write(page); });
            RequestsSent++;
            target.SentRequest = page;
        }

        internal static void SendRequestToHost(Action<BinaryWriter> body)
        {
            // A P2P guest has exactly one peer (the host); SendTo is host-only on that transport.
            if (Authority.NetRoute.ServerMode) Services.Net.SendTo(Services.Net.HostSeat, HostControlMessage, true, body);
            else Services.Net.Send(HostControlMessage, true, body);
        }

        static bool Request(SharedControl target, int page)
        {
            int hostPage;
            if (!HostOwns(target, out hostPage)) return false;
            if (hostPage != StoryControls.Closed && target.Requested != page)
            {
                target.Requested = page;
                NetHud.Toast(L10n.T("story.request.sent"));
                SendPending(target);
            }
            return true;
        }

        static void WatchBox(ShowStoryTextAction action)
        {
            MessageBox box = StoryMessageBox.GetValue(action) as MessageBox;
            if (box != null && Interactive(box) != null) boxes[box] = new SharedControl { Action = action, Sequence = s_inputSequence };
        }

        static void CloseBox(ShowStoryTextAction action)
        {
            MessageBox box = StoryMessageBox.GetValue(action) as MessageBox;
            SharedControl target;
            if (box == null || !boxes.TryGetValue(box, out target)) return;
            PublishControl(target, StoryControls.Closed, false);
            boxes.Remove(box);
        }

        static InteractiveMessageBox Interactive(MessageBox box)
        {
            InteractiveMessageBox input = box.GetComponentInParent<InteractiveMessageBox>();
            return input != null && input.MessageBox == box ? input : null;
        }

        static void TickControls()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextControlTick) return;
            nextControlTick = now + 0.1f;
            controlStates.Expire(Services.Net.HostSeat, now);
            if (boxes.Count != 0)
            {
                openBoxes.Clear(); openBoxes.AddRange(boxes.Keys);
                foreach (MessageBox box in openBoxes)
                {
                    SharedControl target;
                    if (box == null) { boxes.Remove(box); continue; }
                    if (!boxes.TryGetValue(box, out target)) continue;
                    if (Services.Net.IsHost) { PublishControl(target, box.MessageIndex, true); continue; }
                    int page;
                    if (!HostOwns(target, out page)) continue;
                    SendPending(target);
                    InteractiveMessageBox input = Interactive(box);
                    if (input == null) continue;
                    applyingControl = true;
                    try
                    {
                        if (page == StoryControls.Closed) input.EndMessageBox();
                        else
                        {
                            while (box.MessageIndex < page && !box.IsLastMessage)
                            {
                                box.NextMessage(); input.ChangeState(InteractiveMessageBox.State.Writing);
                            }
                        }
                    }
                    finally { applyingControl = false; }
                }
            }
            if (skipControl != null && !skipControl.Closed && CurrentSkip(skipControl))
            {
                if (Services.Net.IsHost) PublishControl(skipControl, 0, true);
                else if (Match(skipControl)) SendPending(skipControl);
                int page;
                if (HostOwns(skipControl, out page) && page == StoryControls.Closed)
                {
                    applyingControl = true;
                    try { skipControl.Closed = true; SkipCutsceneController.Instance.SkipCutscene(); }
                    finally { applyingControl = false; }
                }
            }
            if (runs.Count > 0)
            {
                oldRuns.Clear();
                foreach (ulong token in runs.Keys) if (!Local.ContainsKey(token) && !Remote.ContainsKey(token)) oldRuns.Add(token);
                foreach (ulong token in oldRuns) runs.Remove(token);
            }
        }

        static bool CurrentSkip(SharedControl target)
        {
            RegisterSkipCutsceneAction register = target.Action as RegisterSkipCutsceneAction;
            if (register == null || register.MasterTimelineSequence == null || SkipCutsceneController.Instance == null) return false;
            Action callback = registeredSkip.GetValue(SkipCutsceneController.Instance) as Action;
            return callback != null && object.ReferenceEquals(callback.Target, register.MasterTimelineSequence);
        }

        [HarmonyPatch(typeof(RegisterSkipCutsceneAction), "Perform")]
        static class RegisterSharedSkip
        {
            static void Postfix(RegisterSkipCutsceneAction __instance)
            {
                skipControl = Online && !PuppetCall ? new SharedControl { Action = __instance, Sequence = s_inputSequence } : null;
            }
        }

        static bool ControlledSkip(out bool allow)
        {
            allow = true;
            if (applyingControl) return true;
            if (skipControl == null || !Online || !CurrentSkip(skipControl)) return false;
            if (HostControlsEnabled && Match(skipControl))
            {
                PublishControl(skipControl, StoryControls.Closed, false);
                skipControl.Closed = true; return true;
            }
            if (!Request(skipControl, 0)) return false;
            allow = false; return true;
        }

        // Hook native player input only. Programmatic closes (death, teardown, or a scene change)
        // must never be suppressed. A player may still finish animating the current page locally.
        static bool RequestBox(InteractiveMessageBox input)
        {
            SharedControl target;
            return input.MessageBox != null && boxes.TryGetValue(input.MessageBox, out target) && Request(target, input.MessageBox.MessageIndex);
        }

        [HarmonyPatch(typeof(InteractiveMessageBox), "UpdateWriting")]
        static class SharedTextWriting
        {
            static void Prefix(InteractiveMessageBox __instance)
            {
                if (__instance.CanCancel && Core.Input.Cancel.OnPressedNotUsed && RequestBox(__instance))
                    Core.Input.Cancel.Used = true;
            }
        }

        [HarmonyPatch(typeof(InteractiveMessageBox), "UpdateComplete")]
        static class SharedTextAdvance
        {
            static bool Prefix(InteractiveMessageBox __instance)
            {
                if (!__instance.OnButtonPressed && !(__instance.CanCancel && Core.Input.Cancel.OnPressedNotUsed)) return true;
                if (!RequestBox(__instance)) return true;
                if (__instance.OnButtonPressed) { Core.Input.Jump.Used = true; Core.Input.SpiritFlame.Used = true; }
                if (__instance.CanCancel && Core.Input.Cancel.OnPressedNotUsed) Core.Input.Cancel.Used = true;
                return false;
            }
        }
    }
}
