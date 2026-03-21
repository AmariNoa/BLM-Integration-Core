using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class AmariBlmCatalogWindowGateway : IAmariBlmCatalogWindowGateway
    {
        public static AmariBlmCatalogWindowGateway Shared { get; } = new AmariBlmCatalogWindowGateway();

        public event Action<AmariBlmImportBatchRequest> BatchRequestConfirmed;

        public void Open(AmariBlmPickerContext context)
        {
            CatalogWindow.Open(context, HandleBatchRequestConfirmed);
        }

        private void HandleBatchRequestConfirmed(AmariBlmImportBatchRequest request)
        {
            BatchRequestConfirmed?.Invoke(request);
        }
    }
}
