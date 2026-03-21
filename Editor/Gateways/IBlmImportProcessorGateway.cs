using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public interface IAmariBlmImportProcessorGateway
    {
        event Action<AmariBlmImportBatchResultContext> ImportBatchCompleted;
        void Execute(AmariBlmImportBatchRequest request, AmariBlmPickerContext context);
    }
}
