using com.amari_noa.unity_editor_localization_core.editor;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    [InitializeOnLoad]
    internal static class AmariBlmIntegrationCoreLocalizationSourceRegistration
    {
        static AmariBlmIntegrationCoreLocalizationSourceRegistration()
        {
            var localizationFolderGuid = AssetDatabase.AssetPathToGUID(AmariBlmConstants.LocalizationFolderAssetPath);
            if (string.IsNullOrWhiteSpace(localizationFolderGuid))
            {
                Debug.LogWarning("[BLM Integration Core] Localization folder guid could not be resolved.");
                return;
            }

            EditorLocalization.Service.RegisterSource(new EditorLocalizationSourceDefinition
            {
                SourceId = AmariBlmConstants.LocalizationSourceId,
                DisplayName = AmariBlmConstants.LocalizationDisplayName,
                LocalizationFolderGuid = localizationFolderGuid,
                DefaultLanguageCode = AmariBlmConstants.LocalizationDefaultLanguageCode,
                BaseLanguageCode = AmariBlmConstants.LocalizationDefaultLanguageCode
            });
        }
    }
}
