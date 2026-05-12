using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using com.amari_noa.unitypackage_pipeline_core.editor;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal readonly struct BlmUnityPackageAssetEntry
    {
        public string Guid { get; }
        public bool HasAssetData { get; }
        public string AssetSha256 { get; }

        public BlmUnityPackageAssetEntry(string guid, bool hasAssetData, string assetSha256)
        {
            Guid = guid ?? string.Empty;
            HasAssetData = hasAssetData;
            AssetSha256 = assetSha256 ?? string.Empty;
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmUnityPackageGuidCacheDocument
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = BlmConstants.UnityPackageGuidCacheSchemaVersion;

        [JsonProperty("records")]
        public List<BlmUnityPackageGuidCacheRecord> Records { get; set; } = new List<BlmUnityPackageGuidCacheRecord>();
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmUnityPackageGuidCacheRecord
    {
        [JsonProperty("packagePath")]
        public string PackagePath { get; set; } = string.Empty;

        [JsonProperty("packageSize")]
        public long PackageSize { get; set; }

        [JsonProperty("packageLastWriteTimeUtcTicks")]
        public long PackageLastWriteTimeUtcTicks { get; set; }

        [JsonProperty("entries")]
        public List<BlmUnityPackageGuidCacheEntryRecord> Entries { get; set; } = new List<BlmUnityPackageGuidCacheEntryRecord>();
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmUnityPackageGuidCacheEntryRecord
    {
        [JsonProperty("guid")]
        public string Guid { get; set; } = string.Empty;

        [JsonProperty("pathname")]
        public string Pathname { get; set; } = string.Empty;

        [JsonProperty("hasAsset")]
        public bool HasAsset { get; set; }

        [JsonProperty("assetSize")]
        public long AssetSize { get; set; }

        [JsonProperty("assetSha256")]
        public string AssetSha256 { get; set; } = string.Empty;

        [JsonProperty("hasMeta")]
        public bool HasMeta { get; set; }

        [JsonProperty("metaSha256")]
        public string MetaSha256 { get; set; } = string.Empty;

        [JsonProperty("metaGuid")]
        public string MetaGuid { get; set; } = string.Empty;
    }

    internal sealed partial class BlmUnityPackageGuidCache
    {
        private static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, CacheEntry> _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new LinkedList<string>();
        private readonly Dictionary<string, BlmUnityPackageGuidCacheRecord> _persistedRecords =
            new Dictionary<string, BlmUnityPackageGuidCacheRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly string _cachePath;
        private bool _loaded;
        private bool _dirty;
        private bool _saveScheduled;

        internal static BlmUnityPackageGuidCache Shared { get; } = new BlmUnityPackageGuidCache();

        private BlmUnityPackageGuidCache()
        {
            _cachePath = BuildCachePath();
            AssemblyReloadEvents.beforeAssemblyReload += FlushPendingSaves;
            EditorApplication.quitting += FlushPendingSaves;
        }

        public void ClearAll()
        {
            lock (_syncRoot)
            {
                _entries.Clear();
                _lru.Clear();
                _persistedRecords.Clear();
                _dirty = false;
                _saveScheduled = false;
                _loaded = true;

                try
                {
                    if (File.Exists(_cachePath))
                    {
                        File.Delete(_cachePath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BLM Integration Core] Failed to delete unitypackage guid cache file. path={_cachePath}, error={ex.Message}");
                }
            }
        }

        public bool TryGetGuids(string sourcePath, out IReadOnlyList<string> guids)
        {
            guids = Array.Empty<string>();
            if (!TryGetEntries(sourcePath, out var entries) ||
                entries == null ||
                entries.Count == 0)
            {
                return false;
            }

            guids = BuildGuidArray(entries);
            return guids.Count > 0;
        }

        public bool TryGetEntries(string sourcePath, out IReadOnlyList<BlmUnityPackageAssetEntry> entries)
        {
            return TryGetEntries(sourcePath, CancellationToken.None, out entries);
        }

        public bool TryGetEntries(
            string sourcePath,
            CancellationToken cancellationToken,
            out IReadOnlyList<BlmUnityPackageAssetEntry> entries)
        {
            entries = Array.Empty<BlmUnityPackageAssetEntry>();
            if (!TryGetOrFetchContentEntries(sourcePath, cancellationToken, out var contentEntries, out _))
            {
                return false;
            }

            entries = FilterAndMapAssetEntries(contentEntries);
            return entries.Count > 0;
        }

        public bool TryGetContentEntries(
            string sourcePath,
            out IReadOnlyList<AmariUnityPackageContentEntry> entries,
            out string errorMessage)
        {
            return TryGetContentEntries(sourcePath, CancellationToken.None, out entries, out errorMessage);
        }

        public bool TryGetContentEntries(
            string sourcePath,
            CancellationToken cancellationToken,
            out IReadOnlyList<AmariUnityPackageContentEntry> entries,
            out string errorMessage)
        {
            return TryGetOrFetchContentEntries(sourcePath, cancellationToken, out entries, out errorMessage);
        }

        private bool TryGetOrFetchContentEntries(
            string sourcePath,
            CancellationToken cancellationToken,
            out IReadOnlyList<AmariUnityPackageContentEntry> entries,
            out string errorMessage)
        {
            entries = Array.Empty<AmariUnityPackageContentEntry>();
            errorMessage = string.Empty;

            if (cancellationToken.IsCancellationRequested)
            {
                errorMessage = "Operation cancelled.";
                return false;
            }

            if (!TryBuildCacheKey(sourcePath, out var cacheKey, out var fullPath, out var fileSize, out var lastWriteTimeUtcTicks))
            {
                errorMessage = "UnityPackage path is invalid or file is missing.";
                return false;
            }

            lock (_syncRoot)
            {
                if (_entries.TryGetValue(cacheKey, out var cached))
                {
                    Touch(cached);
                    entries = cached.Entries;
                    return true;
                }
            }

            EnsureLoaded();
            lock (_syncRoot)
            {
                if (_persistedRecords.TryGetValue(fullPath, out var persisted) &&
                    persisted != null &&
                    persisted.PackageSize == fileSize &&
                    persisted.PackageLastWriteTimeUtcTicks == lastWriteTimeUtcTicks)
                {
                    var hydratedEntries = HydrateContentEntries(persisted.Entries);
                    AddOrReplace(cacheKey, hydratedEntries);
                    entries = hydratedEntries;
                    return true;
                }
            }

            if (!AmariUnityPackageContentReader.TryRead(
                    fullPath,
                    out var parsedEntries,
                    out var readError,
                    cancellationToken))
            {
                errorMessage = string.IsNullOrWhiteSpace(readError)
                    ? "Failed to read UnityPackage contents."
                    : readError;
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                errorMessage = "Operation cancelled.";
                return false;
            }

            var documentChanged = false;
            lock (_syncRoot)
            {
                AddOrReplace(cacheKey, parsedEntries);
                var record = new BlmUnityPackageGuidCacheRecord
                {
                    PackagePath = fullPath,
                    PackageSize = fileSize,
                    PackageLastWriteTimeUtcTicks = lastWriteTimeUtcTicks,
                    Entries = parsedEntries
                        .Select(entry => new BlmUnityPackageGuidCacheEntryRecord
                        {
                            Guid = entry.Guid ?? string.Empty,
                            Pathname = entry.Pathname ?? string.Empty,
                            HasAsset = entry.HasAsset,
                            AssetSize = entry.AssetSize,
                            AssetSha256 = entry.AssetSha256 ?? string.Empty,
                            HasMeta = entry.HasMeta,
                            MetaSha256 = entry.MetaSha256 ?? string.Empty,
                            MetaGuid = entry.MetaGuid ?? string.Empty
                        })
                        .ToList()
                };

                if (!_persistedRecords.TryGetValue(fullPath, out var existing) ||
                    !ArePersistedRecordsEqual(existing, record))
                {
                    _persistedRecords[fullPath] = record;
                    documentChanged = true;
                }
            }

            if (documentChanged)
            {
                MarkDirty();
            }

            entries = parsedEntries;
            return true;
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

        private void AddOrReplace(
            string cacheKey,
            IReadOnlyList<AmariUnityPackageContentEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return;
            }

            var safeEntries = entries ?? Array.Empty<AmariUnityPackageContentEntry>();
            if (_entries.TryGetValue(cacheKey, out var existing))
            {
                existing.Entries = safeEntries;
                return;
            }

            var node = new LinkedListNode<string>(cacheKey);
            _lru.AddFirst(node);
            _entries[cacheKey] = new CacheEntry
            {
                LruNode = node,
                Entries = safeEntries
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

        private static IReadOnlyList<BlmUnityPackageAssetEntry> FilterAndMapAssetEntries(
            IReadOnlyList<AmariUnityPackageContentEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return Array.Empty<BlmUnityPackageAssetEntry>();
            }

            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Pathname) && (entry.HasAsset || entry.HasMeta))
                .Select(entry => new BlmUnityPackageAssetEntry(
                    entry.Guid,
                    entry.HasAsset,
                    entry.AssetSha256))
                .ToArray();
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
    }
}
