using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class BlmCatalogWindowGateway : IBlmCatalogWindowGateway
    {
        public static BlmCatalogWindowGateway Shared { get; } = new BlmCatalogWindowGateway();

        public event Action<BlmImportBatchRequest> BatchRequestConfirmed;

        public void Open(BlmPickerContext context)
        {
            CatalogWindow.Open(context, HandleBatchRequestConfirmed);
        }

        private void HandleBatchRequestConfirmed(BlmImportBatchRequest request)
        {
            BatchRequestConfirmed?.Invoke(request);
        }
    }
}
