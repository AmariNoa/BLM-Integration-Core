using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private sealed class CardSlots
        {
            public VisualElement Thumb;
            public Label ProductName;
            public Label ShopName;
        }

        private readonly Dictionary<VisualElement, CardSlots> _cardSlots = new Dictionary<VisualElement, CardSlots>();
        private readonly List<VisualElement> _cardPool = new List<VisualElement>();

        private void RebuildGrid()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var total = TotalPages();
            _page = Mathf.Clamp(_page, 1, total);
            var pageItems = _viewItems.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();
            var stats = RebuildGridWithPool(pageItems);

            RefreshPaginationFooter();

            UpdateConfirmButtonState();
            var detailProductId = _detailItem?.ProductId ?? string.Empty;
            if (!string.Equals(_lastShownDetailProductId, detailProductId, StringComparison.Ordinal))
            {
                ShowDetail(_detailItem);
            }
            stopwatch.Stop();
            PerfLog(
                $"RebuildGrid completed in {stopwatch.ElapsedMilliseconds} ms. " +
                $"page={_page}/{total}, pageSize={_pageSize}, renderedItems={pageItems.Count}, totalFilteredItems={_viewItems.Count}, " +
                $"reused={stats.Reused}, pooled={stats.FromPool}, built={stats.Built}, poolSize={_cardPool.Count}");
        }

        private (int Reused, int FromPool, int Built) RebuildGridWithPool(IReadOnlyList<BlmItemRecord> pageItems)
        {
            var targetIds = new HashSet<string>(StringComparer.Ordinal);
            if (pageItems != null)
            {
                foreach (var item in pageItems)
                {
                    if (item != null && !string.IsNullOrWhiteSpace(item.ProductId))
                    {
                        targetIds.Add(item.ProductId);
                    }
                }
            }

            foreach (var existingId in _visibleCardsByProductId.Keys.ToList())
            {
                if (targetIds.Contains(existingId))
                {
                    continue;
                }

                if (_visibleCardsByProductId.TryGetValue(existingId, out var releasedCard))
                {
                    releasedCard.RemoveFromHierarchy();
                    ResetCardForPool(releasedCard);
                    _cardPool.Add(releasedCard);
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
                    if (_cardSlots.ContainsKey(child) && !_cardPool.Contains(child))
                    {
                        ResetCardForPool(child);
                        _cardPool.Add(child);
                    }
                }
            }

            var parent = _productGridContainer.parent;
            var siblingIndex = parent?.IndexOf(_productGridContainer) ?? -1;
            var detached = parent != null && siblingIndex >= 0;
            if (detached)
            {
                _productGridContainer.RemoveFromHierarchy();
            }

            var reused = 0;
            var fromPool = 0;
            var built = 0;
            try
            {
                if (pageItems != null)
                {
                    for (var i = 0; i < pageItems.Count; i++)
                    {
                        var item = pageItems[i];
                        if (item == null)
                        {
                            continue;
                        }

                        var hasProductId = !string.IsNullOrWhiteSpace(item.ProductId);
                        VisualElement card;
                        if (hasProductId && _visibleCardsByProductId.TryGetValue(item.ProductId, out card))
                        {
                            BindCard(card, item);
                            reused++;
                        }
                        else if (_cardPool.Count > 0)
                        {
                            card = _cardPool[_cardPool.Count - 1];
                            _cardPool.RemoveAt(_cardPool.Count - 1);
                            BindCard(card, item);
                            LoadCardThumbnailAsync(card, item);
                            fromPool++;
                        }
                        else
                        {
                            card = BuildCard(item);
                            LoadCardThumbnailAsync(card, item);
                            built++;
                        }

                        if (hasProductId)
                        {
                            _visibleCardsByProductId[item.ProductId] = card;
                        }

                        if (_productGridContainer.childCount <= i)
                        {
                            _productGridContainer.Add(card);
                        }
                        else if (_productGridContainer[i] != card)
                        {
                            if (card.parent == _productGridContainer)
                            {
                                card.RemoveFromHierarchy();
                            }

                            _productGridContainer.Insert(i, card);
                        }
                    }

                    while (_productGridContainer.childCount > pageItems.Count)
                    {
                        var last = _productGridContainer[_productGridContainer.childCount - 1];
                        last.RemoveFromHierarchy();
                        if (_cardSlots.ContainsKey(last) && !_cardPool.Contains(last))
                        {
                            ResetCardForPool(last);
                            _cardPool.Add(last);
                        }
                    }
                }
            }
            finally
            {
                if (detached)
                {
                    parent.Insert(siblingIndex, _productGridContainer);
                }
            }

            return (reused, fromPool, built);
        }

        private void ResetCardForPool(VisualElement card)
        {
            if (card == null)
            {
                return;
            }

            card.userData = null;
            card.RemoveFromClassList("blm-product-card-selected");
            if (_cardSlots.TryGetValue(card, out var slots))
            {
                if (slots.ProductName != null)
                {
                    slots.ProductName.text = string.Empty;
                }

                if (slots.ShopName != null)
                {
                    slots.ShopName.text = string.Empty;
                }

                if (slots.Thumb != null)
                {
                    slots.Thumb.style.backgroundImage = StyleKeyword.Null;
                }
            }
        }

        private void LoadCardThumbnailAsync(VisualElement card, BlmItemRecord item)
        {
            if (card == null || item == null)
            {
                return;
            }

            if (_thumbnailCacheService.TryGetMemoryCachedTexture(item, out var cachedTexture) && cachedTexture != null)
            {
                var thumb = GetCardThumb(card);
                if (thumb != null)
                {
                    thumb.style.backgroundImage = new StyleBackground(cachedTexture);
                }
                return;
            }

            EditorApplication.delayCall += () =>
            {
                if (this == null || rootVisualElement?.panel == null)
                {
                    return;
                }

                if (!(card.userData is BlmItemRecord boundItem) ||
                    !string.Equals(boundItem.ProductId, item.ProductId, StringComparison.Ordinal))
                {
                    return;
                }

                DispatchCardThumbnailFetch(card, item);
            };
        }

        private void DispatchCardThumbnailFetch(VisualElement card, BlmItemRecord item)
        {
            var cancellationToken = EnsureGridThumbnailCancellationToken();
            _ = _thumbnailCacheService.GetTextureAsync(item, cancellationToken).ContinueWith(task =>
            {
                if (cancellationToken.IsCancellationRequested || !task.IsCompletedSuccessfully || task.Result == null)
                {
                    return;
                }

                EditorApplication.delayCall += () =>
                {
                    if (this == null ||
                        cancellationToken.IsCancellationRequested ||
                        rootVisualElement?.panel == null ||
                        !(card.userData is BlmItemRecord boundItem) ||
                        !string.Equals(boundItem.ProductId, item.ProductId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    var thumb = GetCardThumb(card);
                    if (thumb != null)
                    {
                        thumb.style.backgroundImage = new StyleBackground(task.Result);
                    }
                };
            });
        }

        private VisualElement GetCardThumb(VisualElement card)
        {
            return card == null ? null : GetOrCreateCardSlots(card).Thumb;
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
            _cardSlots[card] = new CardSlots { Thumb = thumb, ProductName = pn, ShopName = sn };
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

            var slots = GetOrCreateCardSlots(card);
            if (slots.ProductName != null)
            {
                slots.ProductName.text = item.ProductName;
            }

            if (slots.ShopName != null)
            {
                slots.ShopName.text = item.ShopName;
            }
        }

        private CardSlots GetOrCreateCardSlots(VisualElement card)
        {
            if (_cardSlots.TryGetValue(card, out var slots))
            {
                return slots;
            }

            slots = new CardSlots
            {
                Thumb = card.Q<VisualElement>("Thumb"),
                ProductName = card.Q<Label>("ProductNameLabel"),
                ShopName = card.Q<Label>("ShopNameLabel"),
            };
            _cardSlots[card] = slots;
            return slots;
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
