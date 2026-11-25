using System;
using OriCoop.Authority;

static class StoneSeedTests
{
    const string Key = "w:push:710";

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static EnemyOwnerPolicyTests.LeaseRig LastGuestLeft()
    {
        var rig = new EnemyOwnerPolicyTests.LeaseRig();
        rig.Clients[2].RequestLease(Key, 0, true);
        rig.Clients[2].PublishState(Key, new byte[] { 12 }, false);
        rig.Deliver(rig.Arbiter.Offline(2, ++rig.Now));
        Check(rig.Clients[1].OwnerOf(Key) == 0 && rig.Clients[1].LatestState(Key).Data[0] == 12,
            "the server should retain the last published stone position after its owner leaves");
        return rig;
    }

    internal static void Register(Action<string, Action> run)
    {
        run("T-210 a solo host keeps its unpublished stone when the other seat rejoins", delegate {
            var rig = LastGuestLeft();
            var host = rig.Clients[1];
            StoneSeed scene = new StoneSeed();
            Check(scene.ForReseed(host, Key, 1, 0, 0).Data[0] == 12, "a newly loaded scene must start from the room pose");
            int livePose = 19;
            scene.LastPeerLeft(host, Key, 12, 0, 0);
            Check(scene.ForReseed(host, Key, 12, 0, 0).Data[0] == 12, "no solo move should leave normal seeding active");
            rig.Deliver(rig.Arbiter.JoinAt(2, 2, "second", ++rig.Now));
            StateUpdate seed = scene.ForReseed(host, Key, livePose, 0, 0);
            Check((seed == null ? livePose : seed.Data[0]) == 19, "a rejoin moved the live local stone back from 19 to 12");
            host.RequestLease(Key, 0, true);
            Check(host.Owns(Key) && scene.ForReseed(host, Key, livePose, 0, 0) == null, "the first local grant must publish the local position");
            host.PublishState(Key, new byte[] { 19 }, false);
            Check(rig.Clients[2].LatestState(Key).Data[0] == 19, "the returning guest did not receive the live position");
        });
        run("T-210 a different instance, room or subsequent stone owner uses the room's published pose", delegate {
            var rig = LastGuestLeft();
            var host = rig.Clients[1];
            StoneSeed scene = new StoneSeed();
            scene.LastPeerLeft(host, Key, 12, 0, 0);
            Check(new StoneSeed().ForReseed(host, Key, 19, 0, 0).Data[0] == 12, "another scene instance inherited a local pose");
            var otherRoom = LastGuestLeft();
            Check(scene.ForReseed(otherRoom.Clients[1], Key, 19, 0, 0).Data[0] == 12, "another room inherited a local pose");
            rig.Deliver(rig.Arbiter.JoinAt(2, 2, "second", ++rig.Now));
            rig.Clients[2].RequestLease(Key, 0, true);
            rig.Clients[2].PublishState(Key, new byte[] { 25 }, false);
            Check(scene.ForReseed(host, Key, 19, 0, 0).Data[0] == 25, "new owner did not override the local pose");
            rig.Clients[2].ReleaseLease(Key); host.RequestLease(Key, 0, true);
            Check(scene.ForReseed(host, Key, 19, 0, 0).Data[0] == 25, "a later local lease reused obsolete movement");
        });
    }
}
