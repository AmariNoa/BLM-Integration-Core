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
    public sealed class BlmImportProcessor : IBlmImportProcessorGateway, IBlmDestinationAssetPathUpdater
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

        private static void RemoveFirstRemainingQueueItem(List<BlmImportRequestItem> remainingQueue)
        {
            if (remainingQueue == null || remainingQueue.Count == 0)
            {
                return;
            }

            remainingQueue.RemoveAt(0);
        }

        private static void BeginAssetEditingIfNeeded(ref bool isAssetEditing)
        {
            if (isAssetEditing)
            {
                return;
            }

            AssetDatabase.StartAssetEditing();
            isAssetEditing = true;
        }

        private static void EndAssetEditingIfNeeded(ref bool isAssetEditing)
        {
            if (!isAssetEditing)
            {
                return;
            }

            AssetDatabase.StopAssetEditing();
            isAssetEditing = false;
        }

        private async Task<UnityPackageImportOutcome> ExecuteUnityPackageImportAsync(
            BlmImportRequestItem item,
            string batchId,
            BlmPickerContext context)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.SourcePath))
            {
                return UnityPackageImportOutcome.Failed(AmariUnityPackagePipelineOperationStatus.Failed, "UnityPackage path is empty.");
            }

            if (!File.Exists(item.SourcePath))
            {
                return UnityPackageImportOutcome.Failed(AmariUnityPackagePipelineOperationStatus.Failed, $"UnityPackage file not found: {item.SourcePath}");
            }

            var pipelineService = context.UnityPackageImportPipelineService;

            var tcs = new TaskCompletionSource<AmariUnityPackageImportResultContext>();
            void Handler(AmariUnityPackageImportResultContext contextResult)
            {
                if (contextResult?.Request == null)
                {
                    return;
                }

                if (!string.Equals(
                        NormalizePath(contextResult.Request.PackagePath),
                        NormalizePath(item.SourcePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                tcs.TrySetResult(contextResult);
            }

            context.UnityPackageImportPipelineService.ImportRequestFinalized += Handler;

            try
            {
                pipelineService.Enqueue(new AmariUnityPackageImportRequest(item.SourcePath));
                pipelineService.StartImport();

                var resultContext = await tcs.Task;
                if (resultContext == null)
                {
                    return UnityPackageImportOutcome.Failed(AmariUnityPackagePipelineOperationStatus.Failed, "UnityPackage result was null.");
                }

                context.DestinationAssetPathUpdater.UpdateDestinationAssetPaths(
                    batchId,
                    item.ProductId,
                    item.SourcePath,
                    resultContext.ImportedAssets);

                if (resultContext.ImportStatus != AmariUnityPackagePipelineOperationStatus.Completed)
                {
                    return UnityPackageImportOutcome.Failed(resultContext.ImportStatus, resultContext.ErrorMessage);
                }

                return UnityPackageImportOutcome.Completed();
            }
            catch (Exception ex)
            {
                return UnityPackageImportOutcome.Failed(AmariUnityPackagePipelineOperationStatus.Failed, ex.Message);
            }
            finally
            {
                context.UnityPackageImportPipelineService.ImportRequestFinalized -= Handler;
            }
        }

        private void RegisterTrackedItems(BlmImportBatchRequest request)
        {
            lock (_syncRoot)
            {
                foreach (var item in request.Items)
                {
                    var key = BuildTrackingKey(request.BatchId, item.ProductId, item.SourcePath);
                    _trackedItems[key] = item;
                }
            }
        }

        private void UnregisterTrackedItems(BlmImportBatchRequest request)
        {
            lock (_syncRoot)
            {
                foreach (var item in request.Items)
                {
                    var key = BuildTrackingKey(request.BatchId, item.ProductId, item.SourcePath);
                    _trackedItems.Remove(key);
                }
            }
        }

        private void RaiseBatchCompleted(BlmImportBatchResultContext result)
        {
            try
            {
                ImportBatchCompleted?.Invoke(result);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BLM Integration Core] ImportBatchCompleted callback failed: {ex.Message}");
            }
        }

        private void RaiseImportQueueProgress(string batchId, int processedCount, int remainingCount, int totalCount)
        {
            try
            {
                var normalizedBatchId = batchId ?? string.Empty;
                var normalizedProcessedCount = Math.Max(0, processedCount);
                var normalizedRemainingCount = Math.Max(0, remainingCount);
                var normalizedTotalCount = Math.Max(normalizedProcessedCount + normalizedRemainingCount, totalCount);

                ImportQueueProgressed?.Invoke(new BlmImportQueueProgressContext(
                    normalizedBatchId,
                    normalizedProcessedCount,
                    normalizedRemainingCount,
                    normalizedTotalCount));

                if (ImportQueueUpdated != null)
                {
                    ImportQueueUpdated.Invoke(normalizedBatchId, Array.Empty<BlmImportRequestItem>());
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BLM Integration Core] ImportQueue progress callback failed: {ex.Message}");
            }
        }

        private static string BuildTrackingKey(string batchId, string productId, string sourcePath)
        {
            return $"{batchId ?? string.Empty}::{productId ?? string.Empty}::{NormalizePath(sourcePath)}";
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').ToLowerInvariant();
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Replace('\\', '/');
        }

        private static string GetExtension(string path)
        {
            return (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
        }

        private readonly struct UnityPackageImportOutcome
        {
            public bool IsSuccess { get; }
            public AmariUnityPackagePipelineOperationStatus Status { get; }
            public string ErrorMessage { get; }

            private UnityPackageImportOutcome(bool isSuccess, AmariUnityPackagePipelineOperationStatus status, string errorMessage)
            {
                IsSuccess = isSuccess;
                Status = status;
                ErrorMessage = errorMessage ?? string.Empty;
            }

            public static UnityPackageImportOutcome Completed()
            {
                return new UnityPackageImportOutcome(true, AmariUnityPackagePipelineOperationStatus.Completed, string.Empty);
            }

            public static UnityPackageImportOutcome Failed(AmariUnityPackagePipelineOperationStatus status, string errorMessage)
            {
                return new UnityPackageImportOutcome(false, status, errorMessage);
            }
        }
    }
}
