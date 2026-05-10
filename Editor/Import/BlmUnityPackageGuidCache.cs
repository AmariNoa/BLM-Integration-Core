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

        [JsonProperty("hasAssetData")]
        public bool HasAssetData { get; set; }

        [JsonProperty("assetSha256")]
        public string AssetSha256 { get; set; } = string.Empty;
    }

    internal sealed partial class BlmUnityPackageGuidCache
    {
        private const int TarBlockSize = 512;

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

        public bool TryGetGuids(string sourcePath, out IReadOnlyList<string> guids)
        {
            guids = Array.Empty<string>();
            if (!TryGetEntries(sourcePath, out var entries) ||
                entries == null ||
                entries.Count == 0)
            {
                return false;
            }

            var parsedGuids = BuildGuidArray(entries);
            guids = parsedGuids;
            return parsedGuids.Count > 0;
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
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (!TryBuildCacheKey(sourcePath, out var cacheKey, out var fullPath, out var fileSize, out var lastWriteTimeUtcTicks))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (_entries.TryGetValue(cacheKey, out var cached))
                {
                    Touch(cached);
                    entries = cached.Entries;
                    return cached.Entries.Count > 0;
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
                    var hydratedEntries = HydrateAssetEntries(persisted.Entries);
                    AddOrReplace(cacheKey, hydratedEntries, BuildGuidArray(hydratedEntries));
                    entries = hydratedEntries;
                    return hydratedEntries.Count > 0;
                }
            }

            var parsedEntries = ParseUnityPackageEntries(fullPath, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var parsedGuids = BuildGuidArray(parsedEntries);
            var documentChanged = false;
            lock (_syncRoot)
            {
                AddOrReplace(cacheKey, parsedEntries, parsedGuids);
                var record = new BlmUnityPackageGuidCacheRecord
                {
                    PackagePath = fullPath,
                    PackageSize = fileSize,
                    PackageLastWriteTimeUtcTicks = lastWriteTimeUtcTicks,
                    Entries = parsedEntries
                        .Select(entry => new BlmUnityPackageGuidCacheEntryRecord
                        {
                            Guid = entry.Guid ?? string.Empty,
                            HasAssetData = entry.HasAssetData,
                            AssetSha256 = entry.AssetSha256 ?? string.Empty
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
            return parsedEntries.Count > 0;
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
            IReadOnlyList<BlmUnityPackageAssetEntry> entries,
            IReadOnlyList<string> guids)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return;
            }

            if (_entries.TryGetValue(cacheKey, out var existing))
            {
                existing.Entries = entries ?? Array.Empty<BlmUnityPackageAssetEntry>();
                existing.Guids = guids ?? Array.Empty<string>();
                return;
            }

            var node = new LinkedListNode<string>(cacheKey);
            _lru.AddFirst(node);
            _entries[cacheKey] = new CacheEntry
            {
                LruNode = node,
                Entries = entries ?? Array.Empty<BlmUnityPackageAssetEntry>(),
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

    }
}
