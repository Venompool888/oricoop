namespace OriCoop.Ui
{
    internal static class JoinErrorText
    {
        public static string Key(string raw, bool timedOut, bool steam)
        {
            string lower = (raw ?? "").ToLowerInvariant();
            // T-148: a refused C-server join ends with its L10n key (server.err.noroom / full / version / token) as the reason.
            if (lower.StartsWith("server.err.")) return raw;
            if (lower.Contains("no answer")) return steam ? "lobby.error.noanswer.host" : "lobby.error.noanswer";
            if (timedOut || lower.Contains("timeout") || lower.Contains("timed out")) return "lobby.error.timeout";
            if (lower.Contains("not found") || lower.Contains("no such room")) return "lobby.error.room";
            if (lower.Contains("full")) return "lobby.error.full";
            if (lower.Contains("resolve") || lower.Contains("host is known")) return "lobby.error.address";
            if (lower.Contains("steam")) return "lobby.error.steam";
            return "lobby.error.failed";
        }
    }
}
