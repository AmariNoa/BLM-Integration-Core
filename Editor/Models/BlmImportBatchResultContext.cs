using System.Collections.Generic;
using com.amari_noa.unitypackage_pipeline_core.editor;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class BlmImportBatchResultContext
    {
        public string BatchId = string.Empty;
        public BlmInvocationContext InvocationContext = BlmInvocationContext.Integration;
        public List<BlmImportRequestItem> SucceededItems = new List<BlmImportRequestItem>();
        public List<BlmImportRequestItem> FailedItems = new List<BlmImportRequestItem>();
        public AmariUnityPackagePipelineOperationStatus ImportStatus = AmariUnityPackagePipelineOperationStatus.None;
        public string ErrorMessage = string.Empty;
    }
}
