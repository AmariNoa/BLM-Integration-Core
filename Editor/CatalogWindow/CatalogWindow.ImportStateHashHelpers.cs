using System;
using System.IO;
using System.Threading;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private static ImportedStateRowHighlightKind DetermineUnityPackageImportedState(
            bool hasImportedGuids,
            bool hasMissingGuids)
        {
            if (hasImportedGuids)
            {
                return hasMissingGuids
                    ? ImportedStateRowHighlightKind.PartiallyImported
                    : ImportedStateRowHighlightKind.Imported;
            }

            return ImportedStateRowHighlightKind.None;
        }

        private static bool RequiresImportedStateBackgroundPreload(BlmFileRecord file)
        {
            return file != null &&
                   string.Equals(
                       NormalizeExtension(file.FileExtension),
                       ".unitypackage",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static ImportedStateCheckWorkItem PreloadImportedStateWorkItem(
            ImportedStateCheckWorkItem workItem,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return workItem;
            }

            if (RequiresImportedStateBackgroundPreload(workItem.File))
            {
                _ = BlmUnityPackageGuidCache.Shared.TryGetEntries(
                    workItem.File.FullPath,
                    cancellationToken,
                    out _);
            }

            return workItem;
        }

        private static bool TryIsUnityPackageAssetHashMatch(
            string destinationAbsolutePath,
            string expectedSourceSha256,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                string.IsNullOrWhiteSpace(destinationAbsolutePath) ||
                string.IsNullOrWhiteSpace(expectedSourceSha256))
            {
                return false;
            }

            return !cancellationToken.IsCancellationRequested &&
                   BlmImportIndexService.Shared.TryGetFileSha256(
                       destinationAbsolutePath,
                       cancellationToken,
                       out var destinationSha256) &&
                   !cancellationToken.IsCancellationRequested &&
                   string.Equals(destinationSha256, expectedSourceSha256, StringComparison.Ordinal);
        }

        private static bool TryAreFilesContentEqualWithCancellation(
            string leftFilePath,
            string rightFilePath,
            CancellationToken cancellationToken,
            out bool areEqual)
        {
            return BlmImportIndexService.Shared.TryAreFilesContentEqual(
                leftFilePath,
                rightFilePath,
                cancellationToken,
                out areEqual);
        }

        private static string BuildImportedStateProductFileKey(string productId, BlmFileRecord file)
        {
            if (file == null || string.IsNullOrWhiteSpace(productId))
            {
                return string.Empty;
            }

            var normalizedPath = NormalizeImportedStatePath(file.FullPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return string.Empty;
            }

            return $"{productId}|{normalizedPath}";
        }

        private static string NormalizeImportedStatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                fullPath = path;
            }

            return string.IsNullOrWhiteSpace(fullPath)
                ? string.Empty
                : fullPath.Replace('\\', '/').Trim();
        }
    }
}
