using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class AmariBlmImportProcessor : IAmariBlmImportProcessorGateway, IAmariBlmDestinationAssetPathUpdater
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, AmariBlmImportRequestItem> _trackedItems = new Dictionary<string, AmariBlmImportRequestItem>(StringComparer.OrdinalIgnoreCase);

        public static AmariBlmImportProcessor Shared { get; } = new AmariBlmImportProcessor();

        public event Action<AmariBlmImportBatchResultContext> ImportBatchCompleted;

        public void Execute(AmariBlmImportBatchRequest request, AmariBlmPickerContext context)
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

        private async Task ExecuteInternalAsync(AmariBlmImportBatchRequest request, AmariBlmPickerContext context)
        {
            var result = new AmariBlmImportBatchResultContext();
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

            var importer = new AmariBlmNonUnityPackageImporter();
            var batchDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var item in request.Items)
                {
                    var ext = GetExtension(item.SourcePath);
                    if (string.Equals(ext, ".unitypackage", StringComparison.Ordinal))
                    {
                        var unityResult = await ExecuteUnityPackageImportAsync(item, request.BatchId, context);
                        if (!unityResult.IsSuccess)
                        {
                            result.FailedItems.Add(item);
                            result.ImportStatus = unityResult.Status;
                            result.ErrorMessage = unityResult.ErrorMessage;
                            break;
                        }

                        result.SucceededItems.Add(item);
                        continue;
                    }

                    var nonUnityResult = importer.Import(item, context, batchDestinations);
                    if (!nonUnityResult.IsSuccess)
                    {
                        result.FailedItems.Add(item);
                        result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                        result.ErrorMessage = nonUnityResult.ErrorMessage;
                        break;
                    }

                    var destinationPaths = string.IsNullOrWhiteSpace(nonUnityResult.DestinationAssetPath)
                        ? Array.Empty<string>()
                        : new[] { nonUnityResult.DestinationAssetPath };
                    context.DestinationAssetPathUpdater.UpdateDestinationAssetPaths(
                        request.BatchId,
                        item.ProductId,
                        item.SourcePath,
                        destinationPaths);
                    result.SucceededItems.Add(item);
                }
            }
            catch (Exception ex)
            {
                result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Failed;
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                UnregisterTrackedItems(request);
            }

            if (result.ImportStatus == AmariUnityPackagePipelineOperationStatus.None)
            {
                result.ImportStatus = AmariUnityPackagePipelineOperationStatus.Completed;
            }

            RaiseBatchCompleted(result);
        }

        private async Task<UnityPackageImportOutcome> ExecuteUnityPackageImportAsync(
            AmariBlmImportRequestItem item,
            string batchId,
            AmariBlmPickerContext context)
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
                pipelineService.Enqueue(new AmariUnityPackageImportRequest(item.SourcePath, item.Tags));
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

        private void RegisterTrackedItems(AmariBlmImportBatchRequest request)
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

        private void UnregisterTrackedItems(AmariBlmImportBatchRequest request)
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

        private void RaiseBatchCompleted(AmariBlmImportBatchResultContext result)
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
