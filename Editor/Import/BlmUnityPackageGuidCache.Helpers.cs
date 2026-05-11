using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using com.amari_noa.unitypackage_pipeline_core.editor;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed partial class BlmUnityPackageGuidCache
    {
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

        private static IReadOnlyList<AmariUnityPackageContentEntry> HydrateContentEntries(
            IReadOnlyList<BlmUnityPackageGuidCacheEntryRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return Array.Empty<AmariUnityPackageContentEntry>();
            }

            return records
                .Where(record => record != null && !string.IsNullOrWhiteSpace(record.Guid))
                .Select(record => new AmariUnityPackageContentEntry(
                    record.Guid,
                    record.Pathname ?? string.Empty,
                    record.HasAsset,
                    record.AssetSize,
                    record.AssetSha256 ?? string.Empty,
                    record.HasMeta,
                    record.MetaSha256 ?? string.Empty,
                    record.MetaGuid ?? string.Empty))
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
                    !string.Equals(leftEntry.Pathname ?? string.Empty, rightEntry.Pathname ?? string.Empty, StringComparison.Ordinal) ||
                    leftEntry.HasAsset != rightEntry.HasAsset ||
                    leftEntry.AssetSize != rightEntry.AssetSize ||
                    !string.Equals(leftEntry.AssetSha256 ?? string.Empty, rightEntry.AssetSha256 ?? string.Empty, StringComparison.Ordinal) ||
                    leftEntry.HasMeta != rightEntry.HasMeta ||
                    !string.Equals(leftEntry.MetaSha256 ?? string.Empty, rightEntry.MetaSha256 ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(leftEntry.MetaGuid ?? string.Empty, rightEntry.MetaGuid ?? string.Empty, StringComparison.Ordinal))
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
            public IReadOnlyList<AmariUnityPackageContentEntry> Entries = Array.Empty<AmariUnityPackageContentEntry>();
        }
    }
}
