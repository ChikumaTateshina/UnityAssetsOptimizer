using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public static class HashUtility
    {
        struct CacheEntry
        {
            public long Length;
            public DateTime WriteTimeUtc;
            public string Hash;
        }

        static readonly Dictionary<string, CacheEntry> FileCache = new Dictionary<string, CacheEntry>();

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(data));
        }

        public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text ?? string.Empty));

        /// <summary>ファイル内容の SHA256。サイズと更新日時が同じ間はキャッシュを使う（別スレッドから呼んでよい）。</summary>
        public static string Sha256File(string fullPath, bool useCache = true)
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;
            lock (FileCache)
            {
                if (useCache && FileCache.TryGetValue(fullPath, out var cached) &&
                    cached.Length == info.Length && cached.WriteTimeUtc == info.LastWriteTimeUtc)
                    return cached.Hash;
            }

            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(fullPath))
                hash = ToHex(sha.ComputeHash(stream));
            lock (FileCache) FileCache[fullPath] = new CacheEntry { Length = info.Length, WriteTimeUtc = info.LastWriteTimeUtc, Hash = hash };
            return hash;
        }

        public static string Short(string hash, int length = 8) =>
            string.IsNullOrEmpty(hash) ? string.Empty : hash.Substring(0, Math.Min(length, hash.Length));

        static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
