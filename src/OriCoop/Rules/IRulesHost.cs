using System.Collections.Generic;
using Game;
using UnityEngine;

namespace OriCoop.Rules
{
    internal interface IRulesHost
    {
        int LocalSeat { get; }
        int RemoteCount { get; }
        int PuppetCount { get; }
        bool PartnerHere { get; }
        bool RemoteInGameplay { get; }
        bool RemotePos(int seat, out Vector3 position);
        SeinCharacter P1 { get; }
        SeinCharacter P2 { get; }
        bool Inside { get; }
        SeinCharacter SeinAt(int seat);
        bool SeatInSession(int seat);   // online: a teammate's seat in this session, even while it has no Ori (loading)
        IEnumerable<SeinCharacter> RemoteCharacters { get; }
        int SeatOf(SeinCharacter sein);
        int SeatOfSlot(int slot);
        bool IsPuppet(SeinCharacter sein);
        bool SaverHolding(SeinCharacter sein);
        void DropCarried(SeinCharacter sein);
        void SetCompanionActive(SeinCharacter sein, bool active);
        void ReturnCompanion(SeinCharacter sein, string reason);
        void Teleport(SeinCharacter sein, Vector3 position);
        bool CoopMenuOpen { get; }
        bool StoryWatching { get; }
        void InitStory();
        void TickStory();
        void ResetStory();
        void SessionStarted();
        void SessionEnded();
        void Toast(string message);
        void PushCheckpoint();
        bool SaveStarted { get; }
        bool SavePending { get; }
        bool SaveLoading { get; }
        bool SaveDisabled { get; }
        bool GuestWaiting { get; }
        bool GuestNeedsSlot { get; }
        bool PrologueWatching { get; }   // T-203: the host's new-game prologue plays on this PC too (no transfer screen)
        bool HostInPrologue { get; }     // T-203: the host is in its new-game prologue right now
        bool PrologueCatchingUp { get; } // T-205: this PC's copy fast-forwards to the host's place (covered meanwhile)
        float PrologueCatchUpProgress { get; }   // 0..1, -1 unknown
        bool IncomingProgress(out int have, out int total);
        bool L10nProbeOn { get; }
        string Text(string key);
        string Format(string key, params object[] args);
        bool NativeGuiAvailable { get; }
        void BeginPage(string page);
        void EndPage();
        void Label(string id, Rect rect, string text, float fontSize, TextAnchor anchor, Color color);
        void Panel(string id, Rect rect, Color color);
        void Trace(string subsystem, string name, params object[] values);
    }
}
