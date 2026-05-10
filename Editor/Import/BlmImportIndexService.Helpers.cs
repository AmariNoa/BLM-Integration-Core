using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed partial class BlmImportIndexService
    {
        private static string DetermineDeletePolicyForNewGuid(BlmImportedItemKind itemKind, bool destinationWasPreExisting)
        {
            if (itemKind == BlmImportedItemKind.NonUnityPackage && !destinationWasPreExisting)
            {
                return BlmImportIndexDeletePolicies.Deletable;
            }

            return BlmImportIndexDeletePolicies.Protected;
        }

        private static bool IsValidDeletePolicy(string deletePolicy)
        {
            return string.Equals(deletePolicy, BlmImportIndexDeletePolicies.Deletable, StringComparison.Ordinal) ||
                   string.Equals(deletePolicy, BlmImportIndexDeletePolicies.Protected, StringComparison.Ordinal);
        }

        private static string NormalizeProductId(string productId)
        {
            return string.IsNullOrWhiteSpace(productId)
                ? string.Empty
                : productId.Trim();
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            return assetPath.Replace('\\', '/').Trim();
        }

        private static string NormalizeGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                return string.Empty;
            }

            var normalized = guid.Trim().ToLowerInvariant();
            if (normalized.Length != 32)
            {
                return string.Empty;
            }

            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                var isDigit = c >= '0' && c <= '9';
                var isHexLower = c >= 'a' && c <= 'f';
                if (!isDigit && !isHexLower)
                {
                    return string.Empty;
                }
            }

            return normalized;
        }

        private static bool TryValidateAssetPathByGuid(string assetPath, string guid)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(guid))
            {
                return false;
            }

            var pathGuid = NormalizeGuid(AssetDatabase.AssetPathToGUID(assetPath));
            return !string.IsNullOrWhiteSpace(pathGuid) &&
                   string.Equals(pathGuid, guid, StringComparison.Ordinal);
        }

        private static string ResolveGuidByAssetPath(string assetPath, BlmImportIndexAssetResolveCache resolveCache)
        {
            var normalizedAssetPath = NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(normalizedAssetPath))
            {
                return string.Empty;
            }

            if (resolveCache != null &&
                resolveCache.TryGetGuidByAssetPath(normalizedAssetPath, out var cachedGuid))
            {
                return NormalizeGuid(cachedGuid);
            }

            var resolvedGuid = NormalizeGuid(AssetDatabase.AssetPathToGUID(normalizedAssetPath));
            resolveCache?.SetGuidByAssetPath(normalizedAssetPath, resolvedGuid);
            return resolvedGuid;
        }

        private static string ResolveAssetPathByGuid(string guid, BlmImportIndexAssetResolveCache resolveCache)
        {
            var normalizedGuid = NormalizeGuid(guid);
            if (string.IsNullOrWhiteSpace(normalizedGuid))
            {
                return string.Empty;
            }

            if (resolveCache != null &&
                resolveCache.TryGetAssetPathByGuid(normalizedGuid, out var cachedAssetPath))
            {
                return NormalizeAssetPath(cachedAssetPath);
            }

            var resolvedAssetPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(normalizedGuid));
            resolveCache?.SetAssetPathByGuid(normalizedGuid, resolvedAssetPath);
            return resolvedAssetPath;
        }

        private static bool AddDistinctSorted(List<string> source, string value, StringComparer comparer)
        {
            if (source == null || string.IsNullOrWhiteSpace(value) || comparer == null)
            {
                return false;
            }

            if (Contains(source, value, comparer))
            {
                return false;
            }

            source.Add(value);
            source.Sort(comparer);
            return true;
        }

        private static bool Contains(IEnumerable<string> source, string value, StringComparer comparer)
        {
            if (source == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var item in source)
            {
                if (comparer.Equals(item ?? string.Empty, value))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeFileHashPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalizedSeparators = path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalizedSeparators))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(normalizedSeparators);
            }
            catch
            {
                return normalizedSeparators;
            }
        }

        private static string NormalizeSha256(string sha256)
        {
            if (string.IsNullOrWhiteSpace(sha256))
            {
                return string.Empty;
            }

            var normalized = sha256.Trim().ToLowerInvariant();
            if (normalized.Length != 64)
            {
                return string.Empty;
            }

            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                var isDigit = c >= '0' && c <= '9';
                var isHexLower = c >= 'a' && c <= 'f';
                if (!isDigit && !isHexLower)
                {
                    return string.Empty;
                }
            }

            return normalized;
        }

        private static BlmImportIndexFileHashEntry CloneFileHashEntry(BlmImportIndexFileHashEntry entry)
        {
            return new BlmImportIndexFileHashEntry
            {
                FileSize = entry?.FileSize ?? 0,
                LastWriteTimeUtcTicks = entry?.LastWriteTimeUtcTicks ?? 0,
                Sha256 = entry?.Sha256 ?? string.Empty
            };
        }

        private static bool AreFileHashEntriesEqual(BlmImportIndexFileHashEntry left, BlmImportIndexFileHashEntry right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return left.FileSize == right.FileSize &&
                   left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks &&
                   string.Equals(left.Sha256 ?? string.Empty, right.Sha256 ?? string.Empty, StringComparison.Ordinal);
        }

        private static BlmImportIndexFileHashEntry SelectPreferredFileHashEntry(
            BlmImportIndexFileHashEntry existing,
            BlmImportIndexFileHashEntry candidate)
        {
            if (existing == null)
            {
                return CloneFileHashEntry(candidate);
            }

            if (candidate == null)
            {
                return CloneFileHashEntry(existing);
            }

            if (candidate.LastWriteTimeUtcTicks > existing.LastWriteTimeUtcTicks)
            {
                return CloneFileHashEntry(candidate);
            }

            if (candidate.LastWriteTimeUtcTicks < existing.LastWriteTimeUtcTicks)
            {
                return CloneFileHashEntry(existing);
            }

            if (candidate.FileSize > existing.FileSize)
            {
                return CloneFileHashEntry(candidate);
            }

            if (candidate.FileSize < existing.FileSize)
            {
                return CloneFileHashEntry(existing);
            }

            return string.Compare(candidate.Sha256 ?? string.Empty, existing.Sha256 ?? string.Empty, StringComparison.Ordinal) >= 0
                ? CloneFileHashEntry(candidate)
                : CloneFileHashEntry(existing);
        }

        private static bool PruneFileHashEntries(Dictionary<string, BlmImportIndexFileHashEntry> entries)
        {
            if (entries == null || entries.Count <= FileHashCacheMaxEntries)
            {
                return false;
            }

            var keepKeys = new HashSet<string>(
                entries
                    .OrderByDescending(pair => pair.Value?.LastWriteTimeUtcTicks ?? long.MinValue)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(FileHashCacheMaxEntries)
                    .Select(pair => pair.Key),
                StringComparer.OrdinalIgnoreCase);

            var changed = false;
            foreach (var key in entries.Keys.ToArray())
            {
                if (keepKeys.Contains(key))
                {
                    continue;
                }

                entries.Remove(key);
                changed = true;
            }

            return changed;
        }

        private void SyncFileHashCacheFromDocumentUnsafe()
        {
            _fileHashCache.Clear();
            if (_document?.FileHashes == null)
            {
                return;
            }

            foreach (var pair in _document.FileHashes)
            {
                _fileHashCache[pair.Key] = CloneFileHashEntry(pair.Value);
            }
        }

        private bool TryComputeFileSha256(
            string path,
            out string sha256,
            bool updateFileHashCache,
            CancellationToken cancellationToken = default)
        {
            sha256 = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            EnsureLoaded();

            var fullPath = NormalizeFileHashPath(path);

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var fileSize = info.Length;
            var lastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks;

            lock (_syncRoot)
            {
                if (_fileHashCache.TryGetValue(fullPath, out var cached) &&
                    cached != null &&
                    cached.FileSize == fileSize &&
                    cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks &&
                    !string.IsNullOrWhiteSpace(cached.Sha256))
                {
                    sha256 = cached.Sha256;
                    return true;
                }
            }

            string computedSha256;
            try
            {
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    81920,
                    FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                }

                var hashBytes = hash.GetHashAndReset();
                var builder = new StringBuilder(hashBytes.Length * 2);
                for (var i = 0; i < hashBytes.Length; i++)
                {
                    builder.Append(hashBytes[i].ToString("x2"));
                }

                computedSha256 = builder.ToString();
            }
            catch
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var normalizedSha256 = NormalizeSha256(computedSha256);
            if (string.IsNullOrWhiteSpace(normalizedSha256))
            {
                return false;
            }

            if (!updateFileHashCache)
            {
                sha256 = normalizedSha256;
                return true;
            }

            var nextEntry = new BlmImportIndexFileHashEntry
            {
                FileSize = fileSize,
                LastWriteTimeUtcTicks = lastWriteTimeUtcTicks,
                Sha256 = normalizedSha256
            };
            var documentChanged = false;
            lock (_syncRoot)
            {
                _document.FileHashes ??= new Dictionary<string, BlmImportIndexFileHashEntry>(StringComparer.OrdinalIgnoreCase);

                _fileHashCache[fullPath] = CloneFileHashEntry(nextEntry);
                if (!_document.FileHashes.TryGetValue(fullPath, out var currentDocumentEntry) ||
                    !AreFileHashEntriesEqual(currentDocumentEntry, nextEntry))
                {
                    _document.FileHashes[fullPath] = CloneFileHashEntry(nextEntry);
                    documentChanged = true;
                }

                if (PruneFileHashEntries(_document.FileHashes))
                {
                    documentChanged = true;
                    SyncFileHashCacheFromDocumentUnsafe();
                }
            }

            if (documentChanged)
            {
                MarkDirty();
            }

            sha256 = normalizedSha256;
            return true;
        }

        private static bool TryResolveAbsolutePathFromAssetPath(string assetPath, out string absolutePath)
        {
            absolutePath = string.Empty;
            var normalizedAssetPath = NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(normalizedAssetPath))
            {
                return false;
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            try
            {
                absolutePath = Path.GetFullPath(
                    Path.Combine(projectRoot, normalizedAssetPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(absolutePath);
        }

        private bool ContainsProductGuidUnsafe(string normalizedProductId, string normalizedGuid)
        {
            if (string.IsNullOrWhiteSpace(normalizedProductId) || string.IsNullOrWhiteSpace(normalizedGuid))
            {
                return false;
            }

            if (!_productGuidSetCache.TryGetValue(normalizedProductId, out var guidSet) || guidSet == null)
            {
                if (!_document.Products.TryGetValue(normalizedProductId, out var productEntry) ||
                    productEntry?.Guids == null ||
                    productEntry.Guids.Count == 0)
                {
                    return false;
                }

                guidSet = new HashSet<string>(
                    productEntry.Guids.Where(guid => !string.IsNullOrWhiteSpace(guid)),
                    StringComparer.Ordinal);
                _productGuidSetCache[normalizedProductId] = guidSet;
            }

            return guidSet.Contains(normalizedGuid);
        }

        private static string BuildIndexPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return BlmConstants.ImportIndexRelativePath;
            }

            return Path.Combine(projectRoot, BlmConstants.ImportIndexRelativePath);
        }
    }
}
