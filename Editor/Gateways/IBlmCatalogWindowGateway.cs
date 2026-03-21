using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public interface IAmariBlmCatalogWindowGateway
    {
        event Action<AmariBlmImportBatchRequest> BatchRequestConfirmed;
        void Open(AmariBlmPickerContext context);
    }
}
