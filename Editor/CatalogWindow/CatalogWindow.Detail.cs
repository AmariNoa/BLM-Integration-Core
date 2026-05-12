using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private void ShowDetail(BlmItemRecord item)
        {
            if (item == null)
            {
                _detailFileLoadVersion++;
                _detailFilesLoading = false;
                _detailFilesLoadingProductId = string.Empty;
                CancelDetailFileLoadBackgroundWork(disposeSource: false);
                CancelDetailThumbnailLoad(disposeSource: false);
                _detailProductNameLabel.text = string.Empty;
                if (_detailShopNameLabel != null)
                {
                    _detailShopNameLabel.text = string.Empty;
                }
                _detailFolderPathLabel.text = string.Empty;
                CancelImportedStateEvaluation();
                UpdateImportedStateLabel(null);
                SetDetailFileActionButtonsEnabled(false);
                _openFolderPathButton?.SetEnabled(false);
                _detailFiles = new List<BlmFileRecord>();
                _detailDuplicateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _detailFileListEntries = new List<DetailFileListEntry>();
                _detailFileRowsByProductFileKey.Clear();
                _detailFileListView.itemsSource = _detailFileListEntries;
                _detailFileListView.Rebuild();
                _detailThumbnailImage.style.backgroundImage = StyleKeyword.Null;
                return;
            }

            _detailProductNameLabel.text = item.ProductName;
            if (_detailShopNameLabel != null)
            {
                _detailShopNameLabel.text = item.ShopName ?? string.Empty;
            }
            _detailFolderPathLabel.text = string.IsNullOrWhiteSpace(item.RootFolderPath)
                ? L("blm.detail.loading_files", "Loading files...")
                : item.RootFolderPath;
            _detailThumbnailImage.style.backgroundImage = StyleKeyword.Null;
            CancelDetailThumbnailLoad(disposeSource: false);
            TryApplyDetailThumbnailFromVisibleCard(item);
            RequestDetailThumbnailLoad(item);
            BeginDetailFileLoadAsync(item);
        }

        private void TryApplyDetailThumbnailFromVisibleCard(BlmItemRecord item)
        {
            if (item == null ||
                _detailThumbnailImage == null ||
                string.IsNullOrWhiteSpace(item.ProductId) ||
                !_visibleCardsByProductId.TryGetValue(item.ProductId, out var card) ||
                card == null)
            {
                return;
            }

            var thumb = card.Q<VisualElement>("Thumb");
            if (thumb == null)
            {
                return;
            }

            var thumbnailTexture = thumb.style.backgroundImage.value.texture;
            if (thumbnailTexture == null)
            {
                return;
            }

            _detailThumbnailImage.style.backgroundImage = new StyleBackground(thumbnailTexture);
        }

        private void BeginDetailFileLoadAsync(BlmItemRecord item)
        {
            if (item == null)
            {
                return;
            }

            _detailFileLoadVersion++;
            var currentLoadVersion = _detailFileLoadVersion;
            var productId = item.ProductId ?? string.Empty;
            CancelDetailFileLoadBackgroundWork(disposeSource: false);
            var loadCancellationTokenSource = new CancellationTokenSource();
            _detailFileLoadCancellationTokenSource = loadCancellationTokenSource;
            var loadCancellationToken = loadCancellationTokenSource.Token;

            _detailFilesLoading = true;
            _detailFilesLoadingProductId = productId;
            CancelImportedStateEvaluation();
            SetDetailFileActionButtonsEnabled(false);
            _detailFiles = new List<BlmFileRecord>();
            _detailDuplicateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _detailFileListEntries = new List<DetailFileListEntry>
            {
                DetailFileListEntry.CreateSection(L("blm.detail.loading_files", "Loading files..."))
            };
            _detailFileRowsByProductFileKey.Clear();
            _detailFileListView.itemsSource = _detailFileListEntries;
            _detailFileListView.Rebuild();
            UpdateImportedStateLabel(item);
            _openFolderPathButton?.SetEnabled(false);

            var snapshotRootFolderPath = item.RootFolderPath ?? string.Empty;
            var snapshotLibraryRootPath = _db?.LibraryRootPath ?? string.Empty;
            var snapshotShopSubdomain = item.ShopSubdomain ?? string.Empty;
            var snapshotProductId = productId;
            var snapshotExistingFiles = SnapshotExistingDetailFiles(item.Files);
            var preferredDisplayExtensionsSnapshot = (_context?.PreferredDisplayExtensions ?? new List<string>())
                .ToList();
            var collapsedDetailFolderKeysSnapshot = _collapsedDetailFolderKeys.ToList();
            var otherFilesLabelSnapshot = L("blm.detail.other_files", "other files");
            _ = Task.Run(() =>
                LoadDetailFilesInBackground(
                    snapshotRootFolderPath,
                    snapshotLibraryRootPath,
                    snapshotProductId,
                    snapshotShopSubdomain,
                    snapshotExistingFiles,
                    preferredDisplayExtensionsSnapshot,
                    collapsedDetailFolderKeysSnapshot,
                    otherFilesLabelSnapshot,
                    loadCancellationToken), loadCancellationToken).ContinueWith(task =>
            {
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        if (this == null || rootVisualElement?.panel == null)
                        {
                            return;
                        }

                        if (loadCancellationToken.IsCancellationRequested ||
                            !ReferenceEquals(_detailFileLoadCancellationTokenSource, loadCancellationTokenSource))
                        {
                            return;
                        }

                        if (task.IsCanceled)
                        {
                            return;
                        }

                        if (!task.IsCompletedSuccessfully)
                        {
                            ApplyDetailFileLoadResult(
                                item,
                                currentLoadVersion,
                                CreateEmptyDetailFileLoadResult(item.ProductId));
                            return;
                        }

                        var loadResult = task.Result;
                        if (!string.Equals(loadResult.ProductId, item.ProductId, StringComparison.Ordinal))
                        {
                            return;
                        }

                        ApplyDetailFileLoadResult(item, currentLoadVersion, loadResult);
                    }
                    finally
                    {
                        if (ReferenceEquals(_detailFileLoadCancellationTokenSource, loadCancellationTokenSource))
                        {
                            _detailFileLoadCancellationTokenSource = null;
                        }

                        loadCancellationTokenSource.Dispose();
                    }
                };
            });
        }

        private void ApplyDetailFileLoadResult(BlmItemRecord item, int loadVersion, DetailFileLoadResult loadResult)
        {
            if (item == null || loadVersion != _detailFileLoadVersion)
            {
                return;
            }

            if (_detailItem == null ||
                !string.Equals(_detailItem.ProductId, item.ProductId, StringComparison.Ordinal))
            {
                return;
            }

            item.RootFolderPath = loadResult.RootFolderPath ?? string.Empty;
            item.Files = (loadResult.Files ?? Array.Empty<BlmFileRecord>())
                .Where(file => file != null)
                .ToList();

            _detailFilesLoading = false;
            _detailFilesLoadingProductId = string.Empty;
            _detailFolderPathLabel.text = item.RootFolderPath;
            _openFolderPathButton?.SetEnabled(!string.IsNullOrWhiteSpace(item.RootFolderPath) && Directory.Exists(item.RootFolderPath));
            SetDetailFileActionButtonsEnabled(true);

            _detailFiles = item.Files
                .Where(file => file != null)
                .ToList();
            _detailDuplicateFileNames = loadResult.DuplicateFileNames
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _detailFileListEntries = loadResult.DetailEntries
                ?? new List<DetailFileListEntry>();
            _detailFileRowsByProductFileKey.Clear();
            _detailFileListView.itemsSource = _detailFileListEntries;
            _detailFileListView.Rebuild();
            StartImportedStateEvaluation(item, _detailFiles);
            if (!string.IsNullOrWhiteSpace(item.ProductId) && _selectedByProduct.ContainsKey(item.ProductId))
            {
                RebuildSelectedPanel();
            }
        }

        private void RefreshDetailFileListEntries()
        {
            if (_detailFileListView == null)
            {
                return;
            }

            _detailFileListEntries = BuildDetailFileListEntries(_detailItem, _detailFiles, _detailDuplicateFileNames);
            _detailFileRowsByProductFileKey.Clear();
            _detailFileListView.itemsSource = _detailFileListEntries;
            _detailFileListView.Rebuild();
        }

        private void RequestDetailFileListRebuild()
        {
            if (_detailFileListView == null || _detailFileListRebuildScheduled)
            {
                return;
            }

            _detailFileListRebuildScheduled = true;
            EditorApplication.delayCall += () =>
            {
                _detailFileListRebuildScheduled = false;
                if (this == null || rootVisualElement?.panel == null || _detailFileListView == null)
                {
                    return;
                }

                _detailFileListView.Rebuild();
            };
        }

        private void SetDetailFileActionButtonsEnabled(bool enabled)
        {
            rootVisualElement.Q<Button>("SelectAllFilesButton")?.SetEnabled(enabled);
            rootVisualElement.Q<Button>("DeSelectAllFilesButton")?.SetEnabled(enabled);
        }

        private bool IsCurrentDetailFilesLoading()
        {
            return _detailFilesLoading &&
                   _detailItem != null &&
                   !string.IsNullOrWhiteSpace(_detailFilesLoadingProductId) &&
                   string.Equals(_detailFilesLoadingProductId, _detailItem.ProductId, StringComparison.Ordinal);
        }

        private static DetailFileLoadResult LoadDetailFilesInBackground(
            string existingRootFolderPath,
            string libraryRootPath,
            string productId,
            string shopSubdomain,
            IReadOnlyList<BlmFileRecord> existingFiles,
            IReadOnlyList<string> preferredDisplayExtensions,
            IReadOnlyCollection<string> collapsedDetailFolderKeys,
            string otherFilesLabel,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedRootFolderPath = existingRootFolderPath ?? string.Empty;
            List<BlmFileRecord> files;
            var normalizedExistingFiles = SnapshotExistingDetailFiles(existingFiles);
            if (normalizedExistingFiles.Count > 0)
            {
                files = normalizedExistingFiles as List<BlmFileRecord>
                    ?? normalizedExistingFiles.ToList();
            }
            else
            {
                resolvedRootFolderPath = ResolveRootFolderPathForDetailSnapshot(
                    existingRootFolderPath,
                    libraryRootPath,
                    productId,
                    shopSubdomain);
                files = LoadFilesForDetail(resolvedRootFolderPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var duplicateFileNames = BuildDuplicateFileNameSet(files);
            var detailEntries = BuildDetailFileListEntriesSnapshot(
                productId,
                resolvedRootFolderPath,
                files,
                preferredDisplayExtensions,
                collapsedDetailFolderKeys,
                otherFilesLabel,
                cancellationToken);
            return new DetailFileLoadResult(productId, resolvedRootFolderPath, files, duplicateFileNames, detailEntries);
        }

        private static IReadOnlyList<BlmFileRecord> SnapshotExistingDetailFiles(IReadOnlyList<BlmFileRecord> files)
        {
            if (files == null || files.Count == 0)
            {
                return Array.Empty<BlmFileRecord>();
            }

            var normalizedFiles = new List<BlmFileRecord>(files.Count);
            var containsNull = false;
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file == null)
                {
                    containsNull = true;
                    continue;
                }

                normalizedFiles.Add(file);
            }

            if (!containsNull && normalizedFiles.Count == files.Count)
            {
                return files;
            }

            return normalizedFiles;
        }

        private static DetailFileLoadResult CreateEmptyDetailFileLoadResult(string productId)
        {
            return new DetailFileLoadResult(
                productId,
                string.Empty,
                Array.Empty<BlmFileRecord>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new List<DetailFileListEntry>());
        }

        private void EnsureItemFilesLoaded(BlmItemRecord item)
        {
            if (item == null)
            {
                return;
            }

            if (item.Files != null && item.Files.Count > 0)
            {
                return;
            }

            var resolvedRootFolderPath = ResolveRootFolderPathForDetail(item);
            item.RootFolderPath = resolvedRootFolderPath;
            item.Files = LoadFilesForDetail(resolvedRootFolderPath);
        }

        private string ResolveRootFolderPathForDetail(BlmItemRecord item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            return ResolveRootFolderPathForDetailSnapshot(
                item.RootFolderPath,
                _db?.LibraryRootPath,
                item.ProductId,
                item.ShopSubdomain);
        }

        private static string ResolveRootFolderPathForDetailSnapshot(
            string rootFolderPath,
            string libraryRootPath,
            string productId,
            string shopSubdomain)
        {
            if (!string.IsNullOrWhiteSpace(rootFolderPath) && Directory.Exists(rootFolderPath))
            {
                return rootFolderPath;
            }

            if (string.IsNullOrWhiteSpace(libraryRootPath) || string.IsNullOrWhiteSpace(productId))
            {
                return string.Empty;
            }

            foreach (var candidate in EnumerateRootFolderCandidates(libraryRootPath, productId, shopSubdomain))
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static IEnumerable<string> EnumerateRootFolderCandidates(string libraryRootPath, string productId, string shopSubdomain)
        {
            if (string.IsNullOrWhiteSpace(libraryRootPath) || string.IsNullOrWhiteSpace(productId))
            {
                yield break;
            }

            foreach (var candidate in EnumerateRootFolderCandidatesForSingleRoot(libraryRootPath, productId, shopSubdomain))
            {
                yield return candidate;
            }

            var parent = Directory.GetParent(libraryRootPath)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                yield break;
            }

            foreach (var candidate in EnumerateRootFolderCandidatesForSingleRoot(parent, productId, shopSubdomain))
            {
                yield return candidate;
            }
        }

        private static IEnumerable<string> EnumerateRootFolderCandidatesForSingleRoot(string rootPath, string productId, string shopSubdomain)
        {
            yield return Path.Combine(rootPath, $"b{productId}");
            yield return Path.Combine(rootPath, productId);

            if (string.IsNullOrWhiteSpace(shopSubdomain))
            {
                yield break;
            }

            yield return Path.Combine(rootPath, shopSubdomain, $"b{productId}");
            yield return Path.Combine(rootPath, shopSubdomain, productId);
        }

        private static List<BlmFileRecord> LoadFilesForDetail(string rootFolderPath, CancellationToken cancellationToken = default)
        {
            var files = new List<BlmFileRecord>();
            if (string.IsNullOrWhiteSpace(rootFolderPath) || !Directory.Exists(rootFolderPath))
            {
                return files;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(rootFolderPath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(new BlmFileRecord
                    {
                        FileName = Path.GetFileName(path) ?? string.Empty,
                        FullPath = path,
                        FileExtension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant()
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to enumerate files in detail view: {ex.Message}");
            }

            return files;
        }
    }
}
