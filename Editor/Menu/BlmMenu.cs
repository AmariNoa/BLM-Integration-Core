using System.Collections.Generic;
using com.amari_noa.unity_editor_localization_core.editor;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEditor;

namespace com.amari_noa.blm_integration_core.editor
{
    internal static class AmariBlmMenu
    {
        [MenuItem("Tools/BLM Integration Core/BLM Window")]
        private static void OpenStandaloneWindow()
        {
            var context = new AmariBlmPickerContext
            {
                InvocationContext = AmariBlmInvocationContext.Standalone,
                PreferredDisplayExtensions = new List<string>(AmariBlmConstants.StandalonePreferredDisplayExtensions),
                UnityPackageImportPipelineService = AmariUnityPackageImportPipeline.Service,
                DestinationAssetPathUpdater = AmariBlmImportProcessor.Shared,
                EditorLocalizationService = EditorLocalization.Service,
                LocalizationSourceId = AmariBlmConstants.LocalizationSourceId
            };

            AmariBlmCatalogWindowGateway.Shared.Open(context);
        }

        [MenuItem("Tools/BLM Integration Core/Clear thumbnail cache")]
        private static void ClearThumbnailCache()
        {
            if (CatalogWindow.IsWindowOpen())
            {
                EditorUtility.DisplayDialog(
                    L("blm.thumbnail_cache.window_open.title", "BLM Window Is Open"),
                    L("blm.thumbnail_cache.window_open.message", "Close the BLM Window before clearing thumbnail cache."),
                    "OK");
                return;
            }

            var service = new AmariBlmThumbnailCacheService();
            service.ClearAllCacheFiles();

            EditorUtility.DisplayDialog(
                L("blm.thumbnail_cache.cleanup_completed.title", "BLM Integration Core"),
                L("blm.thumbnail_cache.cleanup_completed.message", "Thumbnail cache cleanup completed."),
                "OK");
        }

        private static string L(string key, string fallback)
        {
            return EditorLocalization.Service.Get(AmariBlmConstants.LocalizationSourceId, key, fallback);
        }
    }
}
