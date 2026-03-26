using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmUnityPackageGuidCache
    {
        private const int TarBlockSize = 512;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, CacheEntry> _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new LinkedList<string>();

        internal static BlmUnityPackageGuidCache Shared { get; } = new BlmUnityPackageGuidCache();

        private BlmUnityPackageGuidCache()
        {
        }

        public bool TryGetGuids(string sourcePath, out IReadOnlyList<string> guids)
        {
            guids = Array.Empty<string>();
            if (!TryBuildCacheKey(sourcePath, out var cacheKey, out var fullPath))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (_entries.TryGetValue(cacheKey, out var cached))
                {
                    Touch(cached);
                    guids = cached.Guids;
                    return cached.Guids.Count > 0;
                }
            }

            var parsedGuids = ParseUnityPackageGuids(fullPath);
            lock (_syncRoot)
            {
                AddOrReplace(cacheKey, parsedGuids);
            }

            guids = parsedGuids;
            return parsedGuids.Count > 0;
        }

        private void Touch(CacheEntry entry)
        {
            if (entry?.LruNode == null || entry.LruNode.List == null)
            {
                return;
            }

            _lru.Remove(entry.LruNode);
            _lru.AddFirst(entry.LruNode);
        }

        private void AddOrReplace(string cacheKey, IReadOnlyList<string> guids)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return;
            }

            if (_entries.TryGetValue(cacheKey, out var existing))
            {
                existing.Guids = guids ?? Array.Empty<string>();
                return;
            }

            var node = new LinkedListNode<string>(cacheKey);
            _lru.AddFirst(node);
            _entries[cacheKey] = new CacheEntry
            {
                LruNode = node,
                Guids = guids ?? Array.Empty<string>()
            };

            while (_entries.Count > BlmConstants.UnityPackageGuidCacheMaxEntries)
            {
                var tail = _lru.Last;
                if (tail == null)
                {
                    break;
                }

                _entries.Remove(tail.Value);
                _lru.RemoveLast();
            }
        }

        private static bool TryBuildCacheKey(string sourcePath, out string cacheKey, out string fullPath)
        {
            cacheKey = string.Empty;
            fullPath = string.Empty;

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(sourcePath);
            }
            catch
            {
                fullPath = sourcePath;
            }

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return false;
            }

            var normalizedSourcePath = fullPath.Replace('\\', '/');
            cacheKey = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                normalizedSourcePath,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            return true;
        }

        private static IReadOnlyList<string> ParseUnityPackageGuids(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                using var packageStream = File.OpenRead(fullPath);
                using var gzipStream = new GZipStream(packageStream, CompressionMode.Decompress);
                var records = ReadEntryRecords(gzipStream);
                return records
                    .Where(pair => pair.Value.HasPathname && (pair.Value.HasAsset || pair.Value.HasMeta))
                    .Select(pair => pair.Key)
                    .OrderBy(guid => guid, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to parse unitypackage GUIDs: path={fullPath}, error={ex.Message}");
                return Array.Empty<string>();
            }
        }

        private static Dictionary<string, PackageEntryRecord> ReadEntryRecords(Stream tarStream)
        {
            var records = new Dictionary<string, PackageEntryRecord>(StringComparer.Ordinal);
            var header = new byte[TarBlockSize];
            var consumeBuffer = new byte[8192];

            while (true)
            {
                var headerRead = ReadExactly(tarStream, header, 0, TarBlockSize);
                if (headerRead == 0)
                {
                    break;
                }

                if (headerRead < TarBlockSize || IsAllZeroBlock(header))
                {
                    break;
                }

                var entryName = ReadNullTerminatedAscii(header, 0, 100);
                var entrySize = ParseTarOctal(header, 124, 12);
                if (entrySize < 0)
                {
                    break;
                }

                if (TryExtractRecordKey(entryName, out var guid, out var entryKey))
                {
                    if (!records.TryGetValue(guid, out var record))
                    {
                        record = new PackageEntryRecord();
                        records[guid] = record;
                    }

                    switch (entryKey)
                    {
                        case "pathname":
                            record.HasPathname = true;
                            break;
                        case "asset":
                            record.HasAsset = true;
                            break;
                        case "asset.meta":
                            record.HasMeta = true;
                            break;
                    }
                }

                ConsumeBytes(tarStream, entrySize, consumeBuffer);
                ConsumePadding(tarStream, entrySize, consumeBuffer);
            }

            return records;
        }

        private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }

        private static void ConsumeBytes(Stream stream, long byteCount, byte[] readBuffer)
        {
            var remaining = byteCount;
            while (remaining > 0)
            {
                var readSize = (int)Math.Min(readBuffer.Length, remaining);
                var read = stream.Read(readBuffer, 0, readSize);
                if (read <= 0)
                {
                    break;
                }

                remaining -= read;
            }
        }

        private static void ConsumePadding(Stream stream, long entrySize, byte[] readBuffer)
        {
            var padding = (TarBlockSize - (entrySize % TarBlockSize)) % TarBlockSize;
            if (padding <= 0)
            {
                return;
            }

            ConsumeBytes(stream, padding, readBuffer);
        }

        private static bool IsAllZeroBlock(byte[] block)
        {
            for (var i = 0; i < block.Length; i++)
            {
                if (block[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int count)
        {
            var end = offset;
            var max = offset + count;
            while (end < max && buffer[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        }

        private static long ParseTarOctal(byte[] buffer, int offset, int count)
        {
            var value = 0L;
            var hasDigit = false;
            var end = offset + count;
            for (var i = offset; i < end; i++)
            {
                var c = buffer[i];
                if (c == 0 || c == 32)
                {
                    continue;
                }

                if (c < '0' || c > '7')
                {
                    return -1;
                }

                hasDigit = true;
                value = (value * 8) + (c - '0');
            }

            return hasDigit ? value : 0;
        }

        private static bool TryExtractRecordKey(string tarEntryName, out string guid, out string entryKey)
        {
            guid = string.Empty;
            entryKey = string.Empty;
            if (string.IsNullOrWhiteSpace(tarEntryName))
            {
                return false;
            }

            var normalized = tarEntryName.Replace('\\', '/').Trim('/');
            var firstSlash = normalized.IndexOf('/');
            if (firstSlash <= 0 || firstSlash >= normalized.Length - 1)
            {
                return false;
            }

            var candidateGuid = normalized[..firstSlash];
            var candidateKey = normalized[(firstSlash + 1)..];
            if (!IsHex32(candidateGuid))
            {
                return false;
            }

            if (!string.Equals(candidateKey, "pathname", StringComparison.Ordinal) &&
                !string.Equals(candidateKey, "asset", StringComparison.Ordinal) &&
                !string.Equals(candidateKey, "asset.meta", StringComparison.Ordinal))
            {
                return false;
            }

            guid = candidateGuid.ToLowerInvariant();
            entryKey = candidateKey;
            return true;
        }

        private static bool IsHex32(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var isDigit = c >= '0' && c <= '9';
                var isLower = c >= 'a' && c <= 'f';
                var isUpper = c >= 'A' && c <= 'F';
                if (!isDigit && !isLower && !isUpper)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class CacheEntry
        {
            public LinkedListNode<string> LruNode;
            public IReadOnlyList<string> Guids = Array.Empty<string>();
        }

        private sealed class PackageEntryRecord
        {
            public bool HasPathname;
            public bool HasAsset;
            public bool HasMeta;
        }
    }
}
