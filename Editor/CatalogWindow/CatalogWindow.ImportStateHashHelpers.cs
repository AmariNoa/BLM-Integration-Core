using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
                   TryGetFileSha256WithCancellation(destinationAbsolutePath, cancellationToken, out var destinationSha256) &&
                   !cancellationToken.IsCancellationRequested &&
                   string.Equals(destinationSha256, expectedSourceSha256, StringComparison.Ordinal);
        }

        private static bool TryAreFilesContentEqualWithCancellation(
            string leftFilePath,
            string rightFilePath,
            CancellationToken cancellationToken,
            out bool areEqual)
        {
            areEqual = false;
            if (!TryGetFileSha256WithCancellation(leftFilePath, cancellationToken, out var leftSha256) ||
                !TryGetFileSha256WithCancellation(rightFilePath, cancellationToken, out var rightSha256))
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            areEqual = string.Equals(leftSha256, rightSha256, StringComparison.Ordinal);
            return true;
        }

        private static bool TryGetFileSha256WithCancellation(
            string filePath,
            CancellationToken cancellationToken,
            out string sha256)
        {
            sha256 = string.Empty;
            if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch
            {
                fullPath = filePath;
            }

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
            }
            catch
            {
                return false;
            }

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

                sha256 = builder.ToString();
                return !string.IsNullOrWhiteSpace(sha256);
            }
            catch
            {
                return false;
            }
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
