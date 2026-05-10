using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private readonly struct BatchFileCandidate
        {
            public BatchFileCandidate(BlmFileRecord file, int extensionPriority)
            {
                File = file;
                ExtensionPriority = extensionPriority;
            }

            public BlmFileRecord File { get; }

            public int ExtensionPriority { get; }
        }

        private static class BatchFileCandidateComparer
        {
            public static int Compare(BatchFileCandidate a, BatchFileCandidate b)
            {
                var priorityCompare = a.ExtensionPriority.CompareTo(b.ExtensionPriority);
                if (priorityCompare != 0)
                {
                    return priorityCompare;
                }

                var fileNameCompare = string.Compare(a.File?.FileName, b.File?.FileName, StringComparison.OrdinalIgnoreCase);
                if (fileNameCompare != 0)
                {
                    return fileNameCompare;
                }

                return string.Compare(a.File?.FullPath, b.File?.FullPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string BuildNormalizedRelativePath(string rootFolderPath, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(rootFolderPath))
            {
                return Path.GetFileName(sourcePath) ?? sourcePath;
            }

            try
            {
                var relative = Path.GetRelativePath(rootFolderPath, sourcePath);
                if (!string.IsNullOrWhiteSpace(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return CollapseRedundantTopLevelDirectory(rootFolderPath, relative)
                        .Replace('\\', '/');
                }
            }
            catch
            {
                // Use fallback below.
            }

            return Path.GetFileName(sourcePath) ?? sourcePath;
        }

        private static HashSet<string> BuildDuplicateFileNameSet(IEnumerable<BlmFileRecord> files)
        {
            var duplicateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (files == null)
            {
                return duplicateFileNames;
            }

            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var fileName = file?.FileName;
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                nameCounts.TryGetValue(fileName, out var count);
                nameCounts[fileName] = count + 1;
            }

            foreach (var pair in nameCounts)
            {
                if (pair.Value > 1)
                {
                    duplicateFileNames.Add(pair.Key);
                }
            }

            return duplicateFileNames;
        }

        private static string BuildFileDisplayName(BlmItemRecord item, BlmFileRecord file, HashSet<string> duplicateFileNames)
        {
            var fileName = file?.FileName ?? string.Empty;
            if (file == null || string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }

            if (duplicateFileNames == null || !duplicateFileNames.Contains(fileName))
            {
                return fileName;
            }

            var relativePath = BuildNormalizedRelativePath(item?.RootFolderPath, file.FullPath);
            return string.IsNullOrWhiteSpace(relativePath) ? fileName : relativePath;
        }

        private static string BuildFileSortText(BlmItemRecord item, BlmFileRecord file, HashSet<string> duplicateFileNames)
        {
            var displayText = BuildFileDisplayName(item, file, duplicateFileNames);
            return string.IsNullOrWhiteSpace(displayText)
                ? (file?.FileName ?? string.Empty)
                : displayText;
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

        private static List<DetailFileListEntry> BuildDetailFileListEntriesSnapshot(
            string productId,
            string rootFolderPath,
            IReadOnlyList<BlmFileRecord> files,
            IReadOnlyList<string> preferredDisplayExtensions,
            IReadOnlyCollection<string> collapsedDetailFolderKeys,
            string otherFilesLabel,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new List<DetailFileListEntry>();
            if (files == null || files.Count == 0)
            {
                return entries;
            }

            var filesByExtension = files
                .Where(file => file != null)
                .GroupBy(file => NormalizeExtension(file.FileExtension), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var preferredExtensions = (preferredDisplayExtensions ?? Array.Empty<string>())
                .Select(NormalizeExtension)
                .Where(ext => !string.IsNullOrWhiteSpace(ext))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var collapsedFolderKeys = collapsedDetailFolderKeys == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(collapsedDetailFolderKeys, StringComparer.OrdinalIgnoreCase);

            foreach (var preferredExtension in preferredExtensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!filesByExtension.TryGetValue(preferredExtension, out var sectionFiles) || sectionFiles.Count == 0)
                {
                    continue;
                }

                entries.Add(DetailFileListEntry.CreateSection(
                    BuildDetailSectionHeaderTextSnapshot(preferredExtension, otherFilesLabel),
                    preferredExtension));
                AppendDetailSectionTreeEntriesSnapshot(
                    entries,
                    productId,
                    rootFolderPath,
                    preferredExtension,
                    sectionFiles,
                    collapsedFolderKeys,
                    cancellationToken);

                filesByExtension.Remove(preferredExtension);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var otherFiles = filesByExtension.Values
                .SelectMany(sectionFiles => sectionFiles ?? Enumerable.Empty<BlmFileRecord>())
                .Where(file => file != null)
                .ToList();
            if (otherFiles.Count > 0)
            {
                entries.Add(DetailFileListEntry.CreateSection(
                    BuildDetailSectionHeaderTextSnapshot(string.Empty, otherFilesLabel)));
                AppendDetailSectionTreeEntriesSnapshot(
                    entries,
                    productId,
                    rootFolderPath,
                    string.Empty,
                    otherFiles,
                    collapsedFolderKeys,
                    cancellationToken);
            }

            return entries;
        }

        private static void AppendDetailSectionTreeEntriesSnapshot(
            List<DetailFileListEntry> entries,
            string productId,
            string rootFolderPath,
            string sectionExtension,
            IReadOnlyList<BlmFileRecord> sectionFiles,
            HashSet<string> collapsedFolderKeys,
            CancellationToken cancellationToken)
        {
            if (entries == null || sectionFiles == null || sectionFiles.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var root = BuildDetailFolderTree(rootFolderPath, sectionFiles, cancellationToken);
            PopulateDetailFolderDescendants(root);
            var sectionKey = NormalizeDetailSectionKey(sectionExtension);
            AppendDetailFolderEntriesSnapshot(entries, productId, rootFolderPath, root, sectionKey, depth: 0, collapsedFolderKeys, cancellationToken);
            AppendDetailFileEntriesSnapshot(entries, rootFolderPath, root.DirectFiles, depth: 0, cancellationToken);
        }

        private static void AppendDetailFolderEntriesSnapshot(
            List<DetailFileListEntry> entries,
            string productId,
            string rootFolderPath,
            DetailFolderTreeNode parent,
            string sectionKey,
            int depth,
            HashSet<string> collapsedFolderKeys,
            CancellationToken cancellationToken)
        {
            if (entries == null || parent == null || parent.Children == null || parent.Children.Count == 0)
            {
                return;
            }

            var orderedFolders = parent.Children.Values
                .OrderBy(node => node.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            foreach (var folder in orderedFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderKey = BuildDetailFolderKey(productId, sectionKey, folder.RelativePath);
                entries.Add(DetailFileListEntry.CreateFolder(
                    displayText: folder.Name,
                    folderKey: folderKey,
                    folderFiles: folder.DescendantFiles,
                    depth: depth));
                if (!IsDetailFolderExpandedSnapshot(folderKey, collapsedFolderKeys))
                {
                    continue;
                }

                AppendDetailFolderEntriesSnapshot(
                    entries,
                    productId,
                    rootFolderPath,
                    folder,
                    sectionKey,
                    depth + 1,
                    collapsedFolderKeys,
                    cancellationToken);
                AppendDetailFileEntriesSnapshot(entries, rootFolderPath, folder.DirectFiles, depth + 1, cancellationToken);
            }
        }

        private static void AppendDetailFileEntriesSnapshot(
            List<DetailFileListEntry> entries,
            string rootFolderPath,
            IEnumerable<BlmFileRecord> files,
            int depth,
            CancellationToken cancellationToken)
        {
            if (entries == null || files == null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var orderedFiles = files
                .Where(file => file != null)
                .Select(file =>
                {
                    var sortText = BuildTreeFileName(rootFolderPath, file);
                    return new
                    {
                        File = file,
                        SortText = sortText,
                        DisplayText = sortText
                    };
                })
                .OrderBy(entry => entry.SortText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SortText, StringComparer.Ordinal)
                .ThenBy(entry => entry.File.FullPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var fileEntry in orderedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(DetailFileListEntry.CreateFile(fileEntry.File, fileEntry.DisplayText, depth));
            }
        }

        private static bool IsDetailFolderExpandedSnapshot(string folderKey, HashSet<string> collapsedFolderKeys)
        {
            return string.IsNullOrWhiteSpace(folderKey) ||
                   collapsedFolderKeys == null ||
                   !collapsedFolderKeys.Contains(folderKey);
        }

        private static string BuildDetailSectionHeaderTextSnapshot(string normalizedExtension, string otherFilesLabel)
        {
            if (string.IsNullOrWhiteSpace(normalizedExtension))
            {
                var label = string.IsNullOrWhiteSpace(otherFilesLabel) ? "other files" : otherFilesLabel;
                return $"--- {label} ---";
            }

            return $"--- {normalizedExtension.TrimStart('.')} ---";
        }

        private static DetailFolderTreeNode BuildDetailFolderTree(BlmItemRecord item, IReadOnlyList<BlmFileRecord> files)
        {
            return BuildDetailFolderTree(item?.RootFolderPath, files);
        }

        private static DetailFolderTreeNode BuildDetailFolderTree(
            string rootFolderPath,
            IReadOnlyList<BlmFileRecord> files,
            CancellationToken cancellationToken = default)
        {
            var root = new DetailFolderTreeNode(string.Empty, string.Empty);
            if (files == null || files.Count == 0)
            {
                return root;
            }

            foreach (var file in files.Where(file => file != null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = BuildNormalizedRelativePath(rootFolderPath, file.FullPath);
                var normalizedRelativePath = string.IsNullOrWhiteSpace(relativePath)
                    ? (file.FileName ?? string.Empty)
                    : relativePath.Replace('\\', '/');
                var segments = normalizedRelativePath
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length <= 1)
                {
                    root.DirectFiles.Add(file);
                    continue;
                }

                var node = root;
                var pathBuilder = new StringBuilder();
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    var segment = segments[i];
                    if (string.IsNullOrWhiteSpace(segment))
                    {
                        continue;
                    }

                    if (pathBuilder.Length > 0)
                    {
                        pathBuilder.Append('/');
                    }

                    pathBuilder.Append(segment);
                    if (!node.Children.TryGetValue(segment, out var child))
                    {
                        child = new DetailFolderTreeNode(segment, pathBuilder.ToString());
                        node.Children[segment] = child;
                    }

                    node = child;
                }

                node.DirectFiles.Add(file);
            }

            return root;
        }

        private static List<BlmFileRecord> PopulateDetailFolderDescendants(DetailFolderTreeNode node)
        {
            var descendants = new List<BlmFileRecord>();
            if (node == null)
            {
                return descendants;
            }

            descendants.AddRange(node.DirectFiles.Where(file => file != null));
            foreach (var child in node.Children.Values)
            {
                descendants.AddRange(PopulateDetailFolderDescendants(child));
            }

            node.DescendantFiles = descendants;
            return descendants;
        }

        private static string NormalizeDetailSectionKey(string sectionExtension)
        {
            return string.IsNullOrWhiteSpace(sectionExtension)
                ? "__other__"
                : NormalizeExtension(sectionExtension);
        }

        private static string BuildDetailFolderKey(string productId, string sectionKey, string folderRelativePath)
        {
            var normalizedFolderPath = (folderRelativePath ?? string.Empty).Replace('\\', '/');
            return $"{productId ?? string.Empty}|{sectionKey ?? string.Empty}|{normalizedFolderPath}";
        }

        private static string BuildTreeFileName(BlmItemRecord item, BlmFileRecord file)
        {
            return BuildTreeFileName(item?.RootFolderPath, file);
        }

        private static string BuildTreeFileName(string rootFolderPath, BlmFileRecord file)
        {
            if (file == null)
            {
                return string.Empty;
            }

            var relativePath = BuildNormalizedRelativePath(rootFolderPath, file.FullPath);
            var normalized = string.IsNullOrWhiteSpace(relativePath)
                ? (file.FileName ?? string.Empty)
                : relativePath.Replace('\\', '/');
            var lastSlashIndex = normalized.LastIndexOf('/');
            if (lastSlashIndex >= 0 && lastSlashIndex < normalized.Length - 1)
            {
                return normalized[(lastSlashIndex + 1)..];
            }

            return string.IsNullOrWhiteSpace(normalized)
                ? (file.FileName ?? string.Empty)
                : normalized;
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return string.Empty;
            }

            var normalized = extension.Trim().ToLowerInvariant();
            if (!normalized.StartsWith(".", StringComparison.Ordinal))
            {
                normalized = "." + normalized;
            }

            return normalized;
        }

        private static int CompareDate(DateTime? a, DateTime? b, bool asc)
        {
            if (!a.HasValue && !b.HasValue) return 0;
            if (!a.HasValue) return 1;
            if (!b.HasValue) return -1;
            var cmp = DateTime.Compare(a.Value, b.Value);
            return asc ? cmp : -cmp;
        }
    }
}
