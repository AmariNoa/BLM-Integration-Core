using System.Collections.Generic;
using com.amari_noa.unity_editor_localization_core.editor;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEditor;

namespace com.amari_noa.blm_integration_core.editor
{
    internal static class BlmMenu
    {
        [MenuItem("BLM Integration Core/BLM CatalogWindow")]
        private static void OpenStandaloneWindow()
        {
            var context = new BlmPickerContext
            {
                InvocationContext = BlmInvocationContext.Standalone,
                PreferredDisplayExtensions = new List<string>(BlmConstants.StandalonePreferredDisplayExtensions),
                UnityPackageImportPipelineService = AmariUnityPackageImportPipeline.Service,
                DestinationAssetPathUpdater = BlmImportProcessor.Shared,
                EditorLocalizationService = EditorLocalization.Service,
                LocalizationSourceId = BlmConstants.LocalizationSourceId
            };

            BlmCatalogWindowGateway.Shared.Open(context);
        }

        [MenuItem("BLM Integration Core/Clear thumbnail cache")]
        private static void ClearThumbnailCache()
        {
            if (CatalogWindow.IsWindowOpen())
            {
                EditorUtility.DisplayDialog(
                    L("blm.thumbnail_cache.window_open.title", "BLM Integration Core"),
                    L("blm.thumbnail_cache.window_open.message", "Close the BLM CatalogWindow before clearing thumbnail cache."),
                    "OK");
                return;
            }

            var service = new BlmThumbnailCacheService();
            service.ClearAllCacheFiles();

            EditorUtility.DisplayDialog(
                L("blm.thumbnail_cache.cleanup_completed.title", "BLM Integration Core"),
                L("blm.thumbnail_cache.cleanup_completed.message", "Thumbnail cache cleanup completed."),
                "OK");
        }

        private static string L(string key, string fallback)
        {
            return EditorLocalization.Service.Get(BlmConstants.LocalizationSourceId, key, fallback);
        }
    }
}
