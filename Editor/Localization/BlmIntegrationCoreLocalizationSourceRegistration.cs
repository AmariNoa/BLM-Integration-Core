using com.amari_noa.unity_editor_localization_core.editor;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    [InitializeOnLoad]
    internal static class BlmIntegrationCoreLocalizationSourceRegistration
    {
        static BlmIntegrationCoreLocalizationSourceRegistration()
        {
            var localizationFolderGuid = BlmConstants.LocalizationFolderGuid;
            if (string.IsNullOrWhiteSpace(localizationFolderGuid))
            {
                Debug.LogWarning("[BLM Integration Core] Localization folder guid is empty.");
                return;
            }

            var localizationFolderPath = AssetDatabase.GUIDToAssetPath(localizationFolderGuid);
            if (string.IsNullOrWhiteSpace(localizationFolderPath))
            {
                Debug.LogWarning($"[BLM Integration Core] Localization folder guid could not be resolved. guid={localizationFolderGuid}");
                return;
            }

            EditorLocalization.Service.RegisterSource(new EditorLocalizationSourceDefinition
            {
                SourceId = BlmConstants.LocalizationSourceId,
                DisplayName = BlmConstants.LocalizationDisplayName,
                LocalizationFolderGuid = localizationFolderGuid,
                DefaultLanguageCode = BlmConstants.LocalizationDefaultLanguageCode,
                BaseLanguageCode = BlmConstants.LocalizationDefaultLanguageCode
            });
        }
    }
}
