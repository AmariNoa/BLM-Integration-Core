namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class BlmImportQueueProgressContext
    {
        public string BatchId { get; }
        public int ProcessedCount { get; }
        public int RemainingCount { get; }
        public int TotalCount { get; }

        public BlmImportQueueProgressContext(
            string batchId,
            int processedCount,
            int remainingCount,
            int totalCount)
        {
            BatchId = batchId ?? string.Empty;
            ProcessedCount = processedCount < 0 ? 0 : processedCount;
            RemainingCount = remainingCount < 0 ? 0 : remainingCount;
            TotalCount = totalCount < 0 ? 0 : totalCount;
        }
    }
}
