using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using com.amari_noa.unitypackage_pipeline_core.editor;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private enum ImportedStateUnityPackageRecordKind
        {
            Asset = 0,
            Meta = 1
        }

        private readonly struct ImportedStateUnityPackageCheckRecord
        {
            public string Guid { get; }
            public ImportedStateUnityPackageRecordKind Kind { get; }
            public string AssetSha256 { get; }
            public string MetaSha256 { get; }
            public string MetaGuid { get; }

            public ImportedStateUnityPackageCheckRecord(
                string guid,
                ImportedStateUnityPackageRecordKind kind,
                string assetSha256,
                string metaSha256,
                string metaGuid)
            {
                Guid = guid ?? string.Empty;
                Kind = kind;
                AssetSha256 = assetSha256 ?? string.Empty;
                MetaSha256 = metaSha256 ?? string.Empty;
                MetaGuid = metaGuid ?? string.Empty;
            }
        }

        private enum SelectedItemsFilterMode
        {
            SelectedAndPreferred = 0,
            SelectedOnly = 1,
            All = 2
        }

        private enum FilterCandidateSortMode
        {
            NameAscending = 0,
            CountDescending = 1
        }

        private enum DetailSectionSelectionState
        {
            Off = 0,
            Mixed = 1,
            On = 2
        }

        private enum ImportedStateRowHighlightKind
        {
            None = 0,
            Imported = 1,
            PartiallyImported = 2
        }

        private sealed class DetailFolderTreeNode
        {
            public string Name { get; }
            public string RelativePath { get; }
            public Dictionary<string, DetailFolderTreeNode> Children { get; } =
                new Dictionary<string, DetailFolderTreeNode>(StringComparer.OrdinalIgnoreCase);
            public List<BlmFileRecord> DirectFiles { get; } = new List<BlmFileRecord>();
            public IReadOnlyList<BlmFileRecord> DescendantFiles { get; set; } = Array.Empty<BlmFileRecord>();

            public DetailFolderTreeNode(string name, string relativePath)
            {
                Name = name ?? string.Empty;
                RelativePath = relativePath ?? string.Empty;
            }
        }

        private sealed class ImportedStateUnityPackageCheckState
        {
            public ImportedStateCheckWorkItem WorkItem { get; }
            public IReadOnlyList<ImportedStateUnityPackageCheckRecord> Records { get; }
            public int NextRecordIndex { get; set; }
            public bool HasImportedEntries { get; set; }
            public bool HasMissingEntries { get; set; }
            public Task<bool> ActiveAssetHashComparisonTask { get; set; }
            public BlmImportedStateCacheEntry PendingCacheEntry { get; set; }

            public ImportedStateUnityPackageCheckState(
                ImportedStateCheckWorkItem workItem,
                IReadOnlyList<ImportedStateUnityPackageCheckRecord> records)
            {
                WorkItem = workItem;
                Records = records ?? Array.Empty<ImportedStateUnityPackageCheckRecord>();
                NextRecordIndex = 0;
            }
        }

        private sealed class ImportedStateNonUnityContentCheckState
        {
            public ImportedStateCheckWorkItem WorkItem { get; }
            public BlmImportedStateCacheEntry CacheEntry { get; }
            public Task<bool> HashComparisonTask { get; }

            public ImportedStateNonUnityContentCheckState(
                ImportedStateCheckWorkItem workItem,
                BlmImportedStateCacheEntry cacheEntry,
                Task<bool> hashComparisonTask)
            {
                WorkItem = workItem;
                CacheEntry = cacheEntry;
                HashComparisonTask = hashComparisonTask;
            }
        }

        private readonly struct ImportedStateCheckWorkItem
        {
            public int EvaluationVersion { get; }
            public BlmItemRecord Item { get; }
            public BlmFileRecord File { get; }
            public string ImportIndexFingerprint { get; }

            public ImportedStateCheckWorkItem(
                int evaluationVersion,
                BlmItemRecord item,
                BlmFileRecord file,
                string importIndexFingerprint)
            {
                EvaluationVersion = evaluationVersion;
                Item = item;
                File = file;
                ImportIndexFingerprint = importIndexFingerprint ?? string.Empty;
            }
        }

        private readonly struct DetailFileLoadResult
        {
            public string ProductId { get; }
            public string RootFolderPath { get; }
            public IReadOnlyList<BlmFileRecord> Files { get; }
            public HashSet<string> DuplicateFileNames { get; }
            public List<DetailFileListEntry> DetailEntries { get; }

            public DetailFileLoadResult(
                string productId,
                string rootFolderPath,
                IReadOnlyList<BlmFileRecord> files,
                HashSet<string> duplicateFileNames,
                List<DetailFileListEntry> detailEntries)
            {
                ProductId = productId ?? string.Empty;
                RootFolderPath = rootFolderPath ?? string.Empty;
                Files = files ?? Array.Empty<BlmFileRecord>();
                DuplicateFileNames = duplicateFileNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DetailEntries = detailEntries ?? new List<DetailFileListEntry>();
            }
        }

        private readonly struct DetailFileListEntry
        {
            public bool IsSectionHeader { get; }
            public bool IsFolder { get; }
            public string DisplayText { get; }
            public string SectionExtension { get; }
            public bool CanToggleSectionSelection => IsSectionHeader && !string.IsNullOrWhiteSpace(SectionExtension);
            public BlmFileRecord File { get; }
            public IReadOnlyList<BlmFileRecord> FolderFiles { get; }
            public string FolderKey { get; }
            public int Depth { get; }

            private DetailFileListEntry(
                bool isSectionHeader,
                bool isFolder,
                string displayText,
                string sectionExtension,
                BlmFileRecord file,
                IReadOnlyList<BlmFileRecord> folderFiles,
                string folderKey,
                int depth)
            {
                IsSectionHeader = isSectionHeader;
                IsFolder = isFolder;
                DisplayText = displayText ?? string.Empty;
                SectionExtension = sectionExtension ?? string.Empty;
                File = file;
                FolderFiles = folderFiles ?? Array.Empty<BlmFileRecord>();
                FolderKey = folderKey ?? string.Empty;
                Depth = Math.Max(0, depth);
            }

            public static DetailFileListEntry CreateSection(string displayText, string sectionExtension = "")
            {
                return new DetailFileListEntry(
                    isSectionHeader: true,
                    isFolder: false,
                    displayText: displayText ?? string.Empty,
                    sectionExtension: sectionExtension,
                    file: null,
                    folderFiles: Array.Empty<BlmFileRecord>(),
                    folderKey: string.Empty,
                    depth: 0);
            }

            public static DetailFileListEntry CreateFolder(
                string displayText,
                string folderKey,
                IReadOnlyList<BlmFileRecord> folderFiles,
                int depth)
            {
                return new DetailFileListEntry(
                    isSectionHeader: false,
                    isFolder: true,
                    displayText: displayText ?? string.Empty,
                    sectionExtension: string.Empty,
                    file: null,
                    folderFiles: folderFiles ?? Array.Empty<BlmFileRecord>(),
                    folderKey: folderKey ?? string.Empty,
                    depth: depth);
            }

            public static DetailFileListEntry CreateFile(BlmFileRecord file, string displayText, int depth)
            {
                return new DetailFileListEntry(
                    isSectionHeader: false,
                    isFolder: false,
                    displayText: string.IsNullOrWhiteSpace(displayText) ? (file?.FileName ?? string.Empty) : displayText,
                    sectionExtension: string.Empty,
                    file: file,
                    folderFiles: Array.Empty<BlmFileRecord>(),
                    folderKey: string.Empty,
                    depth: depth);
            }
        }

        private readonly struct ShopEntry
        {
            public string Shop { get; }
            public int Count { get; }
            public ShopEntry(string shop, int count)
            {
                Shop = shop;
                Count = count;
            }
        }

        private readonly struct TagEntry
        {
            public string Tag { get; }
            public int Count { get; }
            public TagEntry(string tag, int count)
            {
                Tag = tag;
                Count = count;
            }
        }

        private readonly struct SearchHit
        {
            public BlmItemRecord Item { get; }
            public int Rank { get; }
            public SearchHit(BlmItemRecord item, int rank)
            {
                Item = item;
                Rank = rank;
            }
        }
    }
}
