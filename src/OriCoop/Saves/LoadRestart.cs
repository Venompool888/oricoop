namespace OriCoop.Saves
{
    // Is a GameController.RestartGame SaveSync's own restart to load a save (a guest taking the host's checkpoint, the
    // host adopting the partner's progress)? Those restarts are not a return to the title: the online session
    // (TitleReturn) and the room session (RoomGame) go on.
    // T-157: once SaveSync has disabled itself (a load timed out or threw), it never restarts the game again
    // (BeginRestart returns at once) and its "loading" flag is never cleared, so any later restart is a real quit to the
    // title. Before, that quit was taken for the load: the room did not upload or give up its lease and the online
    // session did not end.
    internal static class LoadRestart
    {
        public static bool IsOwn(bool loading, bool disabled) { return loading && !disabled; }
    }
}
