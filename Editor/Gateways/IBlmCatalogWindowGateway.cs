using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public interface IBlmCatalogWindowGateway
    {
        event Action<BlmImportBatchRequest> BatchRequestConfirmed;
        void Open(BlmPickerContext context);
    }
}
