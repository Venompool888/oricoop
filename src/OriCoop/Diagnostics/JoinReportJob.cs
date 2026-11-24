using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using OriCoop.Authority;

namespace OriCoop.Diagnostics
{
    internal sealed class JoinReportJob
    {
        internal delegate string Request(Uri uri, string joinId, string form, Func<bool> cancelled);
        static readonly Uri Ipify = new Uri("https://api64.ipify.org/");
        static readonly Uri Icanhazip = new Uri("https://icanhazip.com/");
        volatile bool cancelled;

        public static JoinReportJob Start(string joinedHost, string gameLanguage, string gameDirectory, string saveDirectory, byte[] joinToken)
        {
            JoinReportJob job = new JoinReportJob();
            if (joinToken == null || joinToken.Length != 16) return job;
            byte[] token = (byte[])joinToken.Clone();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { job.Run(joinedHost, gameLanguage, gameDirectory, saveDirectory, token, WindowsHttps.Request); }
                catch (Exception) { } // Diagnostics are best-effort. Do not log the report or server/token.
                finally { Array.Clear(token, 0, token.Length); }
            });
            return job;
        }

        public void Cancel() { cancelled = true; }
        bool IsCancelled() { return cancelled; }

        internal void Run(string joinedHost, string gameLanguage, string gameDirectory, string saveDirectory, byte[] joinToken, Request request)
        {
            if (cancelled || string.IsNullOrEmpty(joinedHost) || joinedHost.IndexOf('%') >= 0 ||
                Uri.CheckHostName(joinedHost) == UriHostNameType.Unknown) return;
            Uri receiver = new UriBuilder("https", joinedHost, 443, "/oricoop/diagnostics/v1").Uri;
            string joinId = JoinDiagnosticsKey.RequestId(joinToken);
            string capability = request(receiver, joinId, null, IsCancelled);
            if (cancelled || capability != JoinDiagnosticsKey.Capability(joinToken)) return;

            Dictionary<string, string> fields = DeviceEnvironment.Collect(gameLanguage, gameDirectory, saveDirectory);
            string provider = "ipify";
            string ip = Probe(request, Ipify);
            if (ip == null && !cancelled)
            {
                provider = "icanhazip";
                ip = Probe(request, Icanhazip);
            }
            if (cancelled) return;
            fields["v"] = "1";
            fields["probe"] = ip == null ? "failed" : "ok";
            if (ip != null) { fields["provider"] = provider; fields["ip"] = ip; }
            StringBuilder form = new StringBuilder();
            foreach (KeyValuePair<string, string> field in fields)
            {
                if (form.Length != 0) form.Append('&');
                form.Append(field.Key).Append('=').Append(Uri.EscapeDataString(field.Value));
            }
            if (!cancelled && form.Length <= 2048) request(receiver, joinId, form.ToString(), IsCancelled);
        }

        string Probe(Request request, Uri site)
        {
            if (cancelled) return null;
            string response = request(site, null, null, IsCancelled);
            return response == null ? null : DiagnosticAddress.PublicLiteral(response.Trim());
        }
    }
}
