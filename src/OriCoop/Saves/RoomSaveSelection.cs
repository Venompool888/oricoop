namespace OriCoop.Saves
{
    public static class RoomSaveSelection
    {
        public static bool CanSelect(bool host, bool inGameplay, bool connected, bool linkedRoom)
        { return host && !inGameplay && !connected && !linkedRoom; }
    }
}
