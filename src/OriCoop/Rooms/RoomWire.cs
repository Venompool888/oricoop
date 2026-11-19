using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OriCoop.Rooms
{
    public static class RoomWire
    {
        public const int Protocol = 1;
        public const int MaximumBodyBytes = 5 * 1024 * 1024;
        public const int MaximumSaveBytes = 4 * 1024 * 1024 - 64;
        public const string CodeAlphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";   // accepted (older rooms keep letter codes)
        public const string NewCodeAlphabet = "23456789";   // T-046: six-digit codes from 2-9 only, so older clients (alphabet A-Z + 2-9) can still type and accept them

        public static long Now
        {
            get
            {
                return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            }
        }

        public static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            RandomNumberGenerator random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
            return bytes;
        }

        public static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static byte[] Verifier(string password, byte[] salt)
        {
            Rfc2898DeriveBytes derive = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, 20000);
            return derive.GetBytes(32);
        }

        public static byte[] Proof(byte[] verifier, byte[] nonce)
        {
            using (HMACSHA256 hmac = new HMACSHA256(verifier))
            {
                return hmac.ComputeHash(nonce);
            }
        }

        public static string TokenHash(string token)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(token)));
            }
        }

        public static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            int difference = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                difference |= a[i] ^ b[i];
            }
            return difference == 0;
        }

        public static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path + ".tmp", bytes);
            if (File.Exists(path))
            {
                File.Replace(path + ".tmp", path, null);
            }
            else
            {
                File.Move(path + ".tmp", path);
            }
        }

        // Far above the largest real record (a room.dat with 20 saves and their history is about 5,100 fields); a request
        // with more is hostile and is refused before any work is done on it (T-124).
        public const int MaximumFields = 65536;

        public sealed class Fields
        {
            // Insertion order is kept for Encode; the index makes Set/Get O(1). With a linear search, decoding a 5 MB
            // body of ~800,000 distinct tiny fields took ~10^11 string compares on the server's request thread (T-124).
            readonly List<KeyValuePair<string, byte[]>> items = new List<KeyValuePair<string, byte[]>>();
            readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

            public int Count
            {
                get
                {
                    return items.Count;
                }
            }

            public void Set(string name, string value)
            {
                Set(name, Encoding.UTF8.GetBytes(value ?? ""));
            }

            public void Set(string name, long value)
            {
                Set(name, value.ToString(CultureInfo.InvariantCulture));
            }

            public void Set(string name, byte[] value)
            {
                int i;
                if (index.TryGetValue(name, out i))
                {
                    items[i] = new KeyValuePair<string, byte[]>(name, value);
                    return;
                }
                index.Add(name, items.Count);
                items.Add(new KeyValuePair<string, byte[]>(name, value));
            }

            public bool Has(string name)
            {
                return GetBytes(name) != null;
            }

            public byte[] GetBytes(string name)
            {
                int i;
                return name != null && index.TryGetValue(name, out i) ? items[i].Value : null;
            }

            public string GetString(string name)
            {
                byte[] bytes = GetBytes(name);
                return bytes == null ? "" : Encoding.UTF8.GetString(bytes);
            }

            public long GetLong(string name, long fallback)
            {
                long value;
                return long.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                    ? value : fallback;
            }

            public byte[] Encode()
            {
                using (MemoryStream memory = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(memory))
                    {
                        foreach (KeyValuePair<string, byte[]> pair in items)
                        {
                            byte[] name = Encoding.ASCII.GetBytes(pair.Key);
                            if (name.Length == 0 || name.Length > 255)
                            {
                                throw new FormatException("field name");
                            }
                            writer.Write((byte)name.Length);
                            writer.Write(name);
                            writer.Write((uint)pair.Value.Length);
                            writer.Write(pair.Value);
                        }
                    }
                    return memory.ToArray();
                }
            }

            public static Fields Decode(byte[] bytes)
            {
                Fields fields = new Fields();
                int position = 0;
                while (position < bytes.Length)
                {
                    int length = bytes[position++];
                    if (length == 0 || length > bytes.Length - position)
                    {
                        throw new FormatException("truncated name");
                    }
                    string name = Encoding.ASCII.GetString(bytes, position, length);
                    position += length;
                    if (bytes.Length - position < 4)
                    {
                        throw new FormatException("truncated length");
                    }
                    uint size = (uint)(bytes[position] | bytes[position + 1] << 8
                        | bytes[position + 2] << 16 | bytes[position + 3] << 24);
                    position += 4;
                    if (size > bytes.Length - position)
                    {
                        throw new FormatException("truncated value");
                    }
                    byte[] value = new byte[(int)size];
                    Buffer.BlockCopy(bytes, position, value, 0, value.Length);
                    position += value.Length;
                    fields.Set(name, value);
                    if (fields.Count > MaximumFields)
                    {
                        throw new FormatException("too many fields");
                    }
                }
                return fields;
            }
        }
    }
}
