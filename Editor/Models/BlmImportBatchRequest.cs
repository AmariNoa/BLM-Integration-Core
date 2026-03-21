using System.Collections.Generic;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class AmariBlmImportBatchRequest
    {
        public string BatchId = string.Empty;
        public AmariBlmInvocationContext InvocationContext = AmariBlmInvocationContext.Integration;
        public List<AmariBlmImportRequestItem> Items = new List<AmariBlmImportRequestItem>();
    }
}
