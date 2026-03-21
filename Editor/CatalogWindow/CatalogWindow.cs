using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using com.amari_noa.unity_editor_localization_core.editor;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class CatalogWindow : EditorWindow
    {
        [SerializeField] private VisualTreeAsset visualTreeAsset;
        private const string NotSetToken = "__NOT_SET__";
        private static readonly Vector2 InitialWindowSize = new Vector2(1320f, 840f);
        private static readonly Vector2 InitialWindowPosition = new Vector2(80f, 80f);
        private static readonly Vector2 MinimumWindowSize = new Vector2(960f, 800f);
        private static readonly Vector2 MaximumWindowSize = new Vector2(10000f, 10000f);
        private static FontAsset _catalogWindowFontAsset;

        private readonly AmariBlmDatabaseService _dbService = new AmariBlmDatabaseService();
        private readonly AmariBlmThumbnailCacheService _thumbnailCacheService = new AmariBlmThumbnailCacheService();
        private readonly Dictionary<string, HashSet<string>> _selectedByProduct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly List<string> _selectedOrder = new List<string>();
        private readonly HashSet<string> _selectedShops = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _selectedTags = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _categoryValues = new List<string>();
        private readonly List<string> _subCategoryValues = new List<string>();
        private readonly List<ShopEntry> _allShops = new List<ShopEntry>();
        private readonly List<ShopEntry> _visibleShops = new List<ShopEntry>();
        private readonly List<TagEntry> _allTags = new List<TagEntry>();
        private readonly List<TagEntry> _visibleTags = new List<TagEntry>();
        private readonly Dictionary<string, VisualElement> _visibleCardsByProductId = new Dictionary<string, VisualElement>(StringComparer.Ordinal);

        private AmariBlmPickerContext _context;
        private Action<AmariBlmImportBatchRequest> _onConfirmed;
        private AmariBlmDatabaseLoadResult _db = new AmariBlmDatabaseLoadResult();

        private DropdownField _displayModeField;
        private DropdownField _listSelectorField;
        private DropdownField _sortKeyField;
        private DropdownField _sortOrderField;
        private DropdownField _categoryFilterField;
        private DropdownField _subCategoryFilterField;
        private DropdownField _ageFilterField;
        private DropdownField _pageSizeField;
        private DropdownField _editorLanguageDropdownField;
        private TextField _searchField;
        private TextField _shopSearchField;
        private TextField _tagSearchField;
        private ListView _shopListView;
        private ListView _tagListView;
        private ListView _detailFileListView;
        private VisualElement _productGridContainer;
        private VisualElement _detailThumbnailImage;
        private ScrollView _selectedProductsScrollView;
        private IntegerField _thumbnailCacheMaxEntriesField;
        private Button _confirmButton;
        private Label _detailProductNameLabel;
        private Label _detailProductListLabel;
        private Label _detailFolderPathLabel;
        private Label _activeFilterCountLabel;
        private Label _listModeEmptyStateLabel;
        private VisualElement _contentRoot;
        private VisualElement _loadingOverlay;

        private AmariBlmItemRecord _detailItem;
        private List<AmariBlmFileRecord> _detailFiles = new List<AmariBlmFileRecord>();
        private List<AmariBlmItemRecord> _viewItems = new List<AmariBlmItemRecord>();
        private int _page = 1;
        private int _pageSize = AmariBlmConstants.DefaultPageSize;
        private bool _languageSubscribed;
        private bool _importSubscribed;
        private bool _suppressUiCallbacks;
        private string _standaloneBatchId = string.Empty;

        public static CatalogWindow Open(AmariBlmPickerContext context, Action<AmariBlmImportBatchRequest> onConfirmed)
        {
            if (context == null)
            {
                Debug.LogError("[BLM Integration Core] Picker context is null.");
                return null;
            }

            if (!context.ValidateRequiredServices(out var error))
            {
                Debug.LogError($"[BLM Integration Core] {error}");
                return null;
            }

            var hasOpenInstance = HasOpenInstances<CatalogWindow>();
            var window = GetWindow<CatalogWindow>(false, AmariBlmConstants.WindowTitle, true);
            window.ApplyWindowSizeConstraints();
            if (!hasOpenInstance)
            {
                window.position = new Rect(InitialWindowPosition, InitialWindowSize);
            }

            window._context = context;
            window._onConfirmed = onConfirmed;
            window.titleContent = new GUIContent(AmariBlmConstants.WindowTitle);
            window.Show();
            window.Focus();
            if (hasOpenInstance)
            {
                window.RefreshIfInitialized();
            }

            return window;
        }

        public static bool IsWindowOpen()
        {
            return HasOpenInstances<CatalogWindow>();
        }

        private void OnEnable()
        {
            ApplyWindowSizeConstraints();
        }

        private void ApplyWindowSizeConstraints()
        {
            minSize = MinimumWindowSize;
            maxSize = MaximumWindowSize;
        }

        public void CreateGUI()
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            PerfLog("CreateGUI start");

            var settingsStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _thumbnailCacheService.LoadSettingsFromEditorPrefs();
            settingsStopwatch.Stop();
            PerfLog($"CreateGUI step='LoadSettingsFromEditorPrefs' elapsedMs={settingsStopwatch.ElapsedMilliseconds}");

            var clearStopwatch = System.Diagnostics.Stopwatch.StartNew();
            rootVisualElement.Clear();
            clearStopwatch.Stop();
            PerfLog($"CreateGUI step='rootVisualElement.Clear' elapsedMs={clearStopwatch.ElapsedMilliseconds}");

            var loadUxmlStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var tree = visualTreeAsset;
            loadUxmlStopwatch.Stop();
            PerfLog($"CreateGUI step='ResolveVisualTreeAsset' elapsedMs={loadUxmlStopwatch.ElapsedMilliseconds}");
            if (tree == null)
            {
                rootVisualElement.Add(new Label("CatalogWindow VisualTreeAsset is not assigned."));
                totalStopwatch.Stop();
                PerfLog($"CreateGUI aborted in {totalStopwatch.ElapsedMilliseconds} ms. reason='VisualTreeAsset not assigned'");
                return;
            }

            var instantiateStopwatch = System.Diagnostics.Stopwatch.StartNew();
            rootVisualElement.Add(tree.Instantiate());
            instantiateStopwatch.Stop();
            PerfLog($"CreateGUI step='InstantiateUXML' elapsedMs={instantiateStopwatch.ElapsedMilliseconds}");

            var fontStopwatch = System.Diagnostics.Stopwatch.StartNew();
            ApplyPackageFont(rootVisualElement);
            fontStopwatch.Stop();
            PerfLog($"CreateGUI step='ApplyPackageFont' elapsedMs={fontStopwatch.ElapsedMilliseconds}");

            var bindStopwatch = System.Diagnostics.Stopwatch.StartNew();
            BindUi();
            bindStopwatch.Stop();
            PerfLog($"CreateGUI step='BindUi' elapsedMs={bindStopwatch.ElapsedMilliseconds}");

            var setupStopwatch = System.Diagnostics.Stopwatch.StartNew();
            SetupUi();
            setupStopwatch.Stop();
            PerfLog($"CreateGUI step='SetupUi' elapsedMs={setupStopwatch.ElapsedMilliseconds}");

            var localizationStopwatch = System.Diagnostics.Stopwatch.StartNew();
            ApplyLocalization();
            localizationStopwatch.Stop();
            PerfLog($"CreateGUI step='ApplyLocalization' elapsedMs={localizationStopwatch.ElapsedMilliseconds}");

            var reloadStopwatch = System.Diagnostics.Stopwatch.StartNew();
            ReloadDb(false);
            reloadStopwatch.Stop();
            PerfLog($"CreateGUI step='ReloadDb' elapsedMs={reloadStopwatch.ElapsedMilliseconds}");

            totalStopwatch.Stop();
            PerfLog($"CreateGUI completed in {totalStopwatch.ElapsedMilliseconds} ms");
        }

        private static void ApplyPackageFont(VisualElement root)
        {
            if (root == null)
            {
                return;
            }

            var fontAsset = ResolveCatalogWindowFontAsset();
            if (fontAsset == null)
            {
                Debug.LogWarning($"[BLM Integration Core] Catalog font asset not found: {AmariBlmConstants.CatalogWindowFontAssetPath}");
                return;
            }

            root.style.unityFontDefinition = FontDefinition.FromSDFFont(fontAsset);
        }

        private static FontAsset ResolveCatalogWindowFontAsset()
        {
            if (_catalogWindowFontAsset != null)
            {
                return _catalogWindowFontAsset;
            }

            var baseFontFile = AssetDatabase.LoadAssetAtPath<Font>(AmariBlmConstants.CatalogWindowFontFileAssetPath);
            if (baseFontFile != null)
            {
                var baseFontAsset = FontAsset.CreateFontAsset(baseFontFile);
                if (baseFontAsset != null)
                {
                    var fallbackAssets = new List<FontAsset>();
                    var emojiFontFile = AssetDatabase.LoadAssetAtPath<Font>(AmariBlmConstants.CatalogWindowEmojiFontFileAssetPath);
                    if (emojiFontFile != null)
                    {
                        var emojiFontAsset = FontAsset.CreateFontAsset(emojiFontFile);
                        if (emojiFontAsset != null)
                        {
                            fallbackAssets.Add(emojiFontAsset);
                        }
                    }

                    baseFontAsset.fallbackFontAssetTable = fallbackAssets;
                    _catalogWindowFontAsset = baseFontAsset;
                    return _catalogWindowFontAsset;
                }
            }

            _catalogWindowFontAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(AmariBlmConstants.CatalogWindowFontAssetPath);
            return _catalogWindowFontAsset;
        }

        private void RefreshIfInitialized()
        {
            if (_displayModeField == null ||
                _listSelectorField == null ||
                _sortKeyField == null ||
                _sortOrderField == null)
            {
                return;
            }

            ApplyLocalization();
            ReloadDb(false);
        }

        private void OnDisable()
        {
            if (_languageSubscribed)
            {
                EditorLocalization.Service.LanguageChanged -= OnLanguageChanged;
                _languageSubscribed = false;
            }

            if (_importSubscribed)
            {
                AmariBlmImportProcessor.Shared.ImportBatchCompleted -= OnImportBatchCompleted;
                _importSubscribed = false;
            }
        }

        private void BindUi()
        {
            _displayModeField = rootVisualElement.Q<DropdownField>("DisplayModeField");
            _contentRoot = rootVisualElement.childCount > 0 ? rootVisualElement[0] : null;
            _loadingOverlay = rootVisualElement.Q<VisualElement>("LoadingOverlay");
            _listSelectorField = rootVisualElement.Q<DropdownField>("ListSelectorField");
            _sortKeyField = rootVisualElement.Q<DropdownField>("SortKeyField");
            _sortOrderField = rootVisualElement.Q<DropdownField>("SortOrderField");
            _categoryFilterField = rootVisualElement.Q<DropdownField>("CategoryFilterField");
            _subCategoryFilterField = rootVisualElement.Q<DropdownField>("SubCategoryFilterField");
            _ageFilterField = rootVisualElement.Q<DropdownField>("AgeRestrictionFilterField");
            _pageSizeField = rootVisualElement.Q<DropdownField>("PageSizeField");
            _editorLanguageDropdownField = rootVisualElement.Q<DropdownField>("EditorLanguageDropdownField");
            _searchField = rootVisualElement.Q<TextField>("SearchTextField");
            _shopSearchField = rootVisualElement.Q<TextField>("ShopSearchField");
            _tagSearchField = rootVisualElement.Q<TextField>("TagSearchField");
            _shopListView = rootVisualElement.Q<ListView>("ShopFilterListView");
            _tagListView = rootVisualElement.Q<ListView>("TagFilterListView");
            _detailFileListView = rootVisualElement.Q<ListView>("DetailFileListView");
            _productGridContainer = rootVisualElement.Q<VisualElement>("ProductGridContainer");
            _detailThumbnailImage = rootVisualElement.Q<VisualElement>("DetailThumbnailImage");
            _selectedProductsScrollView = rootVisualElement.Q<ScrollView>("SelectedProductsScrollView");
            _thumbnailCacheMaxEntriesField = rootVisualElement.Q<IntegerField>("ThumbnailCacheMaxEntriesField");
            _confirmButton = rootVisualElement.Q<Button>("ConfirmSelectionButton");
            _detailProductNameLabel = rootVisualElement.Q<Label>("DetailProductNameLabel");
            _detailProductListLabel = rootVisualElement.Q<Label>("DetailProductListLabel");
            _detailFolderPathLabel = rootVisualElement.Q<Label>("DetailFolderPathLabel");
            _activeFilterCountLabel = rootVisualElement.Q<Label>("ActiveFilterCountLabel");
            _listModeEmptyStateLabel = rootVisualElement.Q<Label>("ListModeEmptyStateLabel");
        }

        private void SetupUi()
        {
            _displayModeField.choices = new List<string> { "All", "ByList" };
            _displayModeField.index = 0;
            _sortKeyField.choices = new List<string> { "ProductName", "ShopName", "RegisteredAt", "PublishedAt" };
            _sortKeyField.index = 0;
            _sortOrderField.choices = new List<string> { "Ascending", "Descending" };
            _sortOrderField.index = 0;
            _ageFilterField.choices = new List<string> { "Any", "AllAges", "R18" };
            _ageFilterField.index = 0;
            _pageSizeField.choices = AmariBlmConstants.PageSizes.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
            var defaultPageSizeIndex = Array.IndexOf(AmariBlmConstants.PageSizes, 100);
            if (defaultPageSizeIndex < 0)
            {
                defaultPageSizeIndex = 0;
            }

            _pageSizeField.index = defaultPageSizeIndex;
            if (!int.TryParse(_pageSizeField.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _pageSize))
            {
                _pageSize = AmariBlmConstants.PageSizes[Mathf.Clamp(defaultPageSizeIndex, 0, AmariBlmConstants.PageSizes.Length - 1)];
            }

            SyncEditorLanguageDropdownChoices();
            _thumbnailCacheMaxEntriesField.SetValueWithoutNotify(_thumbnailCacheService.MaxEntries);
            _searchField.value = string.Empty;
            _searchField.pickingMode = PickingMode.Position;
            _shopSearchField.value = string.Empty;
            _shopSearchField.pickingMode = PickingMode.Position;
            _tagSearchField.value = string.Empty;
            _tagSearchField.pickingMode = PickingMode.Position;
            _loadingOverlay.style.display = DisplayStyle.None;
            _detailThumbnailImage.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
            _detailThumbnailImage.style.flexGrow = 0f;
            _detailThumbnailImage.style.flexShrink = 0f;
            _detailThumbnailImage.style.alignSelf = Align.Center;
            _detailThumbnailImage.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 1f);
            _detailThumbnailImage.RegisterCallback<GeometryChangedEvent>(_ => UpdateDetailThumbnailImageSize());
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(_ => UpdateDetailThumbnailImageSize());
            UpdateDetailThumbnailImageSize();

            _shopListView.makeItem = () =>
            {
                var toggle = new Toggle();
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (toggle.userData is string shop)
                    {
                        if (evt.newValue) _selectedShops.Add(shop);
                        else _selectedShops.Remove(shop);
                        PerfLog(
                            $"ShopFilter selection changed. " +
                            $"shop='{SanitizeForLog(shop)}', selected={evt.newValue.ToString().ToLowerInvariant()}, selectedCount={_selectedShops.Count}");
                        ApplyFilter(true);
                    }
                });
                return toggle;
            };
            _shopListView.bindItem = (element, i) =>
            {
                var toggle = (Toggle)element;
                var shop = _visibleShops[i];
                toggle.userData = shop.Shop;
                toggle.text = $"{shop.Shop} ({shop.Count.ToString(CultureInfo.InvariantCulture)})";
                toggle.SetValueWithoutNotify(_selectedShops.Contains(shop.Shop));
            };
            _shopListView.selectionType = SelectionType.None;

            _tagListView.makeItem = () =>
            {
                var toggle = new Toggle();
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (toggle.userData is string tag)
                    {
                        if (evt.newValue) _selectedTags.Add(tag);
                        else _selectedTags.Remove(tag);
                        ApplyFilter(true);
                    }
                });
                return toggle;
            };
            _tagListView.bindItem = (element, i) =>
            {
                var toggle = (Toggle)element;
                var tag = _visibleTags[i];
                toggle.userData = tag.Tag;
                toggle.text = $"{tag.Tag} ({tag.Count.ToString(CultureInfo.InvariantCulture)})";
                toggle.SetValueWithoutNotify(_selectedTags.Contains(tag.Tag));
            };
            _tagListView.selectionType = SelectionType.None;

            _detailFileListView.makeItem = () =>
            {
                var row = new VisualElement();
                row.AddToClassList("blm-file-row");
                var toggle = new Toggle { name = "Toggle" };
                var label = new Label { name = "Label" };
                label.AddToClassList("blm-file-row-label");
                row.Add(toggle);
                row.Add(label);
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (toggle.userData is AmariBlmFileRecord file && _detailItem != null)
                    {
                        SetSelected(_detailItem, file.FullPath, evt.newValue);
                        _detailFileListView.Rebuild();
                        RebuildSelectedPanel();
                        UpdateConfirmButtonState();
                    }
                });
                return row;
            };
            _detailFileListView.bindItem = (element, i) =>
            {
                var row = (VisualElement)element;
                var toggle = row.Q<Toggle>("Toggle");
                var label = row.Q<Label>("Label");
                var file = _detailFiles[i];
                toggle.userData = file;
                toggle.SetValueWithoutNotify(IsSelected(_detailItem?.ProductId, file.FullPath));
                label.text = file.FileName;
            };
            _detailFileListView.selectionType = SelectionType.None;

            _displayModeField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                UpdateListModeState();
                ApplyFilter(true);
            });
            _listSelectorField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                ApplyFilter(true);
            });
            _sortKeyField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                ApplyFilter(true);
            });
            _sortOrderField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                ApplyFilter(true);
            });
            _categoryFilterField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                RebuildSubCategoryChoices();
                ApplyFilter(true);
            });
            _subCategoryFilterField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                ApplyFilter(true);
            });
            _ageFilterField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                ApplyFilter(true);
            });
            _searchField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                ApplyFilter(true);
            });
            _shopSearchField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                PerfLog($"ShopFilter query changed. query='{SanitizeForLog((_shopSearchField.value ?? string.Empty).Trim())}'");
                RebuildVisibleShops();
            });
            _tagSearchField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                RebuildVisibleTags();
            });
            _pageSizeField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                if (int.TryParse(_pageSizeField.value, out var parsed)) _pageSize = parsed;
                ApplyFilter(true);
            });
            _editorLanguageDropdownField?.RegisterValueChangedCallback(evt =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                var service = EditorLocalization.Service;

                if (!_editorLanguageDropdownField.choices.Contains(evt.newValue))
                {
                    _editorLanguageDropdownField.SetValueWithoutNotify(service.CurrentLanguageCode);
                    return;
                }

                var result = service.SetLanguage(AmariBlmConstants.LocalizationSourceId, evt.newValue);
                if (result is EditorLocalizationSetLanguageResult.FAILED or EditorLocalizationSetLanguageResult.NOT_REGISTERED)
                {
                    _editorLanguageDropdownField.SetValueWithoutNotify(service.CurrentLanguageCode);
                    return;
                }

                _editorLanguageDropdownField.SetValueWithoutNotify(service.CurrentLanguageCode);
            });
            _thumbnailCacheMaxEntriesField.RegisterValueChangedCallback(evt =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                _thumbnailCacheMaxEntriesField.SetValueWithoutNotify(_thumbnailCacheService.SetMaxEntries(evt.newValue));
            });
            rootVisualElement.Q<Button>("RefreshDbButton").clicked += () => ReloadDb(true);
            rootVisualElement.Q<Button>("ShopSearchFieldClearButton").clicked += () =>
            {
                _shopSearchField.value = string.Empty;
                RebuildVisibleShops();
                PerfLog("ShopFilter query cleared.");
            };
            rootVisualElement.Q<Button>("ShopClearButton").clicked += () =>
            {
                var before = _selectedShops.Count;
                _selectedShops.Clear();
                RebuildVisibleShops();
                PerfLog($"ShopFilter selections cleared. before={before}, after={_selectedShops.Count}");
                ApplyFilter(true);
            };
            rootVisualElement.Q<Button>("TagSearchFieldClearButton").clicked += () =>
            {
                _tagSearchField.value = string.Empty;
                RebuildVisibleTags();
            };
            rootVisualElement.Q<Button>("TagClearButton").clicked += () =>
            {
                _selectedTags.Clear();
                RebuildVisibleTags();
                ApplyFilter(true);
            };
            rootVisualElement.Q<Button>("CancelButton").clicked += Close;
            var deSelectAllFilesButton = rootVisualElement.Q<Button>("DeSelectAllFilesButton");
            if (deSelectAllFilesButton != null)
            {
                deSelectAllFilesButton.clicked += () => SetAllFilesSelection(false);
            }

            rootVisualElement.Q<Button>("SelectAllFilesButton").clicked += () => SetAllFilesSelection(true);
            _confirmButton.clicked += OnConfirmClicked;
        }

        private void UpdateDetailThumbnailImageSize()
        {
            if (_detailThumbnailImage == null)
            {
                return;
            }

            var parent = _detailThumbnailImage.parent;
            if (parent == null)
            {
                return;
            }

            var availableWidth = parent.resolvedStyle.width - 8f;
            if (availableWidth <= 0f || float.IsNaN(availableWidth))
            {
                return;
            }

            var side = Mathf.Clamp(availableWidth, 120f, 220f);
            _detailThumbnailImage.style.width = side;
            _detailThumbnailImage.style.height = side;
        }

        private void ReloadDb(bool forceClearCaches)
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            PerfLog("ReloadDb start");
            SetLoading(true);

            var clearResolverStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (forceClearCaches)
            {
                _dbService.ClearCaches();
            }

            clearResolverStopwatch.Stop();
            PerfLog($"ReloadDb step='DatabaseService.ClearCaches' elapsedMs={clearResolverStopwatch.ElapsedMilliseconds}, executed={forceClearCaches.ToString().ToLowerInvariant()}");

            var cleanupThumbCacheStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _thumbnailCacheService.CleanupExpiredCacheFiles();
            cleanupThumbCacheStopwatch.Stop();
            PerfLog($"ReloadDb step='ThumbnailCache.CleanupExpiredCacheFiles' elapsedMs={cleanupThumbCacheStopwatch.ElapsedMilliseconds}");

            var loadDbStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _db = _dbService.Load();
            loadDbStopwatch.Stop();
            PerfLog($"ReloadDb step='DatabaseService.Load' elapsedMs={loadDbStopwatch.ElapsedMilliseconds}");
            if (_db.HasError)
            {
                Debug.LogError($"[BLM Integration Core] {_db.ErrorMessage}");
            }

            var rebuildUiStopwatch = System.Diagnostics.Stopwatch.StartNew();
            WithSuppressedUiCallbacks(() =>
            {
                _selectedByProduct.Clear();
                _selectedOrder.Clear();
                _selectedShops.Clear();
                _selectedTags.Clear();
                _listSelectorField.choices = _db.Lists.Select(l => string.IsNullOrWhiteSpace(l.Title) ? l.Id.ToString(CultureInfo.InvariantCulture) : l.Title).ToList();
                _listSelectorField.index = _listSelectorField.choices.Count > 0 ? 0 : -1;
                RebuildCategoryChoices();
                RebuildSubCategoryChoices();
                RebuildShopChoices();
                RebuildVisibleShops();
                RebuildTagChoices();
                RebuildVisibleTags();
                UpdateListModeState();
            });
            PerfLog(
                $"ReloadDb filter caches rebuilt. " +
                $"shopCandidates={_allShops.Count}, visibleShops={_visibleShops.Count}, tagCandidates={_allTags.Count}, visibleTags={_visibleTags.Count}");
            ApplyFilter(true);
            SetLoading(false);
            rebuildUiStopwatch.Stop();
            totalStopwatch.Stop();
            PerfLog(
                $"ReloadDb completed in {totalStopwatch.ElapsedMilliseconds} ms. " +
                $"uiRefreshElapsedMs={rebuildUiStopwatch.ElapsedMilliseconds}, itemCount={_db.Items.Count}, listCount={_db.Lists.Count}, hasError={_db.HasError.ToString().ToLowerInvariant()}");
        }

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

            var rebuildGridStopwatch = System.Diagnostics.Stopwatch.StartNew();
            RebuildGrid();
            rebuildGridStopwatch.Stop();

            var updateCountStopwatch = System.Diagnostics.Stopwatch.StartNew();
            UpdateFilterCount();
            updateCountStopwatch.Stop();

            totalStopwatch.Stop();
            PerfLog(
                $"ApplyFilter completed in {totalStopwatch.ElapsedMilliseconds} ms. " +
                $"resetPage={resetPage.ToString().ToLowerInvariant()}, baseSetCount={baseSetCount}, afterBaseFilters={working.Count}, afterRank={_viewItems.Count}, " +
                $"baseFilterElapsedMs={baseFilterStopwatch.ElapsedMilliseconds}, rankElapsedMs={rankStopwatch.ElapsedMilliseconds}, " +
                $"rebuildGridElapsedMs={rebuildGridStopwatch.ElapsedMilliseconds}, updateCountElapsedMs={updateCountStopwatch.ElapsedMilliseconds}, " +
                $"selectedShops={_selectedShops.Count}, selectedTags={_selectedTags.Count}, search='{SanitizeForLog((_searchField.value ?? string.Empty).Trim())}'");
        }

        private List<AmariBlmItemRecord> ResolveBaseSet()
        {
            if (_displayModeField.index != 1 || _listSelectorField.index < 0 || _listSelectorField.index >= _db.Lists.Count)
            {
                return _db.Items.ToList();
            }

            var listId = _db.Lists[_listSelectorField.index].Id;
            if (!_db.ListProductIdsByListId.TryGetValue(listId, out var ids))
            {
                return new List<AmariBlmItemRecord>();
            }

            return _db.Items.Where(i => ids.Contains(i.ProductId)).ToList();
        }

        private List<AmariBlmItemRecord> ApplyBaseFilters(IEnumerable<AmariBlmItemRecord> source)
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
            if (_ageFilterField.index == 1) list = list.Where(i => i.AgeRestriction == AmariBlmAgeRestriction.AllAges).ToList();
            else if (_ageFilterField.index == 2) list = list.Where(i => i.AgeRestriction == AmariBlmAgeRestriction.R18).ToList();
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
            IEnumerable<AmariBlmItemRecord> items = _db.Items;
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

        private void RebuildShopChoices()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _allShops.Clear();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in _db.Items)
            {
                var shop = item.ShopName;
                if (string.IsNullOrWhiteSpace(shop))
                {
                    continue;
                }

                if (!counts.ContainsKey(shop)) counts[shop] = 0;
                counts[shop]++;
            }

            _allShops.AddRange(counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => new ShopEntry(x.Key, x.Value)));
            stopwatch.Stop();
            PerfLog(
                $"RebuildShopChoices completed in {stopwatch.ElapsedMilliseconds} ms. " +
                $"items={_db.Items.Count}, uniqueShops={_allShops.Count}");
        }

        private void RebuildVisibleShops()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _visibleShops.Clear();
            var query = (_shopSearchField.value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                _visibleShops.AddRange(_allShops.Take(AmariBlmConstants.TagInitialTopCount));
            }
            else
            {
                var normalized = AmariBlmProductFolderResolver.NormalizeForComparison(query);
                _visibleShops.AddRange(_allShops.Where(x => AmariBlmProductFolderResolver.NormalizeForComparison(x.Shop).Contains(normalized)));
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

        private void RebuildTagChoices()
        {
            _allTags.Clear();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in _db.Items)
            {
                foreach (var tag in item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    if (!counts.ContainsKey(tag)) counts[tag] = 0;
                    counts[tag]++;
                }
            }
            _allTags.AddRange(counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => new TagEntry(x.Key, x.Value)));
        }

        private void RebuildVisibleTags()
        {
            _visibleTags.Clear();
            var query = (_tagSearchField.value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                _visibleTags.AddRange(_allTags.Take(AmariBlmConstants.TagInitialTopCount));
            }
            else
            {
                var normalized = AmariBlmProductFolderResolver.NormalizeForComparison(query);
                _visibleTags.AddRange(_allTags.Where(x => AmariBlmProductFolderResolver.NormalizeForComparison(x.Tag).Contains(normalized)));
            }
            _tagListView.itemsSource = _visibleTags;
            _tagListView.Rebuild();
            if (_visibleTags.Count > 0)
            {
                _tagListView.ScrollToItem(0);
            }
        }

        private string SelectedCategoryValue()
        {
            return _categoryFilterField.index >= 0 && _categoryFilterField.index < _categoryValues.Count ? _categoryValues[_categoryFilterField.index] : string.Empty;
        }

        private string SelectedSubCategoryValue()
        {
            return _subCategoryFilterField.index >= 0 && _subCategoryFilterField.index < _subCategoryValues.Count ? _subCategoryValues[_subCategoryFilterField.index] : string.Empty;
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

        private List<SearchHit> Rank(IEnumerable<AmariBlmItemRecord> source)
        {
            var query = AmariBlmProductFolderResolver.NormalizeForComparison((_searchField.value ?? string.Empty).Trim());
            var hits = new List<SearchHit>();
            foreach (var item in source)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    hits.Add(new SearchHit(item, 0));
                    continue;
                }

                var pn = AmariBlmProductFolderResolver.NormalizeForComparison(item.ProductName);
                var sn = AmariBlmProductFolderResolver.NormalizeForComparison(item.ShopName);
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

        private void RebuildGrid()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var total = TotalPages();
            _page = Mathf.Clamp(_page, 1, total);
            var pageItems = _viewItems.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();
            _productGridContainer.Clear();
            _visibleCardsByProductId.Clear();
            foreach (var item in pageItems)
            {
                var card = BuildCard(item);
                _productGridContainer.Add(card);
                if (!string.IsNullOrWhiteSpace(item.ProductId))
                {
                    _visibleCardsByProductId[item.ProductId] = card;
                }
                _ = _thumbnailCacheService.GetTextureAsync(item).ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully || task.Result == null) return;
                    EditorApplication.delayCall += () => card.Q<VisualElement>("Thumb").style.backgroundImage = new StyleBackground(task.Result);
                });
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
                $"page={_page}/{total}, pageSize={_pageSize}, renderedItems={pageItems.Count}, totalFilteredItems={_viewItems.Count}");
        }

        private VisualElement BuildCard(AmariBlmItemRecord item)
        {
            var card = new VisualElement();
            card.AddToClassList("blm-product-card");
            if (_detailItem != null && _detailItem.ProductId == item.ProductId) card.AddToClassList("blm-product-card-selected");
            var thumb = new VisualElement { name = "Thumb" };
            thumb.AddToClassList("blm-product-card-thumbnail");
            card.Add(thumb);
            var pn = new Label(item.ProductName);
            pn.AddToClassList("blm-product-card-product-name");
            card.Add(pn);
            var sn = new Label(item.ShopName);
            sn.AddToClassList("blm-product-card-shop-name");
            card.Add(sn);
            card.RegisterCallback<ClickEvent>(_ =>
            {
                SelectDetailItem(item);
            });
            return card;
        }

        private void SelectDetailItem(AmariBlmItemRecord item)
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

        private void ShowDetail(AmariBlmItemRecord item)
        {
            if (item == null)
            {
                _detailProductNameLabel.text = L("blm.detail.product_name", "Product name");
                _detailFolderPathLabel.text = L("blm.detail.folder_path", "Folder path");
                _detailFiles = new List<AmariBlmFileRecord>();
                _detailFileListView.itemsSource = _detailFiles;
                _detailFileListView.Rebuild();
                _detailThumbnailImage.style.backgroundImage = StyleKeyword.Null;
                return;
            }

            EnsureItemFilesLoaded(item);
            _detailProductNameLabel.text = item.ProductName;
            _detailFolderPathLabel.text = item.RootFolderPath;
            _detailFiles = item.Files.OrderBy(f => ExtensionPriority(f.FileExtension)).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            _detailFileListView.itemsSource = _detailFiles;
            _detailFileListView.Rebuild();
            _ = _thumbnailCacheService.GetTextureAsync(item).ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully || task.Result == null) return;
                EditorApplication.delayCall += () => _detailThumbnailImage.style.backgroundImage = new StyleBackground(task.Result);
            });
        }

        private void EnsureItemFilesLoaded(AmariBlmItemRecord item)
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

        private string ResolveRootFolderPathForDetail(AmariBlmItemRecord item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(item.RootFolderPath) && Directory.Exists(item.RootFolderPath))
            {
                return item.RootFolderPath;
            }

            var libraryRootPath = _db?.LibraryRootPath;
            if (string.IsNullOrWhiteSpace(libraryRootPath) || string.IsNullOrWhiteSpace(item.ProductId))
            {
                return string.Empty;
            }

            foreach (var candidate in EnumerateRootFolderCandidates(libraryRootPath, item.ProductId, item.ShopSubdomain))
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

        private static List<AmariBlmFileRecord> LoadFilesForDetail(string rootFolderPath)
        {
            var files = new List<AmariBlmFileRecord>();
            if (string.IsNullOrWhiteSpace(rootFolderPath) || !Directory.Exists(rootFolderPath))
            {
                return files;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(rootFolderPath, "*", SearchOption.AllDirectories))
                {
                    files.Add(new AmariBlmFileRecord
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

        private void SetSelected(AmariBlmItemRecord item, string filePath, bool selected)
        {
            if (!_selectedByProduct.TryGetValue(item.ProductId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _selectedByProduct[item.ProductId] = set;
            }

            if (selected)
            {
                set.Add(filePath);
                if (!_selectedOrder.Contains(item.ProductId)) _selectedOrder.Add(item.ProductId);
            }
            else
            {
                set.Remove(filePath);
                if (set.Count == 0)
                {
                    _selectedByProduct.Remove(item.ProductId);
                    _selectedOrder.Remove(item.ProductId);
                }
            }
        }

        private void SetAllFilesSelection(bool selected)
        {
            if (_detailItem == null)
            {
                return;
            }

            EnsureItemFilesLoaded(_detailItem);
            foreach (var file in _detailItem.Files)
            {
                SetSelected(_detailItem, file.FullPath, selected);
            }

            _detailFileListView.Rebuild();
            RebuildSelectedPanel();
            UpdateConfirmButtonState();
        }

        private void RebuildSelectedPanel()
        {
            _selectedProductsScrollView.Clear();
            foreach (var productId in _selectedOrder.ToList())
            {
                if (!_selectedByProduct.TryGetValue(productId, out var selected) || selected.Count == 0)
                {
                    _selectedOrder.Remove(productId);
                    continue;
                }

                var item = _db.Items.FirstOrDefault(x => x.ProductId == productId);
                if (item == null) continue;
                var fold = new Foldout { text = $"{item.ProductName} / {item.ShopName}" };
                foreach (var file in item.Files.OrderBy(f => ExtensionPriority(f.FileExtension)).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    var row = new VisualElement();
                    row.AddToClassList("blm-file-row");
                    var toggle = new Toggle();
                    toggle.userData = file;
                    toggle.SetValueWithoutNotify(selected.Contains(file.FullPath));
                    toggle.RegisterValueChangedCallback(evt =>
                    {
                        if (toggle.userData is AmariBlmFileRecord rf)
                        {
                            SetSelected(item, rf.FullPath, evt.newValue);
                            RebuildSelectedPanel();
                            if (_detailItem != null && _detailItem.ProductId == item.ProductId) _detailFileListView.Rebuild();
                            UpdateConfirmButtonState();
                        }
                    });
                    var label = new Label(file.FileName);
                    label.AddToClassList("blm-file-row-label");
                    row.Add(toggle);
                    row.Add(label);
                    fold.Add(row);
                }
                _selectedProductsScrollView.Add(fold);
            }
        }

        private void OnConfirmClicked()
        {
            var batch = BuildBatch();
            if (batch.Items.Count == 0) return;
            if (_context.InvocationContext == AmariBlmInvocationContext.Integration)
            {
                _onConfirmed?.Invoke(batch);
                Close();
                return;
            }

            if (!_importSubscribed)
            {
                AmariBlmImportProcessor.Shared.ImportBatchCompleted += OnImportBatchCompleted;
                _importSubscribed = true;
            }

            var pipelineService = _context?.UnityPackageImportPipelineService;
            if (pipelineService != null && (pipelineService.RemainingCount > 0 || pipelineService.IsImporting))
            {
                PerfLog($"OnConfirmClicked resetting UPPC pipeline. remainingBefore={pipelineService.RemainingCount}, isImporting={pipelineService.IsImporting.ToString().ToLowerInvariant()}");
                pipelineService.ResetPipelineAndClearQueue();
                PerfLog($"OnConfirmClicked reset UPPC pipeline. remainingAfter={pipelineService.RemainingCount}, isImporting={pipelineService.IsImporting.ToString().ToLowerInvariant()}");
            }

            _standaloneBatchId = batch.BatchId;
            AmariBlmImportProcessor.Shared.Execute(batch, _context);
        }

        private void OnImportBatchCompleted(AmariBlmImportBatchResultContext result)
        {
            if (result == null || !string.Equals(result.BatchId, _standaloneBatchId, StringComparison.Ordinal))
            {
                return;
            }

            if (_context == null || _context.InvocationContext != AmariBlmInvocationContext.Standalone)
            {
                return;
            }

            if (result.ImportStatus == AmariUnityPackagePipelineOperationStatus.Completed)
            {
                _standaloneBatchId = string.Empty;
                EditorUtility.DisplayDialog(
                    L("blm.import.completed.title", "BLM Window"),
                    L("blm.import.completed.message", "Import completed."),
                    "OK");
                return;
            }

            _standaloneBatchId = string.Empty;
            EditorUtility.DisplayDialog(L("blm.window.title", "BLM Window"), string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Import failed." : result.ErrorMessage, "OK");
        }

        private AmariBlmImportBatchRequest BuildBatch()
        {
            var batch = new AmariBlmImportBatchRequest
            {
                BatchId = Guid.NewGuid().ToString("N"),
                InvocationContext = _context.InvocationContext,
                Items = new List<AmariBlmImportRequestItem>()
            };

            foreach (var productId in _selectedOrder)
            {
                if (!_selectedByProduct.TryGetValue(productId, out var selected) || selected.Count == 0) continue;
                var item = _db.Items.FirstOrDefault(x => x.ProductId == productId);
                if (item == null) continue;
                foreach (var file in item.Files.Where(f => selected.Contains(f.FullPath)).OrderBy(f => ExtensionPriority(f.FileExtension)).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    batch.Items.Add(new AmariBlmImportRequestItem
                    {
                        ProductId = item.ProductId,
                        ProductName = item.ProductName,
                        ShopName = item.ShopName,
                        SourcePath = file.FullPath,
                        RootFolderPath = item.RootFolderPath,
                        DestinationAssetPaths = new List<string>(),
                        Tags = BuildTags(item.ProductId)
                    });
                }
            }

            return batch;
        }

        private static List<string> BuildTags(string productId)
        {
            var tags = new List<string> { "AMARI_BLM" };
            if (!string.IsNullOrWhiteSpace(productId)) tags.Add($"AMARI_BLM_P{productId}");
            return tags;
        }

        private int ExtensionPriority(string extension)
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();
            for (var i = 0; i < _context.PreferredDisplayExtensions.Count; i++)
            {
                if (_context.PreferredDisplayExtensions[i] == ext) return i;
            }
            return int.MaxValue;
        }

        private void OnPrevPageClicked()
        {
            if (_page <= 1) return;
            _page--;
            RebuildGrid();
        }

        private void OnNextPageClicked()
        {
            if (_page >= TotalPages()) return;
            _page++;
            RebuildGrid();
        }

        private int TotalPages()
        {
            return Mathf.Max(1, Mathf.CeilToInt(_viewItems.Count / (float)_pageSize));
        }

        private int CompareSort(AmariBlmItemRecord a, AmariBlmItemRecord b)
        {
            var asc = _sortOrderField.index == 0;
            switch (_sortKeyField.index)
            {
                case 1:
                    return asc ? string.Compare(a.ShopName, b.ShopName, StringComparison.OrdinalIgnoreCase) : string.Compare(b.ShopName, a.ShopName, StringComparison.OrdinalIgnoreCase);
                case 2:
                    return CompareDate(a.RegisteredAt, b.RegisteredAt, asc);
                case 3:
                    return CompareDate(a.PublishedAt, b.PublishedAt, asc);
                default:
                    return asc ? string.Compare(a.ProductName, b.ProductName, StringComparison.OrdinalIgnoreCase) : string.Compare(b.ProductName, a.ProductName, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static int CompareDate(DateTime? a, DateTime? b, bool asc)
        {
            if (!a.HasValue && !b.HasValue) return 0;
            if (!a.HasValue) return 1;
            if (!b.HasValue) return -1;
            var cmp = DateTime.Compare(a.Value, b.Value);
            return asc ? cmp : -cmp;
        }

        private void UpdateConfirmButtonState()
        {
            _confirmButton.SetEnabled(_selectedByProduct.Values.Any(x => x.Count > 0));
        }

        private void UpdateFilterCount()
        {
            var count = 0;
            if (!string.IsNullOrWhiteSpace(SelectedCategoryValue())) count++;
            if (!string.IsNullOrWhiteSpace(SelectedSubCategoryValue())) count++;
            if (_ageFilterField.index > 0) count++;
            count += _selectedShops.Count;
            count += _selectedTags.Count;
            _activeFilterCountLabel.text = string.Format(
                CultureInfo.InvariantCulture,
                L("blm.filter.applied_count", "{0} filter(s) active"),
                count);
        }

        private bool IsSelected(string productId, string path)
        {
            return !string.IsNullOrWhiteSpace(productId) &&
                   !string.IsNullOrWhiteSpace(path) &&
                   _selectedByProduct.TryGetValue(productId, out var set) &&
                   set.Contains(path);
        }

        private void ApplyLocalization()
        {
            titleContent = new GUIContent(L("blm.window.title", AmariBlmConstants.WindowTitle));
            WithSuppressedUiCallbacks(() =>
            {
                ReplaceChoicesPreservingIndex(
                    _displayModeField,
                    new List<string> { L("blm.display_mode.all", "All Purchased"), L("blm.display_mode.by_list", "BLM List") });
                ReplaceChoicesPreservingIndex(
                    _sortKeyField,
                    new List<string>
                {
                    L("blm.sort.product_name", "Product Name"),
                    L("blm.sort.shop_name", "Shop Name"),
                    L("blm.sort.registered_at", "Registered At"),
                    L("blm.sort.published_at", "Published At")
                });
                ReplaceChoicesPreservingIndex(
                    _sortOrderField,
                    new List<string> { L("blm.sort.asc", "Ascending"), L("blm.sort.desc", "Descending") });
                ReplaceChoicesPreservingIndex(
                    _ageFilterField,
                    new List<string>
                {
                    L("blm.filter.age.any", "Any"),
                    L("blm.filter.age.all_ages", "All Ages Only"),
                    L("blm.filter.age.r18", "R-18 Only")
                });
                SyncEditorLanguageDropdownChoices();
            });
            if (_editorLanguageDropdownField != null)
            {
                _editorLanguageDropdownField.label = L("blm.editor_language.label", "Language");
            }
            _searchField.label = L("blm.search.label", "Search");
            _pageSizeField.label = L("blm.page_size", "Page size");
            rootVisualElement.Q<Label>("SortLabel").text = L("blm.sort.label", "Sort");
            rootVisualElement.Q<Label>("CategoryFilterLabel").text = L("blm.filter.category", "Category Filter");
            rootVisualElement.Q<Label>("AgeRestrictionFilterLabel").text = L("blm.filter.age.label", "Age Restriction Filter");
            rootVisualElement.Q<Label>("ShopFilterLabel").text = L("blm.filter.shop", "Shop Filter");
            rootVisualElement.Q<Label>("TagFilterLabel").text = L("blm.filter.tag", "Tag Filter");
            rootVisualElement.Q<Label>("SelectedItemsTitleLabel").text = L("blm.selected_items", "Selected Items");
            rootVisualElement.Q<Button>("ShopClearButton").text = L("blm.filter.shop_clear", "Clear selected shop(s)");
            rootVisualElement.Q<Button>("TagClearButton").text = L("blm.filter.tag_clear", "Clear selected tag(s)");
            rootVisualElement.Q<Button>("RefreshDbButton").text = L("blm.reload_db", "ReloadDB");
            var deSelectAllFilesButton = rootVisualElement.Q<Button>("DeSelectAllFilesButton");
            if (deSelectAllFilesButton != null)
            {
                deSelectAllFilesButton.text = L("blm.detail.deselect_all_files", "DeSelect all file(s)");
            }

            rootVisualElement.Q<Button>("SelectAllFilesButton").text = L("blm.detail.select_all_files", "Select all file(s)");
            rootVisualElement.Q<Button>("CancelButton").text = L("blm.button.cancel", "Cancel");
            _confirmButton.text = L("blm.button.import", "Import");
            _detailProductListLabel.text = L("blm.detail.product_files", "Product file(s)");
            rootVisualElement.Q<Label>("LoadingLabel").text = L("blm.loading", "Loading...");
            _listModeEmptyStateLabel.text = L("blm.list.empty", "No list is available.");

            if (!_languageSubscribed)
            {
                EditorLocalization.Service.LanguageChanged += OnLanguageChanged;
                _languageSubscribed = true;
            }
        }

        private void OnLanguageChanged(string _)
        {
            ApplyLocalization();
            UpdateListModeState();
            ApplyFilter(false);
            RebuildSelectedPanel();
            ShowDetail(_detailItem);
        }

        private string L(string key, string fallback)
        {
            return EditorLocalization.Service.Get(AmariBlmConstants.LocalizationSourceId, key, fallback);
        }

        private void SyncEditorLanguageDropdownChoices()
        {
            if (_editorLanguageDropdownField == null)
            {
                return;
            }

            var service = EditorLocalization.Service;
            var choices = new List<string>(service.GetAvailableLanguages(AmariBlmConstants.LocalizationSourceId));
            _editorLanguageDropdownField.choices = choices;

            var currentLanguageCode = service.CurrentLanguageCode;
            if (choices.Contains(currentLanguageCode))
            {
                _editorLanguageDropdownField.SetValueWithoutNotify(currentLanguageCode);
                return;
            }

            if (choices.Count > 0)
            {
                _editorLanguageDropdownField.SetValueWithoutNotify(choices[0]);
                return;
            }

            _editorLanguageDropdownField.SetValueWithoutNotify(currentLanguageCode);
        }
        private void SetLoading(bool loading)
        {
            if (_contentRoot != null)
            {
                _contentRoot.SetEnabled(!loading);
            }

            if (_loadingOverlay != null)
            {
                _loadingOverlay.style.display = loading ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void WithSuppressedUiCallbacks(Action action)
        {
            var previous = _suppressUiCallbacks;
            _suppressUiCallbacks = true;
            try
            {
                action?.Invoke();
            }
            finally
            {
                _suppressUiCallbacks = previous;
            }
        }

        private static void ReplaceChoicesPreservingIndex(DropdownField field, List<string> choices)
        {
            if (field == null)
            {
                return;
            }

            var oldIndex = field.index;
            field.choices = choices ?? new List<string>();
            if (field.choices.Count == 0)
            {
                field.index = -1;
                return;
            }

            if (oldIndex < 0 || oldIndex >= field.choices.Count)
            {
                field.index = 0;
                return;
            }

            field.index = oldIndex;
        }

        private static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Mathf.Min(Mathf.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }

        private static string SanitizeForLog(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (sanitized.Length <= 80)
            {
                return sanitized;
            }

            return sanitized.Substring(0, 80);
        }

        private static void PerfLog(string message)
        {
            if (!AmariBlmConstants.EnablePerformanceLogging)
            {
                return;
            }

            Debug.Log($"{AmariBlmConstants.PerformanceLogPrefix}[CatalogWindow] {message}");
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
            public AmariBlmItemRecord Item { get; }
            public int Rank { get; }
            public SearchHit(AmariBlmItemRecord item, int rank)
            {
                Item = item;
                Rank = rank;
            }
        }
    }
}
