using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private void SetSelected(BlmItemRecord item, string filePath, bool selected)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ProductId) || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            if (!_selectedByProduct.TryGetValue(item.ProductId, out var set))
            {
                if (!selected)
                {
                    return;
                }

                set = new HashSet<string>(StringComparer.Ordinal);
                _selectedByProduct[item.ProductId] = set;
            }

            var changed = false;
            if (selected)
            {
                changed = set.Add(filePath);
                if (!_selectedOrder.Contains(item.ProductId))
                {
                    _selectedOrder.Add(item.ProductId);
                    changed = true;
                }
            }
            else
            {
                changed = set.Remove(filePath);
                if (set.Count == 0)
                {
                    if (_selectedByProduct.Remove(item.ProductId))
                    {
                        changed = true;
                    }

                    if (_selectedOrder.Remove(item.ProductId))
                    {
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                MarkSelectionChanged();
            }
        }

        private void SetAllFilesSelection(bool selected)
        {
            if (_detailItem == null)
            {
                return;
            }

            if (IsCurrentDetailFilesLoading())
            {
                return;
            }

            EnsureItemFilesLoaded(_detailItem);
            foreach (var file in _detailItem.Files)
            {
                SetSelected(_detailItem, file.FullPath, selected);
            }

            RequestDetailFileListRebuild();
            RebuildSelectedPanel();
            UpdateConfirmButtonState();
        }

        private void SetSectionFilesSelection(string normalizedExtension, bool selected)
        {
            if (_detailItem == null || string.IsNullOrWhiteSpace(normalizedExtension))
            {
                return;
            }

            if (IsCurrentDetailFilesLoading())
            {
                return;
            }

            EnsureItemFilesLoaded(_detailItem);
            foreach (var file in _detailItem.Files)
            {
                if (file == null || !string.Equals(NormalizeExtension(file.FileExtension), normalizedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SetSelected(_detailItem, file.FullPath, selected);
            }
        }

        private void SetFilesSelection(BlmItemRecord item, IEnumerable<BlmFileRecord> files, bool selected)
        {
            if (item == null || files == null)
            {
                return;
            }

            foreach (var file in files)
            {
                if (file == null || string.IsNullOrWhiteSpace(file.FullPath))
                {
                    continue;
                }

                SetSelected(item, file.FullPath, selected);
            }
        }

        private void SetFolderFilesSelection(IEnumerable<BlmFileRecord> folderFiles, bool selected)
        {
            SetFilesSelection(_detailItem, folderFiles, selected);
        }

        private DetailSectionSelectionState GetFileSelectionState(string productId, IEnumerable<BlmFileRecord> files)
        {
            if (string.IsNullOrWhiteSpace(productId) || files == null)
            {
                return DetailSectionSelectionState.Off;
            }

            var fileList = files
                .Where(file => file != null && !string.IsNullOrWhiteSpace(file.FullPath))
                .ToList();
            if (fileList.Count == 0)
            {
                return DetailSectionSelectionState.Off;
            }

            var selectedCount = fileList.Count(file => IsSelected(productId, file.FullPath));
            if (selectedCount <= 0)
            {
                return DetailSectionSelectionState.Off;
            }

            if (selectedCount >= fileList.Count)
            {
                return DetailSectionSelectionState.On;
            }

            return DetailSectionSelectionState.Mixed;
        }

        private DetailSectionSelectionState GetDetailSectionSelectionState(string normalizedExtension)
        {
            if (_detailItem == null || string.IsNullOrWhiteSpace(normalizedExtension))
            {
                return DetailSectionSelectionState.Off;
            }

            EnsureItemFilesLoaded(_detailItem);

            var sectionFiles = (_detailItem.Files ?? new List<BlmFileRecord>())
                .Where(file => file != null && string.Equals(NormalizeExtension(file.FileExtension), normalizedExtension, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sectionFiles.Count == 0)
            {
                return DetailSectionSelectionState.Off;
            }

            var selectedCount = sectionFiles.Count(file => IsSelected(_detailItem.ProductId, file.FullPath));
            if (selectedCount <= 0)
            {
                return DetailSectionSelectionState.Off;
            }

            if (selectedCount >= sectionFiles.Count)
            {
                return DetailSectionSelectionState.On;
            }

            return DetailSectionSelectionState.Mixed;
        }

        private DetailSectionSelectionState GetDetailFolderSelectionState(IEnumerable<BlmFileRecord> folderFiles)
        {
            return GetFileSelectionState(_detailItem?.ProductId, folderFiles);
        }

        private void ApplyDetailSectionToggleState(Toggle toggle, string normalizedExtension)
        {
            if (toggle == null)
            {
                return;
            }

            var state = GetDetailSectionSelectionState(normalizedExtension);
            toggle.showMixedValue = false;
            switch (state)
            {
                case DetailSectionSelectionState.On:
                    toggle.SetValueWithoutNotify(true);
                    break;
                case DetailSectionSelectionState.Mixed:
                    toggle.SetValueWithoutNotify(false);
                    toggle.showMixedValue = true;
                    break;
                default:
                    toggle.SetValueWithoutNotify(false);
                    break;
            }
        }

        private void ApplyDetailFolderToggleState(Toggle toggle, IEnumerable<BlmFileRecord> folderFiles)
        {
            if (toggle == null)
            {
                return;
            }

            var state = GetFileSelectionState(_detailItem?.ProductId, folderFiles);
            toggle.showMixedValue = false;
            switch (state)
            {
                case DetailSectionSelectionState.On:
                    toggle.SetValueWithoutNotify(true);
                    break;
                case DetailSectionSelectionState.Mixed:
                    toggle.SetValueWithoutNotify(false);
                    toggle.showMixedValue = true;
                    break;
                default:
                    toggle.SetValueWithoutNotify(false);
                    break;
            }
        }

        private void ToggleDetailFolderExpanded(string folderKey)
        {
            if (string.IsNullOrWhiteSpace(folderKey))
            {
                return;
            }

            if (!_collapsedDetailFolderKeys.Remove(folderKey))
            {
                _collapsedDetailFolderKeys.Add(folderKey);
            }

            RefreshDetailFileListEntries();
        }

        private bool IsDetailFolderExpanded(string folderKey)
        {
            return string.IsNullOrWhiteSpace(folderKey) || !_collapsedDetailFolderKeys.Contains(folderKey);
        }

        private void ClearAllSelectedFiles()
        {
            if (_selectedByProduct.Count == 0 && _selectedOrder.Count == 0)
            {
                return;
            }

            _selectedByProduct.Clear();
            _selectedOrder.Clear();
            MarkSelectionChanged();

            RequestDetailFileListRebuild();
            RebuildSelectedPanel();
            UpdateConfirmButtonState();
        }

        private void RebuildSelectedPanel()
        {
            if (_selectedProductsScrollView == null)
            {
                return;
            }

            _selectedProductsScrollView.Clear();
            _selectedPanelFileRowsByProductFileKey.Clear();
            foreach (var productId in _selectedOrder.ToList())
            {
                if (!_selectedByProduct.TryGetValue(productId, out var selected) || selected.Count == 0)
                {
                    _selectedOrder.Remove(productId);
                    MarkSelectionChanged();
                    continue;
                }

                if (!_dbItemsByProductId.TryGetValue(productId, out var item) || item == null) continue;
                EnsureItemFilesLoaded(item);
                var visibleFiles = BuildSelectedPanelFileList(item, selected);
                if (visibleFiles.Count == 0)
                {
                    continue;
                }
                var entries = BuildSelectedPanelFileListEntries(item, visibleFiles);
                if (entries.Count == 0)
                {
                    continue;
                }

                var fold = new Foldout { text = $"{item.ProductName} / {item.ShopName}" };
                fold.AddToClassList("blm-selected-product-foldout");
                fold.style.minWidth = 0f;
                var productFoldoutKey = item.ProductId ?? string.Empty;
                fold.SetValueWithoutNotify(IsSelectedProductExpanded(productFoldoutKey));
                fold.RegisterValueChangedCallback(evt => SetSelectedProductExpanded(productFoldoutKey, evt.newValue));
                var foldToggle = fold.Q<Toggle>();
                if (foldToggle != null)
                {
                    foldToggle.AddToClassList("blm-selected-product-foldout-toggle");
                    var foldTitleLabel = foldToggle.Q<Label>(className: "unity-foldout__text")
                                         ?? foldToggle.Q<Label>(className: "unity-toggle__text")
                                         ?? foldToggle.Q<Label>();
                    foldTitleLabel?.AddToClassList("blm-selected-product-foldout-title");
                }

                foreach (var entry in entries)
                {
                    var row = BuildSelectedPanelRow(item, visibleFiles, entry);
                    fold.Add(row);
                }

                _selectedProductsScrollView.Add(fold);
            }
        }

        private List<BlmFileRecord> BuildSelectedPanelFileList(BlmItemRecord item, HashSet<string> selected)
        {
            if (item?.Files == null || selected == null)
            {
                return new List<BlmFileRecord>();
            }

            List<BlmFileRecord> filteredFiles;
            switch (_selectedItemsFilterMode)
            {
                case SelectedItemsFilterMode.SelectedOnly:
                    filteredFiles = item.Files
                        .Where(file => selected.Contains(file.FullPath))
                        .ToList();
                    break;
                case SelectedItemsFilterMode.All:
                    filteredFiles = item.Files.ToList();
                    break;
                default:
                    filteredFiles = item.Files
                        .Where(file => selected.Contains(file.FullPath) || ExtensionPriority(file.FileExtension) != int.MaxValue)
                        .ToList();
                    break;
            }

            var duplicateFileNames = BuildDuplicateFileNameSet(filteredFiles);
            return filteredFiles
                .OrderBy(file => BuildFileSortText(item, file, duplicateFileNames), StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => BuildFileSortText(item, file, duplicateFileNames), StringComparer.Ordinal)
                .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private VisualElement BuildSelectedPanelRow(
            BlmItemRecord item,
            IReadOnlyList<BlmFileRecord> visibleFiles,
            DetailFileListEntry entry)
        {
            var row = new VisualElement();
            row.AddToClassList("blm-file-row");
            row.style.minWidth = 0f;

            var toggle = new Toggle();
            toggle.name = "Toggle";
            toggle.AddToClassList("blm-file-row-select-toggle");
            var folderFoldout = new Foldout();
            folderFoldout.name = "FolderFoldout";
            folderFoldout.AddToClassList("blm-file-row-folder-foldout");
            var label = new Label();
            label.name = "Label";
            label.AddToClassList("blm-file-row-label");

            row.Add(toggle);
            row.Add(folderFoldout);
            row.Add(label);

            if (entry.IsSectionHeader)
            {
                row.AddToClassList("blm-file-row-section");
                row.RemoveFromClassList(ImportedFileRowClassName);
                row.RemoveFromClassList(PartiallyImportedFileRowClassName);
                SetRowImportedStateTooltip(row, string.Empty);
                label.AddToClassList("blm-file-row-section-label");
                row.style.paddingLeft = 0f;
                folderFoldout.style.display = DisplayStyle.None;
                folderFoldout.userData = null;
                folderFoldout.text = string.Empty;
                folderFoldout.SetValueWithoutNotify(false);
                label.style.display = DisplayStyle.Flex;

                if (entry.CanToggleSectionSelection)
                {
                    var sectionFiles = (visibleFiles ?? Array.Empty<BlmFileRecord>())
                        .Where(file => file != null
                                       && string.Equals(
                                           NormalizeExtension(file.FileExtension),
                                           entry.SectionExtension,
                                           StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    row.AddToClassList("blm-file-row-section-with-toggle");
                    toggle.style.display = DisplayStyle.Flex;
                    toggle.userData = sectionFiles;
                    ApplySelectedPanelToggleState(toggle, item?.ProductId, sectionFiles);
                    toggle.RegisterValueChangedCallback(evt =>
                    {
                        if (toggle.userData is IReadOnlyList<BlmFileRecord> files)
                        {
                            SetFilesSelection(item, files, evt.newValue);
                            RefreshAfterSelectedPanelSelectionChanged(item);
                        }
                    });
                }
                else
                {
                    toggle.style.display = DisplayStyle.None;
                    toggle.userData = null;
                    toggle.showMixedValue = false;
                    toggle.SetValueWithoutNotify(false);
                }

                label.text = entry.DisplayText;
                return row;
            }

            row.style.paddingLeft = entry.Depth * 14f;
            toggle.style.display = DisplayStyle.Flex;
            if (entry.IsFolder)
            {
                toggle.userData = entry.FolderFiles;
                ApplySelectedPanelToggleState(toggle, item?.ProductId, entry.FolderFiles);
                row.RemoveFromClassList(ImportedFileRowClassName);
                row.RemoveFromClassList(PartiallyImportedFileRowClassName);
                SetRowImportedStateTooltip(row, string.Empty);
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (toggle.userData is IReadOnlyList<BlmFileRecord> files)
                    {
                        SetFilesSelection(item, files, evt.newValue);
                        RefreshAfterSelectedPanelSelectionChanged(item);
                    }
                });

                folderFoldout.style.display = DisplayStyle.Flex;
                folderFoldout.userData = entry.FolderKey;
                folderFoldout.text = entry.DisplayText;
                folderFoldout.SetValueWithoutNotify(IsSelectedFolderExpanded(entry.FolderKey));
                folderFoldout.RegisterValueChangedCallback(evt =>
                {
                    if (folderFoldout.userData is string folderKey && IsSelectedFolderExpanded(folderKey) != evt.newValue)
                    {
                        ToggleSelectedFolderExpanded(folderKey);
                    }
                });

                label.style.display = DisplayStyle.None;
                label.text = string.Empty;
                return row;
            }

            folderFoldout.style.display = DisplayStyle.None;
            folderFoldout.userData = null;
            folderFoldout.text = string.Empty;
            folderFoldout.SetValueWithoutNotify(false);

            toggle.showMixedValue = false;
            var fileEntry = entry.File;
            toggle.userData = fileEntry;
            toggle.SetValueWithoutNotify(fileEntry != null && IsSelected(item?.ProductId, fileEntry.FullPath));
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (toggle.userData is BlmFileRecord file)
                {
                    SetSelected(item, file.FullPath, evt.newValue);
                    RefreshAfterSelectedPanelSelectionChanged(item);
                }
            });

            label.style.display = DisplayStyle.Flex;
            label.text = entry.DisplayText;
            RegisterSelectedPanelFileRow(item?.ProductId, fileEntry, row);
            ApplyImportedStateVisualToFileRow(row, GetImportedStateForFile(item, fileEntry));
            return row;
        }

        private void RefreshAfterSelectedPanelSelectionChanged(BlmItemRecord item)
        {
            RebuildSelectedPanel();
            if (_detailItem != null && item != null && _detailItem.ProductId == item.ProductId)
            {
                RequestDetailFileListRebuild();
            }

            UpdateConfirmButtonState();
        }

        private List<DetailFileListEntry> BuildSelectedPanelFileListEntries(
            BlmItemRecord item,
            IReadOnlyList<BlmFileRecord> files)
        {
            var entries = new List<DetailFileListEntry>();
            if (files == null || files.Count == 0)
            {
                return entries;
            }

            var normalizedFiles = files
                .Where(file => file != null)
                .ToList();
            if (normalizedFiles.Count == 0)
            {
                return entries;
            }

            var root = BuildDetailFolderTree(item, normalizedFiles);
            PopulateDetailFolderDescendants(root);
            const string sectionKey = "__all__";
            AppendSelectedPanelFolderEntries(entries, item, root, sectionKey, depth: 0);
            AppendDetailFileEntries(entries, item, root.DirectFiles, depth: 0);

            return entries;
        }

        private void AppendSelectedPanelFolderEntries(
            List<DetailFileListEntry> entries,
            BlmItemRecord item,
            DetailFolderTreeNode parent,
            string sectionKey,
            int depth)
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
                var folderKey = BuildSelectedPanelFolderKey(item?.ProductId, sectionKey, folder.RelativePath);
                entries.Add(DetailFileListEntry.CreateFolder(
                    displayText: folder.Name,
                    folderKey: folderKey,
                    folderFiles: folder.DescendantFiles,
                    depth: depth));
                if (!IsSelectedFolderExpanded(folderKey))
                {
                    continue;
                }

                AppendSelectedPanelFolderEntries(entries, item, folder, sectionKey, depth + 1);
                AppendDetailFileEntries(entries, item, folder.DirectFiles, depth + 1);
            }
        }

        private static string BuildSelectedPanelFolderKey(string productId, string sectionKey, string folderRelativePath)
        {
            return $"selected|{BuildDetailFolderKey(productId, sectionKey, folderRelativePath)}";
        }

        private bool IsSelectedFolderExpanded(string folderKey)
        {
            return string.IsNullOrWhiteSpace(folderKey) || !_collapsedSelectedFolderKeys.Contains(folderKey);
        }

        private void ToggleSelectedFolderExpanded(string folderKey)
        {
            if (string.IsNullOrWhiteSpace(folderKey))
            {
                return;
            }

            if (!_collapsedSelectedFolderKeys.Remove(folderKey))
            {
                _collapsedSelectedFolderKeys.Add(folderKey);
            }

            RebuildSelectedPanel();
        }

        private bool IsSelectedProductExpanded(string productId)
        {
            return string.IsNullOrWhiteSpace(productId) || !_collapsedSelectedProductKeys.Contains(productId);
        }

        private void SetSelectedProductExpanded(string productId, bool expanded)
        {
            if (string.IsNullOrWhiteSpace(productId))
            {
                return;
            }

            if (expanded)
            {
                _collapsedSelectedProductKeys.Remove(productId);
            }
            else
            {
                _collapsedSelectedProductKeys.Add(productId);
            }
        }

        private void ApplySelectedPanelToggleState(Toggle toggle, string productId, IEnumerable<BlmFileRecord> files)
        {
            if (toggle == null)
            {
                return;
            }

            var state = GetFileSelectionState(productId, files);
            toggle.showMixedValue = false;
            switch (state)
            {
                case DetailSectionSelectionState.On:
                    toggle.SetValueWithoutNotify(true);
                    break;
                case DetailSectionSelectionState.Mixed:
                    toggle.SetValueWithoutNotify(false);
                    toggle.showMixedValue = true;
                    break;
                default:
                    toggle.SetValueWithoutNotify(false);
                    break;
            }
        }
    }
}
