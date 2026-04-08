using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class BlmImportProcessor : IBlmImportProcessorGateway, IBlmDestinationAssetPathUpdater
    {
        private const int NonUnityImportsPerFrame = 32;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, BlmImportRequestItem> _trackedItems = new Dictionary<string, BlmImportRequestItem>(StringComparer.OrdinalIgnoreCase);
        private readonly BlmImportIndexService _importIndexService = BlmImportIndexService.Shared;

        public static BlmImportProcessor Shared { get; } = new BlmImportProcessor();

        public event Action<BlmImportBatchResultContext> ImportBatchCompleted;
        public event Action<string, IReadOnlyList<BlmImportRequestItem>> ImportQueueUpdated;
        public event Action<BlmImportQueueProgressContext> ImportQueueProgressed;

        public void Execute(BlmImportBatchRequest request, BlmPickerContext context)
        {
            _ = ExecuteInternalAsync(request, context);
        }

        public void UpdateDestinationAssetPaths(
            string batchId,
            string productId,
            string sourcePath,
            IEnumerable<string> destinationAssetPaths)
        {
            var key = BuildTrackingKey(batchId, productId, sourcePath);
            lock (_syncRoot)
            {
                if (!_trackedItems.TryGetValue(key, out var tracked))
                {
                    return;
                }

                tracked.DestinationAssetPaths = destinationAssetPaths?
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeAssetPath)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList() ?? new List<string>();
            }
        }

        private async Task ExecuteInternalAsync(BlmImportBatchRequest request, BlmPickerContext context)
        {
            await Task.Yield();

            var result = new BlmImportBatchResultContext();
            if (request != null)
            {
                result.BatchId = request.BatchId ?? string.Empty;
                result.InvocationContext = request.InvocationContext;
            }

            if (request == null || request.Items == null)
            {
                result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                result.ErrorMessage = "Batch request is null.";
                RaiseBatchCompleted(result);
                return;
            }

            if (context == null)
            {
                result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                result.ErrorMessage = "Picker context is null.";
                RaiseBatchCompleted(result);
                return;
            }

            if (!context.ValidateRequiredServices(out var contextError))
            {
                result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                result.ErrorMessage = contextError;
                RaiseBatchCompleted(result);
                return;
            }

            RegisterTrackedItems(request);
            var remainingQueue = request.Items?.ToList() ?? new List<BlmImportRequestItem>();
            var totalCount = remainingQueue.Count;
            var processedCount = 0;
            RaiseImportQueueProgress(request.BatchId, processedCount, remainingQueue.Count, totalCount);

            var importer = new BlmNonUnityPackageImporter();
            var batchDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nonUnityImportsProcessedInCurrentFrame = 0;
            var isAssetEditing = false;
            var pipelineService = context.UnityPackageImportPipelineService;
            var previousPreImportAnalysisMode = pipelineService.PreImportAnalysisMode;
            var previousQuietMode = pipelineService.QuietMode;
            var resolveCache = new BlmImportIndexAssetResolveCache();

            try
            {
                pipelineService.PreImportAnalysisMode = AmariUnityPackagePreImportAnalysisMode.Skip;
                pipelineService.QuietMode = true;

                foreach (var item in request.Items)
                {
                    var ext = GetExtension(item.SourcePath);
                    if (string.Equals(ext, ".unitypackage", StringComparison.Ordinal))
                    {
                        EndAssetEditingIfNeeded(ref isAssetEditing);
                        var unityResult = await ExecuteUnityPackageImportAsync(item, request.BatchId, context);
                        if (!unityResult.IsSuccess)
                        {
                            result.FailedItems.Add(item);
                            result.ImportStatus = unityResult.Status;
                            result.ErrorMessage = unityResult.ErrorMessage;
                            RemoveFirstRemainingQueueItem(remainingQueue);
                            processedCount++;
                            RaiseImportQueueProgress(request.BatchId, processedCount, remainingQueue.Count, totalCount);
                            break;
                        }

                        _importIndexService.UpdateFromImportedItem(
                            item,
                            BlmImportedItemKind.UnityPackage,
                            destinationWasPreExisting: true,
                            isSkipped: false,
                            resolveCache);
                        result.SucceededItems.Add(item);
                        RemoveFirstRemainingQueueItem(remainingQueue);
                        processedCount++;
                        RaiseImportQueueProgress(request.BatchId, processedCount, remainingQueue.Count, totalCount);
                        nonUnityImportsProcessedInCurrentFrame = 0;
                        continue;
                    }

                    if (nonUnityImportsProcessedInCurrentFrame >= NonUnityImportsPerFrame)
                    {
                        EndAssetEditingIfNeeded(ref isAssetEditing);
                        nonUnityImportsProcessedInCurrentFrame = 0;
                        await Task.Yield();
                    }

                    var nonUnityResult = importer.Import(item, context, batchDestinations, deferAssetImport: true);
                    if (!nonUnityResult.IsSuccess)
                    {
                        result.FailedItems.Add(item);
                        result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                        result.ErrorMessage = nonUnityResult.ErrorMessage;
                        RemoveFirstRemainingQueueItem(remainingQueue);
                        processedCount++;
                        RaiseImportQueueProgress(request.BatchId, processedCount, remainingQueue.Count, totalCount);
                        break;
                    }

                    if (!nonUnityResult.IsSkipped && !string.IsNullOrWhiteSpace(nonUnityResult.DestinationAssetPath))
                    {
                        BeginAssetEditingIfNeeded(ref isAssetEditing);
                        try
                        {
                            AssetDatabase.ImportAsset(
                                nonUnityResult.DestinationAssetPath,
                                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        }
                        catch (Exception importEx)
                        {
                            result.FailedItems.Add(item);
                            result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                            result.ErrorMessage = importEx.Message;
                            RemoveFirstRemainingQueueItem(remainingQueue);
                            processedCount++;
                            RaiseImportQueueProgress(request.BatchId, processedCount, remainingQueue.Count, totalCount);
                            break;
                        }
                    }

                    nonUnityImportsProcessedInCurrentFrame++;
                    var destinationPaths = string.IsNullOrWhiteSpace(nonUnityResult.DestinationAssetPath)
                        ? Array.Empty<string>()
                        : new[] { nonUnityResult.DestinationAssetPath };
                    context.DestinationAssetPathUpdater.UpdateDestinationAssetPaths(
                        request.BatchId,
                        item.ProductId,
                        item.SourcePath,
                        destinationPaths);
                    _importIndexService.UpdateFromImportedItem(
                        item,
                        BlmImportedItemKind.NonUnityPackage,
                        nonUnityResult.DestinationWasPreExisting,
                        nonUnityResult.IsSkipped,
                        resolveCache);
                    result.SucceededItems.Add(item);
                    RemoveFirstRemainingQueueItem(remainingQueue);
                    processedCount++;
                    RaiseImportQueueProgress(request.BatchId, processedCount, remainingQueue.Count, totalCount);
                }
            }
            catch (Exception ex)
            {
                result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                EndAssetEditingIfNeeded(ref isAssetEditing);
                pipelineService.PreImportAnalysisMode = previousPreImportAnalysisMode;
                pipelineService.QuietMode = previousQuietMode;
                UnregisterTrackedItems(request);
                _importIndexService.FlushPendingSaves();
            }

            if (result.ImportStatus == AmariUnityPackagePipelineOperationStatus.None)
            {
                result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Completed;
            }

            RaiseBatchCompleted(result);
        }

    }
}
