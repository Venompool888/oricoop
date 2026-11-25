// The game adapter (OriCoop\Net\RealSteamApi.cs) needs the game's ManagedSteam assembly, which this console
// self-test does not reference; build-nettest.ps1 leaves it out and this stand-in keeps SteamApi.Current compiling.
namespace OriCoop.Net
{
    internal static class RealSteamApi
    {
        internal static ISteamApi GetCurrent() { return null; }
    }
}
