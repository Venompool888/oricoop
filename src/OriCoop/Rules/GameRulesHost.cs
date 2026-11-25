using System.Collections.Generic;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop.Rules
{
    internal sealed class GameRulesHost : IRulesHost
    {
        public int LocalSeat { get { return Players.LocalSeat; } }
        public int RemoteCount { get { return NetPlay.RemoteCount; } }
        public int PuppetCount { get { return Players.Remotes.Count; } }
        public bool PartnerHere { get { return NetPlay.PartnerHere; } }
        public bool RemoteInGameplay { get { return NetPlay.RemoteInGameplay; } }
        public bool RemotePos(int seat, out Vector3 position) { return NetPlay.RemotePos(seat, out position); }
        public SeinCharacter P1 { get { return P2Context.P1; } }
        public SeinCharacter P2 { get { return P2Context.P2; } }
        public bool Inside { get { return P2Context.Inside; } }
        public SeinCharacter SeinAt(int seat)
        {
            if (seat == Players.LocalSeat) return P2Context.P1;
            foreach (Puppet p in Players.Remotes) if (p.Seat == seat) return p.Sein;
            return null;
        }
        public bool SeatInSession(int seat) { return NetPlay.Enabled && NetPlay.RemoteInSession(seat); }
        public IEnumerable<SeinCharacter> RemoteCharacters
        {
            get { foreach (Puppet p in Players.Remotes) yield return p == null ? null : p.Sein; }
        }
        public int SeatOf(SeinCharacter sein) { return Players.SeatOf(sein); }
        public int SeatOfSlot(int slot) { return Roles.OfSlot(slot); }
        public bool IsPuppet(SeinCharacter sein) { return Players.OfSein(sein) != null; }
        public bool SaverHolding(SeinCharacter sein)
        {
            Puppet p = Players.OfSein(sein);
            if (p != null) return p.Input.SpiritFlame.IsPressed;
            return Core.Input.SpiritFlame.IsPressed;
        }
        public void DropCarried(SeinCharacter sein) { P2Lifecycle.DropCarried(sein); }
        public void SetCompanionActive(SeinCharacter sein, bool active) { P2Extras.SetCompanionActive(Players.OfSein(sein), active); }
        public void ReturnCompanion(SeinCharacter sein, string reason) { P2Extras.ReturnCompanion(Players.OfSein(sein), reason); }
        public void Teleport(SeinCharacter sein, Vector3 position) { Plugin.Teleport(sein, position); }
        public bool CoopMenuOpen { get { return CoopMenu.IsOpen; } }
        public bool StoryWatching { get { return StorySync.Watching; } }
        public void InitStory() { StorySync.Init(); }
        public void TickStory() { StorySync.Tick(); }
        public void ResetStory() { StorySync.Reset(); }
        public void SessionStarted() { NetHud.OnSessionStarted(); }
        public void SessionEnded() { NetHud.OnSessionEnded(); }
        public void Toast(string message) { NetHud.Toast(message); }
        public void PushCheckpoint() { SaveSync.PushCheckpointNow(); }
        public bool SaveStarted { get { return SaveSync.TransferStarted; } }
        public bool SavePending { get { return SaveSync.HavePendingCheckpoint; } }
        public bool SaveLoading { get { return SaveSync.TransferLoading; } }
        public bool SaveDisabled { get { return SaveSync.TransferDisabled; } }
        public bool GuestWaiting { get { return GuestJoin.Waiting; } }
        public bool GuestNeedsSlot { get { return GuestJoin.NeedSlotChoice; } }
        public bool PrologueWatching { get { return SharedPrologue.Watching; } }
        public bool HostInPrologue { get { return SharedPrologue.HostPlaying; } }
        public bool PrologueCatchingUp { get { return SharedPrologue.CatchingUp; } }
        public float PrologueCatchUpProgress { get { return SharedPrologue.CatchUpProgress; } }
        public bool IncomingProgress(out int have, out int total)
        {
            have = total = 0;
            return NetPlay.Peer != null && NetPlay.Peer.IncomingProgress(out have, out total);
        }
        public bool L10nProbeOn { get { return L10nProbe.On; } }
        public string Text(string key) { return L10n.SessionText(key); }
        public string Format(string key, params object[] args) { return L10n.F(key, args); }
        public bool NativeGuiAvailable { get { return NativeGui.Available; } }
        public void BeginPage(string page) { NativeGui.BeginPage(page); }
        public void EndPage() { NativeGui.EndPage(); }
        public void Label(string id, Rect rect, string text, float fontSize, TextAnchor anchor, Color color)
        { NativeGui.Label(id, rect, text, fontSize, anchor, color); }
        public void Panel(string id, Rect rect, Color color) { NativeGui.Panel(id, rect, color); }
        public void Trace(string subsystem, string name, params object[] values) { CoopTrace.Event(subsystem, name, values); }
        [HarmonyPatch(typeof(NetPlay), "DrawGUI")]
        static class Status
        {
            static void Postfix()
            {
                // NetHud owns the status display and toasts now.
            }
        }
    }
}
