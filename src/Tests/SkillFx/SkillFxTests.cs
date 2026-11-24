using System;
using System.IO;
using OriCoop.Fx;

static class SkillFxTests
{
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    static byte[] Encode(SkillFxEvent effect)
    {
        MemoryStream stream = new MemoryStream(); BinaryWriter writer = new BinaryWriter(stream);
        SkillFxEvent.Write(writer, effect); writer.Flush(); return stream.ToArray();
    }
    static SkillFxEvent Decode(byte[] bytes)
    {
        MemoryStream stream = new MemoryStream(bytes);
        SkillFxEvent copy = SkillFxEvent.Read(new BinaryReader(stream));
        Check(stream.Position == stream.Length, "entire event read"); return copy;
    }
    static bool Refuses(byte[] bytes)
    {
        try { Decode(bytes); return false; }
        catch (InvalidDataException) { return true; }
        catch (EndOfStreamException) { return true; }
    }
    static void SkillFxWireRoundTrip()
    {
        foreach (SkillFxKind kind in Enum.GetValues(typeof(SkillFxKind)))
        {
            var effect = new SkillFxEvent { Kind = kind, Seat = 4, X = -123.5f, Y = 8.25f, Z = 0.5f };
            if (kind == SkillFxKind.ChargeStart || kind == SkillFxKind.ChargeBurst || kind == SkillFxKind.Dash) effect.Variant = 2;
            if (kind == SkillFxKind.StompLand) effect.Variant = 1;
            if (kind == SkillFxKind.SpiritFlame) { effect.Variant = 0x85; effect.A = -8; effect.B = 11; effect.C = 0.3f; }
            if (kind == SkillFxKind.BashFrom || kind == SkillFxKind.BashOff) { effect.A = 255f; effect.B = 5.65f; }
            byte[] bytes = Encode(effect);
            Check(bytes[0] == 4 && bytes[1] == (byte)kind && bytes[2] == effect.Variant, kind + " header");
            SkillFxEvent copy = Decode(bytes);
            Check(copy.Kind == kind && copy.Seat == 4 && copy.Variant == effect.Variant, kind + " metadata");
            Check(copy.X == effect.X && copy.Y == effect.Y && copy.Z == effect.Z, kind + " position");
            Check(copy.A == effect.A && copy.B == effect.B && copy.C == effect.C, kind + " effect parameters");
            Array.Resize(ref bytes, bytes.Length - 1);
            Check(Refuses(bytes), kind + " truncated packet refused");
        }
    }
    static void SkillFxWireRejectsInvalidValues()
    {
        byte[] bytes = Encode(new SkillFxEvent { Kind = SkillFxKind.ChargeBurst, Seat = 1, Variant = 2 });
        bytes[0] = 5; Check(Refuses(bytes), "invalid seat");
        bytes[0] = 1; bytes[1] = 255; Check(Refuses(bytes), "unknown kind");
        bytes[1] = (byte)SkillFxKind.ChargeBurst; bytes[2] = 3; Check(Refuses(bytes), "unknown burst variant");
        bytes[2] = 0; Array.Copy(BitConverter.GetBytes(float.NaN), 0, bytes, 3, 4);
        Check(Refuses(bytes), "nonfinite position");
    }
    static void SkillFxWireRejectsInvalidBashTrail()
    {
        foreach (SkillFxKind kind in new[] { SkillFxKind.BashFrom, SkillFxKind.BashOff })
        {
            var effect = new SkillFxEvent { Kind = kind, Seat = 1, A = 360f, B = 12f };
            Check(!Refuses(Encode(effect)), "bounded Bash trail is valid");
            foreach (float scale in new[] { -1f, 12.01f, float.MaxValue, float.PositiveInfinity })
            {
                effect.B = scale;
                Check(Refuses(Encode(effect)), kind + " rejects scale " + scale);
            }
            effect.B = 5.65f; effect.A = float.MaxValue;
            Check(Refuses(Encode(effect)), kind + " rejects an oversized angle");
        }
    }
    static void SkillFxPositionsMustBeNearCaster()
    {
        var effect = new SkillFxEvent { Seat = 2, Kind = SkillFxKind.ChargeDashImpact, X = 140, Y = -70, Z = 3 };
        Check(effect.IsNear(100, -70, 3), "impact at the distance limit accepted");
        Check(!effect.IsNear(100, -70, 2), "distance includes depth");
        effect.X = float.MaxValue;
        Check(!effect.IsNear(100, -70, 3), "a finite but far-away impact is rejected");

        effect = new SkillFxEvent { Seat = 2, Kind = SkillFxKind.SpiritFlame, X = 100, Y = -70, A = 108, B = -70 };
        Check(effect.IsNear(100, -70, 0), "nearby launch and endpoint accepted");
        effect.B = -120;
        Check(!effect.IsNear(100, -70, 0), "nearby launch cannot hide a distant endpoint");
        effect = new SkillFxEvent { Seat = 2, Kind = SkillFxKind.ChargeStop };
        Check(effect.IsNear(100, -70, 0), "coordinate-free stop is accepted");
    }
    static void ReplaysAndRemoteCastersCannotSend()
    {
        var replay = new SkillFxReplay();
        int sent = 0;
        Action<bool> sendHook = local => { if (replay.CanSend(local)) sent++; };
        sendHook(true);
        sendHook(false);
        Check(sent == 1, "only a local skill emits the original message");
        Check(replay.Run(delegate {
            sendHook(true);
            replay.Run(delegate { sendHook(true); return 0; });
            sendHook(true);
            return 42;
        }) == 42, "replay still returns the spawned effect");
        Check(sent == 1, "spawning a replay cannot re-emit even with a local caster");
        try { replay.Run<int>(delegate { throw new InvalidOperationException(); }); }
        catch (InvalidOperationException) { }
        sendHook(false);
        sendHook(true);
        Check(sent == 2, "a later local skill still sends, but a delayed remote callback does not");
    }
    static int Main()
    {
        SkillFxWireRoundTrip(); Console.WriteLine("ok SkillFxWireRoundTrip");
        SkillFxWireRejectsInvalidValues(); Console.WriteLine("ok SkillFxWireRejectsInvalidValues");
        SkillFxWireRejectsInvalidBashTrail(); Console.WriteLine("ok SkillFxWireRejectsInvalidBashTrail");
        SkillFxPositionsMustBeNearCaster(); Console.WriteLine("ok SkillFxPositionsMustBeNearCaster");
        ReplaysAndRemoteCastersCannotSend(); Console.WriteLine("ok ReplaysAndRemoteCastersCannotSend");
        return 0;
    }
}
