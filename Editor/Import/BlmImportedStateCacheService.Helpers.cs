using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed partial class BlmImportedStateCacheService
    {
        private static bool TryNormalizeEntry(BlmImportedStateCacheEntry source, out BlmImportedStateCacheEntry normalized)
        {
            normalized = null;
            if (source == null)
            {
                return false;
            }

            var productId = NormalizeProductId(source.ProductId);
            var normalizedSourcePath = NormalizeSourcePath(source.NormalizedSourcePath);
            var importIndexFingerprint = NormalizeImportIndexFingerprint(source.ImportIndexFingerprint);
            var destinationAssetGuid = NormalizeGuid(source.DestinationAssetGuid);
            var destinationFileSize = Math.Max(0L, source.DestinationFileSize);
            var destinationLastWriteTimeUtcTicks = Math.Max(0L, source.DestinationLastWriteTimeUtcTicks);
            if (string.IsNullOrWhiteSpace(productId) ||
                string.IsNullOrWhiteSpace(normalizedSourcePath) ||
                string.IsNullOrWhiteSpace(importIndexFingerprint) ||
                source.SourceFileSize < 0 ||
                source.SourceLastWriteTimeUtcTicks < 0)
            {
                return false;
            }

            normalized = new BlmImportedStateCacheEntry
            {
                ProductId = productId,
                NormalizedSourcePath = normalizedSourcePath,
                SourceFileSize = source.SourceFileSize,
                SourceLastWriteTimeUtcTicks = source.SourceLastWriteTimeUtcTicks,
                ImportIndexFingerprint = importIndexFingerprint,
                DestinationAssetGuid = destinationAssetGuid,
                DestinationFileSize = destinationFileSize,
                DestinationLastWriteTimeUtcTicks = destinationLastWriteTimeUtcTicks,
                IsImportedUnchanged = source.IsImportedUnchanged
            };
            return true;
        }

        private static string BuildLookupKey(BlmImportedStateCacheEntry entry)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}",
                entry.ProductId ?? string.Empty,
                entry.NormalizedSourcePath ?? string.Empty,
                entry.SourceFileSize,
                entry.SourceLastWriteTimeUtcTicks,
                entry.ImportIndexFingerprint ?? string.Empty,
                entry.DestinationAssetGuid ?? string.Empty,
                entry.DestinationFileSize,
                entry.DestinationLastWriteTimeUtcTicks);
        }

        private static string NormalizeProductId(string productId)
        {
            return string.IsNullOrWhiteSpace(productId)
                ? string.Empty
                : productId.Trim();
        }

        private static string NormalizeSourcePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return string.Empty;
            }

            return sourcePath.Replace('\\', '/').Trim();
        }

        private static string NormalizeImportIndexFingerprint(string fingerprint)
        {
            return string.IsNullOrWhiteSpace(fingerprint)
                ? string.Empty
                : fingerprint.Trim();
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

        private static string BuildCachePath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return BlmConstants.ImportedStateCacheRelativePath;
            }

            return Path.Combine(projectRoot, BlmConstants.ImportedStateCacheRelativePath);
        }
    }
}
