using System.Collections.Generic;
using com.amari_noa.unity_editor_localization_core.editor;
using com.amari_noa.unitypackage_pipeline_core.editor;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class AmariBlmPickerContext
    {
        public AmariBlmInvocationContext InvocationContext = AmariBlmInvocationContext.Integration;
        public List<string> PreferredDisplayExtensions = new List<string>();
        public IAmariUnityPackageImportPipelineService UnityPackageImportPipelineService;
        public IAmariBlmDestinationAssetPathUpdater DestinationAssetPathUpdater;
        public IEditorLocalizationService EditorLocalizationService;
        public string LocalizationSourceId = AmariBlmConstants.LocalizationSourceId;
        public object HostContext;

        public bool ValidateRequiredServices(out string errorMessage)
        {
            if (UnityPackageImportPipelineService == null)
            {
                errorMessage = "UnityPackageImportPipelineService is required.";
                return false;
            }

            if (DestinationAssetPathUpdater == null)
            {
                errorMessage = "DestinationAssetPathUpdater is required.";
                return false;
            }

            if (EditorLocalizationService == null)
            {
                errorMessage = "EditorLocalizationService is required.";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }
    }
}
