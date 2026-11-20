using System;

namespace OriCoop.Saves
{
    public interface ISavesHost
    {
        bool LocalInGameplay();
        uint NewestRemoteTick { get; }
        bool HaveRemotePosition { get; }
        SavePosition LatestRemotePosition { get; }
        bool UsesSteam { get; }
        string SteamPartnerName { get; }
        bool LocalizationProbeOn { get; }
        void End(string reason);
        void SendWorldSnapshot(int seat);
        void SendProgressSnapshot(int seat);
        void HoldOwnSkillTree();                 // T-142: guest, before loading a mid-session host checkpoint
        string Translate(string key);
        string Format(string key, params object[] args);
        void Toast(string text, string secondary);
        void SetText(object box, Func<string> text);
        void EnsureSinglePlayerPending();
    }

    public static class SavesHost
    {
        public static ISavesHost Current;
    }
}
