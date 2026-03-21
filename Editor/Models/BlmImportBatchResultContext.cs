using System.Collections.Generic;
using com.amari_noa.unitypackage_pipeline_core.editor;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class AmariBlmImportBatchResultContext
    {
        public string BatchId = string.Empty;
        public AmariBlmInvocationContext InvocationContext = AmariBlmInvocationContext.Integration;
        public List<AmariBlmImportRequestItem> SucceededItems = new List<AmariBlmImportRequestItem>();
        public List<AmariBlmImportRequestItem> FailedItems = new List<AmariBlmImportRequestItem>();
        public AmariUnityPackagePipelineOperationStatus ImportStatus = AmariUnityPackagePipelineOperationStatus.None;
        public string ErrorMessage = string.Empty;
    }
}
