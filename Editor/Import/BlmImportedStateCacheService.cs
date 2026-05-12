using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmImportedStateCacheDocument
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = BlmConstants.ImportedStateCacheSchemaVersion;

        [JsonProperty("entries")]
        public List<BlmImportedStateCacheEntry> Entries { get; set; } = new List<BlmImportedStateCacheEntry>();
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmImportedStateCacheEntry
    {
        [JsonProperty("productId")]
        public string ProductId { get; set; } = string.Empty;

        [JsonProperty("normalizedSourcePath")]
        public string NormalizedSourcePath { get; set; } = string.Empty;

        [JsonProperty("sourceFileSize")]
        public long SourceFileSize { get; set; }

        [JsonProperty("sourceLastWriteTimeUtcTicks")]
        public long SourceLastWriteTimeUtcTicks { get; set; }

        [JsonProperty("importIndexFingerprint")]
        public string ImportIndexFingerprint { get; set; } = string.Empty;

        [JsonProperty("destinationAssetGuid")]
        public string DestinationAssetGuid { get; set; } = string.Empty;

        [JsonProperty("destinationFileSize")]
        public long DestinationFileSize { get; set; }

        [JsonProperty("destinationLastWriteTimeUtcTicks")]
        public long DestinationLastWriteTimeUtcTicks { get; set; }

        [JsonProperty("isImportedUnchanged")]
        public bool IsImportedUnchanged { get; set; }
    }

    internal sealed partial class BlmImportedStateCacheService
    {
        private static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        private readonly object _syncRoot = new object();
        private readonly string _cachePath;
        private readonly Dictionary<string, BlmImportedStateCacheEntry> _entriesByLookupKey =
            new Dictionary<string, BlmImportedStateCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;
        private bool _dirty;
        private bool _saveScheduled;

        internal static BlmImportedStateCacheService Shared { get; } = new BlmImportedStateCacheService();

        private BlmImportedStateCacheService()
        {
            _cachePath = BuildCachePath();
            AssemblyReloadEvents.beforeAssemblyReload += FlushPendingSaves;
            EditorApplication.quitting += FlushPendingSaves;
        }

        public void ClearAll()
        {
            lock (_syncRoot)
            {
                _entriesByLookupKey.Clear();
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
                    Debug.LogWarning($"[BLM Integration Core] Failed to delete imported state cache file. path={_cachePath}, error={ex.Message}");
                }
            }
        }

        public bool TryBuildEntry(
            string productId,
            string sourceFilePath,
            string importIndexFingerprint,
            out BlmImportedStateCacheEntry entry)
        {
            return TryBuildEntry(
                productId,
                sourceFilePath,
                importIndexFingerprint,
                string.Empty,
                0L,
                0L,
                out entry);
        }

        public bool TryBuildEntry(
            string productId,
            string sourceFilePath,
            string importIndexFingerprint,
            string destinationAssetGuid,
            long destinationFileSize,
            long destinationLastWriteTimeUtcTicks,
            out BlmImportedStateCacheEntry entry)
        {
            entry = null;

            var normalizedProductId = NormalizeProductId(productId);
            var normalizedFingerprint = NormalizeImportIndexFingerprint(importIndexFingerprint);
            var normalizedDestinationGuid = NormalizeGuid(destinationAssetGuid);
            if (string.IsNullOrWhiteSpace(normalizedProductId) || string.IsNullOrWhiteSpace(normalizedFingerprint))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourceFilePath))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(sourceFilePath);
            }
            catch
            {
                fullPath = sourceFilePath;
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

            entry = new BlmImportedStateCacheEntry
            {
                ProductId = normalizedProductId,
                NormalizedSourcePath = NormalizeSourcePath(fullPath),
                SourceFileSize = info.Length,
                SourceLastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
                ImportIndexFingerprint = normalizedFingerprint,
                DestinationAssetGuid = normalizedDestinationGuid,
                DestinationFileSize = Math.Max(0L, destinationFileSize),
                DestinationLastWriteTimeUtcTicks = Math.Max(0L, destinationLastWriteTimeUtcTicks)
            };
            return !string.IsNullOrWhiteSpace(entry.NormalizedSourcePath);
        }

        public bool TryGet(BlmImportedStateCacheEntry entry, out bool isImportedUnchanged)
        {
            isImportedUnchanged = false;
            if (!TryNormalizeEntry(entry, out var normalized))
            {
                return false;
            }

            EnsureLoaded();
            var lookupKey = BuildLookupKey(normalized);
            lock (_syncRoot)
            {
                if (!_entriesByLookupKey.TryGetValue(lookupKey, out var existing))
                {
                    return false;
                }

                isImportedUnchanged = existing.IsImportedUnchanged;
                return true;
            }
        }

        public void Upsert(BlmImportedStateCacheEntry entry, bool isImportedUnchanged)
        {
            if (!TryNormalizeEntry(entry, out var normalized))
            {
                return;
            }

            normalized.IsImportedUnchanged = isImportedUnchanged;
            EnsureLoaded();

            var lookupKey = BuildLookupKey(normalized);
            var changed = false;
            lock (_syncRoot)
            {
                if (_entriesByLookupKey.TryGetValue(lookupKey, out var existing) &&
                    existing.IsImportedUnchanged == normalized.IsImportedUnchanged)
                {
                    return;
                }

                _entriesByLookupKey[lookupKey] = normalized;
                changed = true;
            }

            if (changed)
            {
                MarkDirty();
            }
        }

        public void FlushPendingSaves()
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

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            var changed = false;
            lock (_syncRoot)
            {
                if (_loaded)
                {
                    return;
                }

                var document = LoadUnsafe();
                changed = NormalizeDocumentUnsafe(document);
                _loaded = true;
            }

            if (changed)
            {
                MarkDirty();
            }
        }

        private BlmImportedStateCacheDocument LoadUnsafe()
        {
            if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath))
            {
                return new BlmImportedStateCacheDocument();
            }

            try
            {
                var json = File.ReadAllText(_cachePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new BlmImportedStateCacheDocument();
                }

                return JsonConvert.DeserializeObject<BlmImportedStateCacheDocument>(json)
                       ?? new BlmImportedStateCacheDocument();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to load imported-state cache: path={_cachePath}, error={ex.Message}");
                return new BlmImportedStateCacheDocument();
            }
        }

        private bool NormalizeDocumentUnsafe(BlmImportedStateCacheDocument document)
        {
            var changed = false;
            document ??= new BlmImportedStateCacheDocument();
            if (document.SchemaVersion != BlmConstants.ImportedStateCacheSchemaVersion)
            {
                document.SchemaVersion = BlmConstants.ImportedStateCacheSchemaVersion;
                changed = true;
            }

            var entries = document.Entries ?? new List<BlmImportedStateCacheEntry>();
            _entriesByLookupKey.Clear();

            foreach (var entry in entries)
            {
                if (!TryNormalizeEntry(entry, out var normalized))
                {
                    changed = true;
                    continue;
                }

                var lookupKey = BuildLookupKey(normalized);
                if (_entriesByLookupKey.TryGetValue(lookupKey, out var existing))
                {
                    if (existing.IsImportedUnchanged != normalized.IsImportedUnchanged)
                    {
                        _entriesByLookupKey[lookupKey] = normalized;
                    }

                    changed = true;
                    continue;
                }

                _entriesByLookupKey[lookupKey] = normalized;
            }

            return changed;
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

                var document = new BlmImportedStateCacheDocument
                {
                    SchemaVersion = BlmConstants.ImportedStateCacheSchemaVersion,
                    Entries = _entriesByLookupKey.Values
                        .OrderBy(entry => entry.ProductId ?? string.Empty, StringComparer.Ordinal)
                        .ThenBy(entry => entry.NormalizedSourcePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(entry => entry.SourceFileSize)
                        .ThenBy(entry => entry.SourceLastWriteTimeUtcTicks)
                        .ThenBy(entry => entry.ImportIndexFingerprint ?? string.Empty, StringComparer.Ordinal)
                        .ThenBy(entry => entry.DestinationAssetGuid ?? string.Empty, StringComparer.Ordinal)
                        .ThenBy(entry => entry.DestinationFileSize)
                        .ThenBy(entry => entry.DestinationLastWriteTimeUtcTicks)
                        .ToList()
                };

                var json = JsonConvert.SerializeObject(document, JsonSerializerSettings);
                File.WriteAllText(_cachePath, json, new UTF8Encoding(false));
                _dirty = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to save imported-state cache: path={_cachePath}, error={ex.Message}");
            }
        }

    }
}
