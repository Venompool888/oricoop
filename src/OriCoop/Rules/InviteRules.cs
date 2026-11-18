namespace OriCoop.Rules
{
    // What a Steam invite accepted while another online session runs does (SteamInviteHook; T-031).
    // Unit tests: src\Tests\Rules.
    public static class InviteRules
    {
        // End the current session and join the invite: only when nobody else is in it and this player is in the
        // menus (not in gameplay, not loading a save). Anything else would throw a running game away.
        public static bool ReplacesSession(bool online, bool partnerHere, bool inGameplay, bool loading)
        {
            return online && !partnerHere && !inGameplay && !loading;
        }

        // Banner while the invite waits. Playing with someone: that session must end first (returning to the title
        // ends it, then the join starts by itself). Otherwise returning to the title is all it takes.
        public static string BannerKey(bool online, bool partnerHere)
        {
            return online && partnerHere ? "steam.invite_end" : "steam.invite_menu";
        }
    }
}
