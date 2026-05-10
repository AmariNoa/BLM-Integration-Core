using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed partial class BlmUnityPackageGuidCache
    {
        private static bool TryBuildCacheKey(string sourcePath, out string cacheKey, out string fullPath)
        {
            return TryBuildCacheKey(sourcePath, out cacheKey, out fullPath, out _, out _);
        }

        private static bool TryBuildCacheKey(
            string sourcePath,
            out string cacheKey,
            out string fullPath,
            out long fileSize,
            out long lastWriteTimeUtcTicks)
        {
            cacheKey = string.Empty;
            fullPath = string.Empty;
            fileSize = 0L;
            lastWriteTimeUtcTicks = 0L;

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

            fileSize = info.Length;
            lastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks;

            var normalizedSourcePath = fullPath.Replace('\\', '/');
            cacheKey = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                normalizedSourcePath,
                fileSize,
                lastWriteTimeUtcTicks);
            return true;
        }

        private static IReadOnlyList<BlmUnityPackageAssetEntry> ParseUnityPackageEntries(
            string fullPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return Array.Empty<BlmUnityPackageAssetEntry>();
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var packageStream = File.OpenRead(fullPath);
                using var gzipStream = new GZipStream(packageStream, CompressionMode.Decompress);
                var records = ReadEntryRecords(gzipStream, cancellationToken);
                return records
                    .Where(pair => pair.Value.HasPathname && (pair.Value.HasAsset || pair.Value.HasMeta))
                    .Select(pair => new BlmUnityPackageAssetEntry(
                        pair.Key,
                        pair.Value.HasAsset,
                        pair.Value.AssetSha256))
                    .OrderBy(entry => entry.Guid ?? string.Empty, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<BlmUnityPackageAssetEntry>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to parse unitypackage records: path={fullPath}, error={ex.Message}");
                return Array.Empty<BlmUnityPackageAssetEntry>();
            }
        }

        private static IReadOnlyList<string> BuildGuidArray(IReadOnlyList<BlmUnityPackageAssetEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return Array.Empty<string>();
            }

            return entries
                .Select(entry => entry.Guid ?? string.Empty)
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(guid => guid, StringComparer.Ordinal)
                .ToArray();
        }

        private static Dictionary<string, PackageEntryRecord> ReadEntryRecords(
            Stream tarStream,
            CancellationToken cancellationToken = default)
        {
            var records = new Dictionary<string, PackageEntryRecord>(StringComparer.Ordinal);
            var header = new byte[TarBlockSize];
            var consumeBuffer = new byte[8192];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var headerRead = ReadExactly(tarStream, header, 0, TarBlockSize, cancellationToken);
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

                var handled = false;
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
                            ConsumeBytes(tarStream, entrySize, consumeBuffer, cancellationToken);
                            ConsumePadding(tarStream, entrySize, consumeBuffer, cancellationToken);
                            handled = true;
                            break;
                        case "asset":
                            record.HasAsset = true;
                            record.AssetSha256 = ConsumeBytesAndComputeSha256(tarStream, entrySize, consumeBuffer, cancellationToken);
                            ConsumePadding(tarStream, entrySize, consumeBuffer, cancellationToken);
                            handled = true;
                            break;
                        case "asset.meta":
                            record.HasMeta = true;
                            ConsumeBytes(tarStream, entrySize, consumeBuffer, cancellationToken);
                            ConsumePadding(tarStream, entrySize, consumeBuffer, cancellationToken);
                            handled = true;
                            break;
                    }
                }

                if (handled)
                {
                    continue;
                }

                ConsumeBytes(tarStream, entrySize, consumeBuffer, cancellationToken);
                ConsumePadding(tarStream, entrySize, consumeBuffer, cancellationToken);
            }

            return records;
        }

        private static int ReadExactly(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken = default)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }

        private static string ConsumeBytesAndComputeSha256(
            Stream stream,
            long byteCount,
            byte[] readBuffer,
            CancellationToken cancellationToken = default)
        {
            if (stream == null || readBuffer == null || readBuffer.Length == 0 || byteCount <= 0)
            {
                return string.Empty;
            }

            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remaining = byteCount;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readSize = (int)Math.Min(readBuffer.Length, remaining);
                var read = stream.Read(readBuffer, 0, readSize);
                if (read <= 0)
                {
                    return string.Empty;
                }

                incrementalHash.AppendData(readBuffer, 0, read);
                remaining -= read;
            }

            var hashBytes = incrementalHash.GetHashAndReset();
            return ToLowerHex(hashBytes);
        }

        private static string ToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void ConsumeBytes(
            Stream stream,
            long byteCount,
            byte[] readBuffer,
            CancellationToken cancellationToken = default)
        {
            var remaining = byteCount;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readSize = (int)Math.Min(readBuffer.Length, remaining);
                var read = stream.Read(readBuffer, 0, readSize);
                if (read <= 0)
                {
                    break;
                }

                remaining -= read;
            }
        }

        private static void ConsumePadding(
            Stream stream,
            long entrySize,
            byte[] readBuffer,
            CancellationToken cancellationToken = default)
        {
            var padding = (TarBlockSize - (entrySize % TarBlockSize)) % TarBlockSize;
            if (padding <= 0)
            {
                return;
            }

            ConsumeBytes(stream, padding, readBuffer, cancellationToken);
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

        private static IReadOnlyList<BlmUnityPackageAssetEntry> HydrateAssetEntries(
            IReadOnlyList<BlmUnityPackageGuidCacheEntryRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return Array.Empty<BlmUnityPackageAssetEntry>();
            }

            return records
                .Where(record => record != null && !string.IsNullOrWhiteSpace(record.Guid))
                .Select(record => new BlmUnityPackageAssetEntry(
                    record.Guid,
                    record.HasAssetData,
                    record.AssetSha256 ?? string.Empty))
                .OrderBy(entry => entry.Guid ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool ArePersistedRecordsEqual(
            BlmUnityPackageGuidCacheRecord left,
            BlmUnityPackageGuidCacheRecord right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            if (left.PackageSize != right.PackageSize ||
                left.PackageLastWriteTimeUtcTicks != right.PackageLastWriteTimeUtcTicks)
            {
                return false;
            }

            if (!string.Equals(left.PackagePath ?? string.Empty, right.PackagePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var leftEntries = left.Entries ?? new List<BlmUnityPackageGuidCacheEntryRecord>();
            var rightEntries = right.Entries ?? new List<BlmUnityPackageGuidCacheEntryRecord>();
            if (leftEntries.Count != rightEntries.Count)
            {
                return false;
            }

            for (var i = 0; i < leftEntries.Count; i++)
            {
                var leftEntry = leftEntries[i];
                var rightEntry = rightEntries[i];
                if (leftEntry == null || rightEntry == null)
                {
                    if (leftEntry != rightEntry)
                    {
                        return false;
                    }

                    continue;
                }

                if (!string.Equals(leftEntry.Guid ?? string.Empty, rightEntry.Guid ?? string.Empty, StringComparison.Ordinal) ||
                    leftEntry.HasAssetData != rightEntry.HasAssetData ||
                    !string.Equals(leftEntry.AssetSha256 ?? string.Empty, rightEntry.AssetSha256 ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildCachePath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return BlmConstants.UnityPackageGuidCacheRelativePath;
            }

            return Path.Combine(projectRoot, BlmConstants.UnityPackageGuidCacheRelativePath);
        }

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_loaded)
                {
                    return;
                }

                var document = LoadUnsafe();
                _persistedRecords.Clear();
                if (document?.Records != null)
                {
                    foreach (var record in document.Records)
                    {
                        if (record == null || string.IsNullOrWhiteSpace(record.PackagePath))
                        {
                            continue;
                        }

                        _persistedRecords[record.PackagePath] = record;
                    }
                }

                _loaded = true;
            }
        }

        private BlmUnityPackageGuidCacheDocument LoadUnsafe()
        {
            if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath))
            {
                return new BlmUnityPackageGuidCacheDocument();
            }

            try
            {
                var json = File.ReadAllText(_cachePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new BlmUnityPackageGuidCacheDocument();
                }

                var document = JsonConvert.DeserializeObject<BlmUnityPackageGuidCacheDocument>(json)
                               ?? new BlmUnityPackageGuidCacheDocument();
                if (document.SchemaVersion != BlmConstants.UnityPackageGuidCacheSchemaVersion)
                {
                    return new BlmUnityPackageGuidCacheDocument();
                }

                return document;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to load unitypackage guid cache: path={_cachePath}, error={ex.Message}");
                return new BlmUnityPackageGuidCacheDocument();
            }
        }

        private void MarkDirty()
        {
            var shouldSchedule = false;
            lock (_syncRoot)
            {
                _dirty = true;
                if (!_saveScheduled)
                {
                    _saveScheduled = true;
                    shouldSchedule = true;
                }
            }

            if (shouldSchedule)
            {
                EditorApplication.delayCall += FlushScheduledSave;
            }
        }

        private void FlushScheduledSave()
        {
            lock (_syncRoot)
            {
                _saveScheduled = false;
                if (!_loaded || !_dirty)
                {
                    return;
                }

                SaveUnsafe();
            }
        }

        private void FlushPendingSaves()
        {
            lock (_syncRoot)
            {
                if (!_loaded || !_dirty)
                {
                    _saveScheduled = false;
                    return;
                }

                SaveUnsafe();
                _saveScheduled = false;
            }
        }

        private void SaveUnsafe()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_cachePath))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var document = new BlmUnityPackageGuidCacheDocument
                {
                    SchemaVersion = BlmConstants.UnityPackageGuidCacheSchemaVersion,
                    Records = _persistedRecords.Values
                        .Where(record => record != null && !string.IsNullOrWhiteSpace(record.PackagePath))
                        .OrderBy(record => record.PackagePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                var json = JsonConvert.SerializeObject(document, JsonSerializerSettings);
                File.WriteAllText(_cachePath, json, new UTF8Encoding(false));
                _dirty = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to save unitypackage guid cache: path={_cachePath}, error={ex.Message}");
            }
        }

        private sealed class CacheEntry
        {
            public LinkedListNode<string> LruNode;
            public IReadOnlyList<BlmUnityPackageAssetEntry> Entries = Array.Empty<BlmUnityPackageAssetEntry>();
            public IReadOnlyList<string> Guids = Array.Empty<string>();
        }

        private sealed class PackageEntryRecord
        {
            public bool HasPathname;
            public bool HasAsset;
            public bool HasMeta;
            public string AssetSha256 = string.Empty;
        }
    }
}
