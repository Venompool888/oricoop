using System;
using OriCoop.Authority;

class JoinKeyTest
{
    static int Main()
    {
        byte[] seat = new byte[16];
        for (int i = 0; i < seat.Length; ++i) seat[i] = (byte)i;
        string request = JoinDiagnosticsKey.RequestId(seat);
        string capability = JoinDiagnosticsKey.Capability(seat);
        if (request != "6eee81bbee41c321742754d171dddea03375e5e21cfe0dbad1a28b9986f5cde6" ||
            capability != "oricoop-diagnostics-v1.5fca75edec5dce14ab4b1101f0ab682e994b99cf464d590895a34d2c8a1ad7a8")
            return 1;
        seat[0] = 1;
        return request != JoinDiagnosticsKey.RequestId(seat) && capability != JoinDiagnosticsKey.Capability(seat) ? 0 : 1;
    }
}
