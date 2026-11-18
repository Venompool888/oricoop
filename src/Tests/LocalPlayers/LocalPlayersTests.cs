using System;
using System.IO;
using OriCoop.Seats;
using OriCoop.Rules;

static class LocalPlayersTests
{
    static int cases, failed;
    static void Run(string name, Action test)
    {
        cases++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Near(float value, float expected) { Check(Math.Abs(value - expected) < 0.001f, "expected " + expected + ", got " + value); }
    static void Main()
    {
        Run("one input per local seat and one keyboard per device", delegate {
            SeatInputs inputs = new SeatInputs();
            Check(inputs.Add(2, -1) && inputs.Add(3, 0) && inputs.Add(4, 1), "independent input assignments");
            Check(!inputs.Add(1, -1) && !inputs.Add(1, 0), "keyboard and pad cannot be duplicated");
            Check(inputs.Set(3, -1) && inputs.Get(3) == -1 && inputs.Get(2) == 0, "reassign swaps without duplicating");
            inputs.Remove(3); Check(inputs.Add(1, -1), "released keyboard reused");
            Check(!inputs.Set(1, 4) && !inputs.Set(1, -2) && !inputs.Add(0, 2) && !inputs.Set(5, 2), "invalid device or seat rejected");
        });
        Run("lower primary preserves the existing player's input identity", delegate {
            SeatInputs inputs = new SeatInputs(); inputs.Add(3, -1);
            Check(inputs.AddAtNewPrimary(3, 2, 0), "lower seat allocated");
            Check(inputs.Get(3) == -1 && inputs.Get(2) == 0, "old seat keeps keyboard; new seat gets the added pad");
        });
        Run("lowest free seat and full room", delegate {
            SeatTable t = new SeatTable();
            Check(t.Add(1) == 1 && t.Add(2) == 2 && t.Add(2) == 3 && t.Add(4) == 4, "unique lowest seats");
            Check(t.Add(2) == 0 && t.Count == 4, "full refuses");
        });
        Run("release reuses hole and primary cannot leave extras", delegate {
            SeatTable t = new SeatTable(); t.Add(1); t.Add(2); t.Add(2);
            Check(!t.Release(2, 2), "primary retained");
            Check(!t.Release(1, 3), "cannot release other device");
            Check(t.Release(2, 3) && t.Add(1) == 3, "lowest hole reused");
            Check(t.Release(2, 2) && t.Owner(2) == 0, "last seat released");
            t.RemoveDevice(1); Check(t.Count == 0, "disconnect releases all");
        });
        Run("anti spoof accepts only connection owned seats", delegate {
            SeatTable t = new SeatTable(); t.Add(1); t.Add(2); t.Add(2); t.Add(4);
            Check(t.Accepts(2, 2, 3), "extra owned by source");
            Check(!t.Accepts(2, 3, 3), "header must be primary");
            Check(!t.Accepts(2, 2, 4) && !t.Accepts(2, 2, 1), "foreign stream rejected");
            Check(!t.Accepts(0, 2, 2) && !t.Accepts(2, 2, 0) && !t.Accepts(2, 2, 5), "invalid seat rejected");
        });
        Run("lower hole changes device primary to lowest local seat", delegate {
            SeatTable t = new SeatTable(); t.Add(1); t.Add(2); t.Add(3); t.Release(2, 2);
            Check(t.Add(3) == 2 && t.Owner(2) == 2 && t.Owner(3) == 2, "device primary follows lower hole");
            Check(t.Accepts(2, 2, 3) && !t.Accepts(3, 3, 3), "new primary owns connection");
        });
        Run("single-peer reconnect retains host extras and drops guest seats", delegate {
            SeatTable t = new SeatTable(); t.Add(1); t.Add(1); t.Add(3); t.Add(3);
            t.KeepDevice(1);
            Check(t.Count == 2 && t.Owner(1) == 1 && t.Owner(2) == 1 && t.Owner(3) == 0 && t.Owner(4) == 0, "host locals survive connection reset");
            Check(t.Add(3) == 3, "next guest takes lowest remaining seat");
        });
        Run("seat table snapshot round trip and invalid snapshot atomic", delegate {
            SeatTable t = new SeatTable(); t.Add(1); t.Add(2); t.Add(2);
            MemoryStream m = new MemoryStream(); t.Write(new BinaryWriter(m)); m.Position = 0;
            SeatTable copy = new SeatTable(); copy.Read(new BinaryReader(m));
            Check(copy.Owner(3) == 2 && copy.SeatsOf(2).Length == 2 && copy.Count == 3, "round trip");
            try { copy.Read(new BinaryReader(new MemoryStream(new byte[] { 1, 3, 0, 0 }))); Check(false, "invalid accepted"); }
            catch (InvalidDataException) { }
            Check(copy.Owner(3) == 2 && copy.Count == 3, "invalid snapshot cannot partially apply");
        });
        Run("frame streams preserve per seat payload and primary header", delegate {
            byte[] packet = FrameStreams.Encode(2, new int[] { 2, 3 }, new byte[][] { new byte[] { 9, 8 }, new byte[] { 7 } });
            Check(packet[0] == 9 && packet[1] == 2, "message id and primary header");
            SeatStream[] streams = FrameStreams.Decode(new BinaryReader(new MemoryStream(packet, 2, packet.Length - 2)));
            Check(streams.Length == 2 && streams[0].Seat == 2 && streams[1].Seat == 3, "stream seats");
            Check(streams[0].Body.Length == 2 && streams[0].Body[1] == 8 && streams[1].Body[0] == 7, "isolated payloads");
        });
        Run("frame streams reject duplicate invalid truncated and trailing bytes", delegate {
            byte[][] bad = { new byte[] { 2, 2, 0, 0, 2, 0, 0 }, new byte[] { 1, 5, 0, 0 }, new byte[] { 1, 2, 3, 0, 7 }, new byte[] { 0 }, new byte[] { 1, 2, 0, 0, 9 } };
            foreach (byte[] bytes in bad) {
                bool rejected = false;
                try { FrameStreams.Decode(new BinaryReader(new MemoryStream(bytes))); } catch (EndOfStreamException) { rejected = true; } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "malformed stream accepted");
            }
        });
        Run("local wall undoes outward movement only", delegate {
            EdgePoint[] p = { new EdgePoint(1, true, 0, 0, 0, 0), new EdgePoint(2, true, 12, 0, 9, 0) };
            LocalEdgeWall.Apply(p, 10, 8); Near(p[0].X, 0); Near(p[1].X, 10);
            p[1] = new EdgePoint(2, true, 11, 0, 12, 0); LocalEdgeWall.Apply(p, 10, 8); Near(p[1].X, 11);
        });
        Run("offline wall width respects the view and the configured leash", delegate {
            Near(LocalEdgeWall.OfflineWidth(32, 2.5f, 0), 27);
            Near(LocalEdgeWall.OfflineWidth(32, 2.5f, -1), 27);
            Near(LocalEdgeWall.OfflineWidth(32, 2.5f, 2), 2);
            Near(LocalEdgeWall.OfflineWidth(32, 2.5f, 40), 27);
            Near(LocalEdgeWall.OfflineWidth(3, 2.5f, 0), 3);
        });
        Run("offline horizontal wall leaves a vertical fall for regroup", delegate {
            EdgePoint[] p = { new EdgePoint(1, true, 0, 0, 0, 0), new EdgePoint(4, true, 8, -12, 5, 0) };
            LocalEdgeWall.Apply(p, 6, float.PositiveInfinity);
            Near(p[1].X, 6); Near(p[1].Y, -12);
        });
        Run("local wall ignores all remote players", delegate {
            EdgePoint[] p = { new EdgePoint(1, true, -3, 0, 0, 0), new EdgePoint(2, true, 12, 0, 9, 0), new EdgePoint(3, false, 200, 70, 190, 60) };
            LocalEdgeWall.Apply(p, 10, 8); Near(p[0].X, -0.5f); Near(p[1].X, 9.5f); Near(p[2].X, 200); Near(p[2].Y, 70);
        });
        Run("local wall four locals horizontal vertical and group bounds", delegate {
            EdgePoint[] p = { new EdgePoint(1, true, 0, 0, 0, 0), new EdgePoint(2, true, 13, 11, 9, 7), new EdgePoint(3, true, 14, 12, 9, 7), new EdgePoint(4, false, -90, -90, -90, -90) };
            LocalEdgeWall.Apply(p, 10, 8); Near(p[1].X, 10); Near(p[2].X, 10); Near(p[1].Y, 8); Near(p[2].Y, 8);
            float minX, minY, maxX, maxY;
            Check(LocalEdgeWall.Bounds(p, out minX, out minY, out maxX, out maxY), "local bounds"); Near(minX, 0); Near(maxX, 10); Near(maxY, 8);
        });
        Run("wall keeps valid four-local groups in frame under arbitrary movement", delegate {
            Random random = new Random(59);
            for (int sample = 0; sample < 500; sample++) {
                EdgePoint[] p = new EdgePoint[4];
                for (int i = 0; i < 4; i++) {
                    float x = (float)random.NextDouble() * 10, y = (float)random.NextDouble() * 8;
                    p[i] = new EdgePoint(i + 1, true, x + (float)random.NextDouble() * 12 - 6, y + (float)random.NextDouble() * 10 - 5, x, y);
                }
                LocalEdgeWall.Apply(p, 10, 8);
                float minX, minY, maxX, maxY; LocalEdgeWall.Bounds(p, out minX, out minY, out maxX, out maxY);
                Check(maxX - minX <= 10.001f && maxY - minY <= 8.001f, "four-local box overflow at sample " + sample);
            }
        });
        Console.WriteLine("LOCALPLAYERS " + (cases - failed) + "/" + cases + (failed == 0 ? " PASS" : " FAIL"));
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
}
