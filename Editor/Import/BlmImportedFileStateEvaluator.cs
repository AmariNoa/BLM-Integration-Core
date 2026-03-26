using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmImportedFileStateEvaluator
    {
        private readonly BlmImportIndexService _importIndexService;
        private readonly BlmUnityPackageGuidCache _unityPackageGuidCache;

        public BlmImportedFileStateEvaluator(
            BlmImportIndexService importIndexService,
            BlmUnityPackageGuidCache unityPackageGuidCache)
        {
            _importIndexService = importIndexService ?? throw new ArgumentNullException(nameof(importIndexService));
            _unityPackageGuidCache = unityPackageGuidCache ?? throw new ArgumentNullException(nameof(unityPackageGuidCache));
        }

        public bool HasAnyImportedAndUnchanged(BlmItemRecord item, IEnumerable<BlmFileRecord> files)
        {
            if (item == null || files == null)
            {
                return false;
            }

            foreach (var file in files)
            {
                if (IsImportedAndUnchanged(item, file))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsImportedAndUnchanged(BlmItemRecord item, BlmFileRecord file)
        {
            if (item == null || file == null || string.IsNullOrWhiteSpace(item.ProductId))
            {
                return false;
            }

            var normalizedExtension = NormalizeExtension(file.FileExtension);
            if (string.Equals(normalizedExtension, ".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                return IsUnityPackageImportedAndUnchanged(item, file);
            }

            return IsNonUnityImportedAndUnchanged(item, file);
        }

        private bool IsNonUnityImportedAndUnchanged(BlmItemRecord item, BlmFileRecord file)
        {
            if (!TryResolveNonUnityGuid(item, file, out var guid))
            {
                return false;
            }

            return _importIndexService.IsGuidImportedAndUnchangedForProduct(item.ProductId, guid);
        }

        private bool IsUnityPackageImportedAndUnchanged(BlmItemRecord item, BlmFileRecord file)
        {
            if (!_unityPackageGuidCache.TryGetGuids(file.FullPath, out var guids) || guids == null || guids.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < guids.Count; i++)
            {
                if (!_importIndexService.IsGuidImportedAndUnchangedForProduct(item.ProductId, guids[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryResolveNonUnityGuid(BlmItemRecord item, BlmFileRecord file, out string guid)
        {
            guid = string.Empty;
            if (item == null || file == null)
            {
                return false;
            }

            var destinationAssetPath = BlmNonUnityPackageImporter.BuildDestinationAssetPath(
                item.ShopName,
                item.ProductName,
                item.RootFolderPath,
                file.FullPath);
            if (!string.IsNullOrWhiteSpace(destinationAssetPath))
            {
                var directGuid = NormalizeGuid(AssetDatabase.AssetPathToGUID(destinationAssetPath));
                if (!string.IsNullOrWhiteSpace(directGuid))
                {
                    guid = directGuid;
                    return true;
                }
            }

            var sourceFileName = Path.GetFileName(file.FullPath ?? file.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                return false;
            }

            return _importIndexService.TryFindUniqueGuidByProductAndFileName(item.ProductId, sourceFileName, out guid);
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return string.Empty;
            }

            var normalized = extension.Trim();
            if (!normalized.StartsWith(".", StringComparison.Ordinal))
            {
                normalized = "." + normalized;
            }

            return normalized.ToLowerInvariant();
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

            return normalized.All(c =>
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f'))
                ? normalized
                : string.Empty;
        }
    }
}
