using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public interface IBlmImportProcessorGateway
    {
        event Action<BlmImportBatchResultContext> ImportBatchCompleted;
        void Execute(BlmImportBatchRequest request, BlmPickerContext context);
    }
}
