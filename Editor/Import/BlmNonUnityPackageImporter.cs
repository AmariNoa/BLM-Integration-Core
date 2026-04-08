using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmNonUnityPackageImporter
    {
        private DuplicateMode _duplicateMode = DuplicateMode.Undecided;

        public NonUnityImportResult Import(
            BlmImportRequestItem item,
            BlmPickerContext context,
            ISet<string> batchDestinationPaths,
            bool deferAssetImport = false)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.SourcePath))
            {
                return NonUnityImportResult.Failed("SourcePath is empty.");
            }

            if (!File.Exists(item.SourcePath))
            {
                return NonUnityImportResult.Failed($"Source file not found: {item.SourcePath}");
            }

            var destinationAssetPath = BuildDestinationAssetPath(item);
            var destinationAbsolutePath = ToAbsolutePath(destinationAssetPath);
            var isExistingDuplicate = File.Exists(destinationAbsolutePath);
            var isBatchDuplicate = !batchDestinationPaths.Add(destinationAssetPath);
            var isDuplicate = isExistingDuplicate || isBatchDuplicate;
            var destinationWasPreExisting = isExistingDuplicate;

            var shouldOverwrite = false;
            if (isDuplicate)
            {
                var action = DecideDuplicateAction(context, destinationAssetPath);
                if (action == DuplicateAction.Skip)
                {
                    return NonUnityImportResult.Skipped(destinationAssetPath, destinationWasPreExisting);
                }

                shouldOverwrite = true;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationAbsolutePath) ?? string.Empty);
                File.Copy(item.SourcePath, destinationAbsolutePath, shouldOverwrite);
                if (!deferAssetImport)
                {
                    AssetDatabase.ImportAsset(destinationAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }

                return NonUnityImportResult.Completed(destinationAssetPath, destinationWasPreExisting);
            }
            catch (Exception ex)
            {
                return NonUnityImportResult.Failed(ex.Message);
            }
        }

        private DuplicateAction DecideDuplicateAction(BlmPickerContext context, string destinationAssetPath)
        {
            if (_duplicateMode == DuplicateMode.SkipAll)
            {
                return DuplicateAction.Skip;
            }

            if (_duplicateMode == DuplicateMode.OverwriteAll)
            {
                return DuplicateAction.Overwrite;
            }

            if (_duplicateMode == DuplicateMode.Undecided)
            {
                var continueImport = EditorUtility.DisplayDialog(
                    L(context, "blm.import.duplicate_detected.title", "Duplicate Files Detected"),
                    L(context, "blm.import.duplicate_detected.message", "Duplicate files were found. Continue importing?"),
                    L(context, "blm.common.yes", "Yes"),
                    L(context, "blm.common.no", "No"));

                if (!continueImport)
                {
                    _duplicateMode = DuplicateMode.SkipAll;
                    return DuplicateAction.Skip;
                }

                var overwriteAll = EditorUtility.DisplayDialogComplex(
                    L(context, "blm.import.overwrite_options.title", "Overwrite Options"),
                    L(context, "blm.import.overwrite_options.message", "Overwrite all duplicate files?"),
                    L(context, "blm.common.yes", "Yes"),
                    L(context, "blm.import.choose_per_file", "Choose Per File"),
                    string.Empty);

                _duplicateMode = overwriteAll == 0 ? DuplicateMode.OverwriteAll : DuplicateMode.AskPerFile;
            }

            if (_duplicateMode == DuplicateMode.AskPerFile)
            {
                var overwrite = EditorUtility.DisplayDialog(
                    L(context, "blm.import.file_conflict.title", "File Conflict"),
                    $"{L(context, "blm.import.file_conflict.message", "Overwrite this file?")}\n{destinationAssetPath}",
                    L(context, "blm.import.overwrite", "Overwrite"),
                    L(context, "blm.common.skip", "Skip"));
                return overwrite ? DuplicateAction.Overwrite : DuplicateAction.Skip;
            }

            return DuplicateAction.Overwrite;
        }

        internal static string BuildDestinationAssetPath(
            string shopName,
            string productName,
            string rootFolderPath,
            string sourcePath)
        {
            var shop = SanitizePathSegment(shopName);
            var product = SanitizePathSegment(productName);
            var relative = BuildRelativePath(rootFolderPath, sourcePath);
            var sanitizedRelative = string.Join(
                "/",
                relative
                    .Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(SanitizePathSegment));

            var destination = $"Assets/BLM/{shop}/{product}";
            if (!string.IsNullOrWhiteSpace(sanitizedRelative))
            {
                destination = $"{destination}/{sanitizedRelative}";
            }

            return NormalizeAssetPath(destination);
        }

        private static string BuildDestinationAssetPath(BlmImportRequestItem item)
        {
            return BuildDestinationAssetPath(
                item?.ShopName,
                item?.ProductName,
                item?.RootFolderPath,
                item?.SourcePath);
        }

        private static string BuildRelativePath(string rootFolderPath, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(rootFolderPath))
            {
                return Path.GetFileName(sourcePath) ?? sourcePath;
            }

            try
            {
                var relative = Path.GetRelativePath(rootFolderPath, sourcePath);
                if (!string.IsNullOrWhiteSpace(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return CollapseRedundantTopLevelDirectory(rootFolderPath, relative);
                }
            }
            catch
            {
                // Use fallback below.
            }

            return Path.GetFileName(sourcePath) ?? sourcePath;
        }

        private static string CollapseRedundantTopLevelDirectory(string rootFolderPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(rootFolderPath) || string.IsNullOrWhiteSpace(relativePath))
            {
                return relativePath;
            }

            var segments = relativePath
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3)
            {
                return relativePath;
            }

            var topLevel = segments[0];
            var secondLevel = segments[1];
            if (!string.Equals(topLevel, secondLevel, StringComparison.OrdinalIgnoreCase))
            {
                return relativePath;
            }

            var topLevelDirectoryPath = Path.Combine(rootFolderPath, topLevel);
            if (!Directory.Exists(topLevelDirectoryPath))
            {
                return relativePath;
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(topLevelDirectoryPath);
            }
            catch
            {
                return relativePath;
            }

            if (childDirectories.Length != 1)
            {
                return relativePath;
            }

            var onlyChildDirectoryName = Path.GetFileName(
                childDirectories[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(onlyChildDirectoryName, topLevel, StringComparison.OrdinalIgnoreCase))
            {
                return relativePath;
            }

            var collapsedSegments = new string[segments.Length - 1];
            collapsedSegments[0] = topLevel;
            Array.Copy(segments, 2, collapsedSegments, 1, segments.Length - 2);
            return string.Join("/", collapsedSegments);
        }

        private static string ToAbsolutePath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var relative = assetPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(projectRoot, relative);
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            return assetPath.Replace('\\', '/');
        }

        private static string SanitizePathSegment(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Unknown";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = raw.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 ||
                    chars[i] == '\\' ||
                    chars[i] == '/' ||
                    chars[i] == ':' ||
                    chars[i] == '*' ||
                    chars[i] == '?' ||
                    chars[i] == '"' ||
                    chars[i] == '<' ||
                    chars[i] == '>' ||
                    chars[i] == '|')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static string L(BlmPickerContext context, string key, string fallback)
        {
            if (context?.EditorLocalizationService == null)
            {
                return fallback;
            }

            var sourceId = string.IsNullOrWhiteSpace(context.LocalizationSourceId)
                ? BlmConstants.LocalizationSourceId
                : context.LocalizationSourceId;
            return context.EditorLocalizationService.Get(sourceId, key, fallback);
        }

        private enum DuplicateMode
        {
            Undecided,
            OverwriteAll,
            AskPerFile,
            SkipAll
        }

        private enum DuplicateAction
        {
            Overwrite,
            Skip
        }

        internal readonly struct NonUnityImportResult
        {
            public bool IsSuccess { get; }
            public bool IsSkipped { get; }
            public string DestinationAssetPath { get; }
            public bool DestinationWasPreExisting { get; }
            public string ErrorMessage { get; }

            private NonUnityImportResult(
                bool isSuccess,
                bool isSkipped,
                string destinationAssetPath,
                bool destinationWasPreExisting,
                string errorMessage)
            {
                IsSuccess = isSuccess;
                IsSkipped = isSkipped;
                DestinationAssetPath = destinationAssetPath ?? string.Empty;
                DestinationWasPreExisting = destinationWasPreExisting;
                ErrorMessage = errorMessage ?? string.Empty;
            }

            public static NonUnityImportResult Completed(string destinationAssetPath, bool destinationWasPreExisting)
            {
                return new NonUnityImportResult(true, false, destinationAssetPath, destinationWasPreExisting, string.Empty);
            }

            public static NonUnityImportResult Skipped(string destinationAssetPath, bool destinationWasPreExisting)
            {
                return new NonUnityImportResult(true, true, destinationAssetPath, destinationWasPreExisting, string.Empty);
            }

            public static NonUnityImportResult Failed(string errorMessage)
            {
                return new NonUnityImportResult(false, false, string.Empty, false, errorMessage);
            }
        }
    }
}
