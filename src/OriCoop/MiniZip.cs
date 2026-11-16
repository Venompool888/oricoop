using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OriCoop
{
    // Minimal zip reader (stored + deflate). The game's Mono has no MonoPosixHelper, so System.IO.Compression
    // can't be used; this reads the artist's skin zip directly.
    public static class MiniZip
    {
        const int MaxEntry = 64 << 20;   // a skin zip is a local file, but a broken or hostile one must not exhaust memory

        // Returns file name (no folders) -> bytes for every entry whose name passes `want`.
        public static Dictionary<string, byte[]> Read(string zipPath, Predicate<string> want)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            if (new FileInfo(zipPath).Length > 4L * MaxEntry) throw new InvalidDataException("zip too large");
            byte[] z = File.ReadAllBytes(zipPath);
            int eocd = -1;
            for (int i = z.Length - 22; i >= Math.Max(0, z.Length - 22 - 65536); i--)
                if (U32(z, i) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("not a zip file");
            int count = U16(z, eocd + 10);
            int p = (int)U32(z, eocd + 16);
            for (int e = 0; e < count; e++)
            {
                if (U32(z, p) != 0x02014b50) throw new InvalidDataException("bad zip directory");
                int flags = U16(z, p + 8), method = U16(z, p + 10);
                int csize = (int)U32(z, p + 20), usize = (int)U32(z, p + 24);
                int nlen = U16(z, p + 28), xlen = U16(z, p + 30), clen = U16(z, p + 32);
                int local = (int)U32(z, p + 42);
                string name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.Default).GetString(z, p + 46, nlen).Replace('\\', '/');
                p += 46 + nlen + xlen + clen;
                if (name.EndsWith("/")) continue;
                string file = name.Substring(name.LastIndexOf('/') + 1);
                if (file.StartsWith("._") || name.Contains("__MACOSX/") || !want(file)) continue;
                if ((flags & 1) != 0) throw new InvalidDataException(file + " is encrypted");
                if (usize < 0 || usize > MaxEntry || csize < 0) throw new InvalidDataException(file + ": entry too large");
                int data = local + 30 + U16(z, local + 26) + U16(z, local + 28);
                byte[] bytes;
                if (method == 0) { bytes = new byte[usize]; Buffer.BlockCopy(z, data, bytes, 0, usize); }
                else if (method == 8) bytes = new Inflater(z, data, csize, usize).Run();
                else throw new InvalidDataException(file + ": unsupported zip compression " + method);
                result[file] = bytes;
                total += bytes.Length;
                if (total > 4L * MaxEntry) throw new InvalidDataException("zip too large");
            }
            return result;
        }

        static int U16(byte[] b, int i) { return b[i] | b[i + 1] << 8; }
        static uint U32(byte[] b, int i) { return (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24); }

        class Tree { public int[] Counts = new int[16]; public int[] Symbols = new int[288]; }

        class Inflater
        {
            static readonly int[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
            static readonly int[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
            static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
            static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
            static readonly int[] ClOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

            readonly byte[] m_src; int m_pos; readonly int m_end;
            int m_bits, m_bitCount;
            byte[] m_out; int m_outPos;

            public Inflater(byte[] src, int start, int length, int outSize) { m_src = src; m_pos = start; m_end = start + length; m_out = new byte[Math.Max(outSize, 1)]; }

            int Bit()
            {
                if (m_bitCount == 0)
                {
                    if (m_pos >= m_end) throw new InvalidDataException("zip data ended early");
                    m_bits = m_src[m_pos++]; m_bitCount = 8;
                }
                int b = m_bits & 1; m_bits >>= 1; m_bitCount--; return b;
            }

            int Bits(int n) { int v = 0; for (int i = 0; i < n; i++) v |= Bit() << i; return v; }

            static void Build(Tree t, int[] lengths, int offset, int num)
            {
                Array.Clear(t.Counts, 0, 16);
                for (int i = 0; i < num; i++) t.Counts[lengths[offset + i]]++;
                t.Counts[0] = 0;
                int[] offs = new int[16];
                for (int i = 1, sum = 0; i < 16; i++) { offs[i] = sum; sum += t.Counts[i]; }
                for (int i = 0; i < num; i++) if (lengths[offset + i] != 0) t.Symbols[offs[lengths[offset + i]]++] = i;
            }

            int Decode(Tree t)
            {
                int sum = 0, cur = 0, len = 0;
                do
                {
                    cur = 2 * cur + Bit();
                    if (++len > 15) throw new InvalidDataException("bad deflate code");
                    sum += t.Counts[len];
                    cur -= t.Counts[len];
                } while (cur >= 0);
                return t.Symbols[sum + cur];
            }

            void Put(byte b)
            {
                if (m_outPos == m_out.Length) { if (m_out.Length >= MaxEntry) throw new InvalidDataException("zip entry too large"); Array.Resize(ref m_out, Math.Min(m_out.Length * 2, MaxEntry)); }
                m_out[m_outPos++] = b;
            }

            public byte[] Run()
            {
                Tree lit = new Tree(), dist = new Tree();
                int final;
                do
                {
                    final = Bit();
                    int type = Bits(2);
                    if (type == 0)
                    {
                        m_bitCount = 0; // align to byte
                        int len = m_src[m_pos] | m_src[m_pos + 1] << 8; m_pos += 4;
                        for (int i = 0; i < len; i++) Put(m_src[m_pos++]);
                        continue;
                    }
                    if (type == 1)
                    {
                        int[] l = new int[288 + 30];
                        for (int i = 0; i < 144; i++) l[i] = 8;
                        for (int i = 144; i < 256; i++) l[i] = 9;
                        for (int i = 256; i < 280; i++) l[i] = 7;
                        for (int i = 280; i < 288; i++) l[i] = 8;
                        for (int i = 0; i < 30; i++) l[288 + i] = 5;
                        Build(lit, l, 0, 288); Build(dist, l, 288, 30);
                    }
                    else if (type == 2)
                    {
                        int hlit = Bits(5) + 257, hdist = Bits(5) + 1, hclen = Bits(4) + 4;
                        int[] cl = new int[19];
                        for (int i = 0; i < hclen; i++) cl[ClOrder[i]] = Bits(3);
                        Tree clt = new Tree(); Build(clt, cl, 0, 19);
                        int[] l = new int[hlit + hdist];
                        for (int n = 0; n < hlit + hdist;)
                        {
                            int sym = Decode(clt);
                            if (sym < 16) { l[n++] = sym; continue; }
                            int rep, val = 0;
                            if (sym == 16) { if (n == 0) throw new InvalidDataException("bad deflate lengths"); val = l[n - 1]; rep = 3 + Bits(2); }
                            else if (sym == 17) rep = 3 + Bits(3);
                            else rep = 11 + Bits(7);
                            while (rep-- > 0) l[n++] = val;
                        }
                        Build(lit, l, 0, hlit); Build(dist, l, hlit, hdist);
                    }
                    else throw new InvalidDataException("bad deflate block");

                    for (;;)
                    {
                        int sym = Decode(lit);
                        if (sym < 256) { Put((byte)sym); continue; }
                        if (sym == 256) break;
                        sym -= 257;
                        int len = LenBase[sym] + Bits(LenExtra[sym]);
                        int ds = Decode(dist);
                        int d = DistBase[ds] + Bits(DistExtra[ds]);
                        for (int i = 0; i < len; i++) Put(m_out[m_outPos - d]);
                    }
                } while (final == 0);
                if (m_outPos != m_out.Length) Array.Resize(ref m_out, m_outPos);
                return m_out;
            }
        }
    }
}
