using System;
using System.IO;

namespace com.amari_noa.blm_integration_core.editor
{
    internal static class BlmConstants
    {
        internal const string LocalizationSourceId = "com.amari-noa.blm-integration-core";
        internal const string LocalizationDisplayName = "BLM Integration Core";
        internal const string LocalizationDefaultLanguageCode = "en-US";
        internal const string LocalizationFolderAssetPath = "Packages/com.amari-noa.blm-integration-core/Editor/Localization";
        internal const string CatalogWindowFontAssetPath = "Packages/com.amari-noa.blm-integration-core/Fonts/Noto_Sans_JP/static/NotoSansJP-Regular SDF.asset";
        internal const string CatalogWindowFontFileAssetPath = "Packages/com.amari-noa.blm-integration-core/Fonts/Noto_Sans_JP/static/NotoSansJP-Regular.ttf";
        internal const string CatalogWindowEmojiFontFileAssetPath = "Packages/com.amari-noa.blm-integration-core/Fonts/Noto_Color_Emoji/NotoColorEmoji-Regular.ttf";
        internal const string PerformanceLogPrefix = "[BLM Perf]";
        internal static readonly bool EnablePerformanceLogging = false;

        internal const string WindowTitle = "BLM Window";
        internal const string ThumbnailCacheEditorPrefsKey = "com.amari-noa.blm-integration-core.thumbnail-cache-max-entries";

        internal static readonly int[] PageSizes = { 50, 100, 150 };

        internal const int DefaultPageSize = 50;
        internal const int ThumbnailMemoryCacheMaxPageCountCap = 25;
        internal static int ThumbnailMemoryCacheMaxEntriesDefault => GetThumbnailMemoryCacheDefaultEntries();
        internal static int ThumbnailMemoryCacheMaxEntriesMin => GetMaxPageSize();
        internal static int ThumbnailMemoryCacheMaxEntriesMax => GetThumbnailMemoryCacheMaxEntries();
        internal const int ThumbnailMaxDisplaySize = 512;
        internal const int ThumbnailRequestConcurrency = 5;
        internal const int ThumbnailRequestTimeoutSeconds = 10;
        internal const int ThumbnailDiskCacheTtlDays = 30;
        internal const int TagInitialTopCount = 200;
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
