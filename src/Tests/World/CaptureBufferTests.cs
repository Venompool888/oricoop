using System;
using System.IO;
using System.Text;
using OriCoop.World;

// Perf (#416): the 4 Hz world scan keeps the previous blob for unchanged Archive bytes and hashes canonical forms
// through HashStream instead of copying them out of a MemoryStream.
static class CaptureBufferTests
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    public static void Register(Action<string, Action> run)
    {
        run("hash stream gives the same FNV-1a hash as hashing the written bytes", delegate {
            Check(CaptureBuffer.Hash(Encoding.ASCII.GetBytes("a")) == 0xaf63dc4c8601ec8cUL, "FNV-1a 64 test vector");
            Check(CaptureBuffer.Hash(new byte[0]) == CaptureBuffer.Offset, "empty input is the offset basis");
            MemoryStream copy = new MemoryStream(); HashStream hashed = new HashStream();
            foreach (Stream target in new Stream[] { copy, hashed })
            {
                BinaryWriter w = new BinaryWriter(target);
                w.Write((byte)7); w.Write(123456); w.Write(-1.5f); w.Write(true); w.Write("alpha é");
                w.Write(new byte[300], 0, 300); w.Write(ulong.MaxValue); target.WriteByte(9); w.Flush();
            }
            byte[] bytes = copy.ToArray();
            Check(hashed.Hash == CaptureBuffer.Hash(bytes), "hash stream differs from hashing ToArray");
            Check(hashed.Length == bytes.Length && hashed.Position == bytes.Length, "length " + hashed.Length);
            Check(new HashStream().Hash == CaptureBuffer.Offset, "nothing written");
        });
        run("hash stream rejects reading, seeking and bad ranges", delegate {
            HashStream s = new HashStream();
            Check(s.CanWrite && !s.CanRead && !s.CanSeek, "capabilities");
            bool threw = false;
            try { s.Write(new byte[2], 1, 2); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check(threw && s.Length == 0 && s.Hash == CaptureBuffer.Offset, "out-of-range write changes nothing");
            threw = false;
            try { s.Write(new byte[1], 1, int.MaxValue); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check(threw && s.Length == 0, "overflowed range is refused before advancing the hash");
            threw = false; try { s.Seek(0, SeekOrigin.Begin); } catch (NotSupportedException) { threw = true; }
            Check(threw, "seek");
        });
        run("unchanged Archive bytes are recognised only when every byte matches", delegate {
            byte[] known = { 1, 2, 3, 4 };
            Check(CaptureBuffer.Same(new byte[] { 1, 2, 3, 4, 99, 99 }, 4, known), "trailing capacity is ignored");
            Check(!CaptureBuffer.Same(new byte[] { 1, 2, 3, 5 }, 4, known), "last byte differs");
            Check(!CaptureBuffer.Same(new byte[] { 1, 2, 3 }, 3, known), "shorter");
            Check(!CaptureBuffer.Same(new byte[] { 1, 2, 3, 4, 5 }, 5, known), "longer");
            Check(!CaptureBuffer.Same(new byte[] { 1, 2 }, 4, known), "count beyond the buffer");
            Check(!CaptureBuffer.Same(null, 0, known) && !CaptureBuffer.Same(new byte[0], 0, null), "no data");
            Check(CaptureBuffer.Same(new byte[0], 0, new byte[0]), "empty records match");
        });
        run("a stream's bytes are compared in place, never through a hidden buffer", delegate {
            byte[] known = { 5, 6, 7 };
            MemoryStream grown = new MemoryStream(); grown.Write(new byte[] { 5, 6, 7 }, 0, 3);   // capacity > length
            Check(grown.Capacity > grown.Length && CaptureBuffer.Same(grown, known), "grown stream matches");
            grown.WriteByte(8);
            Check(!CaptureBuffer.Same(grown, known), "extra byte");
            MemoryStream hidden = new MemoryStream(new byte[] { 5, 6, 7 }, 0, 3, true, false);
            Check(CaptureBuffer.Exposed(hidden) == null && !CaptureBuffer.Same(hidden, known),
                "a stream that hides its buffer counts as changed (the capture copies it)");
            MemoryStream segment = new MemoryStream(new byte[] { 5, 6, 7, 8 }, 1, 3, true, true);
            Check(!CaptureBuffer.Same(segment, known), "sliced visible stream cannot reuse bytes preceding its origin");
            Check(!CaptureBuffer.Same(new MemoryStream(), null), "no previous blob");
        });
    }
}
