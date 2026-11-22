using System;
using System.Collections.Generic;
using OriCoop.Pads;

static class PadsTests
{
    static int s_failed, s_cases;

    static void Run(string name, Action test)
    {
        s_cases++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { s_failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("expected '" + expected + "', got '" + actual + "'");
    }

    static PadSnapshot Parse(byte[] report, bool dualSenseUsb)
    {
        PadSnapshot s;
        Check(PsReport.TryParse(report, report.Length, dualSenseUsb, out s), "report rejected");
        return s;
    }

    static void CheckDualSenseFull(PadSnapshot s)
    {
        Equal((ushort)(0x1000 | 0x10 | 0x100 | 0x8), s.Buttons);
        Equal((byte)200, s.LT);
        Equal((byte)0, s.RT);
        Check(s.LX <= -32000 && s.LY >= 32000, "left stick must point up-left");
    }

    static long Ms(int time) { return time * TimeSpan.TicksPerMillisecond; }

    static PsRoute Vote(MirrorJudge judge, int edge, bool paired)
    {
        judge.OnPsEdge(Ms(edge));
        if (paired) judge.OnXInputActive(Ms(edge + 20));
        return judge.Tick(Ms(edge + 250));
    }

    static int Main()
    {
        Run("DualSense USB full", delegate {
            CheckDualSenseFull(Parse(new byte[] { 0x01, 0, 0, 128, 128, 200, 0, 0, 0x22, 0x21, 0 }, true));
        });
        Run("DualSense Bluetooth full", delegate {
            CheckDualSenseFull(Parse(new byte[] { 0x31, 0, 0, 0, 128, 128, 200, 0, 0, 0x22, 0x21, 0 }, false));
        });
        Run("DualSense Bluetooth basic", delegate {
            PadSnapshot s = Parse(new byte[] { 0x01, 128, 128, 128, 128, 0x48, 0, 0x02, 0, 217 }, false);
            Equal((ushort)(0x2000 | 0x20), s.Buttons);
            Equal((byte)217, s.RT);
        });
        Run("DualShock 4 Bluetooth full", delegate {
            PadSnapshot s = Parse(new byte[] { 0x11, 0, 0, 128, 128, 128, 128, 0x88, 0, 0, 0, 0 }, false);
            Equal((ushort)0x8000, s.Buttons);
        });
        Run("centered sticks and released hat are idle", delegate {
            PadSnapshot s = Parse(new byte[] { 1, 128, 128, 128, 128, 8, 0, 0, 0, 0 }, false);
            Equal((ushort)0, s.Buttons);
            Check(s.LX == 0 && s.LY == 0 && s.RX == 0 && s.RY == 0, "sticks must be zero");
            Check(!PsReport.IsActive(s), "neutral report must be idle");
        });
        Run("center jitter is zero; northwest hat presses up and left", delegate {
            PadSnapshot s = Parse(new byte[] { 1, 127, 129, 129, 127, 7, 0, 0, 0, 0 }, false);
            Check(s.LX == 0 && s.LY == 0 && s.RX == 0 && s.RY == 0, "sticks must be zero within one step of center");
            Equal((ushort)(0x1 | 0x4), s.Buttons);
            Check(PsReport.IsActive(s), "d-pad counts as activity");
        });
        Run("unknown report ID", delegate {
            PadSnapshot s;
            Check(!PsReport.TryParse(new byte[] { 5, 128, 128, 128, 128, 8, 0, 0, 0, 0 }, 10, false, out s), "unknown ID accepted");
        });
        Run("truncated basic and full reports", delegate {
            PadSnapshot s;
            Check(!PsReport.TryParse(new byte[] { 1, 128, 128, 128, 128, 8, 0, 0, 0 }, 9, false, out s), "missing R2 accepted");
            Check(!PsReport.TryParse(new byte[] { 1, 128, 128, 128, 128, 0, 0, 0, 8, 0, 0 }, 10, true, out s), "read past valid bytes");
        });
        Run("activity thresholds are strict", delegate {
            Check(!PsReport.IsActive(new PadSnapshot { LX = 16000, RY = -16000, LT = 64, RT = 64 }), "threshold is idle");
            Check(PsReport.IsActive(new PadSnapshot { LX = 16001 }), "positive stick is active");
            Check(PsReport.IsActive(new PadSnapshot { RY = -16001 }), "negative stick is active");
            Check(PsReport.IsActive(new PadSnapshot { RT = 65 }), "trigger is active");
        });
        Run("no initial XInput starts native", delegate {
            Equal(PsRoute.Native, new MirrorJudge(false).Tick(Ms(0)));
        });
        Run("two delayed paired votes route to mirrored", delegate {
            var judge = new MirrorJudge(true);
            Equal(PsRoute.Unknown, Vote(judge, 1000, true));
            Equal(PsRoute.Mirrored, Vote(judge, 1500, true));
        });
        Run("two unpaired votes route to native", delegate {
            var judge = new MirrorJudge(true);
            Equal(PsRoute.Unknown, Vote(judge, 1000, false));
            Equal(PsRoute.Native, Vote(judge, 1500, false));
        });
        Run("mirrored stays until two unpaired votes", delegate {
            var judge = new MirrorJudge(true);
            Vote(judge, 1000, true);
            Equal(PsRoute.Mirrored, Vote(judge, 1500, true));
            Equal(PsRoute.Mirrored, Vote(judge, 2000, false));
            Equal(PsRoute.Native, Vote(judge, 2500, false));
        });
        Run("native also needs two paired votes after XInput connects", delegate {
            var judge = new MirrorJudge(false);
            judge.OnXInputConnectedChanged(true);
            Equal(PsRoute.Native, Vote(judge, 1000, true));
            Equal(PsRoute.Mirrored, Vote(judge, 1500, true));
        });
        Run("one opposite vote breaks the streak", delegate {
            var judge = new MirrorJudge(true);
            Vote(judge, 1000, true);
            Vote(judge, 1500, false);
            Equal(PsRoute.Unknown, Vote(judge, 2000, true));
            Equal(PsRoute.Mirrored, Vote(judge, 2500, true));
        });
        Run("pairing endpoints are inclusive and each edge waits 250 ms", delegate {
            var judge = new MirrorJudge(true);
            judge.OnXInputActive(Ms(950));
            judge.OnPsEdge(Ms(1000));
            judge.OnPsEdge(Ms(1100));
            Equal(PsRoute.Unknown, judge.Tick(Ms(1200)));
            Equal(PsRoute.Unknown, judge.Tick(Ms(1250)));
            Equal(PsRoute.Unknown, judge.Tick(Ms(1349)));
            judge.OnXInputActive(Ms(1350));
            Equal(PsRoute.Mirrored, judge.Tick(Ms(1350)));
        });
        Run("activity outside the pairing window does not count", delegate {
            var judge = new MirrorJudge(true);
            judge.OnXInputActive(Ms(949));
            judge.OnPsEdge(Ms(1000));
            judge.OnXInputActive(Ms(1251));
            Equal(PsRoute.Unknown, judge.Tick(Ms(1251)));
            Equal(PsRoute.Native, Vote(judge, 1500, false));
        });

        Console.WriteLine((s_cases - s_failed) + "/" + s_cases + " passed");
        return s_failed == 0 ? 0 : 1;
    }
}
