using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OriCoop.Diagnostics
{
    // The game's Unity Mono predates contemporary TLS. Use Windows' WinHTTP
    // stack and normal certificate/hostname validation on the background worker.
    internal static class WindowsHttps
    {
        const int TimeoutMs = 3500;
        const int ResponseLimit = 256;
        const int Secure = 0x00800000;
        const int QueryStatusAsNumber = 19 | 0x20000000;
        const int DisableFeatures = 63, DisableCookiesRedirectsAndAuthentication = 1 | 2 | 4;
        const int MaxHeaderSize = 91, ConnectRetries = 4;

        internal static string Request(Uri target, string joinId, string form, Func<bool> cancelled)
        {
            if (target.Scheme != Uri.UriSchemeHttps || cancelled()) return null;
            IntPtr session = IntPtr.Zero, connection = IntPtr.Zero, request = IntPtr.Zero;
            try
            {
                // Automatic proxy supports the player's Windows configuration on
                // Windows 8.1+. Older Windows can use its WinHTTP default proxy.
                session = WinHttpOpen("OriCoop", 4, null, null, 0);
                if (session == IntPtr.Zero) session = WinHttpOpen("OriCoop", 0, null, null, 0);
                if (session == IntPtr.Zero || !WinHttpSetTimeouts(session, TimeoutMs, TimeoutMs, TimeoutMs, TimeoutMs)) return null;
                if (!Set(session, ConnectRetries, 1)) return null;
                connection = WinHttpConnect(session, target.Host, (ushort)target.Port, 0);
                if (connection == IntPtr.Zero) return null;
                request = WinHttpOpenRequest(connection, form == null ? "GET" : "POST", target.PathAndQuery, null, null, IntPtr.Zero, Secure);
                if (request == IntPtr.Zero || !Set(request, DisableFeatures, DisableCookiesRedirectsAndAuthentication)
                    || !Set(request, MaxHeaderSize, 16 * 1024)) return null;

                string headers = joinId == null ? "" : "X-OriCoop-Join: " + joinId + "\r\n";
                byte[] payload = null;
                if (form != null)
                {
                    headers += "Content-Type: application/x-www-form-urlencoded\r\n";
                    payload = Encoding.ASCII.GetBytes(form);
                }
                if (cancelled() || !WinHttpSendRequest(request, headers, headers.Length, IntPtr.Zero, 0,
                    payload == null ? 0 : payload.Length, IntPtr.Zero)) return null;
                // Recheck after the handshake. Leaving during DNS/TLS must not
                // start uploading a diagnostic body after the join was cancelled.
                if (payload != null)
                {
                    int written;
                    if (cancelled() || !WinHttpWriteData(request, payload, payload.Length, out written) || written != payload.Length) return null;
                }
                if (cancelled() || !WinHttpReceiveResponse(request, IntPtr.Zero)) return null;
                int status, size = sizeof(int);
                if (!WinHttpQueryHeaders(request, QueryStatusAsNumber, null, out status, ref size, IntPtr.Zero)) return null;
                if (payload != null) return status == 204 ? "" : null;
                return status == 200 ? ReadShortResponse(request, cancelled) : null;
            }
            finally
            {
                if (request != IntPtr.Zero) WinHttpCloseHandle(request);
                if (connection != IntPtr.Zero) WinHttpCloseHandle(connection);
                if (session != IntPtr.Zero) WinHttpCloseHandle(session);
            }
        }

        static string ReadShortResponse(IntPtr request, Func<bool> cancelled)
        {
            byte[] buffer = new byte[ResponseLimit + 1];
            StringBuilder result = new StringBuilder();
            Stopwatch clock = Stopwatch.StartNew();
            while (!cancelled() && clock.ElapsedMilliseconds < TimeoutMs)
            {
                int count;
                if (!WinHttpReadData(request, buffer, buffer.Length, out count) || result.Length + count > ResponseLimit) return null;
                if (count == 0) return result.ToString();
                result.Append(Encoding.ASCII.GetString(buffer, 0, count));
            }
            return null;
        }

        static bool Set(IntPtr handle, int option, int value)
        {
            return WinHttpSetOption(handle, option, ref value, sizeof(int));
        }

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr WinHttpOpen(string agent, int access, string proxy, string bypass, int flags);
        [DllImport("winhttp.dll")] static extern bool WinHttpSetTimeouts(IntPtr session, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll")] static extern bool WinHttpSetOption(IntPtr handle, int option, ref int value, int length);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr WinHttpConnect(IntPtr session, string host, ushort port, int reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr WinHttpOpenRequest(IntPtr connection, string verb, string path, string version, string referer, IntPtr acceptTypes, int flags);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool WinHttpSendRequest(IntPtr request, string headers, int headerLength, IntPtr body, int length, int total, IntPtr context);
        [DllImport("winhttp.dll")] static extern bool WinHttpWriteData(IntPtr request, byte[] buffer, int length, out int written);
        [DllImport("winhttp.dll")] static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool WinHttpQueryHeaders(IntPtr request, int level, string name, out int value, ref int length, IntPtr index);
        [DllImport("winhttp.dll")] static extern bool WinHttpReadData(IntPtr request, [Out] byte[] buffer, int length, out int count);
        [DllImport("winhttp.dll")] static extern bool WinHttpCloseHandle(IntPtr handle);
    }
}
