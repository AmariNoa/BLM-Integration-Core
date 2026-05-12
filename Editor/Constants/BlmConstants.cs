using System;
using System.IO;

namespace com.amari_noa.blm_integration_core.editor
{
    internal static class BlmConstants
    {
        internal const string LocalizationSourceId = "com.amari-noa.blm-integration-core";
        internal const string LocalizationDisplayName = "BLM Integration Core";
        internal const string LocalizationDefaultLanguageCode = "en-US";
        internal const string LocalizationFolderGuid = "73d9097170a79384193da28e6f2d45b7";
        internal const string CatalogWindowFontAssetGuid = "a9b3494c5d20e964381fd5b2559dca9f";
        internal const string CatalogWindowFontFileGuid = "0482f662366228542b7c537a82068dc5";
        internal const string CatalogWindowEmojiFontFileGuid = "aa38dfc743b0d82428e138f1a9f7d3cf";
        internal const string PerformanceLogPrefix = "[BLM Perf]";
        internal static readonly bool EnablePerformanceLogging = false;

        internal const string WindowTitle = "BLM CatalogWindow";
        internal const string ThumbnailCacheEditorPrefsKey = "com.amari-noa.blm-integration-core.thumbnail-cache-max-entries";
        internal const string PageSizeEditorPrefsKey = "com.amari-noa.blm-integration-core.page-size";
        internal const string SortKeyEditorPrefsKey = "com.amari-noa.blm-integration-core.sort-key";
        internal const string SortOrderEditorPrefsKey = "com.amari-noa.blm-integration-core.sort-order";
        internal const string ShopFilterSortEditorPrefsKey = "com.amari-noa.blm-integration-core.shop-filter-sort";
        internal const string TagFilterSortEditorPrefsKey = "com.amari-noa.blm-integration-core.tag-filter-sort";

        internal static readonly int[] PageSizes = { 50, 100, 150 };

        internal const int DefaultPageSize = 100;
        internal const int ThumbnailMemoryCacheMaxPageCountCap = 25;
        internal static int ThumbnailMemoryCacheMaxEntriesDefault => GetThumbnailMemoryCacheDefaultEntries();
        internal static int ThumbnailMemoryCacheMaxEntriesMin => GetMaxPageSize();
        internal static int ThumbnailMemoryCacheMaxEntriesMax => GetThumbnailMemoryCacheMaxEntries();
        internal const int ThumbnailMaxDisplaySize = 512;
        internal const int ThumbnailRequestConcurrency = 5;
        internal const int ThumbnailRequestTimeoutSeconds = 10;
        internal const int ThumbnailDiskCacheTtlDays = 30;
        internal const int ImportIndexSchemaVersion = 1;
        internal const string ImportIndexRelativePath = "ProjectSettings/com.amari-noa.blm-integration-core.import-index.json";
        internal const int ImportedStateCacheSchemaVersion = 1;
        internal const string ImportedStateCacheRelativePath = "Library/BlmIntegrationCore/imported-state-cache-v1.json";
        internal const int UnityPackageGuidCacheMaxEntries = 300;
        internal const int UnityPackageGuidCacheSchemaVersion = 2;
        internal const string UnityPackageGuidCacheRelativePath = "Library/BlmIntegrationCore/unitypackage-guid-cache-v2.json";
        internal static readonly string[] IntegrationPreferredDisplayExtensions = { ".unitypackage", ".amri" };
        internal static readonly string[] StandalonePreferredDisplayExtensions = { ".unitypackage" };

        internal static string GetDefaultBlmDatabasePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "pm.booth.library-manager", "data.db");
        }

        internal static string GetThumbnailCacheRootPath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "BlmIntegrationCore", "Thumbnails");
        }

        private static int GetMaxPageSize()
        {
            if (PageSizes == null || PageSizes.Length == 0)
            {
                return Math.Max(1, DefaultPageSize);
            }

            var max = PageSizes[0];
            for (var i = 1; i < PageSizes.Length; i++)
            {
                if (PageSizes[i] > max)
                {
                    max = PageSizes[i];
                }
            }

            return Math.Max(1, max);
        }

        private static int GetThumbnailMemoryCacheDefaultEntries()
        {
            var min = ThumbnailMemoryCacheMaxEntriesMin;
            var max = ThumbnailMemoryCacheMaxEntriesMax;
            if (min > max)
            {
                min = max;
            }

            var doubled = min > int.MaxValue / 2 ? int.MaxValue : min * 2;
            if (doubled < min)
            {
                return min;
            }

            if (doubled > max)
            {
                return max;
            }

            return doubled;
        }

        private static int GetThumbnailMemoryCacheMaxEntries()
        {
            var maxPageSize = GetMaxPageSize();
            var rawMax = (long)maxPageSize * ThumbnailMemoryCacheMaxPageCountCap;
            if (rawMax < 1)
            {
                return 1;
            }

            return rawMax > int.MaxValue ? int.MaxValue : (int)rawMax;
        }
    }
}
