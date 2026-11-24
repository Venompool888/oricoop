using System;
using UnityEngine;

namespace OriCoop.Saves
{
    internal sealed class GameSavesHost : ISavesHost
    {
        public bool LocalInGameplay() { return NetPlay.LocalInGameplay(); }
        public uint NewestRemoteTick { get { return NetPlay.NewestRemoteTick; } }
        public bool HaveRemotePosition { get { return NetPlay.HaveRemotePos; } }
        public SavePosition LatestRemotePosition
        {
            get
            {
                Vector3 p = NetPlay.LatestRemotePos;
                SavePosition result = new SavePosition(); result.X = p.x; result.Y = p.y; result.Z = p.z;
                return result;
            }
        }
        public bool UsesSteam { get { return NetPlay.UsesSteam; } }
        public string SteamPartnerName { get { return NetPlay.SteamPartnerName; } }
        public bool LocalizationProbeOn { get { return L10nProbe.On; } }
        public void End(string reason) { NetPlay.End(reason); }
        public void SendWorldSnapshot(int seat) { WorldSync.SendSnapshot(seat); }
        public void SendProgressSnapshot(int seat) { ProgressSync.SendSnapshot(seat); }
        public void HoldOwnSkillTree(bool joined) { ProgressSync.HoldOwnSkillTree(joined); }
        public string Translate(string key) { return L10n.SessionText(key); }
        public string Format(string key, params object[] args) { return L10n.SessionFormat(key, args); }
        public void Toast(string text, string secondary) { NetHud.Toast(text, secondary); }
        public void SetText(object box, Func<string> text) { L10n.Set((MessageBox)box, text); }
        public void EnsureSinglePlayerPending() { if (NativeModeMenu.Pending == PlayMode.Undecided) NativeModeMenu.Pending = PlayMode.Single; }
    }
}
