using System;
using System.IO;

namespace OriCoop.World
{
    // The 4 Hz world scan serializes every loaded save object. Mono 2 (Unity 5.3) has no generational GC, so each
    // per-scan copy and hash buffer adds to the next full collection. These helpers let the scan skip both for an
    // object whose Archive bytes did not change.
    internal static class CaptureBuffer
    {
        internal const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

        // True when the first count bytes of buffer equal known exactly.
        internal static bool Same(byte[] buffer, int count, byte[] known)
        {
            if (buffer == null || known == null || count != known.Length || count > buffer.Length) return false;
            for (int i = 0; i < count; i++) if (buffer[i] != known[i]) return false;
            return true;
        }

        // The stream's written bytes without copying them, or null when the stream does not expose its buffer.
        internal static byte[] Exposed(MemoryStream stream)
        {
            try { return stream.GetBuffer(); }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static bool Same(MemoryStream stream, byte[] known)
        {
            if (known == null || stream.Length != known.Length) return false;
            byte[] buffer = Exposed(stream);
            // A publicly visible segment can start inside its backing array. Archives allocate whole
            // streams; refuse sliced buffers when the logical origin cannot be established on .NET 3.5.
            return buffer != null && buffer.Length == stream.Capacity && Same(buffer, known.Length, known);
        }

        internal static ulong Hash(byte[] bytes)
        {
            ulong hash = Offset;
            unchecked { foreach (byte b in bytes) { hash ^= b; hash *= Prime; } }
            return hash;
        }
    }

    // Write-only stream that folds what is written into the same FNV-1a hash as CaptureBuffer.Hash of those bytes,
    // so a canonical form can be hashed without keeping it in a MemoryStream and copying it out with ToArray.
    internal sealed class HashStream : Stream
    {
        ulong hash = CaptureBuffer.Offset;
        long length;

        public ulong Hash { get { return hash; } }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return length; } }
        public override long Position { get { return length; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException("count");
            unchecked { for (int i = offset, end = offset + count; i < end; i++) { hash ^= buffer[i]; hash *= Prime; } }
            length += count;
        }

        public override void WriteByte(byte value)
        {
            unchecked { hash ^= value; hash *= Prime; }
            length++;
        }

        const ulong Prime = CaptureBuffer.Prime;
    }
}
