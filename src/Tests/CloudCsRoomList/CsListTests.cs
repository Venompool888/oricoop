using System;
using System.Collections.Generic;
using OriCoop.Authority;

static class CsListTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    static int Main()
    {
        try
        {
            CsRoomListCache cache = new CsRoomListCache();
            int read = cache.BeginFetch("local", 0, 5);
            CsRoomListEntry room = new CsRoomListEntry { Code = "614230" };
            cache.Complete(read, new List<CsRoomListEntry> { room });
            Check(cache.TakeShown(1).Count == 1, "live room was not shown");
            cache.Complete(cache.BeginFetch("local", 5, 5), null, "timed out");
            Check(cache.State == CsRoomListCache.Failed && cache.Error == "timed out", "reason missing");
            Check(cache.Drawn(0, room.Code, row => row.Code) == null, "old room still accepts a press");
            Check(cache.TakeShown(2).Count == 0, "old room still displayed");
            cache.ReloadNow();
            int retry = cache.BeginFetch("local", 6, 5);
            Check(retry >= 0 && cache.State == CsRoomListCache.Loading, "explicit retry did not start");
            cache.Complete(retry, new List<CsRoomListEntry> { room });
            Check(cache.TakeShown(3).Count == 1 && cache.Drawn(0, room.Code, row => row.Code) == room, "recovered room unavailable");
            Console.WriteLine("PASS room listing closes stale rows on failure and recovers on retry");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error.Message); return 1; }
    }
}
