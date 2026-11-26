using System;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using OriCoop;

static class TeammateIdentityTests
{
    static int checks;
    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
        checks++; Console.WriteLine("ok " + what);
    }

    static byte[] Names(int sender, params string[] names)
    {
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream);
        writer.Write((byte)103); writer.Write((byte)sender);
        int mask = 0;
        for (int i = 0; i < names.Length; i++) if (names[i] != null) mask |= 1 << i;
        writer.Write((byte)mask);
        foreach (string value in names)
        {
            if (value == null) continue;
            byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write((byte)bytes.Length); writer.Write(bytes);
        }
        return stream.ToArray();
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void Main()
    {
        Environment.SetEnvironmentVariable("ORICOOP_PLAYER_NAME", null);
        ConfigFile config = new ConfigFile();
        TeammateIdentity.Init(config);
        Check(TeammateIdentity.Label(1) == "P1" && TeammateIdentity.Label(2) == "P2", "anonymous by default both locally and remotely");
        Environment.SetEnvironmentVariable("ORICOOP_PLAYER_NAME", "溪光");
        UnityEngine.Time.realtimeSinceStartup = 1;
        TeammateIdentity.Tick();
        Check(TeammateIdentity.Label(1) == "P1 溪光", "explicit environment name supplies the primary default");
        TeammateIdentity.SetLocalName(1, "");
        Check(TeammateIdentity.Name(1) == "" && !config.Bind("TeammateNames", "UseEnvironmentName", true, "").Value, "clearing a name overrides the environment persistently");

        NetPlay.Room.Online = true;
        Peer two = NetPlay.Room.Add(2), three = NetPlay.Room.Add(3);
        TeammateIdentity.SetLocalName(1, "  Ori\n<光>  ");
        byte[] own = Names(1, "Ori光");
        Check(TeammateIdentity.Label(1) == "P1 Ori光" && Same(two.Sent[two.Sent.Count - 1], own), "live change sends only the seat mask and chosen short name");
        Check(config.Bind("TeammateNames", "Primary", "", "").Value == "Ori光", "local setting holds the same sanitized name after editing");
        byte[] remote = Names(2, "outsider", "叶", null, "星");
        NetPlay.Room.Bus.Dispatch(remote, true, 2);
        Check(TeammateIdentity.Label(2) == "P2 叶" && TeammateIdentity.Label(4) == "P4 星" && TeammateIdentity.Label(1) == "P1 Ori光",
            "two actors on the remote device have separate names and cannot name another device");
        Check(Same(three.Sent[three.Sent.Count - 1], remote), "direct host forwards the guest's actual reliable snapshot to P3");
        byte[] broken = Names(2, null, "new", null, "broken"); Array.Resize(ref broken, broken.Length - 1);
        NetPlay.Room.Bus.Dispatch(broken, true, 2);
        Check(TeammateIdentity.Name(2) == "叶" && TeammateIdentity.Name(4) == "星", "truncated later entry leaves every current name intact");
        NetPlay.Room.Bus.Dispatch(new byte[] { 103, 2, 2, 1, 255 }, true, 2);
        Check(TeammateIdentity.Name(2) == "叶", "invalid UTF-8 leaves the prior name intact");
        three.Sent.Clear(); NetPlay.Room.Bus.Dispatch(new byte[] { 1, 3 }, true, 3);
        Check(three.Sent.Exists(delegate(byte[] packet) { return Same(packet, own); }), "a new Hello receives the existing primary name");
        NetPlay.Room.Add(4); NetPlay.Room.Session.RemoteLeft(4);
        Check(TeammateIdentity.Name(4) == "" && TeammateIdentity.Name(2) == "叶", "a departing secondary clears only that player's name");
        NetPlay.Room.Session.RemoteLeft(2);
        Check(TeammateIdentity.Name(2) == "", "a reused device seat starts anonymous");
        NetPlay.Room.Add(2); NetPlay.Room.Bus.Dispatch(Names(2, null, "归"), true, 2);
        Check(TeammateIdentity.Name(2) == "归", "the rejoined device publishes its current name");
        TeammateIdentity.TransportReset(false);
        Check(TeammateIdentity.Name(2) == "", "transport reset clears names even before a first Hello");

        NetPlay.Room.Host = false;
        Players.LocalSeat = 3; Players.LocalSeats = new int[] { 3 };
        Check(TeammateIdentity.Name(1) == "" && TeammateIdentity.Name(3) == "Ori光", "seat assignment has the correct labels even before the next game update");
        TeammateIdentity.LocalSeatsChanged(); TeammateIdentity.Tick();
        Check(TeammateIdentity.Label(3) == "P3 Ori光", "the same primary keeps its name when a room assigns a different seat");
        Players.Inputs[2] = new InputDevice { Kind = InputDevice.DeviceKind.Pad, Pad = 2 };
        Players.LocalSeat = 2; Players.LocalSeats = new int[] { 2, 3 }; Players.Rename(3, 2);
        TeammateIdentity.SetLocalName(2, "晴");
        Check(TeammateIdentity.Name(3) == "Ori光" && TeammateIdentity.Name(2) == "晴", "adding a lower seat leaves the original primary name on the original player's input");
        Check(Same(NetPlay.Room.Single.Sent[NetPlay.Room.Single.Sent.Count - 1], Names(2, null, "晴", "Ori光")), "the new network primary publishes both local names");
        Players.LocalSeats = new int[] { 2 }; TeammateIdentity.LocalSeatsChanged(); TeammateIdentity.Tick();
        Players.LocalSeats = new int[] { 2, 3 }; Players.Inputs[3] = new InputDevice { Kind = InputDevice.DeviceKind.Pad, Pad = 3 };
        TeammateIdentity.LocalSeatsChanged(); TeammateIdentity.Tick();
        Check(TeammateIdentity.Name(3) == "" && TeammateIdentity.Name(2) == "晴", "an immediate new local occupant cannot inherit the vanished primary's name");
        Check(TeammateIdentity.Clean("*[ ]#@<>=\u202e\n") == "", "control, bidi and markup-only names are anonymous");
        Check(TeammateIdentity.Clean(new String(' ', 32) + "光") == "光", "leading whitespace does not consume the display limit");
        string face = Char.ConvertFromUtf32(0x1f60a), many = "";
        for (int i = 0; i < 20; i++) many += face;
        Check(Encoding.UTF8.GetByteCount(TeammateIdentity.Clean(many)) == 64, "unicode length limit preserves complete code points");
        Console.WriteLine("TeammateIdentity: " + checks + " checks passed");
    }
}
