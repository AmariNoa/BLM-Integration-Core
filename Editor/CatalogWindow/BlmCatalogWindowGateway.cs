using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class BlmCatalogWindowGateway : IBlmCatalogWindowGateway
    {
        public static BlmCatalogWindowGateway Shared { get; } = new BlmCatalogWindowGateway();

        public event Action<BlmImportBatchRequest> BatchRequestConfirmed;
        public event Action WindowClosed;

        public void Open(BlmPickerContext context)
        {
            CatalogWindow.Open(context, HandleBatchRequestConfirmed, HandleWindowClosed);
        }

        private void HandleBatchRequestConfirmed(BlmImportBatchRequest request)
        {
            BatchRequestConfirmed?.Invoke(request);
        }

        private void HandleWindowClosed()
        {
            WindowClosed?.Invoke();
        }
    }
}
