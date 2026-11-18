using System;
using Game;
using OriCoop;

static class PlayersTests
{
    static int Main()
    {
        Puppet local = new Puppet { Seat = 2, IsLocal = true, Sein = new SeinCharacter() };
        Puppet remote = new Puppet { Seat = 3, IsLocal = false, Sein = new SeinCharacter() };
        Players.Remotes.Add(local); Players.Remotes.Add(remote);
        bool components = !Players.IsRemote(local.Sein) && Players.IsRemote(remote.Sein) && !Players.IsRemote(null);
        P2Context.Current = local;
        bool localContext = !Players.RemoteContext;
        P2Context.Current = remote;
        bool remoteContext = Players.RemoteContext;
        P2Context.Current = null;
        bool primaryContext = !Players.RemoteContext;
        bool passed = components && localContext && remoteContext && primaryContext;
        Console.WriteLine("PLAYERS local/remote component and context ownership " + (passed ? "PASS" : "FAIL"));
        return passed ? 0 : 1;
    }
}
