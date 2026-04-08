using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed partial class BlmImportIndexService
    {
        private static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        private readonly object _syncRoot = new object();
        private readonly string _indexPath;
        private readonly Dictionary<string, HashSet<string>> _productGuidSetCache =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, BlmImportIndexFileHashEntry> _fileHashCache =
            new Dictionary<string, BlmImportIndexFileHashEntry>(StringComparer.OrdinalIgnoreCase);
        private BlmImportIndexDocument _document = new BlmImportIndexDocument();
        private bool _loaded;
        private bool _dirty;
        private bool _saveScheduled;
        private long _saveDeadlineUtcTicks;
        private const int FileHashCacheMaxEntries = 4096;
        private const int SaveDebounceMilliseconds = 500;

        internal static BlmImportIndexService Shared { get; } = new BlmImportIndexService();

        private BlmImportIndexService()
        {
            _indexPath = BuildIndexPath();
            AssemblyReloadEvents.beforeAssemblyReload += FlushPendingSaves;
            EditorApplication.quitting += FlushPendingSaves;
        }

        public bool IsGuidImportedAndUnchangedForProduct(string productId, string guid)
        {
            return TryGetImportedGuidAssetPathForProduct(productId, guid, out _);
        }

        public bool TryGetImportedGuidAssetPathForProduct(string productId, string guid, out string resolvedAssetPath)
        {
            return TryGetImportedGuidAssetPathForProductInternal(
                productId,
                guid,
                out resolvedAssetPath,
                allowGuidOwnerPathRepair: true);
        }

        public bool TryGetImportedGuidAssetPathForProductReadOnly(string productId, string guid, out string resolvedAssetPath)
        {
            return TryGetImportedGuidAssetPathForProductInternal(
                productId,
                guid,
                out resolvedAssetPath,
                allowGuidOwnerPathRepair: false);
        }

        private bool TryGetImportedGuidAssetPathForProductInternal(
            string productId,
            string guid,
            out string resolvedAssetPath,
            bool allowGuidOwnerPathRepair)
        {
            resolvedAssetPath = string.Empty;
            var normalizedProductId = NormalizeProductId(productId);
            var normalizedGuid = NormalizeGuid(guid);
            if (string.IsNullOrWhiteSpace(normalizedProductId) || string.IsNullOrWhiteSpace(normalizedGuid))
            {
                return false;
            }

            EnsureLoaded();

            lock (_syncRoot)
            {
                if (!ContainsProductGuidUnsafe(normalizedProductId, normalizedGuid))
                {
                    return false;
                }

                if (!_document.GuidOwners.TryGetValue(normalizedGuid, out var ownerEntry))
                {
                    return false;
                }

                if (!Contains(ownerEntry.OwnerProductIds, normalizedProductId, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return TryEnsureGuidAssetExists(
                normalizedGuid,
                out resolvedAssetPath,
                allowGuidOwnerPathRepair);
        }

        public bool TryResolveAssetAbsolutePath(string assetPath, out string absolutePath)
        {
            return TryResolveAbsolutePathFromAssetPath(assetPath, out absolutePath);
        }

        public bool TryAreFilesContentEqual(string leftFilePath, string rightFilePath, out bool areEqual)
        {
            return TryAreFilesContentEqualInternal(
                leftFilePath,
                rightFilePath,
                out areEqual,
                updateFileHashCache: true);
        }

        public bool TryAreFilesContentEqualReadOnly(string leftFilePath, string rightFilePath, out bool areEqual)
        {
            return TryAreFilesContentEqualInternal(
                leftFilePath,
                rightFilePath,
                out areEqual,
                updateFileHashCache: false);
        }

        private bool TryAreFilesContentEqualInternal(
            string leftFilePath,
            string rightFilePath,
            out bool areEqual,
            bool updateFileHashCache)
        {
            areEqual = false;
            if (!TryComputeFileSha256(leftFilePath, out var leftSha256, updateFileHashCache) ||
                !TryComputeFileSha256(rightFilePath, out var rightSha256, updateFileHashCache))
            {
                return false;
            }

            areEqual = string.Equals(leftSha256, rightSha256, StringComparison.Ordinal);
            return true;
        }

        public bool TryGetFileSha256(string filePath, out string sha256)
        {
            return TryComputeFileSha256(filePath, out sha256, updateFileHashCache: true);
        }

        public bool TryGetFileSha256ReadOnly(string filePath, out string sha256)
        {
            return TryComputeFileSha256(filePath, out sha256, updateFileHashCache: false);
        }

        public bool TryFindUniqueGuidByProductAndFileName(string productId, string sourceFilePath, out string guid)
        {
            return TryFindUniqueGuidByProductAndFileNameInternal(
                productId,
                sourceFilePath,
                out guid,
                allowGuidOwnerPathRepair: true,
                updateFileHashCache: true);
        }

        public bool TryFindUniqueGuidByProductAndFileNameReadOnly(string productId, string sourceFilePath, out string guid)
        {
            return TryFindUniqueGuidByProductAndFileNameInternal(
                productId,
                sourceFilePath,
                out guid,
                allowGuidOwnerPathRepair: false,
                updateFileHashCache: false);
        }

        private bool TryFindUniqueGuidByProductAndFileNameInternal(
            string productId,
            string sourceFilePath,
            out string guid,
            bool allowGuidOwnerPathRepair,
            bool updateFileHashCache)
        {
            guid = string.Empty;
            var normalizedProductId = NormalizeProductId(productId);
            var sourceFileName = Path.GetFileName(sourceFilePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedProductId) || string.IsNullOrWhiteSpace(sourceFileName))
            {
                return false;
            }

            EnsureLoaded();

            var candidateGuids = new List<string>();
            lock (_syncRoot)
            {
                if (!_document.Products.TryGetValue(normalizedProductId, out var productEntry) ||
                    productEntry?.Guids == null ||
                    productEntry.Guids.Count == 0)
                {
                    return false;
                }

                foreach (var rawGuid in productEntry.Guids)
                {
                    var normalizedGuid = NormalizeGuid(rawGuid);
                    if (string.IsNullOrWhiteSpace(normalizedGuid) ||
                        !_document.GuidOwners.TryGetValue(normalizedGuid, out var ownerEntry))
                    {
                        continue;
                    }

                    var ownerFileName = Path.GetFileName(ownerEntry.LastKnownAssetPath ?? string.Empty);
                    if (!string.Equals(ownerFileName, sourceFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!Contains(candidateGuids, normalizedGuid, StringComparer.Ordinal))
                    {
                        candidateGuids.Add(normalizedGuid);
                    }
                }
            }

            if (candidateGuids.Count == 0)
            {
                return false;
            }

            if (!TryComputeFileSha256(sourceFilePath, out var sourceSha256, updateFileHashCache))
            {
                return false;
            }

            string matchedGuid = null;
            for (var i = 0; i < candidateGuids.Count; i++)
            {
                var candidateGuid = candidateGuids[i];
                if (string.IsNullOrWhiteSpace(candidateGuid) ||
                    !TryEnsureGuidAssetExists(candidateGuid, out var resolvedAssetPath, allowGuidOwnerPathRepair) ||
                    !TryResolveAbsolutePathFromAssetPath(resolvedAssetPath, out var candidateAbsolutePath) ||
                    !TryComputeFileSha256(candidateAbsolutePath, out var candidateSha256, updateFileHashCache) ||
                    !string.Equals(candidateSha256, sourceSha256, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(matchedGuid))
                {
                    matchedGuid = candidateGuid;
                    continue;
                }

                if (!string.Equals(matchedGuid, candidateGuid, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(matchedGuid))
            {
                return false;
            }

            guid = matchedGuid;
            return true;
        }

        public bool TryEnsureGuidAssetExists(string guid, out string resolvedAssetPath)
        {
            return TryEnsureGuidAssetExists(guid, out resolvedAssetPath, allowPathRepair: true);
        }

        private bool TryEnsureGuidAssetExists(string guid, out string resolvedAssetPath, bool allowPathRepair)
        {
            resolvedAssetPath = string.Empty;
            var normalizedGuid = NormalizeGuid(guid);
            if (string.IsNullOrWhiteSpace(normalizedGuid))
            {
                return false;
            }

            EnsureLoaded();

            string knownPath;
            lock (_syncRoot)
            {
                if (!_document.GuidOwners.TryGetValue(normalizedGuid, out var ownerEntry))
                {
                    return false;
                }

                knownPath = NormalizeAssetPath(ownerEntry.LastKnownAssetPath);
            }

            if (TryValidateAssetPathByGuid(knownPath, normalizedGuid))
            {
                resolvedAssetPath = knownPath;
                return true;
            }

            var reResolvedPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(normalizedGuid));
            if (string.IsNullOrWhiteSpace(reResolvedPath))
            {
                return false;
            }

            if (!allowPathRepair)
            {
                resolvedAssetPath = reResolvedPath;
                return true;
            }

            var changed = false;
            lock (_syncRoot)
            {
                if (_document.GuidOwners.TryGetValue(normalizedGuid, out var ownerEntry) &&
                    !string.Equals(ownerEntry.LastKnownAssetPath, reResolvedPath, StringComparison.Ordinal))
                {
                    ownerEntry.LastKnownAssetPath = reResolvedPath;
                    changed = true;
                }
            }

            if (changed)
            {
                MarkDirty();
            }

            resolvedAssetPath = reResolvedPath;
            return true;
        }

        public string GetIndexFileFingerprint()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_indexPath))
                {
                    return "0:0";
                }

                var info = new FileInfo(_indexPath);
                return info.Exists
                    ? string.Format(CultureInfo.InvariantCulture, "{0}:{1}", info.Length, info.LastWriteTimeUtc.Ticks)
                    : "0:0";
            }
            catch
            {
                return "0:0";
            }
        }

        public void UpdateFromImportedItem(
            BlmImportRequestItem item,
            BlmImportedItemKind itemKind,
            bool destinationWasPreExisting,
            bool isSkipped,
            BlmImportIndexAssetResolveCache resolveCache = null)
        {
            if (item == null || isSkipped)
            {
                return;
            }

            var productId = NormalizeProductId(item.ProductId);
            if (string.IsNullOrWhiteSpace(productId))
            {
                return;
            }

            var normalizedDestinationPaths = item.DestinationAssetPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            if (normalizedDestinationPaths.Length == 0)
            {
                return;
            }

            EnsureLoaded();

            var changed = false;
            lock (_syncRoot)
            {
                if (!_document.Products.TryGetValue(productId, out var productEntry) || productEntry == null)
                {
                    productEntry = new BlmImportIndexProductEntry();
                    _document.Products[productId] = productEntry;
                    changed = true;
                }

                productEntry.Guids ??= new List<string>();

                foreach (var destinationPath in normalizedDestinationPaths)
                {
                    var guid = ResolveGuidByAssetPath(destinationPath, resolveCache);
                    if (string.IsNullOrWhiteSpace(guid))
                    {
                        continue;
                    }

                    if (AddDistinctSorted(productEntry.Guids, guid, StringComparer.Ordinal))
                    {
                        changed = true;
                    }

                    if (!_document.GuidOwners.TryGetValue(guid, out var ownerEntry) || ownerEntry == null)
                    {
                        ownerEntry = new BlmImportIndexGuidOwnerEntry
                        {
                            DeletePolicy = DetermineDeletePolicyForNewGuid(itemKind, destinationWasPreExisting)
                        };
                        _document.GuidOwners[guid] = ownerEntry;
                        changed = true;
                    }
                    else if (!IsValidDeletePolicy(ownerEntry.DeletePolicy))
                    {
                        ownerEntry.DeletePolicy = BlmImportIndexDeletePolicies.Protected;
                        changed = true;
                    }

                    ownerEntry.OwnerProductIds ??= new List<string>();
                    if (AddDistinctSorted(ownerEntry.OwnerProductIds, productId, StringComparer.Ordinal))
                    {
                        changed = true;
                    }

                    var resolvedAssetPath = ResolveAssetPathByGuid(guid, resolveCache);
                    if (string.IsNullOrWhiteSpace(resolvedAssetPath))
                    {
                        resolvedAssetPath = destinationPath;
                    }

                    if (!string.Equals(ownerEntry.LastKnownAssetPath, resolvedAssetPath, StringComparison.Ordinal))
                    {
                        ownerEntry.LastKnownAssetPath = resolvedAssetPath;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                lock (_syncRoot)
                {
                    _productGuidSetCache.Remove(productId);
                }

                MarkDirty();
            }
        }

        public void HandleDeletedAssets(IEnumerable<string> deletedAssetPaths)
        {
            if (deletedAssetPaths == null)
            {
                return;
            }

            var normalizedDeletedPaths = deletedAssetPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                               !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedDeletedPaths.Length == 0)
            {
                return;
            }

            EnsureLoaded();

            var changed = false;
            var removedGuids = new HashSet<string>(StringComparer.Ordinal);
            lock (_syncRoot)
            {
                var candidateGuids = _document.GuidOwners
                    .Where(pair => Contains(normalizedDeletedPaths, pair.Value?.LastKnownAssetPath, StringComparer.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToArray();
                if (candidateGuids.Length == 0)
                {
                    return;
                }

                foreach (var guid in candidateGuids)
                {
                    var normalizedGuid = NormalizeGuid(guid);
                    if (string.IsNullOrWhiteSpace(normalizedGuid) ||
                        !_document.GuidOwners.TryGetValue(normalizedGuid, out var ownerEntry))
                    {
                        continue;
                    }

                    var reResolvedPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(normalizedGuid));
                    if (!string.IsNullOrWhiteSpace(reResolvedPath))
                    {
                        if (!string.Equals(ownerEntry.LastKnownAssetPath, reResolvedPath, StringComparison.Ordinal))
                        {
                            ownerEntry.LastKnownAssetPath = reResolvedPath;
                            changed = true;
                        }

                        continue;
                    }

                    _document.GuidOwners.Remove(normalizedGuid);
                    removedGuids.Add(normalizedGuid);
                    changed = true;
                }

                if (removedGuids.Count > 0)
                {
                    foreach (var productPair in _document.Products.ToArray())
                    {
                        var productEntry = productPair.Value;
                        if (productEntry?.Guids == null || productEntry.Guids.Count == 0)
                        {
                            _document.Products.Remove(productPair.Key);
                            changed = true;
                            continue;
                        }

                        productEntry.Guids = productEntry.Guids
                            .Where(guid => !removedGuids.Contains(guid))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(guid => guid, StringComparer.Ordinal)
                            .ToList();
                        if (productEntry.Guids.Count == 0)
                        {
                            _document.Products.Remove(productPair.Key);
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    NormalizeDocumentUnsafe();
                }
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
                    _saveDeadlineUtcTicks = 0L;
                    return;
                }

                SaveUnsafe();
                _saveScheduled = false;
                _saveDeadlineUtcTicks = 0L;
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

                _document = LoadUnsafe() ?? new BlmImportIndexDocument();
                changed = NormalizeDocumentUnsafe();
                _loaded = true;
            }

            if (changed)
            {
                MarkDirty();
            }
        }

        private BlmImportIndexDocument LoadUnsafe()
        {
            if (string.IsNullOrWhiteSpace(_indexPath) || !File.Exists(_indexPath))
            {
                return new BlmImportIndexDocument();
            }

            try
            {
                var json = File.ReadAllText(_indexPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new BlmImportIndexDocument();
                }

                return JsonConvert.DeserializeObject<BlmImportIndexDocument>(json) ?? new BlmImportIndexDocument();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to load import index: path={_indexPath}, error={ex.Message}");
                return new BlmImportIndexDocument();
            }
        }

        private bool NormalizeDocumentUnsafe()
        {
            var changed = false;
            _document ??= new BlmImportIndexDocument();

            if (_document.SchemaVersion != BlmConstants.ImportIndexSchemaVersion)
            {
                _document.SchemaVersion = BlmConstants.ImportIndexSchemaVersion;
                changed = true;
            }

            _document.Products ??= new Dictionary<string, BlmImportIndexProductEntry>(StringComparer.Ordinal);
            _document.GuidOwners ??= new Dictionary<string, BlmImportIndexGuidOwnerEntry>(StringComparer.Ordinal);
            _document.FileHashes ??= new Dictionary<string, BlmImportIndexFileHashEntry>(StringComparer.OrdinalIgnoreCase);

            var normalizedProducts = new Dictionary<string, BlmImportIndexProductEntry>(StringComparer.Ordinal);
            foreach (var pair in _document.Products)
            {
                var productId = NormalizeProductId(pair.Key);
                if (string.IsNullOrWhiteSpace(productId))
                {
                    changed = true;
                    continue;
                }

                var entry = pair.Value ?? new BlmImportIndexProductEntry();
                var normalizedGuids = (entry.Guids ?? new List<string>())
                    .Select(NormalizeGuid)
                    .Where(guid => !string.IsNullOrWhiteSpace(guid))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(guid => guid, StringComparer.Ordinal)
                    .ToList();
                if (normalizedGuids.Count == 0)
                {
                    changed = true;
                    continue;
                }

                if (!normalizedProducts.TryGetValue(productId, out var existing))
                {
                    normalizedProducts[productId] = new BlmImportIndexProductEntry
                    {
                        Guids = normalizedGuids
                    };
                    continue;
                }

                existing.Guids = existing.Guids
                    .Concat(normalizedGuids)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(guid => guid, StringComparer.Ordinal)
                    .ToList();
                changed = true;
            }

            var normalizedOwners = new Dictionary<string, BlmImportIndexGuidOwnerEntry>(StringComparer.Ordinal);
            foreach (var pair in _document.GuidOwners)
            {
                var guid = NormalizeGuid(pair.Key);
                if (string.IsNullOrWhiteSpace(guid))
                {
                    changed = true;
                    continue;
                }

                var entry = pair.Value ?? new BlmImportIndexGuidOwnerEntry();
                var ownerProductIds = (entry.OwnerProductIds ?? new List<string>())
                    .Select(NormalizeProductId)
                    .Where(productId => !string.IsNullOrWhiteSpace(productId))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(productId => productId, StringComparer.Ordinal)
                    .ToList();
                var deletePolicy = IsValidDeletePolicy(entry.DeletePolicy)
                    ? entry.DeletePolicy
                    : BlmImportIndexDeletePolicies.Protected;
                var lastKnownAssetPath = NormalizeAssetPath(entry.LastKnownAssetPath);

                if (normalizedOwners.TryGetValue(guid, out var existing))
                {
                    existing.OwnerProductIds = existing.OwnerProductIds
                        .Concat(ownerProductIds)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(productId => productId, StringComparer.Ordinal)
                        .ToList();
                    if (string.IsNullOrWhiteSpace(existing.LastKnownAssetPath) && !string.IsNullOrWhiteSpace(lastKnownAssetPath))
                    {
                        existing.LastKnownAssetPath = lastKnownAssetPath;
                    }

                    if (string.Equals(existing.DeletePolicy, BlmImportIndexDeletePolicies.Protected, StringComparison.Ordinal) ||
                        string.Equals(deletePolicy, BlmImportIndexDeletePolicies.Protected, StringComparison.Ordinal))
                    {
                        existing.DeletePolicy = BlmImportIndexDeletePolicies.Protected;
                    }
                    else
                    {
                        existing.DeletePolicy = BlmImportIndexDeletePolicies.Deletable;
                    }

                    changed = true;
                    continue;
                }

                normalizedOwners[guid] = new BlmImportIndexGuidOwnerEntry
                {
                    OwnerProductIds = ownerProductIds,
                    DeletePolicy = deletePolicy,
                    LastKnownAssetPath = lastKnownAssetPath
                };
            }

            foreach (var productPair in normalizedProducts)
            {
                var productId = productPair.Key;
                var entry = productPair.Value;
                if (entry?.Guids == null)
                {
                    continue;
                }

                foreach (var guid in entry.Guids)
                {
                    if (!normalizedOwners.TryGetValue(guid, out var owner))
                    {
                        owner = new BlmImportIndexGuidOwnerEntry();
                        normalizedOwners[guid] = owner;
                        changed = true;
                    }

                    if (!Contains(owner.OwnerProductIds, productId, StringComparer.Ordinal))
                    {
                        owner.OwnerProductIds.Add(productId);
                        owner.OwnerProductIds = owner.OwnerProductIds
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToList();
                        changed = true;
                    }
                }
            }

            foreach (var ownerPair in normalizedOwners)
            {
                var guid = ownerPair.Key;
                var owner = ownerPair.Value;
                if (owner?.OwnerProductIds == null)
                {
                    continue;
                }

                foreach (var productId in owner.OwnerProductIds)
                {
                    if (!normalizedProducts.TryGetValue(productId, out var product))
                    {
                        product = new BlmImportIndexProductEntry();
                        normalizedProducts[productId] = product;
                        changed = true;
                    }

                    if (!Contains(product.Guids, guid, StringComparer.Ordinal))
                    {
                        product.Guids.Add(guid);
                        product.Guids = product.Guids
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToList();
                        changed = true;
                    }
                }
            }

            var normalizedFileHashes = new Dictionary<string, BlmImportIndexFileHashEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _document.FileHashes)
            {
                var normalizedPath = NormalizeFileHashPath(pair.Key);
                var entry = pair.Value;
                var normalizedSha256 = NormalizeSha256(entry?.Sha256);
                var normalizedFileSize = entry?.FileSize ?? -1;
                var normalizedLastWriteTimeUtcTicks = entry?.LastWriteTimeUtcTicks ?? -1;

                if (string.IsNullOrWhiteSpace(normalizedPath) ||
                    string.IsNullOrWhiteSpace(normalizedSha256) ||
                    normalizedFileSize < 0 ||
                    normalizedLastWriteTimeUtcTicks < 0)
                {
                    changed = true;
                    continue;
                }

                var normalizedEntry = new BlmImportIndexFileHashEntry
                {
                    FileSize = normalizedFileSize,
                    LastWriteTimeUtcTicks = normalizedLastWriteTimeUtcTicks,
                    Sha256 = normalizedSha256
                };

                if (normalizedFileHashes.TryGetValue(normalizedPath, out var existing))
                {
                    var preferred = SelectPreferredFileHashEntry(existing, normalizedEntry);
                    if (!AreFileHashEntriesEqual(existing, preferred))
                    {
                        normalizedFileHashes[normalizedPath] = CloneFileHashEntry(preferred);
                    }

                    changed = true;
                    continue;
                }

                normalizedFileHashes[normalizedPath] = CloneFileHashEntry(normalizedEntry);
                if (!string.Equals(pair.Key, normalizedPath, StringComparison.Ordinal) ||
                    entry == null ||
                    !AreFileHashEntriesEqual(entry, normalizedEntry))
                {
                    changed = true;
                }
            }

            if (PruneFileHashEntries(normalizedFileHashes))
            {
                changed = true;
            }

            _document.Products = normalizedProducts;
            _document.GuidOwners = normalizedOwners;
            _document.FileHashes = normalizedFileHashes;
            _productGuidSetCache.Clear();
            SyncFileHashCacheFromDocumentUnsafe();
            return changed;
        }

        private void MarkDirty()
        {
            var shouldSchedule = false;
            lock (_syncRoot)
            {
                _dirty = true;
                _saveDeadlineUtcTicks = DateTime.UtcNow.AddMilliseconds(SaveDebounceMilliseconds).Ticks;
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
                if (!_loaded || !_dirty)
                {
                    _saveScheduled = false;
                    _saveDeadlineUtcTicks = 0L;
                    return;
                }

                if (DateTime.UtcNow.Ticks < _saveDeadlineUtcTicks)
                {
                    EditorApplication.delayCall += FlushScheduledSave;
                    return;
                }

                SaveUnsafe();
                _saveScheduled = false;
                _saveDeadlineUtcTicks = 0L;
            }
        }

        private void SaveUnsafe()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_indexPath))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_indexPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_document, JsonSerializerSettings);
                File.WriteAllText(_indexPath, json, new UTF8Encoding(false));
                _dirty = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to save import index: path={_indexPath}, error={ex.Message}");
            }
        }

    }
}
