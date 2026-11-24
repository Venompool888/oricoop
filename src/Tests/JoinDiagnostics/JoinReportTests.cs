using System;
using System.Collections.Generic;
using System.Net;
using OriCoop.Authority;
using OriCoop.Diagnostics;

static class JoinReportTests
{
    static int checks;
    static readonly byte[] Token = new byte[16];
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        checks++;
    }

    sealed class Server
    {
        public string Capability = JoinDiagnosticsKey.Capability(Token);
        public string Primary = "1.1.1.1\n";
        public string Fallback = "2606:4700:4700::1111\n";
        public bool CancelAfterGet;
        public readonly List<string> Calls = new List<string>();
        public string Report;
        public readonly JoinReportJob Job = new JoinReportJob();

        public void Run()
        {
            Job.Run("joined.example", "French", null, null, Token, Request);
        }

        string Request(Uri target, string joinId, string form, Func<bool> cancelled)
        {
            Calls.Add(target.Host);
            if (target.Host == "joined.example")
            {
                Check(target.Scheme == "https" && target.AbsolutePath == "/oricoop/diagnostics/v1", "joined endpoint");
                Check(joinId == JoinDiagnosticsKey.RequestId(Token), "join proof on both calls");
                if (form != null) Report = form;
                return form == null ? Capability : "";
            }
            Check(joinId == null && form == null, "no join id or report sent to a probe");
            if (CancelAfterGet) Job.Cancel();
            if (target.Host == "api64.ipify.org") return Primary;
            if (target.Host == "icanhazip.com") return Fallback;
            throw new Exception("unexpected probe");
        }
    }

    static int Main()
    {
        Server good = new Server();
        good.Run();
        Check(good.Calls.Count == 3 && good.Report.Contains("probe=ok") && good.Report.Contains("ip=1.1.1.1")
            && good.Report.Contains("provider=ipify") && good.Report.Contains("gameLang=French"), "one combined report");
        Server fallback = new Server { Primary = "127.0.0.1" };
        fallback.Run();
        Check(fallback.Calls.Count == 4 && fallback.Report.Contains("provider=icanhazip") && fallback.Report.Contains("%3A"), "IPv6 fallback");
        Server failed = new Server { Primary = null, Fallback = "<html>" };
        failed.Run();
        Check(failed.Report.Contains("probe=failed") && failed.Report.Contains("gameLang=French")
            && !failed.Report.Contains("&ip=") && !failed.Report.Contains("provider="), "failure retains device fields");
        Server unsupported = new Server { Capability = "oricoop-diagnostics-v1" };
        unsupported.Run();
        Check(unsupported.Calls.Count == 1 && unsupported.Report == null, "old server must not trigger probes");
        Server revoked = new Server { CancelAfterGet = true, Primary = null };
        revoked.Run();
        Check(revoked.Calls.Count == 2 && revoked.Report == null, "revocation stops fallback and upload");
        foreach (string bad in new string[] { "10.255.255.1", "::1", "fd00::1", "fe80::1%4", "255.255.255.255",
            "203.0.113.1", "2001:db8::1", "127.1", "0x08080808", "134744072", "1.1.1.1:80", "1.1.1.1\nextra" })
            Check(DiagnosticAddress.PublicLiteral(bad) == null, "invalid public literal");
        foreach (byte[] prefix in new byte[][] { new byte[] { 192, 168 }, new byte[] { 169, 254 },
            new byte[] { 100, 64 }, new byte[] { 198, 18 }, new byte[] { 224, 0 } })
        {
            string special = new IPAddress(new byte[] { prefix[0], prefix[1], 0, 1 }).ToString();
            Check(DiagnosticAddress.PublicLiteral(special) == null, "special-purpose prefix");
        }
        Check(DeviceEnvironment.FreeBucket((1L << 30) - 1) == "lt1GiB" && DeviceEnvironment.FreeBucket(1L << 30) == "1to10GiB"
            && DeviceEnvironment.FreeBucket(10L << 30) == "10to50GiB" && DeviceEnvironment.FreeBucket(50L << 30) == "ge50GiB", "bucket edges");
        string folder = System.IO.Path.GetTempPath();
        Dictionary<string, string> fields = DeviceEnvironment.Collect("Polish", folder, folder);
        Check(fields["gameLang"] == "Polish" && fields.ContainsKey("gameFree") && !fields.ContainsKey("saveFree"), "one local volume");
        Check(fields.ContainsKey("timezone") && fields.ContainsKey("utcOffset") && fields.ContainsKey("systemLang"), "local culture and time");
        fields = DeviceEnvironment.Collect("invalid game language!", null, null);
        Check(!fields.ContainsKey("gameLang") && fields.ContainsKey("utcOffset"), "omit only the unsuitable optional value");
        Console.WriteLine("JoinReportTests PASS " + checks);
        return 0;
    }
}
