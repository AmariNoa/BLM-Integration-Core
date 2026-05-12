using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using com.amari_noa.unity_editor_localization_core.editor;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow : EditorWindow
    {
        [SerializeField] private VisualTreeAsset visualTreeAsset;
        private const string NotSetToken = "__NOT_SET__";
        private const string ImportedFileRowClassName = "blm-file-row-imported";
        private const string PartiallyImportedFileRowClassName = "blm-file-row-imported-partial";
        private const int ImportedStateChecksPerUpdate = 1;
        private const int ImportedStateUnityPackageGuidChecksPerUpdate = 8;
        private const int ImportQueueDiffRefreshThreshold = 8;
        private static readonly Vector2 InitialWindowSize = new Vector2(1320f, 850f);
        private static readonly Vector2 InitialWindowPosition = new Vector2(80f, 80f);
        private static readonly Vector2 MinimumWindowSize = new Vector2(1320f, 800f);
        private static readonly Vector2 MaximumWindowSize = new Vector2(10000f, 10000f);
        private static FontAsset _catalogWindowFontAsset;

        private readonly BlmDatabaseService _dbService = new BlmDatabaseService();
        private readonly BlmThumbnailCacheService _thumbnailCacheService = new BlmThumbnailCacheService();
        private readonly BlmImportedFileStateEvaluator _importedFileStateEvaluator =
            new BlmImportedFileStateEvaluator(BlmImportIndexService.Shared, BlmUnityPackageGuidCache.Shared);
        private readonly BlmImportedStateCacheService _importedStateCacheService = BlmImportedStateCacheService.Shared;
        private readonly Dictionary<string, HashSet<string>> _selectedByProduct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly List<string> _selectedOrder = new List<string>();
        private readonly HashSet<string> _selectedShops = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _selectedTags = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _collapsedDetailFolderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _collapsedSelectedFolderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _collapsedSelectedProductKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BlmItemRecord> _dbItemsByProductId = new Dictionary<string, BlmItemRecord>(StringComparer.Ordinal);
        private readonly List<string> _categoryValues = new List<string>();
        private readonly List<string> _subCategoryValues = new List<string>();
        private readonly List<ShopEntry> _allShops = new List<ShopEntry>();
        private readonly List<ShopEntry> _visibleShops = new List<ShopEntry>();
        private readonly List<TagEntry> _allTags = new List<TagEntry>();
        private readonly List<TagEntry> _visibleTags = new List<TagEntry>();
        private readonly Dictionary<string, VisualElement> _visibleCardsByProductId = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
        private readonly Dictionary<string, ImportedStateRowHighlightKind> _importedStateByProductFileKey =
            new Dictionary<string, ImportedStateRowHighlightKind>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<VisualElement>> _detailFileRowsByProductFileKey =
            new Dictionary<string, List<VisualElement>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<VisualElement>> _selectedPanelFileRowsByProductFileKey =
            new Dictionary<string, List<VisualElement>>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<BlmFileRecord> _pendingImportedStateFiles = Array.Empty<BlmFileRecord>();
        private BlmItemRecord _pendingImportedStateItem;
        private int _pendingImportedStateFileIndex;

        private BlmPickerContext _context;
        private Action<BlmImportBatchRequest> _onConfirmed;
        private BlmDatabaseLoadResult _db = new BlmDatabaseLoadResult();

        private DropdownField _displayModeField;
        private DropdownField _listSelectorField;
        private DropdownField _sortKeyField;
        private DropdownField _sortOrderField;
        private DropdownField _categoryFilterField;
        private DropdownField _subCategoryFilterField;
        private DropdownField _ageFilterField;
        private DropdownField _pageSizeField;
        private DropdownField _editorLanguageDropdownField;
        private DropdownField _selectedItemsFilterDropDownField;
        private DropdownField _shopFilterSortDropdownField;
        private DropdownField _tagFilterSortDropdownField;
        private TextField _searchField;
        private TextField _shopSearchField;
        private TextField _tagSearchField;
        private ListView _shopListView;
        private ListView _tagListView;
        private ListView _detailFileListView;
        private ListView _importQueueListView;
        private VisualElement _productGridContainer;
        private VisualElement _detailThumbnailImage;
        private ScrollView _selectedProductsScrollView;
        private IntegerField _thumbnailCacheMaxEntriesField;
        private Button _confirmButton;
        private Button _openFolderPathButton;
        private Label _detailProductNameLabel;
        private Label _detailShopNameLabel;
        private Label _detailProductListLabel;
        private TextField _paginationCurrentField;
        private Button _paginationMinPageButton;
        private Button _paginationPrev2Button;
        private Button _paginationPrev1Button;
        private Label _paginationMinDotLabel;
        private Button _paginationMaxPageButton;
        private Button _paginationNext1Button;
        private Button _paginationNext2Button;
        private Label _paginationMaxDotLabel;
        private bool _suppressPaginationFieldCallback;
        private Label _importedStateLabel;
        private Label _detailFolderPathLabel;
        private Label _importQueueTitleLabel;
        private Label _activeFilterCountLabel;
        private Label _filteredProductCountLabel;
        private Label _listModeEmptyStateLabel;
        private Button _cancelButton;
        private VisualElement _importNowLoadingOverlay;
        private VisualElement _catalogLoadingOverlay;
        private Label _catalogLoadingLabel;
        private Label _importProcessingTitleLabel;
        private Label _importProcessingStatusLabel;

        private BlmItemRecord _detailItem;
        private List<BlmFileRecord> _detailFiles = new List<BlmFileRecord>();
        private HashSet<string> _detailDuplicateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<DetailFileListEntry> _detailFileListEntries = new List<DetailFileListEntry>();
        private List<BlmItemRecord> _viewItems = new List<BlmItemRecord>();
        private int _page = 1;
        private int _pageSize = BlmConstants.DefaultPageSize;
        private SelectedItemsFilterMode _selectedItemsFilterMode = SelectedItemsFilterMode.SelectedAndPreferred;
        private bool _languageSubscribed;
        private bool _importSubscribed;
        private bool _suppressUiCallbacks;
        private string _standaloneBatchId = string.Empty;
        private bool _standaloneImportQueueStarting;
        private string _lastShopAggregationKey;
        private string _lastTagAggregationKey;
        private readonly List<string> _importQueueDisplayItems = new List<string>();
        private readonly List<BlmImportRequestItem> _importQueuePreviewItems = new List<BlmImportRequestItem>();
        private BlmImportBatchRequest _pendingStandaloneImportStartBatch;
        private int _selectionVersion;
        private int _databaseVersion;
        private int _importQueuePreviewSelectionVersion = -1;
        private int _importQueuePreviewDatabaseVersion = -1;
        private int _importQueueAppliedSelectionVersion = -1;
        private int _importQueueAppliedDatabaseVersion = -1;
        private bool _hasQueuedRuntimeImportQueueProgress;
        private bool _runtimeImportQueueProgressUiUpdateScheduled;
        private BlmImportQueueProgressContext _queuedRuntimeImportQueueProgress;
        private int _runtimeImportQueueRemainingCount = -1;
        private bool _importQuietModeEnabled;
        private int _importedStateEvaluationVersion;
        private bool _importedStateCheckLoopSubscribed;
        private int _activeImportedStatePendingCount;
        private int _activeImportedStateTotalCount;
        private bool _activeImportedStateHasImportedFiles;
        private string _activeImportedStateProductId = string.Empty;
        private string _activeImportedStateImportIndexFingerprint = "0:0";
        private Task<ImportedStateCheckWorkItem> _activeImportedStatePreloadTask;
        private ImportedStateCheckWorkItem? _activeImportedStatePreloadWorkItem;
        private ImportedStateUnityPackageCheckState _activeImportedStateUnityPackageCheck;
        private ImportedStateNonUnityContentCheckState _activeImportedStateNonUnityContentCheck;
        private CancellationTokenSource _activeImportedStateCancellationTokenSource;
        private int _detailFileLoadVersion;
        private bool _detailFilesLoading;
        private string _detailFilesLoadingProductId = string.Empty;
        private CancellationTokenSource _detailFileLoadCancellationTokenSource;
        private bool _detailFileListRebuildScheduled;
        private bool _confirmExecutionScheduled;
        private int _detailThumbnailRequestVersion;
        private CancellationTokenSource _detailThumbnailLoadCancellationTokenSource;
        private CancellationTokenSource _gridThumbnailLoadCancellationTokenSource;
        private string _lastShownDetailProductId = "";
        private bool _closeRequestedFromUnsavedChangesPrompt;

        public static CatalogWindow Open(BlmPickerContext context, Action<BlmImportBatchRequest> onConfirmed)
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
            var window = GetWindow<CatalogWindow>(false, BlmConstants.WindowTitle, true);
            window.ApplyWindowSizeConstraints();
            Rect? initialPlacement = null;
            if (!hasOpenInstance)
            {
                initialPlacement = LoadStoredWindowPlacement() ?? new Rect(InitialWindowPosition, InitialWindowSize);
                window.position = initialPlacement.Value;
            }

            window._context = context;
            window._onConfirmed = onConfirmed;
            window.titleContent = new GUIContent(BlmConstants.WindowTitle);
            window.Show();
            window.Focus();
            if (initialPlacement.HasValue)
            {
                var targetPlacement = initialPlacement.Value;
                EditorApplication.delayCall += () =>
                {
                    if (window == null)
                    {
                        return;
                    }

                    if (window.position != targetPlacement)
                    {
                        window.position = targetPlacement;
                    }
                };
            }

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
            _closeRequestedFromUnsavedChangesPrompt = false;
        }

        public override void SaveChanges()
        {
            _closeRequestedFromUnsavedChangesPrompt = true;
            AbortStandaloneImportQueueIfRunning("SaveChanges");
            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            _closeRequestedFromUnsavedChangesPrompt = true;
            AbortStandaloneImportQueueIfRunning("DiscardChanges");
            base.DiscardChanges();
        }

        private void ApplyWindowSizeConstraints()
        {
            minSize = MinimumWindowSize;
            maxSize = MaximumWindowSize;
        }

        private static Rect? LoadStoredWindowPlacement()
        {
            if (!EditorPrefs.HasKey(BlmConstants.WindowPositionXEditorPrefsKey) ||
                !EditorPrefs.HasKey(BlmConstants.WindowPositionYEditorPrefsKey) ||
                !EditorPrefs.HasKey(BlmConstants.WindowSizeWidthEditorPrefsKey) ||
                !EditorPrefs.HasKey(BlmConstants.WindowSizeHeightEditorPrefsKey))
            {
                return null;
            }

            var x = EditorPrefs.GetFloat(BlmConstants.WindowPositionXEditorPrefsKey);
            var y = EditorPrefs.GetFloat(BlmConstants.WindowPositionYEditorPrefsKey);
            var w = EditorPrefs.GetFloat(BlmConstants.WindowSizeWidthEditorPrefsKey);
            var h = EditorPrefs.GetFloat(BlmConstants.WindowSizeHeightEditorPrefsKey);

            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(w) || float.IsNaN(h) ||
                float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(w) || float.IsInfinity(h))
            {
                return null;
            }

            if (w <= 0f || h <= 0f)
            {
                return null;
            }

            const float sanityRange = 50000f;
            if (Mathf.Abs(x) > sanityRange || Mathf.Abs(y) > sanityRange ||
                w > sanityRange || h > sanityRange)
            {
                return null;
            }

            return new Rect(x, y, w, h);
        }

        private void PersistWindowPlacement()
        {
            var rect = position;
            if (float.IsNaN(rect.x) || float.IsNaN(rect.y) ||
                float.IsNaN(rect.width) || float.IsNaN(rect.height) ||
                rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            EditorPrefs.SetFloat(BlmConstants.WindowPositionXEditorPrefsKey, rect.x);
            EditorPrefs.SetFloat(BlmConstants.WindowPositionYEditorPrefsKey, rect.y);
            EditorPrefs.SetFloat(BlmConstants.WindowSizeWidthEditorPrefsKey, rect.width);
            EditorPrefs.SetFloat(BlmConstants.WindowSizeHeightEditorPrefsKey, rect.height);
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
            tree.CloneTree(rootVisualElement);
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

            ShowCatalogLoadingPlaceholder();
            EditorApplication.delayCall += ExecuteInitialReloadDeferred;
            PerfLog("CreateGUI step='ReloadDb' deferred=true");

            totalStopwatch.Stop();
            PerfLog($"CreateGUI completed in {totalStopwatch.ElapsedMilliseconds} ms");
        }

        private void ShowCatalogLoadingPlaceholder()
        {
            if (_catalogLoadingLabel != null)
            {
                _catalogLoadingLabel.text = L("blm.catalog.loading", "Loading...");
            }

            if (_catalogLoadingOverlay != null)
            {
                _catalogLoadingOverlay.style.display = DisplayStyle.Flex;
            }
        }

        private void HideCatalogLoadingPlaceholder()
        {
            if (_catalogLoadingOverlay != null)
            {
                _catalogLoadingOverlay.style.display = DisplayStyle.None;
            }
        }

        private void ExecuteInitialReloadDeferred()
        {
            if (this == null || rootVisualElement?.panel == null)
            {
                return;
            }

            var reloadStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ReloadDb(false);
            }
            finally
            {
                HideCatalogLoadingPlaceholder();
                reloadStopwatch.Stop();
                PerfLog($"CreateGUI deferred-step='ReloadDb' elapsedMs={reloadStopwatch.ElapsedMilliseconds}");
            }
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
            AbortStandaloneImportQueueIfRunning(
                _closeRequestedFromUnsavedChangesPrompt
                    ? "OnDisable(CloseConfirmed)"
                    : "OnDisable");
            _confirmExecutionScheduled = false;
            EditorApplication.delayCall -= ExecuteInitialReloadDeferred;
            ClearQueuedRuntimeImportQueueUiUpdate();
            CancelImportStartImportedStateCheck();
            SetImportQuietMode(false);
            _detailFileLoadVersion++;
            _detailFilesLoading = false;
            _detailFilesLoadingProductId = string.Empty;
            CancelDetailFileLoadBackgroundWork(disposeSource: true);
            CancelDetailThumbnailLoad(disposeSource: true);
            CancelGridThumbnailLoads(disposeSource: true);
            CancelActiveImportedStateTasks(disposeSource: true);
            ClearPausedBackgroundWorkStateForImport();
            StopImportedStateCheckLoop();
            _pendingImportedStateFiles = Array.Empty<BlmFileRecord>();
            _pendingImportedStateItem = null;
            _pendingImportedStateFileIndex = 0;
            _detailFileListRebuildScheduled = false;
            _activeImportedStatePreloadTask = null;
            _activeImportedStatePreloadWorkItem = null;
            _activeImportedStateUnityPackageCheck = null;
            _detailFileRowsByProductFileKey.Clear();
            _selectedPanelFileRowsByProductFileKey.Clear();

            if (_languageSubscribed)
            {
                EditorLocalization.Service.LanguageChanged -= OnLanguageChanged;
                _languageSubscribed = false;
            }

            if (_importSubscribed)
            {
                BlmImportProcessor.Shared.ImportBatchCompleted -= OnImportBatchCompleted;
                BlmImportProcessor.Shared.ImportQueueProgressed -= OnImportQueueProgressed;
                _importSubscribed = false;
            }

            BlmImportIndexService.Shared.FlushPendingSaves();
            _importedStateCacheService.FlushPendingSaves();
            PersistWindowPlacement();
            _closeRequestedFromUnsavedChangesPrompt = false;
        }

        private void OnDestroy()
        {
            AbortStandaloneImportQueueIfRunning(
                _closeRequestedFromUnsavedChangesPrompt
                    ? "OnDestroy(CloseConfirmed)"
                    : "OnDestroy");
            _closeRequestedFromUnsavedChangesPrompt = false;
        }

        private void CancelDetailFileLoadBackgroundWork(bool disposeSource)
        {
            var source = _detailFileLoadCancellationTokenSource;
            _detailFileLoadCancellationTokenSource = null;
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch
            {
                // ignored
            }

            if (disposeSource)
            {
                try
                {
                    source.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void CancelDetailThumbnailLoad(bool disposeSource)
        {
            _detailThumbnailRequestVersion++;
            var source = _detailThumbnailLoadCancellationTokenSource;
            _detailThumbnailLoadCancellationTokenSource = null;
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch
            {
                // ignored
            }

            if (disposeSource)
            {
                try
                {
                    source.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void CancelGridThumbnailLoads(bool disposeSource)
        {
            var source = _gridThumbnailLoadCancellationTokenSource;
            _gridThumbnailLoadCancellationTokenSource = null;
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch
            {
                // ignored
            }

            if (!disposeSource)
            {
                return;
            }

            try
            {
                source.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        private CancellationToken EnsureGridThumbnailCancellationToken()
        {
            if (_gridThumbnailLoadCancellationTokenSource == null)
            {
                _gridThumbnailLoadCancellationTokenSource = new CancellationTokenSource();
            }

            return _gridThumbnailLoadCancellationTokenSource.Token;
        }

        private void CancelActiveImportedStateTasks(bool disposeSource)
        {
            var source = _activeImportedStateCancellationTokenSource;
            _activeImportedStateCancellationTokenSource = null;
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch
            {
                // ignored
            }

            if (!disposeSource)
            {
                return;
            }

            try
            {
                source.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        private void RequestDetailThumbnailLoad(BlmItemRecord item)
        {
            if (item == null)
            {
                return;
            }

            CancelDetailThumbnailLoad(disposeSource: false);
            var requestVersion = _detailThumbnailRequestVersion;
            var cancellationSource = new CancellationTokenSource();
            var cancellationToken = cancellationSource.Token;
            _detailThumbnailLoadCancellationTokenSource = cancellationSource;

            _ = _thumbnailCacheService.GetTextureAsync(item, cancellationToken).ContinueWith(task =>
            {
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        if (this == null ||
                            cancellationToken.IsCancellationRequested ||
                            rootVisualElement?.panel == null ||
                            requestVersion != _detailThumbnailRequestVersion ||
                            !ReferenceEquals(_detailThumbnailLoadCancellationTokenSource, cancellationSource) ||
                            _detailItem == null ||
                            !string.Equals(_detailItem.ProductId, item.ProductId, StringComparison.Ordinal))
                        {
                            return;
                        }

                        if (task.IsCompletedSuccessfully && task.Result != null)
                        {
                            _detailThumbnailImage.style.backgroundImage = new StyleBackground(task.Result);
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(_detailThumbnailLoadCancellationTokenSource, cancellationSource))
                        {
                            _detailThumbnailLoadCancellationTokenSource = null;
                        }

                        cancellationSource.Dispose();
                    }
                };
            });
        }

        private void BindUi()
        {
            _displayModeField = rootVisualElement.Q<DropdownField>("DisplayModeField");
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
            _shopFilterSortDropdownField = rootVisualElement.Q<DropdownField>("ShopFilterSortDropdown");
            _tagFilterSortDropdownField = rootVisualElement.Q<DropdownField>("TagFilterSortDropdown");
            _shopListView = rootVisualElement.Q<ListView>("ShopFilterListView");
            _tagListView = rootVisualElement.Q<ListView>("TagFilterListView");
            _detailFileListView = rootVisualElement.Q<ListView>("DetailFileListView");
            _importQueueListView = rootVisualElement.Q<ListView>("ImportQueueListView");
            _importQueueListView?.RegisterCallback<GeometryChangedEvent>(_ => ApplyImportQueueEmptyLabelText());
            _selectedItemsFilterDropDownField = rootVisualElement.Q<DropdownField>("SelectedItemsFilterDropdown");
            _productGridContainer = rootVisualElement.Q<VisualElement>("ProductGridContainer");
            _detailThumbnailImage = rootVisualElement.Q<VisualElement>("DetailThumbnailImage");
            _selectedProductsScrollView = rootVisualElement.Q<ScrollView>("SelectedProductsScrollView");
            _thumbnailCacheMaxEntriesField = rootVisualElement.Q<IntegerField>("ThumbnailCacheMaxEntriesField");
            _confirmButton = rootVisualElement.Q<Button>("ConfirmSelectionButton");
            _cancelButton = rootVisualElement.Q<Button>("CancelButton");
            _openFolderPathButton = rootVisualElement.Q<Button>("OpenFolderPathButton");
            _detailProductNameLabel = rootVisualElement.Q<Label>("DetailProductNameLabel");
            _detailShopNameLabel = rootVisualElement.Q<Label>("DetailShopNameLabel");
            _detailProductListLabel = rootVisualElement.Q<Label>("DetailProductListLabel");
            _paginationCurrentField = rootVisualElement.Q<TextField>("CurrentPageField");
            _paginationMinPageButton = rootVisualElement.Q<Button>("ButtonMinPage");
            _paginationPrev2Button = rootVisualElement.Q<Button>("ButtonCurrentPage-2");
            _paginationPrev1Button = rootVisualElement.Q<Button>("ButtonCurrentPage-1");
            _paginationMinDotLabel = rootVisualElement.Q<Label>("LabelMinPageDot");
            _paginationMaxPageButton = rootVisualElement.Q<Button>("ButtonMaxPage");
            _paginationNext1Button = rootVisualElement.Q<Button>("ButtonCurrentPage+1");
            _paginationNext2Button = rootVisualElement.Q<Button>("ButtonCurrentPage+2");
            _paginationMaxDotLabel = rootVisualElement.Q<Label>("LabelMaxPageDot");
            SetupPaginationFooter();
            _importedStateLabel = rootVisualElement.Q<Label>("ImportedStateLabel");
            _detailFolderPathLabel = rootVisualElement.Q<Label>("DetailFolderPathLabel");
            _importQueueTitleLabel = rootVisualElement.Q<Label>("ImportQueueTitleLabel");
            _activeFilterCountLabel = rootVisualElement.Q<Label>("ActiveFilterCountLabel");
            _filteredProductCountLabel = rootVisualElement.Q<Label>("FilteredProductCountLabel");
            _listModeEmptyStateLabel = rootVisualElement.Q<Label>("ListModeEmptyStateLabel");
            _importNowLoadingOverlay = rootVisualElement.Q<VisualElement>("ImportNowLoadingOverlay");
            _catalogLoadingOverlay = rootVisualElement.Q<VisualElement>("CatalogLoadingOverlay");
            _catalogLoadingLabel = rootVisualElement.Q<Label>("CatalogLoadingLabel");
            _importProcessingTitleLabel = rootVisualElement.Q<Label>("ImportProcessingTitleLabel");
            _importProcessingStatusLabel = rootVisualElement.Q<Label>("ImportProcessingStatusLabel");
        }

        private void SetupUi()
        {
            _displayModeField.choices = new List<string> { "All", "ByList" };
            _displayModeField.index = 0;
            _sortKeyField.choices = new List<string> { "ProductName", "ShopName", "RegisteredAt", "PublishedAt" };
            _sortKeyField.index = LoadDropdownIndexFromEditorPrefs(
                BlmConstants.SortKeyEditorPrefsKey,
                defaultIndex: 0,
                choiceCount: _sortKeyField.choices.Count);
            _sortOrderField.choices = new List<string> { "Ascending", "Descending" };
            _sortOrderField.index = LoadDropdownIndexFromEditorPrefs(
                BlmConstants.SortOrderEditorPrefsKey,
                defaultIndex: 0,
                choiceCount: _sortOrderField.choices.Count);
            _ageFilterField.choices = new List<string> { "Any", "AllAges", "R18" };
            _ageFilterField.index = 0;
            _pageSizeField.choices = BlmConstants.PageSizes.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
            var loadedPageSize = LoadPageSizeFromEditorPrefs();
            var pageSizeIndex = Array.IndexOf(BlmConstants.PageSizes, loadedPageSize);
            if (pageSizeIndex >= 0)
            {
                _pageSizeField.index = pageSizeIndex;
                _pageSize = loadedPageSize;
            }
            else if (BlmConstants.PageSizes.Length > 0)
            {
                _pageSize = BlmConstants.PageSizes[0];
                _pageSizeField.index = 0;
                PersistPageSizeToEditorPrefs(_pageSize);
            }
            else
            {
                _pageSize = Math.Max(1, BlmConstants.DefaultPageSize);
                _pageSizeField.index = -1;
            }

            if (_selectedItemsFilterDropDownField != null)
            {
                _selectedItemsFilterDropDownField.choices = new List<string> { "SelectedAndPreferred", "SelectedOnly", "All" };
                _selectedItemsFilterDropDownField.index = 0;
            }

            SetupFilterSortDropdown(_shopFilterSortDropdownField, BlmConstants.ShopFilterSortEditorPrefsKey);
            SetupFilterSortDropdown(_tagFilterSortDropdownField, BlmConstants.TagFilterSortEditorPrefsKey);

            SyncSelectedItemsFilterMode();
            SyncEditorLanguageDropdownChoices();
            _thumbnailCacheMaxEntriesField.isDelayed = true;
            _thumbnailCacheMaxEntriesField.SetValueWithoutNotify(_thumbnailCacheService.MaxEntries);
            _searchField.value = string.Empty;
            _searchField.isDelayed = true;
            _searchField.pickingMode = PickingMode.Position;
            _shopSearchField.value = string.Empty;
            _shopSearchField.pickingMode = PickingMode.Position;
            _tagSearchField.value = string.Empty;
            _tagSearchField.pickingMode = PickingMode.Position;
            _openFolderPathButton?.SetEnabled(false);
            if (_importNowLoadingOverlay != null)
            {
                _importNowLoadingOverlay.pickingMode = PickingMode.Position;
                _importNowLoadingOverlay.style.display = DisplayStyle.None;
            }

            SetDetailFileActionButtonsEnabled(false);
            if (_selectedProductsScrollView != null)
            {
                _selectedProductsScrollView.mode = ScrollViewMode.Vertical;
                _selectedProductsScrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                _selectedProductsScrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
                _selectedProductsScrollView.contentContainer.style.minWidth = 0f;
                _selectedProductsScrollView.AddToClassList("blm-selected-products-scroll-view");
            }

            if (_importQueueListView != null)
            {
                _importQueueListView.AddToClassList("blm-import-queue-list-view");
                _importQueueListView.selectionType = SelectionType.None;
                _importQueueListView.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
                _importQueueListView.itemsSource = _importQueueDisplayItems;
                _importQueueListView.makeItem = () =>
                {
                    var row = new VisualElement();
                    row.AddToClassList("blm-import-queue-item-row");

                    var indexLabel = new Label { name = "IndexLabel" };
                    indexLabel.AddToClassList("blm-import-queue-item-index");

                    var fileLabel = new Label { name = "FileLabel" };
                    fileLabel.AddToClassList("blm-import-queue-item-label");

                    row.Add(indexLabel);
                    row.Add(fileLabel);
                    return row;
                };
                _importQueueListView.bindItem = (element, i) =>
                {
                    if (element is VisualElement row && i >= 0 && i < _importQueueDisplayItems.Count)
                    {
                        var indexLabel = row.Q<Label>("IndexLabel");
                        var fileLabel = row.Q<Label>("FileLabel");
                        if (indexLabel != null)
                        {
                            indexLabel.text = (i + 1).ToString(CultureInfo.InvariantCulture);
                        }

                        if (fileLabel != null)
                        {
                            fileLabel.text = _importQueueDisplayItems[i];
                        }
                    }
                };
                _importQueueListView.Rebuild();
            }

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
                toggle.AddToClassList("blm-file-row-select-toggle");
                var folderFoldout = new Foldout { name = "FolderFoldout" };
                folderFoldout.AddToClassList("blm-file-row-folder-foldout");
                var label = new Label { name = "Label" };
                label.AddToClassList("blm-file-row-label");
                row.Add(toggle);
                row.Add(folderFoldout);
                row.Add(label);
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (_detailItem == null)
                    {
                        return;
                    }

                    if (toggle.userData is BlmFileRecord file)
                    {
                        SetSelected(_detailItem, file.FullPath, evt.newValue);
                        RequestDetailFileListRebuild();
                        RebuildSelectedPanel();
                        UpdateConfirmButtonState();
                        return;
                    }

                    if (toggle.userData is string sectionExtension)
                    {
                        SetSectionFilesSelection(sectionExtension, evt.newValue);
                        RequestDetailFileListRebuild();
                        RebuildSelectedPanel();
                        UpdateConfirmButtonState();
                        return;
                    }

                    if (toggle.userData is IReadOnlyList<BlmFileRecord> folderFiles)
                    {
                        SetFolderFilesSelection(folderFiles, evt.newValue);
                        RequestDetailFileListRebuild();
                        RebuildSelectedPanel();
                        UpdateConfirmButtonState();
                    }
                });
                folderFoldout.RegisterValueChangedCallback(evt =>
                {
                    if (folderFoldout.userData is string folderKey)
                    {
                        if (IsDetailFolderExpanded(folderKey) != evt.newValue)
                        {
                            ToggleDetailFolderExpanded(folderKey);
                        }
                    }
                });
                return row;
            };
            _detailFileListView.bindItem = (element, i) =>
            {
                var row = (VisualElement)element;
                var toggle = row.Q<Toggle>("Toggle");
                var folderFoldout = row.Q<Foldout>("FolderFoldout");
                var label = row.Q<Label>("Label");
                if (toggle == null || label == null || folderFoldout == null)
                {
                    return;
                }

                if (i < 0 || i >= _detailFileListEntries.Count)
                {
                    UnregisterDetailFileRow(row);
                    row.RemoveFromClassList("blm-file-row-section");
                    row.RemoveFromClassList("blm-file-row-section-with-toggle");
                    row.RemoveFromClassList(ImportedFileRowClassName);
                    row.RemoveFromClassList(PartiallyImportedFileRowClassName);
                    label.RemoveFromClassList("blm-file-row-section-label");
                    row.style.paddingLeft = 0f;
                    toggle.style.display = DisplayStyle.None;
                    toggle.userData = null;
                    toggle.showMixedValue = false;
                    toggle.SetValueWithoutNotify(false);
                    folderFoldout.style.display = DisplayStyle.None;
                    folderFoldout.userData = null;
                    folderFoldout.text = string.Empty;
                    folderFoldout.SetValueWithoutNotify(false);
                    label.style.display = DisplayStyle.Flex;
                    label.text = string.Empty;
                    SetRowImportedStateTooltip(row, string.Empty);
                    return;
                }

                var entry = _detailFileListEntries[i];
                if (entry.IsSectionHeader)
                {
                    UnregisterDetailFileRow(row);
                    row.AddToClassList("blm-file-row-section");
                    label.AddToClassList("blm-file-row-section-label");
                    row.RemoveFromClassList(ImportedFileRowClassName);
                    row.RemoveFromClassList(PartiallyImportedFileRowClassName);
                    SetRowImportedStateTooltip(row, string.Empty);
                    row.style.paddingLeft = 0f;
                    folderFoldout.style.display = DisplayStyle.None;
                    folderFoldout.userData = null;
                    folderFoldout.text = string.Empty;
                    folderFoldout.SetValueWithoutNotify(false);
                    label.style.display = DisplayStyle.Flex;
                    if (entry.CanToggleSectionSelection)
                    {
                        row.AddToClassList("blm-file-row-section-with-toggle");
                        toggle.style.display = DisplayStyle.Flex;
                        toggle.userData = entry.SectionExtension;
                        ApplyDetailSectionToggleState(toggle, entry.SectionExtension);
                    }
                    else
                    {
                        row.RemoveFromClassList("blm-file-row-section-with-toggle");
                        toggle.style.display = DisplayStyle.None;
                        toggle.userData = null;
                        toggle.showMixedValue = false;
                        toggle.SetValueWithoutNotify(false);
                    }

                    label.text = entry.DisplayText;
                    return;
                }

                row.RemoveFromClassList("blm-file-row-section");
                row.RemoveFromClassList("blm-file-row-section-with-toggle");
                label.RemoveFromClassList("blm-file-row-section-label");
                row.style.paddingLeft = entry.Depth * 14f;
                toggle.style.display = DisplayStyle.Flex;
                if (entry.IsFolder)
                {
                    UnregisterDetailFileRow(row);
                    toggle.userData = entry.FolderFiles;
                    ApplyDetailFolderToggleState(toggle, entry.FolderFiles);
                    row.RemoveFromClassList(ImportedFileRowClassName);
                    row.RemoveFromClassList(PartiallyImportedFileRowClassName);
                    SetRowImportedStateTooltip(row, string.Empty);
                    folderFoldout.style.display = DisplayStyle.Flex;
                    folderFoldout.userData = entry.FolderKey;
                    folderFoldout.text = entry.DisplayText;
                    folderFoldout.SetValueWithoutNotify(IsDetailFolderExpanded(entry.FolderKey));
                    label.style.display = DisplayStyle.None;
                    label.text = string.Empty;
                    return;
                }

                folderFoldout.style.display = DisplayStyle.None;
                folderFoldout.userData = null;
                folderFoldout.text = string.Empty;
                folderFoldout.SetValueWithoutNotify(false);
                toggle.showMixedValue = false;
                var file = entry.File;
                toggle.userData = file;
                toggle.SetValueWithoutNotify(file != null && IsSelected(_detailItem?.ProductId, file.FullPath));
                label.style.display = DisplayStyle.Flex;
                label.text = entry.DisplayText;
                RegisterDetailFileRow(_detailItem?.ProductId, file, row);
                ApplyImportedStateVisualToFileRow(row, GetImportedStateForFile(_detailItem, file));
            };
            _detailFileListView.selectionType = SelectionType.None;
            _detailFileListView.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;

            _displayModeField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                UpdateListModeState();
                ForceResetCategoryAndSubCategoryFiltersToAll();
                PruneSelectionsOutsideCurrentBaseSet();
                ApplyFilter(true);
            });
            _listSelectorField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                ForceResetCategoryAndSubCategoryFiltersToAll();
                PruneSelectionsOutsideCurrentBaseSet();
                ApplyFilter(true);
            });
            _sortKeyField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                PersistDropdownIndexToEditorPrefs(
                    BlmConstants.SortKeyEditorPrefsKey,
                    _sortKeyField.index,
                    _sortKeyField.choices.Count,
                    defaultIndex: 0);
                ApplyFilter(true);
            });
            _sortOrderField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                PersistDropdownIndexToEditorPrefs(
                    BlmConstants.SortOrderEditorPrefsKey,
                    _sortOrderField.index,
                    _sortOrderField.choices.Count,
                    defaultIndex: 0);
                ApplyFilter(true);
            });
            _categoryFilterField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                WithSuppressedUiCallbacks(() =>
                {
                    RebuildSubCategoryChoices();
                    _subCategoryFilterField.index = _subCategoryValues.Count > 0 ? 0 : -1;
                });
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
            _shopFilterSortDropdownField?.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                PersistFilterSortMode(BlmConstants.ShopFilterSortEditorPrefsKey, _shopFilterSortDropdownField);
                ResortShopChoices();
                RebuildVisibleShops();
            });
            _tagFilterSortDropdownField?.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                PersistFilterSortMode(BlmConstants.TagFilterSortEditorPrefsKey, _tagFilterSortDropdownField);
                ResortTagChoices();
                RebuildVisibleTags();
            });
            _pageSizeField.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                if (int.TryParse(_pageSizeField.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    var normalizedPageSize = NormalizePageSize(parsed);
                    _pageSize = normalizedPageSize;
                    PersistPageSizeToEditorPrefs(normalizedPageSize);
                    if (normalizedPageSize != parsed)
                    {
                        _pageSizeField.SetValueWithoutNotify(normalizedPageSize.ToString(CultureInfo.InvariantCulture));
                    }
                }

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

                var result = service.SetLanguage(BlmConstants.LocalizationSourceId, evt.newValue);
                if (result is EditorLocalizationSetLanguageResult.FAILED or EditorLocalizationSetLanguageResult.NOT_REGISTERED)
                {
                    _editorLanguageDropdownField.SetValueWithoutNotify(service.CurrentLanguageCode);
                    return;
                }

                _editorLanguageDropdownField.SetValueWithoutNotify(service.CurrentLanguageCode);
            });
            _selectedItemsFilterDropDownField?.RegisterValueChangedCallback(_ =>
            {
                if (_suppressUiCallbacks)
                {
                    return;
                }

                SyncSelectedItemsFilterMode();
                RebuildSelectedPanel();
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
            rootVisualElement.Q<Button>("SearchTextFieldClearButton").clicked += () =>
            {
                _searchField.SetValueWithoutNotify(string.Empty);
                ApplyFilter(true);
            };
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
            if (_cancelButton != null)
            {
                _cancelButton.clicked += Close;
            }
            var deSelectAllFilesButton = rootVisualElement.Q<Button>("DeSelectAllFilesButton");
            if (deSelectAllFilesButton != null)
            {
                deSelectAllFilesButton.clicked += () => SetAllFilesSelection(false);
            }

            var deSelectAllProductsFilesButton = rootVisualElement.Q<Button>("DeSelectAllProductsFilesButton");
            if (deSelectAllProductsFilesButton != null)
            {
                deSelectAllProductsFilesButton.clicked += ClearAllSelectedFiles;
            }

            if (_openFolderPathButton != null)
            {
                _openFolderPathButton.clicked += OnOpenFolderPathClicked;
            }

            rootVisualElement.Q<Button>("SelectAllFilesButton").clicked += () => SetAllFilesSelection(true);
            _confirmButton.clicked += OnConfirmClicked;
            UpdateStandaloneImportUiState();
        }

        private bool IsStandaloneImportQueueRunning()
        {
            return _context != null &&
                   _context.InvocationContext == BlmInvocationContext.Standalone &&
                   (_standaloneImportQueueStarting || !string.IsNullOrEmpty(_standaloneBatchId));
        }

        private void UpdateStandaloneImportUiState()
        {
            var isImportQueueRunning = IsStandaloneImportQueueRunning();

            if (_importNowLoadingOverlay != null)
            {
                _importNowLoadingOverlay.style.display = isImportQueueRunning ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_importProcessingTitleLabel != null)
            {
                _importProcessingTitleLabel.text = L("blm.processing.title", "Processing...");
            }

            if (_importProcessingStatusLabel != null)
            {
                var statusText = GetImportProcessingStatusText();
                _importProcessingStatusLabel.text = statusText;
                _importProcessingStatusLabel.style.display = string.IsNullOrWhiteSpace(statusText) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            hasUnsavedChanges = isImportQueueRunning;
            saveChangesMessage = isImportQueueRunning
                ? L("blm.import.close_during_queue.message", "The import queue is still running. Close the window and abort the queue?")
                : string.Empty;

            UpdateConfirmButtonState();
        }

        private string GetImportProcessingStatusText()
        {
            if (!IsStandaloneImportQueueRunning())
            {
                return string.Empty;
            }

            if (_standaloneImportQueueStarting)
            {
                return L("blm.processing.status.starting_import_queue", "Starting import queue...");
            }

            var remainingCount = _runtimeImportQueueRemainingCount >= 0
                ? _runtimeImportQueueRemainingCount
                : _importQueueDisplayItems.Count;
            if (remainingCount <= 0)
            {
                return L("blm.processing.status.import_queue", "Processing import queue...");
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                L("blm.processing.status.import_queue_with_count", "Processing import queue... ({0} item(s) remaining)"),
                remainingCount);
        }

        private void AbortStandaloneImportQueueIfRunning(string reason)
        {
            if (!IsStandaloneImportQueueRunning())
            {
                return;
            }

            PerfLog($"AbortStandaloneImportQueueIfRunning reason='{SanitizeForLog(reason)}'");
            var pipelineService = _context?.UnityPackageImportPipelineService;
            if (pipelineService != null && (pipelineService.RemainingCount > 0 || pipelineService.IsImporting))
            {
                pipelineService.ResetPipelineAndClearQueue();
            }

            _standaloneImportQueueStarting = false;
            _standaloneBatchId = string.Empty;
            _runtimeImportQueueRemainingCount = -1;
            _pendingStandaloneImportStartBatch = null;
            SetImportQuietMode(false);
            CancelImportStartImportedStateCheck();
            ClearPausedBackgroundWorkStateForImport();
            SetRuntimeImportQueueItems(Array.Empty<BlmImportRequestItem>());
            UpdateStandaloneImportUiState();
        }

        private void ClearPausedBackgroundWorkStateForImport()
        {
        }

        private void SetImportQuietMode(bool enabled)
        {
            if (_importQuietModeEnabled == enabled)
            {
                return;
            }

            _importQuietModeEnabled = enabled;
            if (enabled)
            {
                CancelImportedStateEvaluation();
            }
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
            RebuildDbItemLookup();
            _databaseVersion++;
            InvalidateImportQueuePreviewCache();
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
                MarkSelectionChanged();
                _selectedShops.Clear();
                _selectedTags.Clear();
                _listSelectorField.choices = _db.Lists.Select(l => string.IsNullOrWhiteSpace(l.Title) ? l.Id.ToString(CultureInfo.InvariantCulture) : l.Title).ToList();
                _listSelectorField.index = _listSelectorField.choices.Count > 0 ? 0 : -1;
                RebuildCategoryChoices();
                RebuildSubCategoryChoices();
                _lastShopAggregationKey = null;
                _lastTagAggregationKey = null;
                UpdateListModeState();
            });
            PerfLog(
                "ReloadDb filter caches invalidated.");
            RefreshDetailItemFromReloadedDb();
            ApplyFilter(true);
            rebuildUiStopwatch.Stop();
            totalStopwatch.Stop();
            PerfLog(
                $"ReloadDb completed in {totalStopwatch.ElapsedMilliseconds} ms. " +
                $"uiRefreshElapsedMs={rebuildUiStopwatch.ElapsedMilliseconds}, itemCount={_db.Items.Count}, listCount={_db.Lists.Count}, hasError={_db.HasError.ToString().ToLowerInvariant()}");
        }

        private void RefreshDetailItemFromReloadedDb()
        {
            if (_detailItem == null)
            {
                _lastShownDetailProductId = string.Empty;
                return;
            }

            var productId = _detailItem.ProductId;
            if (!string.IsNullOrWhiteSpace(productId) &&
                _dbItemsByProductId.TryGetValue(productId, out var refreshedItem) &&
                refreshedItem != null)
            {
                _detailItem = refreshedItem;
            }
            else
            {
                _detailItem = null;
            }

            _lastShownDetailProductId = string.Empty;
        }

        private void RebuildDbItemLookup()
        {
            _dbItemsByProductId.Clear();
            if (_db?.Items == null)
            {
                return;
            }

            foreach (var item in _db.Items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ProductId))
                {
                    continue;
                }

                _dbItemsByProductId[item.ProductId] = item;
            }
        }

        private void MarkSelectionChanged()
        {
            _selectionVersion++;
            _importQueuePreviewSelectionVersion = -1;
            _importQueueAppliedSelectionVersion = -1;
            _importQueueAppliedDatabaseVersion = -1;
        }

        private void InvalidateImportQueuePreviewCache()
        {
            _importQueuePreviewItems.Clear();
            _importQueuePreviewSelectionVersion = -1;
            _importQueuePreviewDatabaseVersion = -1;
            _importQueueAppliedSelectionVersion = -1;
            _importQueueAppliedDatabaseVersion = -1;
        }

        private void OnConfirmClicked()
        {
            if (_confirmExecutionScheduled || IsStandaloneImportQueueRunning())
            {
                return;
            }

            if (_context != null && _context.InvocationContext == BlmInvocationContext.Standalone)
            {
                _standaloneImportQueueStarting = true;
                SetImportQuietMode(true);
                UpdateStandaloneImportUiState();
            }

            _confirmExecutionScheduled = true;
            EditorApplication.delayCall += ExecuteConfirmClickedDeferred;
        }

        private void ExecuteConfirmClickedDeferred()
        {
            _confirmExecutionScheduled = false;
            if (this == null)
            {
                return;
            }

            ExecuteConfirmClickedCore();
        }

        private void ExecuteConfirmClickedCore()
        {
            if (_context == null)
            {
                _standaloneImportQueueStarting = false;
                _pendingStandaloneImportStartBatch = null;
                CancelImportStartImportedStateCheck();
                ClearPausedBackgroundWorkStateForImport();
                UpdateStandaloneImportUiState();
                return;
            }

            var batch = BuildBatch();
            if (batch.Items.Count == 0)
            {
                _standaloneImportQueueStarting = false;
                SetImportQuietMode(false);
                _pendingStandaloneImportStartBatch = null;
                CancelImportStartImportedStateCheck();
                ClearPausedBackgroundWorkStateForImport();
                UpdateStandaloneImportUiState();
                return;
            }

            if (_context.InvocationContext == BlmInvocationContext.Integration)
            {
                _standaloneImportQueueStarting = false;
                SetImportQuietMode(false);
                _pendingStandaloneImportStartBatch = null;
                CancelImportStartImportedStateCheck();
                ClearPausedBackgroundWorkStateForImport();
                _onConfirmed?.Invoke(batch);
                Close();
                return;
            }

            _pendingStandaloneImportStartBatch = batch;
            CancelImportStartImportedStateCheck();
            UpdateStandaloneImportUiState();
            TryStartStandaloneImportAfterImportStartCheckCompleted();
        }

        private void TryStartStandaloneImportAfterImportStartCheckCompleted()
        {
            var batch = _pendingStandaloneImportStartBatch;
            _pendingStandaloneImportStartBatch = null;
            if (batch == null)
            {
                return;
            }

            if (_context == null || _context.InvocationContext != BlmInvocationContext.Standalone)
            {
                _standaloneImportQueueStarting = false;
                SetImportQuietMode(false);
                ClearPausedBackgroundWorkStateForImport();
                UpdateStandaloneImportUiState();
                return;
            }

            if (batch.Items == null || batch.Items.Count == 0)
            {
                _standaloneImportQueueStarting = false;
                SetImportQuietMode(false);
                ClearPausedBackgroundWorkStateForImport();
                UpdateStandaloneImportUiState();
                return;
            }

            if (!_importSubscribed)
            {
                BlmImportProcessor.Shared.ImportBatchCompleted += OnImportBatchCompleted;
                BlmImportProcessor.Shared.ImportQueueProgressed += OnImportQueueProgressed;
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
            _standaloneImportQueueStarting = false;
            _runtimeImportQueueRemainingCount = batch.Items.Count;
            SetRuntimeImportQueueItems(batch.Items);
            UpdateStandaloneImportUiState();
            BlmImportProcessor.Shared.Execute(batch, _context);
        }

        private void OnImportQueueProgressed(BlmImportQueueProgressContext progress)
        {
            if (progress == null ||
                !string.Equals(progress.BatchId, _standaloneBatchId, StringComparison.Ordinal))
            {
                return;
            }

            QueueRuntimeImportQueueUiUpdate(progress);
        }

        private void QueueRuntimeImportQueueUiUpdate(BlmImportQueueProgressContext progress)
        {
            _hasQueuedRuntimeImportQueueProgress = true;
            _queuedRuntimeImportQueueProgress = progress;

            if (_runtimeImportQueueProgressUiUpdateScheduled)
            {
                return;
            }

            _runtimeImportQueueProgressUiUpdateScheduled = true;
            EditorApplication.delayCall += ApplyQueuedRuntimeImportQueueUiUpdate;
        }

        private void ApplyQueuedRuntimeImportQueueUiUpdate()
        {
            _runtimeImportQueueProgressUiUpdateScheduled = false;
            if (!_hasQueuedRuntimeImportQueueProgress)
            {
                return;
            }

            _hasQueuedRuntimeImportQueueProgress = false;
            if (this == null)
            {
                return;
            }

            if (_queuedRuntimeImportQueueProgress == null ||
                !string.Equals(_queuedRuntimeImportQueueProgress.BatchId, _standaloneBatchId, StringComparison.Ordinal))
            {
                return;
            }

            ApplyRuntimeImportQueueProgress(_queuedRuntimeImportQueueProgress);
            UpdateStandaloneImportUiState();
        }

        private void ApplyRuntimeImportQueueProgress(BlmImportQueueProgressContext progress)
        {
            if (progress == null)
            {
                return;
            }

            var remainingCount = Mathf.Max(0, progress.RemainingCount);
            _runtimeImportQueueRemainingCount = remainingCount;

            var currentCount = _importQueueDisplayItems.Count;
            if (currentCount <= remainingCount)
            {
                return;
            }

            var removeCount = currentCount - remainingCount;
            if (removeCount >= currentCount)
            {
                _importQueueDisplayItems.Clear();
            }
            else
            {
                _importQueueDisplayItems.RemoveRange(0, removeCount);
            }

            RefreshImportQueueListView();
        }

        private void OnImportBatchCompleted(BlmImportBatchResultContext result)
        {
            if (result == null || !string.Equals(result.BatchId, _standaloneBatchId, StringComparison.Ordinal))
            {
                return;
            }

            if (_context == null || _context.InvocationContext != BlmInvocationContext.Standalone)
            {
                return;
            }

            _standaloneImportQueueStarting = false;
            _standaloneBatchId = string.Empty;
            _runtimeImportQueueRemainingCount = -1;
            _pendingStandaloneImportStartBatch = null;
            SetImportQuietMode(false);
            CancelImportStartImportedStateCheck();
            ClearQueuedRuntimeImportQueueUiUpdate();
            UpdateStandaloneImportUiState();
            var resumedDetailFileLoad = false;
            var removedImportedItems = RemoveImportedItemsFromSelection(result.SucceededItems);
            if (!removedImportedItems)
            {
                RefreshImportQueueFromSelection();
            }

            if (_detailItem != null)
            {
                if (!resumedDetailFileLoad)
                {
                    StartImportedStateEvaluation(_detailItem, _detailFiles);
                }

                RequestDetailFileListRebuild();
                RebuildSelectedPanel();
            }
            else
            {
                UpdateImportedStateLabel(null);
            }

            if (result.ImportStatus == AmariUnityPackagePipelineOperationStatus.Completed)
            {
                EditorUtility.DisplayDialog(
                    L("blm.import.completed.title", "BLM Integration Core"),
                    L("blm.import.completed.message", "Import completed."),
                    "OK");
                return;
            }

            if (result.ImportStatus == AmariUnityPackagePipelineOperationStatus.Cancelled)
            {
                var cancelledMessage = BuildLocalizedCancelledMessage(result);
                EditorUtility.DisplayDialog(
                    L("blm.import.cancelled.title", "BLM Integration Core"),
                    cancelledMessage,
                    "OK");
                return;
            }

            EditorUtility.DisplayDialog(
                L("blm.import.failed.title", "BLM Integration Core"),
                BuildLocalizedFailedMessage(result),
                "OK");
        }

        private string BuildLocalizedCancelledMessage(BlmImportBatchResultContext result)
        {
            if (result == null)
            {
                return L("blm.import.cancelled.message", "Import cancelled.");
            }

            var packageName = ResolveResultPackageNameForDialog(result);

            switch (result.CancellationReason)
            {
                case AmariUnityPackageImportCancellationReason.WindowClosedFallback:
                    return FormatLocalizedReasonMessage(
                        packageName,
                        keyWithoutPath: "blm.import.cancelled.by_window_close",
                        fallbackWithoutPath: "Package import was cancelled by closing the import window.",
                        keyWithPath: "blm.import.cancelled.by_window_close_with_path",
                        fallbackWithPath: "Package import was cancelled by closing the import window: {0}");

                case AmariUnityPackageImportCancellationReason.HangTimeoutAfterImportConfirm:
                    return FormatLocalizedReasonMessage(
                        packageName,
                        keyWithoutPath: "blm.import.cancelled.by_hang_timeout",
                        fallbackWithoutPath: "Package import was cancelled because Unity stopped responding after the import was confirmed.",
                        keyWithPath: "blm.import.cancelled.by_hang_timeout_with_path",
                        fallbackWithPath: "Package import was cancelled because Unity stopped responding after the import was confirmed: {0}");

                case AmariUnityPackageImportCancellationReason.PipelineReset:
                    return FormatLocalizedReasonMessage(
                        packageName,
                        keyWithoutPath: "blm.import.cancelled.by_pipeline_reset",
                        fallbackWithoutPath: "Import was cancelled because the import pipeline was reset.",
                        keyWithPath: "blm.import.cancelled.by_pipeline_reset_with_path",
                        fallbackWithPath: "Import was cancelled because the import pipeline was reset: {0}");

                case AmariUnityPackageImportCancellationReason.StaleRecovery:
                    return FormatLocalizedReasonMessage(
                        packageName,
                        keyWithoutPath: "blm.import.cancelled.by_stale_recovery",
                        fallbackWithoutPath: "Import was cancelled while recovering inconsistent pipeline state.",
                        keyWithPath: "blm.import.cancelled.by_stale_recovery_with_path",
                        fallbackWithPath: "Import was cancelled while recovering inconsistent pipeline state: {0}");

                default:
                    return FormatLocalizedReasonMessage(
                        packageName,
                        keyWithoutPath: "blm.import.cancelled.message",
                        fallbackWithoutPath: "Import cancelled.",
                        keyWithPath: "blm.import.cancelled.with_package",
                        fallbackWithPath: "Import cancelled: {0}");
            }
        }

        private string BuildLocalizedFailedMessage(BlmImportBatchResultContext result)
        {
            if (result == null)
            {
                return L("blm.import.failed.message", "Import failed.");
            }

            var packageName = ResolveResultPackageNameForDialog(result);

            switch (result.FailureReason)
            {
                case AmariUnityPackageImportFailureReason.PackageImportWindowTypesUnresolved:
                    return FormatLocalizedReasonMessage(
                        packageName,
                        keyWithoutPath: "blm.import.failed.by_window_types_unresolved",
                        fallbackWithoutPath: "Failed to start interactive package import (package import window could not be resolved).",
                        keyWithPath: "blm.import.failed.by_window_types_unresolved_with_path",
                        fallbackWithPath: "Failed to start interactive package import (package import window could not be resolved): {0}");

                default:
                    if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        return result.ErrorMessage;
                    }

                    return FormatLocalizedReasonMessage(
                        packageName,
                        keyWithoutPath: "blm.import.failed.message",
                        fallbackWithoutPath: "Import failed.",
                        keyWithPath: "blm.import.failed.with_package",
                        fallbackWithPath: "Import failed: {0}");
            }
        }

        private string FormatLocalizedReasonMessage(
            string packageName,
            string keyWithoutPath,
            string fallbackWithoutPath,
            string keyWithPath,
            string fallbackWithPath)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                return L(keyWithoutPath, fallbackWithoutPath);
            }

            return string.Format(L(keyWithPath, fallbackWithPath), packageName);
        }

        private static string ResolveResultPackageNameForDialog(BlmImportBatchResultContext result)
        {
            var sourcePath = result?.FailedItems?
                .FirstOrDefault(item => item != null && !string.IsNullOrWhiteSpace(item.SourcePath))
                ?.SourcePath;
            return string.IsNullOrWhiteSpace(sourcePath)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(sourcePath);
        }

        private BlmImportBatchRequest BuildBatch()
        {
            var batch = new BlmImportBatchRequest
            {
                BatchId = Guid.NewGuid().ToString("N"),
                InvocationContext = _context?.InvocationContext ?? BlmInvocationContext.Integration,
                Items = BuildBatchItems()
            };

            return batch;
        }

        private List<BlmImportRequestItem> BuildBatchItems()
        {
            var items = new List<BlmImportRequestItem>();
            if (_context == null || _selectedOrder.Count == 0)
            {
                return items;
            }

            foreach (var productId in _selectedOrder)
            {
                if (!_selectedByProduct.TryGetValue(productId, out var selected) || selected == null || selected.Count == 0)
                {
                    continue;
                }

                if (!_dbItemsByProductId.TryGetValue(productId, out var item) || item?.Files == null || item.Files.Count == 0)
                {
                    continue;
                }

                var selectedFiles = new List<BatchFileCandidate>(Mathf.Min(selected.Count, item.Files.Count));
                foreach (var file in item.Files)
                {
                    if (file == null || string.IsNullOrWhiteSpace(file.FullPath) || !selected.Contains(file.FullPath))
                    {
                        continue;
                    }

                    selectedFiles.Add(new BatchFileCandidate(file, ExtensionPriority(file.FileExtension)));
                }

                if (selectedFiles.Count == 0)
                {
                    continue;
                }

                selectedFiles.Sort(BatchFileCandidateComparer.Compare);
                foreach (var candidate in selectedFiles)
                {
                    var file = candidate.File;
                    items.Add(new BlmImportRequestItem
                    {
                        ProductId = item.ProductId,
                        ProductName = item.ProductName,
                        ShopName = item.ShopName,
                        SourcePath = file.FullPath,
                        RootFolderPath = item.RootFolderPath,
                        NormalizedRelativePath = BuildNormalizedRelativePath(item.RootFolderPath, file.FullPath),
                        DestinationAssetPaths = new List<string>()
                    });
                }
            }

            return items;
        }

        private void CancelImportStartImportedStateCheck()
        {
            // Import-start partial imported-state pre-check flow was removed intentionally.
        }


        private List<DetailFileListEntry> BuildDetailFileListEntries(
            BlmItemRecord item,
            IReadOnlyList<BlmFileRecord> files,
            HashSet<string> duplicateFileNames)
        {
            _ = duplicateFileNames;
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

            var preferredExtensions = (_context?.PreferredDisplayExtensions ?? new List<string>())
                .Select(NormalizeExtension)
                .Where(ext => !string.IsNullOrWhiteSpace(ext))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var preferredExtension in preferredExtensions)
            {
                if (!filesByExtension.TryGetValue(preferredExtension, out var sectionFiles) || sectionFiles.Count == 0)
                {
                    continue;
                }

                entries.Add(DetailFileListEntry.CreateSection(BuildDetailSectionHeaderText(preferredExtension), preferredExtension));
                AppendDetailSectionTreeEntries(entries, item, preferredExtension, sectionFiles);

                filesByExtension.Remove(preferredExtension);
            }

            var otherFiles = filesByExtension.Values
                .SelectMany(sectionFiles => sectionFiles ?? Enumerable.Empty<BlmFileRecord>())
                .Where(file => file != null)
                .ToList();
            if (otherFiles.Count > 0)
            {
                entries.Add(DetailFileListEntry.CreateSection(BuildDetailSectionHeaderText(string.Empty)));
                AppendDetailSectionTreeEntries(entries, item, string.Empty, otherFiles);
            }

            return entries;
        }

        private void AppendDetailSectionTreeEntries(
            List<DetailFileListEntry> entries,
            BlmItemRecord item,
            string sectionExtension,
            IReadOnlyList<BlmFileRecord> sectionFiles)
        {
            if (entries == null || sectionFiles == null || sectionFiles.Count == 0)
            {
                return;
            }

            var root = BuildDetailFolderTree(item, sectionFiles);
            PopulateDetailFolderDescendants(root);
            var sectionKey = NormalizeDetailSectionKey(sectionExtension);
            AppendDetailFolderEntries(entries, item, root, sectionKey, depth: 0);
            AppendDetailFileEntries(entries, item, root.DirectFiles, depth: 0);
        }

        private void AppendDetailFolderEntries(
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
                var folderKey = BuildDetailFolderKey(item?.ProductId, sectionKey, folder.RelativePath);
                entries.Add(DetailFileListEntry.CreateFolder(
                    displayText: folder.Name,
                    folderKey: folderKey,
                    folderFiles: folder.DescendantFiles,
                    depth: depth));
                if (!IsDetailFolderExpanded(folderKey))
                {
                    continue;
                }

                AppendDetailFolderEntries(entries, item, folder, sectionKey, depth + 1);
                AppendDetailFileEntries(entries, item, folder.DirectFiles, depth + 1);
            }
        }

        private void AppendDetailFileEntries(
            List<DetailFileListEntry> entries,
            BlmItemRecord item,
            IEnumerable<BlmFileRecord> files,
            int depth)
        {
            if (entries == null || files == null)
            {
                return;
            }

            var orderedFiles = files
                .Where(file => file != null)
                .Select(file =>
                {
                    var sortText = BuildTreeFileName(item, file);
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
                entries.Add(DetailFileListEntry.CreateFile(fileEntry.File, fileEntry.DisplayText, depth));
            }
        }

        private string BuildDetailSectionHeaderText(string normalizedExtension)
        {
            if (string.IsNullOrWhiteSpace(normalizedExtension))
            {
                return $"--- {L("blm.detail.other_files", "other files")} ---";
            }

            return $"--- {normalizedExtension.TrimStart('.')} ---";
        }

        private int ExtensionPriority(string extension)
        {
            var ext = NormalizeExtension(extension);
            var preferredDisplayExtensions = _context?.PreferredDisplayExtensions;
            if (preferredDisplayExtensions == null)
            {
                return int.MaxValue;
            }

            for (var i = 0; i < preferredDisplayExtensions.Count; i++)
            {
                if (NormalizeExtension(preferredDisplayExtensions[i]) == ext) return i;
            }
            return int.MaxValue;
        }

        private void SetupPaginationFooter()
        {
            if (_paginationCurrentField != null)
            {
                _paginationCurrentField.RegisterValueChangedCallback(OnPaginationFieldValueChanged);
                _paginationCurrentField.RegisterCallback<KeyDownEvent>(OnPaginationFieldKeyDown);
                _paginationCurrentField.RegisterCallback<FocusOutEvent>(OnPaginationFieldFocusOut);
            }

            BindPaginationButton(_paginationMinPageButton);
            BindPaginationButton(_paginationPrev2Button);
            BindPaginationButton(_paginationPrev1Button);
            BindPaginationButton(_paginationMaxPageButton);
            BindPaginationButton(_paginationNext1Button);
            BindPaginationButton(_paginationNext2Button);
        }

        private void BindPaginationButton(Button button)
        {
            if (button == null)
            {
                return;
            }

            button.clicked += () => OnPaginationButtonClicked(button);
        }

        private void OnPaginationButtonClicked(Button button)
        {
            if (button?.userData is int targetPage)
            {
                NavigateToPage(targetPage);
            }
        }

        private void OnPaginationFieldValueChanged(ChangeEvent<string> evt)
        {
            if (_suppressPaginationFieldCallback)
            {
                return;
            }

            var input = evt.newValue ?? string.Empty;
            var filtered = new string(input.Where(char.IsDigit).ToArray());
            if (!string.Equals(filtered, input, StringComparison.Ordinal))
            {
                _suppressPaginationFieldCallback = true;
                _paginationCurrentField.SetValueWithoutNotify(filtered);
                _suppressPaginationFieldCallback = false;
            }
        }

        private void OnPaginationFieldKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
            {
                return;
            }

            CommitPaginationFieldInput();
            evt.StopPropagation();
        }

        private void OnPaginationFieldFocusOut(FocusOutEvent evt)
        {
            CommitPaginationFieldInput();
        }

        private void CommitPaginationFieldInput()
        {
            if (_paginationCurrentField == null)
            {
                return;
            }

            var text = _paginationCurrentField.value ?? string.Empty;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                SetPaginationFieldTextSilently(_page.ToString(CultureInfo.InvariantCulture));
                return;
            }

            NavigateToPage(parsed);
        }

        private void NavigateToPage(int targetPage)
        {
            var total = TotalPages();
            var clamped = Mathf.Clamp(targetPage, 1, total);
            if (clamped == _page)
            {
                SetPaginationFieldTextSilently(_page.ToString(CultureInfo.InvariantCulture));
                return;
            }

            _page = clamped;
            RebuildGrid();
        }

        private void SetPaginationFieldTextSilently(string text)
        {
            if (_paginationCurrentField == null)
            {
                return;
            }

            _suppressPaginationFieldCallback = true;
            _paginationCurrentField.SetValueWithoutNotify(text ?? string.Empty);
            _suppressPaginationFieldCallback = false;
        }

        private void RefreshPaginationFooter()
        {
            var total = TotalPages();
            var current = Mathf.Clamp(_page, 1, total);

            SetPaginationFieldTextSilently(current.ToString(CultureInfo.InvariantCulture));

            var pagesBelow = current - 1;
            ApplyPaginationButton(_paginationMinPageButton, pagesBelow >= 1, 1);
            ApplyPaginationButton(_paginationPrev1Button, pagesBelow >= 2, current - 1);
            ApplyPaginationButton(_paginationPrev2Button, pagesBelow >= 3, current - 2);
            ApplyPaginationDot(_paginationMinDotLabel, pagesBelow >= 4);

            var pagesAbove = total - current;
            ApplyPaginationButton(_paginationMaxPageButton, pagesAbove >= 1, total);
            ApplyPaginationButton(_paginationNext1Button, pagesAbove >= 2, current + 1);
            ApplyPaginationButton(_paginationNext2Button, pagesAbove >= 3, current + 2);
            ApplyPaginationDot(_paginationMaxDotLabel, pagesAbove >= 4);
        }

        private static void ApplyPaginationButton(Button button, bool visible, int targetPage)
        {
            if (button == null)
            {
                return;
            }

            button.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible)
            {
                button.userData = targetPage;
                button.text = targetPage.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                button.userData = null;
            }
        }

        private static void ApplyPaginationDot(Label label, bool visible)
        {
            if (label == null)
            {
                return;
            }

            label.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnOpenFolderPathClicked()
        {
            if (_detailItem == null)
            {
                return;
            }

            var folderPath = _detailFolderPathLabel?.text;
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                Debug.LogWarning($"[BLM Integration Core] Detail folder path is invalid: {folderPath}");
                return;
            }

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to open detail folder path: {folderPath}, error={ex.Message}");
            }
        }

        private int TotalPages()
        {
            return Mathf.Max(1, Mathf.CeilToInt(_viewItems.Count / (float)_pageSize));
        }

        private int CompareSort(BlmItemRecord a, BlmItemRecord b)
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

        private void UpdateConfirmButtonState()
        {
            var isImportQueueRunning = IsStandaloneImportQueueRunning();
            _confirmButton?.SetEnabled(!isImportQueueRunning && _selectedByProduct.Values.Any(x => x.Count > 0));
            if (!isImportQueueRunning && string.IsNullOrEmpty(_standaloneBatchId))
            {
                RefreshImportQueueFromSelection();
            }
        }

        private void RefreshImportQueueFromSelection()
        {
            if (_context == null || _db?.Items == null)
            {
                SetImportQueueItems(Array.Empty<BlmImportRequestItem>());
                InvalidateImportQueuePreviewCache();
                return;
            }

            if (_importQueuePreviewSelectionVersion != _selectionVersion ||
                _importQueuePreviewDatabaseVersion != _databaseVersion)
            {
                _importQueuePreviewItems.Clear();
                _importQueuePreviewItems.AddRange(BuildBatchItems());
                _importQueuePreviewSelectionVersion = _selectionVersion;
                _importQueuePreviewDatabaseVersion = _databaseVersion;
                _importQueueAppliedSelectionVersion = -1;
                _importQueueAppliedDatabaseVersion = -1;
            }

            if (_importQueueAppliedSelectionVersion == _selectionVersion &&
                _importQueueAppliedDatabaseVersion == _databaseVersion)
            {
                return;
            }

            SetImportQueueItems(_importQueuePreviewItems);
            _importQueueAppliedSelectionVersion = _selectionVersion;
            _importQueueAppliedDatabaseVersion = _databaseVersion;
        }

        private bool RemoveImportedItemsFromSelection(IEnumerable<BlmImportRequestItem> importedItems)
        {
            if (importedItems == null)
            {
                return false;
            }

            var changed = false;
            foreach (var importedItem in importedItems)
            {
                if (importedItem == null ||
                    string.IsNullOrWhiteSpace(importedItem.ProductId) ||
                    string.IsNullOrWhiteSpace(importedItem.SourcePath))
                {
                    continue;
                }

                if (!_selectedByProduct.TryGetValue(importedItem.ProductId, out var selectedFiles))
                {
                    continue;
                }

                if (!selectedFiles.Remove(importedItem.SourcePath))
                {
                    continue;
                }

                changed = true;
                if (selectedFiles.Count == 0)
                {
                    _selectedByProduct.Remove(importedItem.ProductId);
                    _selectedOrder.Remove(importedItem.ProductId);
                }
            }

            if (!changed)
            {
                return false;
            }

            MarkSelectionChanged();
            RequestDetailFileListRebuild();
            RebuildSelectedPanel();
            UpdateConfirmButtonState();
            return true;
        }

        private void SetImportQueueItems(IEnumerable<BlmImportRequestItem> items)
        {
            SetImportQueueItems(BuildImportQueueLabels(items));
        }

        private void SetImportQueueItems(IReadOnlyList<string> nextLabels)
        {
            if (nextLabels == null)
            {
                nextLabels = Array.Empty<string>();
            }

            var previousCount = _importQueueDisplayItems.Count;
            var nextCount = nextLabels.Count;
            if (previousCount != nextCount)
            {
                _importQueueDisplayItems.Clear();
                for (var i = 0; i < nextLabels.Count; i++)
                {
                    _importQueueDisplayItems.Add(nextLabels[i] ?? string.Empty);
                }

                RefreshImportQueueListView(forceRebuild: true);
                return;
            }

            if (nextCount <= 0)
            {
                return;
            }

            List<int> changedIndices = null;
            for (var i = 0; i < nextCount; i++)
            {
                var nextLabel = nextLabels[i] ?? string.Empty;
                if (string.Equals(_importQueueDisplayItems[i], nextLabel, StringComparison.Ordinal))
                {
                    continue;
                }

                _importQueueDisplayItems[i] = nextLabel;
                changedIndices ??= new List<int>();
                changedIndices.Add(i);
            }

            if (changedIndices == null || changedIndices.Count == 0)
            {
                return;
            }

            RefreshImportQueueListView(changedIndices.Count > ImportQueueDiffRefreshThreshold);
        }

        private void SetRuntimeImportQueueItems(IEnumerable<BlmImportRequestItem> items)
        {
            ClearQueuedRuntimeImportQueueUiUpdate();
            _importQueueAppliedSelectionVersion = -1;
            _importQueueAppliedDatabaseVersion = -1;
            SetImportQueueItems(items);
            _runtimeImportQueueRemainingCount = _importQueueDisplayItems.Count;
        }

        private void ClearQueuedRuntimeImportQueueUiUpdate()
        {
            _hasQueuedRuntimeImportQueueProgress = false;
            _queuedRuntimeImportQueueProgress = null;
        }

        private void RefreshImportQueueListView(bool forceRebuild = false)
        {
            if (_importQueueListView == null)
            {
                return;
            }

            if (forceRebuild)
            {
                _importQueueListView.Rebuild();
                ApplyImportQueueEmptyLabelText();
                return;
            }

            try
            {
                _importQueueListView.RefreshItems();
            }
            catch
            {
                _importQueueListView.Rebuild();
            }

            ApplyImportQueueEmptyLabelText();
        }

        private void StartImportedStateEvaluation(BlmItemRecord item, IReadOnlyList<BlmFileRecord> files)
        {
            if (_importQuietModeEnabled)
            {
                UpdateImportedStateLabel(item);
                return;
            }

            CancelImportedStateEvaluation();

            var normalizedProductId = item?.ProductId ?? string.Empty;
            if (item == null || string.IsNullOrWhiteSpace(normalizedProductId) || files == null || files.Count == 0)
            {
                UpdateImportedStateLabel(item);
                return;
            }

            _importedStateEvaluationVersion++;
            _activeImportedStateProductId = normalizedProductId;
            _activeImportedStateHasImportedFiles = false;
            _activeImportedStatePendingCount = 0;
            _activeImportedStateTotalCount = 0;
            _activeImportedStateImportIndexFingerprint = BlmImportIndexService.Shared.GetIndexFileFingerprint();

            ClearImportedStateForProduct(normalizedProductId);
            _pendingImportedStateFiles = files;
            _pendingImportedStateItem = item;
            _pendingImportedStateFileIndex = 0;
            _activeImportedStatePendingCount = _pendingImportedStateFiles?.Count ?? 0;
            _activeImportedStateTotalCount = _activeImportedStatePendingCount;

            if (_activeImportedStatePendingCount > 0)
            {
                _activeImportedStateCancellationTokenSource = new CancellationTokenSource();
                EnsureImportedStateCheckLoop();
            }

            UpdateImportedStateLabel(item);
        }

        private void CancelImportedStateEvaluation()
        {
            _pendingImportedStateFiles = Array.Empty<BlmFileRecord>();
            _pendingImportedStateItem = null;
            _pendingImportedStateFileIndex = 0;
            _activeImportedStatePreloadTask = null;
            _activeImportedStatePreloadWorkItem = null;
            _activeImportedStateUnityPackageCheck = null;
            _activeImportedStateNonUnityContentCheck = null;
            _activeImportedStatePendingCount = 0;
            _activeImportedStateTotalCount = 0;
            _activeImportedStateHasImportedFiles = false;
            _activeImportedStateProductId = string.Empty;
            _activeImportedStateImportIndexFingerprint = "0:0";
            CancelActiveImportedStateTasks(disposeSource: true);
            StopImportedStateCheckLoop();
        }

        private void EnsureImportedStateCheckLoop()
        {
            if (_importedStateCheckLoopSubscribed)
            {
                return;
            }

            EditorApplication.update += ProcessImportedStateCheckQueue;
            _importedStateCheckLoopSubscribed = true;
        }

        private void StopImportedStateCheckLoop()
        {
            if (!_importedStateCheckLoopSubscribed)
            {
                return;
            }

            EditorApplication.update -= ProcessImportedStateCheckQueue;
            _importedStateCheckLoopSubscribed = false;
        }

        private void ProcessImportedStateCheckQueue()
        {
            if (_importQuietModeEnabled)
            {
                return;
            }

            if (_activeImportedStatePreloadTask != null)
            {
                if (!_activeImportedStatePreloadTask.IsCompleted)
                {
                    return;
                }

                var preloadedItem = _activeImportedStatePreloadWorkItem;
                if (_activeImportedStatePreloadTask.IsCompletedSuccessfully)
                {
                    preloadedItem = _activeImportedStatePreloadTask.Result;
                }

                if (preloadedItem.HasValue)
                {
                    var workItem = preloadedItem.Value;
                    if (workItem.EvaluationVersion == _importedStateEvaluationVersion)
                    {
                        BeginImportedStateUnityPackageCheck(workItem);
                    }
                }

                _activeImportedStatePreloadTask = null;
                _activeImportedStatePreloadWorkItem = null;
            }

            if (ProcessActiveImportedStateUnityPackageCheck())
            {
                if (ShouldStopImportedStateCheckLoop())
                {
                    StopImportedStateCheckLoop();
                }

                return;
            }

            if (ProcessActiveImportedStateNonUnityContentCheck())
            {
                if (ShouldStopImportedStateCheckLoop())
                {
                    StopImportedStateCheckLoop();
                }

                return;
            }

            var processed = 0;
            while (processed < ImportedStateChecksPerUpdate &&
                   _pendingImportedStateFileIndex >= 0 &&
                   _pendingImportedStateFileIndex < _pendingImportedStateFiles.Count)
            {
                var file = _pendingImportedStateFiles[_pendingImportedStateFileIndex];
                _pendingImportedStateFileIndex++;
                if (file == null)
                {
                    _activeImportedStatePendingCount = Math.Max(0, _activeImportedStatePendingCount - 1);
                    continue;
                }

                var workItem = new ImportedStateCheckWorkItem(
                    _importedStateEvaluationVersion,
                    _pendingImportedStateItem,
                    file,
                    _activeImportedStateImportIndexFingerprint);
                processed++;
                if (RequiresImportedStateBackgroundPreload(workItem.File))
                {
                    _activeImportedStatePreloadWorkItem = workItem;
                    var cancellationToken = _activeImportedStateCancellationTokenSource?.Token ?? CancellationToken.None;
                    _activeImportedStatePreloadTask = Task.Run(
                        () => PreloadImportedStateWorkItem(workItem, cancellationToken),
                        cancellationToken);
                    UpdateImportedStateLabel(workItem.Item ?? _detailItem);
                    break;
                }

                if (TryBeginImportedStateNonUnityContentCheck(workItem))
                {
                    break;
                }
            }

            if (ShouldStopImportedStateCheckLoop())
            {
                StopImportedStateCheckLoop();
            }
        }

        private bool ProcessActiveImportedStateUnityPackageCheck()
        {
            var state = _activeImportedStateUnityPackageCheck;
            if (state == null)
            {
                return false;
            }

            if (state.WorkItem.EvaluationVersion != _importedStateEvaluationVersion)
            {
                _activeImportedStateUnityPackageCheck = null;
                return true;
            }

            if (state.ActiveAssetHashComparisonTask != null)
            {
                if (!state.ActiveAssetHashComparisonTask.IsCompleted)
                {
                    return true;
                }

                var isHashMatch = state.ActiveAssetHashComparisonTask.IsCompletedSuccessfully &&
                                  state.ActiveAssetHashComparisonTask.Result;
                state.ActiveAssetHashComparisonTask = null;
                if (state.PendingCacheEntry != null)
                {
                    _importedStateCacheService.Upsert(state.PendingCacheEntry, isHashMatch);
                    state.PendingCacheEntry = null;
                }
                if (isHashMatch)
                {
                    state.HasImportedEntries = true;
                }
                else
                {
                    state.HasMissingEntries = true;
                }
            }

            var checksRemaining = ImportedStateUnityPackageGuidChecksPerUpdate;
            while (checksRemaining > 0 &&
                   state.NextRecordIndex < state.Records.Count &&
                   !(state.HasImportedEntries && state.HasMissingEntries))
            {
                var record = state.Records[state.NextRecordIndex];
                state.NextRecordIndex++;
                checksRemaining--;

                if (string.IsNullOrWhiteSpace(record.Guid) ||
                    !BlmImportIndexService.Shared.TryGetImportedGuidAssetPathForProduct(
                        state.WorkItem.Item?.ProductId,
                        record.Guid,
                        out var destinationAssetPath))
                {
                    state.HasMissingEntries = true;
                    continue;
                }

                if (!BlmImportIndexService.Shared.TryResolveAssetAbsolutePath(destinationAssetPath, out var destinationAbsolutePath))
                {
                    state.HasMissingEntries = true;
                    continue;
                }

                var processed = record.Kind == ImportedStateUnityPackageRecordKind.Asset
                    ? TryProcessAssetRecord(state, record, destinationAbsolutePath)
                    : TryProcessMetaRecord(state, record, destinationAbsolutePath);
                if (processed == ImportedStateRecordProcessingResult.HashTaskLaunched)
                {
                    break;
                }
            }

            if (state.ActiveAssetHashComparisonTask == null &&
                ((state.HasImportedEntries && state.HasMissingEntries) ||
                 state.NextRecordIndex >= state.Records.Count))
            {
                var importedState = DetermineUnityPackageImportedState(state.HasImportedEntries, state.HasMissingEntries);
                _activeImportedStateUnityPackageCheck = null;
                UpdateImportedStateForFile(
                    state.WorkItem.Item?.ProductId,
                    state.WorkItem.File,
                    importedState);
            }
            else
            {
                UpdateImportedStateLabel(state.WorkItem.Item ?? _detailItem);
            }

            return true;
        }

        private enum ImportedStateRecordProcessingResult
        {
            ResolvedSynchronously = 0,
            HashTaskLaunched = 1
        }

        private ImportedStateRecordProcessingResult TryProcessAssetRecord(
            ImportedStateUnityPackageCheckState state,
            ImportedStateUnityPackageCheckRecord record,
            string destinationAbsolutePath)
        {
            if (string.IsNullOrWhiteSpace(record.AssetSha256))
            {
                state.HasMissingEntries = true;
                return ImportedStateRecordProcessingResult.ResolvedSynchronously;
            }

            BlmImportedStateCacheEntry cacheEntry = null;
            if (TryGetDestinationFileSnapshot(destinationAbsolutePath, out var destFileSize, out var destLastWriteTimeUtcTicks) &&
                _importedStateCacheService.TryBuildEntry(
                    state.WorkItem.Item?.ProductId,
                    state.WorkItem.File?.FullPath,
                    state.WorkItem.ImportIndexFingerprint,
                    record.Guid,
                    destFileSize,
                    destLastWriteTimeUtcTicks,
                    out var builtCacheEntry))
            {
                cacheEntry = builtCacheEntry;
                if (_importedStateCacheService.TryGet(cacheEntry, out var cachedImported))
                {
                    if (cachedImported)
                    {
                        state.HasImportedEntries = true;
                    }
                    else
                    {
                        state.HasMissingEntries = true;
                    }

                    return ImportedStateRecordProcessingResult.ResolvedSynchronously;
                }
            }

            var cancellationToken = _activeImportedStateCancellationTokenSource?.Token ?? CancellationToken.None;
            var expectedSha256 = record.AssetSha256;
            state.PendingCacheEntry = cacheEntry;
            state.ActiveAssetHashComparisonTask = Task.Run(
                () => TryIsUnityPackageAssetHashMatch(destinationAbsolutePath, expectedSha256, cancellationToken),
                cancellationToken);
            return ImportedStateRecordProcessingResult.HashTaskLaunched;
        }

        private ImportedStateRecordProcessingResult TryProcessMetaRecord(
            ImportedStateUnityPackageCheckState state,
            ImportedStateUnityPackageCheckRecord record,
            string destinationAbsolutePath)
        {
            var absoluteMetaPath = destinationAbsolutePath + ".meta";
            if (!File.Exists(absoluteMetaPath))
            {
                state.HasMissingEntries = true;
                return ImportedStateRecordProcessingResult.ResolvedSynchronously;
            }

            if (!string.IsNullOrWhiteSpace(record.MetaGuid) &&
                TryReadMetaGuidFromFile(absoluteMetaPath, out var projectMetaGuid))
            {
                if (string.Equals(projectMetaGuid, record.MetaGuid, StringComparison.OrdinalIgnoreCase))
                {
                    state.HasImportedEntries = true;
                }
                else
                {
                    state.HasMissingEntries = true;
                }

                return ImportedStateRecordProcessingResult.ResolvedSynchronously;
            }

            if (string.IsNullOrWhiteSpace(record.MetaSha256))
            {
                state.HasMissingEntries = true;
                return ImportedStateRecordProcessingResult.ResolvedSynchronously;
            }

            BlmImportedStateCacheEntry cacheEntry = null;
            if (TryGetDestinationFileSnapshot(absoluteMetaPath, out var metaFileSize, out var metaLastWriteTimeUtcTicks) &&
                _importedStateCacheService.TryBuildEntry(
                    state.WorkItem.Item?.ProductId,
                    state.WorkItem.File?.FullPath,
                    state.WorkItem.ImportIndexFingerprint,
                    record.Guid,
                    metaFileSize,
                    metaLastWriteTimeUtcTicks,
                    out var builtCacheEntry))
            {
                cacheEntry = builtCacheEntry;
                if (_importedStateCacheService.TryGet(cacheEntry, out var cachedImported))
                {
                    if (cachedImported)
                    {
                        state.HasImportedEntries = true;
                    }
                    else
                    {
                        state.HasMissingEntries = true;
                    }

                    return ImportedStateRecordProcessingResult.ResolvedSynchronously;
                }
            }

            var cancellationToken = _activeImportedStateCancellationTokenSource?.Token ?? CancellationToken.None;
            var expectedSha256 = record.MetaSha256;
            state.PendingCacheEntry = cacheEntry;
            state.ActiveAssetHashComparisonTask = Task.Run(
                () => TryIsUnityPackageAssetHashMatch(absoluteMetaPath, expectedSha256, cancellationToken),
                cancellationToken);
            return ImportedStateRecordProcessingResult.HashTaskLaunched;
        }

        private bool ProcessActiveImportedStateNonUnityContentCheck()
        {
            var state = _activeImportedStateNonUnityContentCheck;
            if (state == null)
            {
                return false;
            }

            if (!state.HashComparisonTask.IsCompleted)
            {
                return true;
            }

            _activeImportedStateNonUnityContentCheck = null;
            if (state.WorkItem.EvaluationVersion != _importedStateEvaluationVersion)
            {
                return true;
            }

            var isImported = state.HashComparisonTask.IsCompletedSuccessfully &&
                             state.HashComparisonTask.Result;
            if (state.CacheEntry != null)
            {
                _importedStateCacheService.Upsert(state.CacheEntry, isImported);
            }

            UpdateImportedStateForFile(
                state.WorkItem.Item?.ProductId,
                state.WorkItem.File,
                isImported);
            return true;
        }

        private bool TryBeginImportedStateNonUnityContentCheck(ImportedStateCheckWorkItem workItem)
        {
            if (workItem.Item == null ||
                workItem.File == null ||
                BlmImportedFileStateEvaluator.IsUnityPackageFile(workItem.File))
            {
                UpdateImportedStateForFile(workItem.Item?.ProductId, workItem.File, false);
                return false;
            }

            if (!_importedFileStateEvaluator.TryPrepareNonUnityImportedStateCheck(
                    workItem.Item,
                    workItem.File,
                    out var preparedCheck))
            {
                UpdateImportedStateForFile(workItem.Item?.ProductId, workItem.File, false);
                return false;
            }

            BlmImportedStateCacheEntry cacheEntry = null;
            if (_importedStateCacheService.TryBuildEntry(
                    workItem.Item.ProductId,
                    workItem.File.FullPath,
                    workItem.ImportIndexFingerprint,
                    preparedCheck.Guid,
                    preparedCheck.DestinationFileSize,
                    preparedCheck.DestinationLastWriteTimeUtcTicks,
                    out var builtCacheEntry))
            {
                cacheEntry = builtCacheEntry;
                if (_importedStateCacheService.TryGet(cacheEntry, out var cachedImported))
                {
                    UpdateImportedStateForFile(workItem.Item?.ProductId, workItem.File, cachedImported);
                    return false;
                }
            }

            var cancellationToken = _activeImportedStateCancellationTokenSource?.Token ?? CancellationToken.None;
            var hashComparisonTask = Task.Run(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var isImported = TryAreFilesContentEqualWithCancellation(
                                     preparedCheck.SourceFullPath,
                                     preparedCheck.DestinationFullPath,
                                     cancellationToken,
                                     out var areEqual) &&
                                 areEqual;
                return !cancellationToken.IsCancellationRequested && isImported;
            }, cancellationToken);
            _activeImportedStateNonUnityContentCheck = new ImportedStateNonUnityContentCheckState(
                workItem,
                cacheEntry,
                hashComparisonTask);
            return true;
        }

        private void BeginImportedStateUnityPackageCheck(ImportedStateCheckWorkItem workItem)
        {
            if (workItem.EvaluationVersion != _importedStateEvaluationVersion)
            {
                return;
            }

            if (!BlmUnityPackageGuidCache.Shared.TryGetContentEntries(workItem.File?.FullPath, out var contentEntries, out _) ||
                contentEntries == null ||
                contentEntries.Count == 0)
            {
                UpdateImportedStateForFile(
                    workItem.Item?.ProductId,
                    workItem.File,
                    ImportedStateRowHighlightKind.None);
                return;
            }

            var records = BuildImportedStateUnityPackageCheckRecords(contentEntries);
            if (records.Count == 0)
            {
                UpdateImportedStateForFile(
                    workItem.Item?.ProductId,
                    workItem.File,
                    ImportedStateRowHighlightKind.None);
                return;
            }

            _activeImportedStateUnityPackageCheck = new ImportedStateUnityPackageCheckState(workItem, records);
        }

        private static IReadOnlyList<ImportedStateUnityPackageCheckRecord> BuildImportedStateUnityPackageCheckRecords(
            IReadOnlyList<AmariUnityPackageContentEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return Array.Empty<ImportedStateUnityPackageCheckRecord>();
            }

            var records = new List<ImportedStateUnityPackageCheckRecord>(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Guid) ||
                    string.IsNullOrWhiteSpace(entry.Pathname) ||
                    (!entry.HasAsset && !entry.HasMeta))
                {
                    continue;
                }

                if (entry.HasAsset)
                {
                    records.Add(new ImportedStateUnityPackageCheckRecord(
                        entry.Guid,
                        ImportedStateUnityPackageRecordKind.Asset,
                        entry.AssetSha256,
                        string.Empty,
                        string.Empty));
                }

                if (entry.HasMeta)
                {
                    records.Add(new ImportedStateUnityPackageCheckRecord(
                        entry.Guid,
                        ImportedStateUnityPackageRecordKind.Meta,
                        string.Empty,
                        entry.MetaSha256,
                        entry.MetaGuid));
                }
            }

            return records;
        }

        private bool ShouldStopImportedStateCheckLoop()
        {
            return _activeImportedStatePreloadTask == null &&
                   _activeImportedStateUnityPackageCheck == null &&
                   _activeImportedStateNonUnityContentCheck == null &&
                   (_pendingImportedStateFiles == null ||
                    _pendingImportedStateFileIndex >= _pendingImportedStateFiles.Count);
        }

        private void UpdateImportedStateForFile(string productId, BlmFileRecord file, bool isImported)
        {
            UpdateImportedStateForFile(
                productId,
                file,
                isImported ? ImportedStateRowHighlightKind.Imported : ImportedStateRowHighlightKind.None);
        }

        private void UpdateImportedStateForFile(
            string productId,
            BlmFileRecord file,
            ImportedStateRowHighlightKind importedState)
        {
            var productFileKey = BuildImportedStateProductFileKey(productId, file);
            if (string.IsNullOrWhiteSpace(productFileKey))
            {
                return;
            }

            _importedStateByProductFileKey[productFileKey] = importedState;
            if (string.Equals(productId, _activeImportedStateProductId, StringComparison.Ordinal))
            {
                _activeImportedStatePendingCount = Math.Max(0, _activeImportedStatePendingCount - 1);
                if (importedState != ImportedStateRowHighlightKind.None)
                {
                    _activeImportedStateHasImportedFiles = true;
                }
            }

            RefreshImportedStateVisualForFile(productId, file, importedState);
            UpdateImportedStateLabel(_detailItem);
        }

        private void RefreshImportedStateVisualForFile(
            string productId,
            BlmFileRecord file,
            ImportedStateRowHighlightKind importedState)
        {
            var productFileKey = BuildImportedStateProductFileKey(productId, file);
            if (string.IsNullOrWhiteSpace(productFileKey))
            {
                return;
            }

            if (_detailFileRowsByProductFileKey.TryGetValue(productFileKey, out var detailRows))
            {
                ApplyImportedStateToRowCollection(
                    detailRows,
                    importedState,
                    allowPartialHighlight: true,
                    updateTooltip: true);
            }

            if (_selectedPanelFileRowsByProductFileKey.TryGetValue(productFileKey, out var selectedRows))
            {
                ApplyImportedStateToRowCollection(
                    selectedRows,
                    importedState,
                    allowPartialHighlight: true,
                    updateTooltip: true);
            }
        }

        private ImportedStateRowHighlightKind GetImportedStateForFile(BlmItemRecord item, BlmFileRecord file)
        {
            var key = BuildImportedStateProductFileKey(item?.ProductId, file);
            return !string.IsNullOrWhiteSpace(key) &&
                   _importedStateByProductFileKey.TryGetValue(key, out var importedState)
                ? importedState
                : ImportedStateRowHighlightKind.None;
        }

        private bool HasImportedFilesInSessionState(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId))
            {
                return false;
            }

            var prefix = productId + "|";
            foreach (var pair in _importedStateByProductFileKey)
            {
                if (pair.Value == ImportedStateRowHighlightKind.None)
                {
                    continue;
                }

                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearImportedStateForProduct(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId) || _importedStateByProductFileKey.Count == 0)
            {
                return;
            }

            var prefix = productId + "|";
            var keysToRemove = _importedStateByProductFileKey.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            for (var i = 0; i < keysToRemove.Length; i++)
            {
                _importedStateByProductFileKey.Remove(keysToRemove[i]);
            }
        }

        private void RegisterDetailFileRow(string productId, BlmFileRecord file, VisualElement row)
        {
            if (row == null)
            {
                return;
            }

            UnregisterDetailFileRow(row);
            var productFileKey = BuildImportedStateProductFileKey(productId, file);
            if (string.IsNullOrWhiteSpace(productFileKey))
            {
                return;
            }

            row.userData = productFileKey;
            if (!_detailFileRowsByProductFileKey.TryGetValue(productFileKey, out var rows) || rows == null)
            {
                rows = new List<VisualElement>();
                _detailFileRowsByProductFileKey[productFileKey] = rows;
            }

            rows.Add(row);
        }

        private void UnregisterDetailFileRow(VisualElement row)
        {
            if (row == null)
            {
                return;
            }

            if (row.userData is not string key || string.IsNullOrWhiteSpace(key))
            {
                row.userData = null;
                return;
            }

            if (_detailFileRowsByProductFileKey.TryGetValue(key, out var rows) && rows != null)
            {
                for (var i = rows.Count - 1; i >= 0; i--)
                {
                    if (rows[i] == null || ReferenceEquals(rows[i], row))
                    {
                        rows.RemoveAt(i);
                    }
                }

                if (rows.Count == 0)
                {
                    _detailFileRowsByProductFileKey.Remove(key);
                }
            }

            row.userData = null;
        }

        private void ApplyImportedStateToRowCollection(
            List<VisualElement> rows,
            ImportedStateRowHighlightKind importedState,
            bool allowPartialHighlight,
            bool updateTooltip)
        {
            if (rows == null)
            {
                return;
            }

            for (var i = rows.Count - 1; i >= 0; i--)
            {
                var row = rows[i];
                if (row == null)
                {
                    rows.RemoveAt(i);
                    continue;
                }

                ApplyImportedStateRowClass(row, importedState, allowPartialHighlight);
                if (updateTooltip)
                {
                    SetRowImportedStateTooltip(row, GetImportedStateTooltip(importedState));
                }
            }
        }

        private void RegisterSelectedPanelFileRow(string productId, BlmFileRecord file, VisualElement row)
        {
            if (row == null)
            {
                return;
            }

            var productFileKey = BuildImportedStateProductFileKey(productId, file);
            if (string.IsNullOrWhiteSpace(productFileKey))
            {
                return;
            }

            if (!_selectedPanelFileRowsByProductFileKey.TryGetValue(productFileKey, out var rows) || rows == null)
            {
                rows = new List<VisualElement>();
                _selectedPanelFileRowsByProductFileKey[productFileKey] = rows;
            }

            rows.Add(row);
        }

        private void ApplyImportedStateVisualToFileRow(
            VisualElement row,
            ImportedStateRowHighlightKind importedState)
        {
            ApplyImportedStateRowClass(row, importedState, allowPartialHighlight: true);
            SetRowImportedStateTooltip(row, GetImportedStateTooltip(importedState));
        }

        private void SetRowImportedStateTooltip(VisualElement row, string tooltip)
        {
            if (row == null)
            {
                return;
            }

            var normalizedTooltip = tooltip ?? string.Empty;
            row.tooltip = normalizedTooltip;

            var toggle = row.Q<Toggle>("Toggle");
            if (toggle != null)
            {
                toggle.tooltip = normalizedTooltip;
            }

            var label = row.Q<Label>("Label");
            if (label != null)
            {
                label.tooltip = normalizedTooltip;
            }

            var folderFoldout = row.Q<Foldout>("FolderFoldout");
            if (folderFoldout != null)
            {
                folderFoldout.tooltip = normalizedTooltip;
            }
        }

        private string GetImportedStateTooltip(ImportedStateRowHighlightKind importedState)
        {
            return importedState switch
            {
                ImportedStateRowHighlightKind.Imported =>
                    L("blm.detail.imported_state.tooltip.imported", "Imported"),
                ImportedStateRowHighlightKind.PartiallyImported =>
                    L("blm.detail.imported_state.tooltip.partial", "Partially imported"),
                _ => string.Empty
            };
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

        private void UpdateFilteredProductCount()
        {
            if (_filteredProductCountLabel == null)
            {
                return;
            }

            _filteredProductCountLabel.text = string.Format(
                CultureInfo.InvariantCulture,
                L("blm.filter.filtered_product_count", "{0} product(s) shown"),
                _viewItems?.Count ?? 0);
        }

        private bool IsSelected(string productId, string path)
        {
            return !string.IsNullOrWhiteSpace(productId) &&
                   !string.IsNullOrWhiteSpace(path) &&
                   _selectedByProduct.TryGetValue(productId, out var set) &&
                   set.Contains(path);
        }

        private bool IsImportedStateEvaluationInProgress(string productId)
        {
            return !string.IsNullOrWhiteSpace(productId) &&
                   string.Equals(productId, _activeImportedStateProductId, StringComparison.Ordinal) &&
                   _activeImportedStatePendingCount > 0;
        }

        private string BuildImportedStateCheckingLabelText()
        {
            var totalCount = _activeImportedStateTotalCount;
            if (totalCount < 0)
            {
                totalCount = 0;
            }

            var pendingCount = _activeImportedStatePendingCount;
            if (pendingCount < 0)
            {
                pendingCount = 0;
            }
            else if (pendingCount > totalCount)
            {
                pendingCount = totalCount;
            }

            var checkedCount = totalCount - pendingCount;
            var currentProcessingIndex = totalCount <= 0
                ? 0
                : Math.Min(totalCount, checkedCount + 1);
            var fileLine = string.Format(
                CultureInfo.InvariantCulture,
                L("blm.detail.imported_state.checking", "Checking imported files... ({0}/{1})"),
                currentProcessingIndex,
                totalCount);

            var entryLine = BuildImportedStateCheckingEntriesLabelText();
            return fileLine + "\n" + entryLine;
        }

        private string BuildImportedStateCheckingEntriesLabelText()
        {
            var state = _activeImportedStateUnityPackageCheck;
            if (state != null && state.WorkItem.EvaluationVersion == _importedStateEvaluationVersion)
            {
                var recordsCount = state.Records?.Count ?? 0;
                if (recordsCount > 0)
                {
                    var currentProcessingIndex = Math.Max(1, Math.Min(recordsCount, state.NextRecordIndex));
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        L("blm.detail.imported_state.checking_entries", "Checking entries... ({0}/{1})"),
                        currentProcessingIndex,
                        recordsCount);
                }
            }

            if (_activeImportedStatePreloadTask != null &&
                _activeImportedStatePreloadWorkItem.HasValue &&
                _activeImportedStatePreloadWorkItem.Value.EvaluationVersion == _importedStateEvaluationVersion &&
                RequiresImportedStateBackgroundPreload(_activeImportedStatePreloadWorkItem.Value.File))
            {
                return L("blm.detail.imported_state.parsing_entries", "Parsing entries...");
            }

            return string.Empty;
        }

        private void SetImportedStateLabel(string text, bool visible)
        {
            if (_importedStateLabel == null)
            {
                return;
            }

            var normalizedText = text ?? string.Empty;
            var nextVisibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (!string.Equals(_importedStateLabel.text ?? string.Empty, normalizedText, StringComparison.Ordinal))
            {
                _importedStateLabel.text = normalizedText;
            }

            if (_importedStateLabel.style.display.value != DisplayStyle.Flex)
            {
                _importedStateLabel.style.display = DisplayStyle.Flex;
            }

            if (_importedStateLabel.style.visibility.value != nextVisibility)
            {
                _importedStateLabel.style.visibility = nextVisibility;
            }
        }

        private void UpdateImportedStateLabel(BlmItemRecord item)
        {
            if (_importedStateLabel == null)
            {
                return;
            }

            var productId = item?.ProductId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(productId))
            {
                SetImportedStateLabel(string.Empty, visible: false);
                return;
            }

            var isCheckingImportedState = IsImportedStateEvaluationInProgress(productId);
            if (isCheckingImportedState)
            {
                SetImportedStateLabel(BuildImportedStateCheckingLabelText(), visible: true);
                return;
            }

            var hasImportedFiles = string.Equals(productId, _activeImportedStateProductId, StringComparison.Ordinal)
                ? _activeImportedStateHasImportedFiles
                : HasImportedFilesInSessionState(productId);

            if (hasImportedFiles)
            {
                SetImportedStateLabel(
                    L("blm.detail.imported_state.has_imported_files", "Imported files found"),
                    visible: true);
                return;
            }

            SetImportedStateLabel(string.Empty, visible: false);
        }

        private void ApplyLocalization()
        {
            titleContent = new GUIContent(L("blm.window.title", BlmConstants.WindowTitle));
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
                ReplaceChoicesPreservingIndex(
                    _selectedItemsFilterDropDownField,
                    new List<string>
                    {
                        L("blm.selected_items.filter.selected_and_preferred", "Selected + Preferred"),
                        L("blm.selected_items.filter.selected_only", "Selected Only"),
                        L("blm.selected_items.filter.all", "All")
                    });
                ReplaceChoicesPreservingIndex(_shopFilterSortDropdownField, BuildFilterSortChoices());
                ReplaceChoicesPreservingIndex(_tagFilterSortDropdownField, BuildFilterSortChoices());
                SyncSelectedItemsFilterMode();
                SyncEditorLanguageDropdownChoices();
            });
            if (_editorLanguageDropdownField != null)
            {
                _editorLanguageDropdownField.label = L("blm.editor_language.label", "Language");
            }

            _searchField.label = L("blm.search.label", "Search");
            _pageSizeField.label = L("blm.page_size", "Page size");
            if (_thumbnailCacheMaxEntriesField != null)
            {
                _thumbnailCacheMaxEntriesField.label = L("blm.thumbnail_cache.max_entries_label", "Thumbnail cache max");
            }

            rootVisualElement.Q<Label>("SortLabel").text = L("blm.sort.label", "Sort");
            rootVisualElement.Q<Label>("CategoryFilterLabel").text = L("blm.filter.category", "Category Filter");
            rootVisualElement.Q<Label>("AgeRestrictionFilterLabel").text = L("blm.filter.age.label", "Age Restriction Filter");
            rootVisualElement.Q<Label>("ShopFilterLabel").text = L("blm.filter.shop", "Shop Filter");
            rootVisualElement.Q<Label>("TagFilterLabel").text = L("blm.filter.tag", "Tag Filter");
            rootVisualElement.Q<Label>("SelectedItemsTitleLabel").text = L("blm.selected_items.title", "Selected Items");
            if (_importQueueTitleLabel != null)
            {
                _importQueueTitleLabel.text = L("blm.import_queue.title", "Import Queue");
            }

            var searchFieldClearButton = rootVisualElement.Q<Button>("SearchTextFieldClearButton");
            if (searchFieldClearButton != null)
            {
                searchFieldClearButton.text = L("blm.button.clear", "Clear");
            }

            var shopSearchFieldClearButton = rootVisualElement.Q<Button>("ShopSearchFieldClearButton");
            if (shopSearchFieldClearButton != null)
            {
                shopSearchFieldClearButton.text = L("blm.button.clear", "Clear");
            }

            var tagSearchFieldClearButton = rootVisualElement.Q<Button>("TagSearchFieldClearButton");
            if (tagSearchFieldClearButton != null)
            {
                tagSearchFieldClearButton.text = L("blm.button.clear", "Clear");
            }

            rootVisualElement.Q<Button>("ShopClearButton").text = L("blm.filter.shop_clear", "Clear selected shop(s)");
            rootVisualElement.Q<Button>("TagClearButton").text = L("blm.filter.tag_clear", "Clear selected tag(s)");
            rootVisualElement.Q<Button>("RefreshDbButton").text = L("blm.reload_db", "ReloadDB");
            var deSelectAllFilesButton = rootVisualElement.Q<Button>("DeSelectAllFilesButton");
            if (deSelectAllFilesButton != null)
            {
                deSelectAllFilesButton.text = L("blm.detail.deselect_all_files", "DeSelect all file(s)");
            }

            var deSelectAllProductsFilesButton = rootVisualElement.Q<Button>("DeSelectAllProductsFilesButton");
            if (deSelectAllProductsFilesButton != null)
            {
                deSelectAllProductsFilesButton.text = L("blm.selected_items.deselect_all_products_files", "DeSelect all selected file(s)");
            }

            rootVisualElement.Q<Button>("SelectAllFilesButton").text = L("blm.detail.select_all_files", "Select all file(s)");
            if (_openFolderPathButton != null)
            {
                _openFolderPathButton.text = L("blm.detail.open_folder_path", "Open folder path");
            }

            if (_cancelButton != null)
            {
                _cancelButton.text = L("blm.button.cancel", "Cancel");
            }

            _confirmButton.text = L("blm.button.import", "Import");
            if (_importProcessingTitleLabel != null)
            {
                _importProcessingTitleLabel.text = L("blm.processing.title", "Processing...");
            }

            _detailProductListLabel.text = L("blm.detail.product_files", "Product file(s)");
            _listModeEmptyStateLabel.text = L("blm.list.empty", "No list is available.");
            ApplyImportQueueEmptyLabelText();
            if (_detailItem != null)
            {
                StartImportedStateEvaluation(_detailItem, _detailFiles);
                RequestDetailFileListRebuild();
                RebuildSelectedPanel();
            }
            else
            {
                UpdateImportedStateLabel(null);
            }

            if (!_languageSubscribed)
            {
                EditorLocalization.Service.LanguageChanged += OnLanguageChanged;
                _languageSubscribed = true;
            }

            UpdateStandaloneImportUiState();
        }

        private void ApplyImportQueueEmptyLabelText()
        {
            if (_importQueueListView == null)
            {
                return;
            }

            var emptyLabel = _importQueueListView.Q<Label>(className: "unity-list-view__empty-label")
                ?? _importQueueListView.Q<Label>(className: "unity-collection-view__empty-label");
            if (emptyLabel == null)
            {
                return;
            }

            emptyLabel.text = L("blm.import_queue.empty", "Import queue is empty.");
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
            return EditorLocalization.Service.Get(BlmConstants.LocalizationSourceId, key, fallback);
        }

        private void SyncSelectedItemsFilterMode()
        {
            if (_selectedItemsFilterDropDownField == null)
            {
                _selectedItemsFilterMode = SelectedItemsFilterMode.SelectedAndPreferred;
                return;
            }

            _selectedItemsFilterMode = _selectedItemsFilterDropDownField.index switch
            {
                1 => SelectedItemsFilterMode.SelectedOnly,
                2 => SelectedItemsFilterMode.All,
                _ => SelectedItemsFilterMode.SelectedAndPreferred
            };
        }

        private void SyncEditorLanguageDropdownChoices()
        {
            if (_editorLanguageDropdownField == null)
            {
                return;
            }

            var service = EditorLocalization.Service;
            var choices = new List<string>(service.GetAvailableLanguages(BlmConstants.LocalizationSourceId));
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

    }
}
