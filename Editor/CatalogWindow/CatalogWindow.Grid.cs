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
        private void RebuildGrid()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var total = TotalPages();
            _page = Mathf.Clamp(_page, 1, total);
            var pageItems = _viewItems.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();
            var rebuiltByDiff = TryRebuildGridByDiff(pageItems);
            if (!rebuiltByDiff)
            {
                RebuildGridFromScratch(pageItems);
            }

            rootVisualElement.Q<Label>("CurrentPageLabel").text = _page.ToString(CultureInfo.InvariantCulture);
            rootVisualElement.Q<Label>("TotalPageLabel").text = total.ToString(CultureInfo.InvariantCulture);
            rootVisualElement.Q<Button>("PrevPageButton").SetEnabled(_page > 1);
            rootVisualElement.Q<Button>("NextPageButton").SetEnabled(_page < total);
            rootVisualElement.Q<Button>("PrevPageButton").clicked -= OnPrevPageClicked;
            rootVisualElement.Q<Button>("NextPageButton").clicked -= OnNextPageClicked;
            rootVisualElement.Q<Button>("PrevPageButton").clicked += OnPrevPageClicked;
            rootVisualElement.Q<Button>("NextPageButton").clicked += OnNextPageClicked;

            UpdateConfirmButtonState();
            ShowDetail(_detailItem);
            stopwatch.Stop();
            PerfLog(
                $"RebuildGrid completed in {stopwatch.ElapsedMilliseconds} ms. " +
                $"page={_page}/{total}, pageSize={_pageSize}, renderedItems={pageItems.Count}, totalFilteredItems={_viewItems.Count}, mode={(rebuiltByDiff ? "diff" : "full")}");
        }

        private bool TryRebuildGridByDiff(IReadOnlyList<BlmItemRecord> pageItems)
        {
            if (pageItems == null)
            {
                return false;
            }

            if (pageItems.Any(item => item == null || string.IsNullOrWhiteSpace(item.ProductId)))
            {
                return false;
            }

            var distinctCount = pageItems
                .Select(item => item.ProductId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (distinctCount != pageItems.Count)
            {
                return false;
            }

            var targetIds = new HashSet<string>(pageItems.Select(item => item.ProductId), StringComparer.Ordinal);
            foreach (var existingId in _visibleCardsByProductId.Keys.ToList())
            {
                if (targetIds.Contains(existingId))
                {
                    continue;
                }

                if (_visibleCardsByProductId.TryGetValue(existingId, out var oldCard))
                {
                    oldCard.RemoveFromHierarchy();
                }

                _visibleCardsByProductId.Remove(existingId);
            }

            for (var i = _productGridContainer.childCount - 1; i >= 0; i--)
            {
                var child = _productGridContainer[i];
                if (!(child.userData is BlmItemRecord boundItem) ||
                    string.IsNullOrWhiteSpace(boundItem.ProductId) ||
                    !targetIds.Contains(boundItem.ProductId))
                {
                    child.RemoveFromHierarchy();
                }
            }

            foreach (var item in pageItems)
            {
                if (!_visibleCardsByProductId.TryGetValue(item.ProductId, out var card))
                {
                    card = BuildCard(item);
                    _visibleCardsByProductId[item.ProductId] = card;
                    LoadCardThumbnailAsync(card, item);
                }

                BindCard(card, item);
            }

            for (var i = 0; i < pageItems.Count; i++)
            {
                var item = pageItems[i];
                if (!_visibleCardsByProductId.TryGetValue(item.ProductId, out var card))
                {
                    continue;
                }

                if (_productGridContainer.childCount <= i)
                {
                    _productGridContainer.Add(card);
                }
                else if (_productGridContainer[i] != card)
                {
                    card.RemoveFromHierarchy();
                    _productGridContainer.Insert(i, card);
                }
            }

            while (_productGridContainer.childCount > pageItems.Count)
            {
                _productGridContainer[_productGridContainer.childCount - 1].RemoveFromHierarchy();
            }

            return true;
        }

        private void RebuildGridFromScratch(IReadOnlyList<BlmItemRecord> pageItems)
        {
            _productGridContainer.Clear();
            _visibleCardsByProductId.Clear();
            foreach (var item in pageItems)
            {
                var card = BuildCard(item);
                BindCard(card, item);
                _productGridContainer.Add(card);
                if (!string.IsNullOrWhiteSpace(item.ProductId))
                {
                    _visibleCardsByProductId[item.ProductId] = card;
                }

                LoadCardThumbnailAsync(card, item);
            }
        }

        private void LoadCardThumbnailAsync(VisualElement card, BlmItemRecord item)
        {
            if (card == null || item == null)
            {
                return;
            }

            _ = _thumbnailCacheService.GetTextureAsync(item).ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully || task.Result == null) return;
                EditorApplication.delayCall += () =>
                {
                    if (!(card.userData is BlmItemRecord boundItem) ||
                        !string.Equals(boundItem.ProductId, item.ProductId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    var thumb = card.Q<VisualElement>("Thumb");
                    if (thumb != null)
                    {
                        thumb.style.backgroundImage = new StyleBackground(task.Result);
                    }
                };
            });
        }

        private VisualElement BuildCard(BlmItemRecord item)
        {
            var card = new VisualElement();
            card.AddToClassList("blm-product-card");
            var thumb = new VisualElement { name = "Thumb" };
            thumb.AddToClassList("blm-product-card-thumbnail");
            card.Add(thumb);
            var pn = new Label { name = "ProductNameLabel" };
            pn.AddToClassList("blm-product-card-product-name");
            card.Add(pn);
            var sn = new Label { name = "ShopNameLabel" };
            sn.AddToClassList("blm-product-card-shop-name");
            card.Add(sn);
            BindCard(card, item);
            card.RegisterCallback<ClickEvent>(_ =>
            {
                if (card.userData is BlmItemRecord currentItem)
                {
                    SelectDetailItem(currentItem);
                }
            });
            return card;
        }

        private void BindCard(VisualElement card, BlmItemRecord item)
        {
            if (card == null || item == null)
            {
                return;
            }

            card.userData = item;
            if (_detailItem != null && _detailItem.ProductId == item.ProductId)
            {
                card.AddToClassList("blm-product-card-selected");
            }
            else
            {
                card.RemoveFromClassList("blm-product-card-selected");
            }

            var pn = card.Q<Label>("ProductNameLabel");
            if (pn != null)
            {
                pn.text = item.ProductName;
            }

            var sn = card.Q<Label>("ShopNameLabel");
            if (sn != null)
            {
                sn.text = item.ShopName;
            }
        }

        private void SelectDetailItem(BlmItemRecord item)
        {
            if (item == null)
            {
                return;
            }

            var previousProductId = _detailItem?.ProductId;
            var nextProductId = item.ProductId;
            if (ReferenceEquals(_detailItem, item))
            {
                return;
            }

            _detailItem = item;
            UpdateVisibleCardSelection(previousProductId, nextProductId);
            ShowDetail(_detailItem);
        }

        private void UpdateVisibleCardSelection(string previousProductId, string nextProductId)
        {
            if (!string.IsNullOrWhiteSpace(previousProductId) && _visibleCardsByProductId.TryGetValue(previousProductId, out var previousCard))
            {
                previousCard.RemoveFromClassList("blm-product-card-selected");
            }

            if (!string.IsNullOrWhiteSpace(nextProductId) && _visibleCardsByProductId.TryGetValue(nextProductId, out var nextCard))
            {
                nextCard.AddToClassList("blm-product-card-selected");
            }
        }
    }
}
