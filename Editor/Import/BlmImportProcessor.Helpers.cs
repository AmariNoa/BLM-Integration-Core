using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEditor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class BlmImportProcessor
    {
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
