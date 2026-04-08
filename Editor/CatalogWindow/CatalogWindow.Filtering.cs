using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private void ApplyFilter(bool resetPage)
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (resetPage) _page = 1;
            var baseSet = ResolveBaseSet();
            var baseSetCount = baseSet.Count;

            var baseFilterStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var working = ApplyBaseFilters(baseSet);
            baseFilterStopwatch.Stop();

            var rankStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var ranked = Rank(working);
            rankStopwatch.Stop();

            _viewItems = ranked.Select(x => x.Item).ToList();

            var rebuildFilterCandidatesStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var shopAggregationKey = BuildShopAggregationKey();
            var rebuiltShopCandidates = false;
            if (!string.Equals(_lastShopAggregationKey, shopAggregationKey, StringComparison.Ordinal))
            {
                var shopAggregationSource = ApplyCategoryAndSubCategoryFiltersForAggregation(baseSet);
                RebuildShopChoices(shopAggregationSource);
                RebuildVisibleShops();
                _lastShopAggregationKey = shopAggregationKey;
                rebuiltShopCandidates = true;
            }

            var tagAggregationKey = BuildTagAggregationKey(shopAggregationKey);
            var rebuiltTagCandidates = false;
            if (!string.Equals(_lastTagAggregationKey, tagAggregationKey, StringComparison.Ordinal))
            {
                var shopAggregationSource = ApplyCategoryAndSubCategoryFiltersForAggregation(baseSet);
                var tagAggregationSource = ApplyShopFilterForAggregation(shopAggregationSource);
                RebuildTagChoices(tagAggregationSource);
                RebuildVisibleTags();
                _lastTagAggregationKey = tagAggregationKey;
                rebuiltTagCandidates = true;
            }
            rebuildFilterCandidatesStopwatch.Stop();

            var rebuildGridStopwatch = System.Diagnostics.Stopwatch.StartNew();
            RebuildGrid();
            rebuildGridStopwatch.Stop();

            var updateCountStopwatch = System.Diagnostics.Stopwatch.StartNew();
            UpdateFilterCount();
            UpdateFilteredProductCount();
            updateCountStopwatch.Stop();

            totalStopwatch.Stop();
            PerfLog(
                $"ApplyFilter completed in {totalStopwatch.ElapsedMilliseconds} ms. " +
                $"resetPage={resetPage.ToString().ToLowerInvariant()}, baseSetCount={baseSetCount}, afterBaseFilters={working.Count}, afterRank={_viewItems.Count}, " +
                $"baseFilterElapsedMs={baseFilterStopwatch.ElapsedMilliseconds}, rankElapsedMs={rankStopwatch.ElapsedMilliseconds}, " +
                $"rebuildFilterCandidatesElapsedMs={rebuildFilterCandidatesStopwatch.ElapsedMilliseconds}, " +
                $"rebuiltShopCandidates={rebuiltShopCandidates.ToString().ToLowerInvariant()}, rebuiltTagCandidates={rebuiltTagCandidates.ToString().ToLowerInvariant()}, " +
                $"rebuildGridElapsedMs={rebuildGridStopwatch.ElapsedMilliseconds}, updateCountElapsedMs={updateCountStopwatch.ElapsedMilliseconds}, " +
                $"selectedShops={_selectedShops.Count}, selectedTags={_selectedTags.Count}, search='{SanitizeForLog((_searchField.value ?? string.Empty).Trim())}'");
        }

        private List<BlmItemRecord> ResolveBaseSet()
        {
            if (_displayModeField.index != 1 || _listSelectorField.index < 0 || _listSelectorField.index >= _db.Lists.Count)
            {
                return _db.Items.ToList();
            }

            var listId = _db.Lists[_listSelectorField.index].Id;
            if (!_db.ListProductIdsByListId.TryGetValue(listId, out var ids))
            {
                return new List<BlmItemRecord>();
            }

            return _db.Items.Where(i => ids.Contains(i.ProductId)).ToList();
        }

        private List<BlmItemRecord> ApplyBaseFilters(IEnumerable<BlmItemRecord> source)
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var list = source.ToList();
            var sourceCount = list.Count;

            var category = SelectedCategoryValue();
            var categoryStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (category == NotSetToken) list = list.Where(i => string.IsNullOrWhiteSpace(i.Category)).ToList();
            else if (!string.IsNullOrWhiteSpace(category)) list = list.Where(i => i.Category == category).ToList();
            categoryStopwatch.Stop();
            var afterCategoryCount = list.Count;

            var subCategory = SelectedSubCategoryValue();
            var subCategoryStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (subCategory == NotSetToken) list = list.Where(i => string.IsNullOrWhiteSpace(i.SubCategory)).ToList();
            else if (!string.IsNullOrWhiteSpace(subCategory)) list = list.Where(i => i.SubCategory == subCategory).ToList();
            subCategoryStopwatch.Stop();
            var afterSubCategoryCount = list.Count;

            var ageStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (_ageFilterField.index == 1) list = list.Where(i => i.AgeRestriction == BlmAgeRestriction.AllAges).ToList();
            else if (_ageFilterField.index == 2) list = list.Where(i => i.AgeRestriction == BlmAgeRestriction.R18).ToList();
            ageStopwatch.Stop();
            var afterAgeCount = list.Count;

            var shopStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (_selectedShops.Count > 0) list = list.Where(i => _selectedShops.Contains(i.ShopName)).ToList();
            shopStopwatch.Stop();
            var afterShopCount = list.Count;

            var tagStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (_selectedTags.Count > 0) list = list.Where(i => i.Tags.Any(tag => _selectedTags.Contains(tag))).ToList();
            tagStopwatch.Stop();
            var afterTagCount = list.Count;

            totalStopwatch.Stop();
            PerfLog(
                $"ApplyBaseFilters completed in {totalStopwatch.ElapsedMilliseconds} ms. " +
                $"source={sourceCount}, category='{SanitizeForLog(category)}' -> {afterCategoryCount} ({categoryStopwatch.ElapsedMilliseconds} ms), " +
                $"subCategory='{SanitizeForLog(subCategory)}' -> {afterSubCategoryCount} ({subCategoryStopwatch.ElapsedMilliseconds} ms), " +
                $"ageIndex={_ageFilterField.index} -> {afterAgeCount} ({ageStopwatch.ElapsedMilliseconds} ms), " +
                $"selectedShops={_selectedShops.Count} -> {afterShopCount} ({shopStopwatch.ElapsedMilliseconds} ms), " +
                $"selectedTags={_selectedTags.Count} -> {afterTagCount} ({tagStopwatch.ElapsedMilliseconds} ms)");

            return list;
        }

        private List<BlmItemRecord> ApplyCategoryAndSubCategoryFiltersForAggregation(IEnumerable<BlmItemRecord> source)
        {
            var list = source?.ToList() ?? new List<BlmItemRecord>();

            var category = SelectedCategoryValue();
            if (category == NotSetToken)
            {
                list = list.Where(item => string.IsNullOrWhiteSpace(item.Category)).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(category))
            {
                list = list.Where(item => item.Category == category).ToList();
            }

            var subCategory = SelectedSubCategoryValue();
            if (subCategory == NotSetToken)
            {
                list = list.Where(item => string.IsNullOrWhiteSpace(item.SubCategory)).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(subCategory))
            {
                list = list.Where(item => item.SubCategory == subCategory).ToList();
            }

            return list;
        }

        private List<BlmItemRecord> ApplyShopFilterForAggregation(IEnumerable<BlmItemRecord> source)
        {
            var list = source?.ToList() ?? new List<BlmItemRecord>();
            if (_selectedShops.Count == 0)
            {
                return list;
            }

            return list.Where(item => _selectedShops.Contains(item.ShopName)).ToList();
        }

        private void PruneSelectionsOutsideCurrentBaseSet()
        {
            var baseSet = ResolveBaseSet();
            var validShops = new HashSet<string>(
                baseSet.Select(item => item?.ShopName).Where(shop => !string.IsNullOrWhiteSpace(shop)),
                StringComparer.Ordinal);
            var validTags = new HashSet<string>(
                baseSet
                    .Where(item => item?.Tags != null)
                    .SelectMany(item => item.Tags)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag)),
                StringComparer.Ordinal);

            var removedShopCount = _selectedShops.RemoveWhere(shop => !validShops.Contains(shop));
            var removedTagCount = _selectedTags.RemoveWhere(tag => !validTags.Contains(tag));

            if (removedShopCount > 0 || removedTagCount > 0)
            {
                PerfLog(
                    $"PruneSelectionsOutsideCurrentBaseSet removedShops={removedShopCount}, removedTags={removedTagCount}, baseSetCount={baseSet.Count}");
            }
        }

        private string BuildShopAggregationKey()
        {
            var category = SelectedCategoryValue();
            var subCategory = SelectedSubCategoryValue();
            if (_displayModeField.index != 1)
            {
                return $"all|cat:{category}|sub:{subCategory}";
            }

            if (_listSelectorField.index < 0 || _listSelectorField.index >= _db.Lists.Count)
            {
                return $"list:none|cat:{category}|sub:{subCategory}";
            }

            return $"list:{_db.Lists[_listSelectorField.index].Id}|cat:{category}|sub:{subCategory}";
        }

        private string BuildTagAggregationKey(string shopAggregationKey)
        {
            if (_selectedShops.Count == 0)
            {
                return $"{shopAggregationKey}|shops:*";
            }

            var selectedShopsKey = string.Join("\u001f", _selectedShops.OrderBy(shop => shop, StringComparer.Ordinal));
            return $"{shopAggregationKey}|shops:{selectedShopsKey}";
        }

        private void RebuildCategoryChoices()
        {
            var selected = SelectedCategoryValue();
            _categoryValues.Clear();
            var labels = new List<string> { L("blm.common.all", "All") };
            _categoryValues.Add(string.Empty);
            if (_db.Items.Any(i => string.IsNullOrWhiteSpace(i.Category)))
            {
                _categoryValues.Add(NotSetToken);
                labels.Add(L("blm.common.not_set", "Not Set"));
            }
            foreach (var category in _db.Items.Select(i => i.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))
            {
                _categoryValues.Add(category);
                labels.Add(category);
            }
            _categoryFilterField.choices = labels;
            _categoryFilterField.index = Mathf.Clamp(_categoryValues.IndexOf(selected), 0, _categoryValues.Count - 1);
        }

        private void RebuildSubCategoryChoices()
        {
            var selected = SelectedSubCategoryValue();
            var category = SelectedCategoryValue();
            IEnumerable<BlmItemRecord> items = _db.Items;
            if (category == NotSetToken) items = items.Where(i => string.IsNullOrWhiteSpace(i.Category));
            else if (!string.IsNullOrWhiteSpace(category)) items = items.Where(i => i.Category == category);
            _subCategoryValues.Clear();
            var labels = new List<string> { L("blm.common.all", "All") };
            _subCategoryValues.Add(string.Empty);
            if (items.Any(i => string.IsNullOrWhiteSpace(i.SubCategory)))
            {
                _subCategoryValues.Add(NotSetToken);
                labels.Add(L("blm.common.not_set", "Not Set"));
            }
            foreach (var sub in items.Select(i => i.SubCategory).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
            {
                _subCategoryValues.Add(sub);
                labels.Add(sub);
            }
            _subCategoryFilterField.choices = labels;
            _subCategoryFilterField.index = Mathf.Clamp(_subCategoryValues.IndexOf(selected), 0, _subCategoryValues.Count - 1);
        }

        private void RebuildShopChoices(IEnumerable<BlmItemRecord> source)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _allShops.Clear();
            var sourceItems = source?.ToList() ?? new List<BlmItemRecord>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in sourceItems)
            {
                var shop = item.ShopName;
                if (string.IsNullOrWhiteSpace(shop))
                {
                    continue;
                }

                if (!counts.ContainsKey(shop)) counts[shop] = 0;
                counts[shop]++;
            }

            _allShops.AddRange(SortShopEntries(counts.Select(x => new ShopEntry(x.Key, x.Value))));
            stopwatch.Stop();
            PerfLog(
                $"RebuildShopChoices completed in {stopwatch.ElapsedMilliseconds} ms. " +
                $"items={sourceItems.Count}, uniqueShops={_allShops.Count}");
        }

        private void RebuildVisibleShops()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _visibleShops.Clear();
            var query = (_shopSearchField.value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                _visibleShops.AddRange(_allShops);
            }
            else
            {
                var normalized = BlmProductFolderResolver.NormalizeForComparison(query);
                _visibleShops.AddRange(_allShops.Where(x => BlmProductFolderResolver.NormalizeForComparison(x.Shop).Contains(normalized)));
            }

            _shopListView.itemsSource = _visibleShops;
            _shopListView.Rebuild();
            if (_visibleShops.Count > 0)
            {
                _shopListView.ScrollToItem(0);
            }
            stopwatch.Stop();
            PerfLog(
                $"RebuildVisibleShops completed in {stopwatch.ElapsedMilliseconds} ms. " +
                $"query='{SanitizeForLog(query)}', allShops={_allShops.Count}, visibleShops={_visibleShops.Count}, selectedShops={_selectedShops.Count}");
        }

        private void RebuildTagChoices(IEnumerable<BlmItemRecord> source)
        {
            _allTags.Clear();
            var sourceItems = source?.ToList() ?? new List<BlmItemRecord>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in sourceItems)
            {
                foreach (var tag in item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    if (!counts.ContainsKey(tag)) counts[tag] = 0;
                    counts[tag]++;
                }
            }
            _allTags.AddRange(SortTagEntries(counts.Select(x => new TagEntry(x.Key, x.Value))));
        }

        private void RebuildVisibleTags()
        {
            _visibleTags.Clear();
            var query = (_tagSearchField.value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                _visibleTags.AddRange(_allTags);
            }
            else
            {
                var normalized = BlmProductFolderResolver.NormalizeForComparison(query);
                _visibleTags.AddRange(_allTags.Where(x => BlmProductFolderResolver.NormalizeForComparison(x.Tag).Contains(normalized)));
            }
            _tagListView.itemsSource = _visibleTags;
            _tagListView.Rebuild();
            if (_visibleTags.Count > 0)
            {
                _tagListView.ScrollToItem(0);
            }
        }

        private void SetupFilterSortDropdown(DropdownField field, string editorPrefsKey)
        {
            if (field == null)
            {
                return;
            }

            field.choices = BuildFilterSortChoices();
            field.index = (int)LoadFilterSortMode(editorPrefsKey);
        }

        private static FilterCandidateSortMode LoadFilterSortMode(string editorPrefsKey)
        {
            var hasStoredSetting = EditorPrefs.HasKey(editorPrefsKey);
            var storedValue = EditorPrefs.GetInt(editorPrefsKey, (int)FilterCandidateSortMode.NameAscending);
            var mode = storedValue == (int)FilterCandidateSortMode.CountDescending
                ? FilterCandidateSortMode.CountDescending
                : FilterCandidateSortMode.NameAscending;
            if (!hasStoredSetting || storedValue != (int)mode)
            {
                EditorPrefs.SetInt(editorPrefsKey, (int)mode);
            }

            return mode;
        }

        private static void PersistFilterSortMode(string editorPrefsKey, DropdownField field)
        {
            EditorPrefs.SetInt(editorPrefsKey, (int)GetFilterSortMode(field));
        }

        private static FilterCandidateSortMode GetFilterSortMode(DropdownField field)
        {
            return field != null && field.index == (int)FilterCandidateSortMode.CountDescending
                ? FilterCandidateSortMode.CountDescending
                : FilterCandidateSortMode.NameAscending;
        }

        private List<string> BuildFilterSortChoices()
        {
            return new List<string>
            {
                L("blm.filter.sort.name_asc", "Name"),
                L("blm.filter.sort.count_desc", "Count desc")
            };
        }

        private IEnumerable<ShopEntry> SortShopEntries(IEnumerable<ShopEntry> entries)
        {
            var source = entries ?? Enumerable.Empty<ShopEntry>();
            if (GetFilterSortMode(_shopFilterSortDropdownField) == FilterCandidateSortMode.CountDescending)
            {
                return source
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.Shop, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Shop, StringComparer.Ordinal);
            }

            return source
                .OrderBy(x => x.Shop, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Shop, StringComparer.Ordinal);
        }

        private IEnumerable<TagEntry> SortTagEntries(IEnumerable<TagEntry> entries)
        {
            var source = entries ?? Enumerable.Empty<TagEntry>();
            if (GetFilterSortMode(_tagFilterSortDropdownField) == FilterCandidateSortMode.CountDescending)
            {
                return source
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Tag, StringComparer.Ordinal);
            }

            return source
                .OrderBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Tag, StringComparer.Ordinal);
        }

        private void ResortShopChoices()
        {
            if (_allShops.Count <= 1)
            {
                return;
            }

            var sorted = SortShopEntries(_allShops).ToList();
            _allShops.Clear();
            _allShops.AddRange(sorted);
        }

        private void ResortTagChoices()
        {
            if (_allTags.Count <= 1)
            {
                return;
            }

            var sorted = SortTagEntries(_allTags).ToList();
            _allTags.Clear();
            _allTags.AddRange(sorted);
        }

        private string SelectedCategoryValue()
        {
            return _categoryFilterField.index >= 0 && _categoryFilterField.index < _categoryValues.Count ? _categoryValues[_categoryFilterField.index] : string.Empty;
        }

        private string SelectedSubCategoryValue()
        {
            return _subCategoryFilterField.index >= 0 && _subCategoryFilterField.index < _subCategoryValues.Count ? _subCategoryValues[_subCategoryFilterField.index] : string.Empty;
        }

        private void ForceResetCategoryAndSubCategoryFiltersToAll()
        {
            WithSuppressedUiCallbacks(() =>
            {
                _categoryFilterField.index = _categoryValues.Count > 0 ? 0 : -1;
                RebuildSubCategoryChoices();
                _subCategoryFilterField.index = _subCategoryValues.Count > 0 ? 0 : -1;
            });

            _lastShopAggregationKey = null;
            _lastTagAggregationKey = null;
        }

        private void UpdateListModeState()
        {
            var hasList = _db.Lists.Count > 0;
            if (!hasList)
            {
                _displayModeField.choices = new List<string> { L("blm.display_mode.all", "All Purchased") };
                _displayModeField.index = 0;
            }
            else if (_displayModeField.choices.Count < 2)
            {
                _displayModeField.choices = new List<string>
                {
                    L("blm.display_mode.all", "All Purchased"),
                    L("blm.display_mode.by_list", "BLM List")
                };
                _displayModeField.index = 0;
            }

            var byList = hasList && _displayModeField.index == 1;
            _listSelectorField.SetEnabled(byList && hasList);
            _listModeEmptyStateLabel.style.display = hasList ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private List<SearchHit> Rank(IEnumerable<BlmItemRecord> source)
        {
            var query = BlmProductFolderResolver.NormalizeForComparison((_searchField.value ?? string.Empty).Trim());
            var hits = new List<SearchHit>();
            foreach (var item in source)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    hits.Add(new SearchHit(item, 0));
                    continue;
                }

                var pn = BlmProductFolderResolver.NormalizeForComparison(item.ProductName);
                var sn = BlmProductFolderResolver.NormalizeForComparison(item.ShopName);
                if (pn == query || sn == query) hits.Add(new SearchHit(item, 1));
                else if (pn.StartsWith(query, StringComparison.Ordinal) || sn.StartsWith(query, StringComparison.Ordinal)) hits.Add(new SearchHit(item, 2));
                else if (pn.Contains(query) || sn.Contains(query)) hits.Add(new SearchHit(item, 3));
                else if (Levenshtein(query, pn) <= 2 || Levenshtein(query, sn) <= 2) hits.Add(new SearchHit(item, 4));
            }

            hits.Sort((a, b) =>
            {
                var cmp = a.Rank.CompareTo(b.Rank);
                if (cmp != 0) return cmp;
                cmp = CompareSort(a.Item, b.Item);
                if (cmp != 0) return cmp;
                return string.Compare(a.Item.ProductId, b.Item.ProductId, StringComparison.Ordinal);
            });
            return hits;
        }
    }
}
