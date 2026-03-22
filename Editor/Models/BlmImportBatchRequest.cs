using System.Collections.Generic;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class BlmImportBatchRequest
    {
        public string BatchId = string.Empty;
        public BlmInvocationContext InvocationContext = BlmInvocationContext.Integration;
        public List<BlmImportRequestItem> Items = new List<BlmImportRequestItem>();
    }
}
