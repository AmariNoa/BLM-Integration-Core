using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace com.amari_noa.blm_integration_core.editor
{
    internal readonly struct BlmPreparedNonUnityImportedStateCheck
    {
        public string Guid { get; }
        public string SourceFullPath { get; }
        public long SourceFileSize { get; }
        public long SourceLastWriteTimeUtcTicks { get; }
        public string DestinationAssetPath { get; }
        public string DestinationFullPath { get; }
        public long DestinationFileSize { get; }
        public long DestinationLastWriteTimeUtcTicks { get; }

        public BlmPreparedNonUnityImportedStateCheck(
            string guid,
            string sourceFullPath,
            long sourceFileSize,
            long sourceLastWriteTimeUtcTicks,
            string destinationAssetPath,
            string destinationFullPath,
            long destinationFileSize,
            long destinationLastWriteTimeUtcTicks)
        {
            Guid = guid ?? string.Empty;
            SourceFullPath = sourceFullPath ?? string.Empty;
            SourceFileSize = sourceFileSize;
            SourceLastWriteTimeUtcTicks = sourceLastWriteTimeUtcTicks;
            DestinationAssetPath = destinationAssetPath ?? string.Empty;
            DestinationFullPath = destinationFullPath ?? string.Empty;
            DestinationFileSize = destinationFileSize;
            DestinationLastWriteTimeUtcTicks = destinationLastWriteTimeUtcTicks;
        }
    }

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

            if (IsUnityPackageFile(file))
            {
                return IsUnityPackageImportedAndUnchanged(item, file);
            }

            return IsNonUnityImportedAndUnchanged(item, file);
        }

        public static bool IsUnityPackageFile(BlmFileRecord file)
        {
            if (file == null)
            {
                return false;
            }

            var normalizedExtension = NormalizeExtension(file.FileExtension);
            return string.Equals(normalizedExtension, ".unitypackage", StringComparison.OrdinalIgnoreCase);
        }

        public bool TryPrepareNonUnityImportedStateCheck(
            BlmItemRecord item,
            BlmFileRecord file,
            out BlmPreparedNonUnityImportedStateCheck preparedCheck)
        {
            preparedCheck = default;
            if (item == null || file == null || string.IsNullOrWhiteSpace(item.ProductId) || IsUnityPackageFile(file))
            {
                return false;
            }

            if (!TryResolveNonUnityGuid(item, file, out var guid) ||
                string.IsNullOrWhiteSpace(guid) ||
                !_importIndexService.TryGetImportedGuidAssetPathForProduct(item.ProductId, guid, out var destinationAssetPath) ||
                string.IsNullOrWhiteSpace(destinationAssetPath))
            {
                return false;
            }

            var sourcePath = string.IsNullOrWhiteSpace(file.FullPath)
                ? (file.FileName ?? string.Empty)
                : file.FullPath;
            if (!TryResolveFullPath(sourcePath, out var sourceFullPath) ||
                !_importIndexService.TryResolveAssetAbsolutePath(destinationAssetPath, out var destinationFullPath) ||
                !TryGetFileInfoSnapshot(sourceFullPath, out var sourceFileSize, out var sourceLastWriteTimeUtcTicks) ||
                !TryGetFileInfoSnapshot(destinationFullPath, out var destinationFileSize, out var destinationLastWriteTimeUtcTicks))
            {
                return false;
            }

            preparedCheck = new BlmPreparedNonUnityImportedStateCheck(
                guid,
                sourceFullPath,
                sourceFileSize,
                sourceLastWriteTimeUtcTicks,
                destinationAssetPath,
                destinationFullPath,
                destinationFileSize,
                destinationLastWriteTimeUtcTicks);
            return true;
        }

        private bool IsNonUnityImportedAndUnchanged(BlmItemRecord item, BlmFileRecord file)
        {
            if (!TryPrepareNonUnityImportedStateCheck(item, file, out var preparedCheck))
            {
                return false;
            }

            return _importIndexService.TryAreFilesContentEqual(
                preparedCheck.SourceFullPath,
                preparedCheck.DestinationFullPath,
                out var areEqual) &&
                   areEqual;
        }

        private bool IsUnityPackageImportedAndUnchanged(BlmItemRecord item, BlmFileRecord file)
        {
            if (!_unityPackageGuidCache.TryGetEntries(file.FullPath, out var entries) || entries == null || entries.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry.Guid) ||
                    !_importIndexService.TryGetImportedGuidAssetPathForProduct(item.ProductId, entry.Guid, out var destinationAssetPath))
                {
                    return false;
                }

                if (!entry.HasAssetData)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.AssetSha256) ||
                    !_importIndexService.TryResolveAssetAbsolutePath(destinationAssetPath, out var destinationAbsolutePath) ||
                    !_importIndexService.TryGetFileSha256(destinationAbsolutePath, out var destinationSha256) ||
                    !string.Equals(destinationSha256, entry.AssetSha256, StringComparison.Ordinal))
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

            var sourceFilePath = string.IsNullOrWhiteSpace(file.FullPath)
                ? (file.FileName ?? string.Empty)
                : file.FullPath;
            var sourceFileName = Path.GetFileName(sourceFilePath);
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                return false;
            }

            return _importIndexService.TryFindUniqueGuidByProductAndFileName(item.ProductId, sourceFilePath, out guid);
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

        private static bool TryResolveFullPath(string path, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                fullPath = path;
            }

            return !string.IsNullOrWhiteSpace(fullPath);
        }

        private static bool TryGetFileInfoSnapshot(string fullPath, out long fileSize, out long lastWriteTimeUtcTicks)
        {
            fileSize = 0L;
            lastWriteTimeUtcTicks = 0L;
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
                {
                    return false;
                }

                fileSize = info.Length;
                lastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
