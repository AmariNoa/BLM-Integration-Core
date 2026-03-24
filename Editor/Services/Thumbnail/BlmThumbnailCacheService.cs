using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmThumbnailCacheService
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, Texture2D> _memoryCache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly Dictionary<string, LinkedListNode<string>> _memoryCacheNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        private readonly LinkedList<string> _memoryCacheLru = new LinkedList<string>();
        private readonly Dictionary<string, Task<string>> _inflightDownloads = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _requestSemaphore = new SemaphoreSlim(BlmConstants.ThumbnailRequestConcurrency, BlmConstants.ThumbnailRequestConcurrency);
        private bool _settingsLoaded;

        public int MaxEntries { get; private set; }
        public string CacheRootPath { get; }

        public BlmThumbnailCacheService()
        {
            CacheRootPath = BlmConstants.GetThumbnailCacheRootPath();
            Directory.CreateDirectory(CacheRootPath);
            MaxEntries = GetClampedMaxEntries(BlmConstants.ThumbnailMemoryCacheMaxEntriesDefault);
        }

        public void LoadSettingsFromEditorPrefs()
        {
            if (_settingsLoaded)
            {
                return;
            }

            var hasStoredSetting = EditorPrefs.HasKey(BlmConstants.ThumbnailCacheEditorPrefsKey);
            var storedValue = EditorPrefs.GetInt(
                BlmConstants.ThumbnailCacheEditorPrefsKey,
                BlmConstants.ThumbnailMemoryCacheMaxEntriesDefault);
            MaxEntries = GetClampedMaxEntries(storedValue);
            if (!hasStoredSetting || storedValue != MaxEntries)
            {
                EditorPrefs.SetInt(BlmConstants.ThumbnailCacheEditorPrefsKey, MaxEntries);
            }

            _settingsLoaded = true;
            TrimMemoryCacheIfNeeded();
        }

        public int SetMaxEntries(int maxEntries)
        {
            MaxEntries = GetClampedMaxEntries(maxEntries);
            _settingsLoaded = true;
            EditorPrefs.SetInt(BlmConstants.ThumbnailCacheEditorPrefsKey, MaxEntries);
            TrimMemoryCacheIfNeeded();
            return MaxEntries;
        }

        public void CleanupExpiredCacheFiles()
        {
            var cleanupStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (!Directory.Exists(CacheRootPath))
            {
                PerfLog($"CleanupExpiredCacheFiles skipped in {cleanupStopwatch.ElapsedMilliseconds} ms. reason='Cache root not found'");
                return;
            }

            var scanned = 0;
            var deleted = 0;
            foreach (var filePath in Directory.EnumerateFiles(CacheRootPath))
            {
                scanned++;
                if (!IsExpired(filePath))
                {
                    continue;
                }

                if (TryDeleteFile(filePath))
                {
                    deleted++;
                }
            }

            cleanupStopwatch.Stop();
            PerfLog($"CleanupExpiredCacheFiles completed in {cleanupStopwatch.ElapsedMilliseconds} ms. scanned={scanned}, deleted={deleted}");
        }

        public void ClearAllCacheFiles()
        {
            if (!Directory.Exists(CacheRootPath))
            {
                return;
            }

            foreach (var filePath in Directory.EnumerateFiles(CacheRootPath))
            {
                TryDeleteFile(filePath);
            }

            lock (_syncRoot)
            {
                foreach (var texture in _memoryCache.Values)
                {
                    DestroyTexture(texture);
                }

                _memoryCache.Clear();
                _memoryCacheNodes.Clear();
                _memoryCacheLru.Clear();
            }
        }

        public async Task<Texture2D> GetTextureAsync(BlmItemRecord itemRecord)
        {
            if (itemRecord == null)
            {
                return null;
            }

            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var productId = itemRecord.ProductId ?? string.Empty;
            var resolvedPath = await ResolveThumbnailPathAsync(itemRecord.ThumbnailPath, itemRecord.ThumbnailUrl, productId);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                itemRecord.ThumbnailPath = resolvedPath;
            }

            var texture = await LoadTextureFromPathAsync(resolvedPath, productId);
            totalStopwatch.Stop();
            PerfLog(
                $"GetTextureAsync completed in {totalStopwatch.ElapsedMilliseconds} ms. " +
                $"productId='{productId}', hasTexture={((texture != null).ToString().ToLowerInvariant())}, path='{Path.GetFileName(resolvedPath)}'");
            return texture;
        }

        private async Task<string> ResolveThumbnailPathAsync(string thumbnailPath, string thumbnailUrl, string productId)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (!string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath))
            {
                if (!IsExpired(thumbnailPath))
                {
                    stopwatch.Stop();
                    PerfLog(
                        $"Thumbnail path resolved from existing item path in {stopwatch.ElapsedMilliseconds} ms. " +
                        $"productId='{productId}', source='item-path', file='{Path.GetFileName(thumbnailPath)}'");
                    return thumbnailPath;
                }

                TryDeleteFile(thumbnailPath);
            }

            if (string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                stopwatch.Stop();
                PerfLog(
                    $"Thumbnail path resolve skipped in {stopwatch.ElapsedMilliseconds} ms. " +
                    $"productId='{productId}', reason='thumbnailUrl empty'");
                return string.Empty;
            }

            var hash = ComputeSha256Hex(thumbnailUrl);
            var cachedPath = FindCachedFileByHash(hash);
            if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
            {
                if (!IsExpired(cachedPath))
                {
                    stopwatch.Stop();
                    PerfLog(
                        $"Thumbnail path resolved from disk cache in {stopwatch.ElapsedMilliseconds} ms. " +
                        $"productId='{productId}', source='disk-cache', file='{Path.GetFileName(cachedPath)}'");
                    return cachedPath;
                }

                TryDeleteFile(cachedPath);
            }

            Task<string> downloadTask;
            var reusedInflightTask = false;
            lock (_syncRoot)
            {
                if (_inflightDownloads.TryGetValue(hash, out var existingTask))
                {
                    downloadTask = existingTask;
                    reusedInflightTask = true;
                }
                else
                {
                    downloadTask = DownloadThumbnailInternalAsync(hash, thumbnailUrl, productId);
                    _inflightDownloads[hash] = downloadTask;
                }
            }

            try
            {
                var downloadPath = await downloadTask;
                stopwatch.Stop();
                PerfLog(
                    $"Thumbnail path resolved in {stopwatch.ElapsedMilliseconds} ms. " +
                    $"productId='{productId}', source='{(reusedInflightTask ? "shared-download" : "download")}', hasPath={(!string.IsNullOrWhiteSpace(downloadPath)).ToString().ToLowerInvariant()}");
                return downloadPath;
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (_inflightDownloads.TryGetValue(hash, out var inflightTask) && inflightTask == downloadTask)
                    {
                        _inflightDownloads.Remove(hash);
                    }
                }
            }
        }

        private async Task<string> DownloadThumbnailInternalAsync(string hash, string thumbnailUrl, string productId)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            PerfLog($"Thumbnail download start. productId='{productId}', hash='{hash}'");
            await _requestSemaphore.WaitAsync();
            try
            {
                var basePath = Path.Combine(CacheRootPath, hash);
                DeleteHashVariants(hash);

                using var request = UnityWebRequest.Get(thumbnailUrl);
                request.timeout = BlmConstants.ThumbnailRequestTimeoutSeconds;

                await SendRequestAsync(request);
                if (request.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
                {
                    stopwatch.Stop();
                    PerfLog(
                        $"Thumbnail download failed in {stopwatch.ElapsedMilliseconds} ms. " +
                        $"productId='{productId}', hash='{hash}', result='{request.result}', responseCode={request.responseCode}");
                    return string.Empty;
                }

                var bytes = request.downloadHandler?.data;
                if (bytes == null || bytes.Length == 0)
                {
                    stopwatch.Stop();
                    PerfLog(
                        $"Thumbnail download returned empty payload in {stopwatch.ElapsedMilliseconds} ms. " +
                        $"productId='{productId}', hash='{hash}'");
                    return string.Empty;
                }

                var extension = ResolveFileExtension(request.GetResponseHeader("Content-Type"), thumbnailUrl);
                var finalPath = string.IsNullOrWhiteSpace(extension)
                    ? basePath
                    : $"{basePath}.{extension}";
                var tempPath = $"{basePath}.tmp";

                File.WriteAllBytes(tempPath, bytes);
                if (File.Exists(finalPath))
                {
                    TryDeleteFile(finalPath);
                }

                File.Move(tempPath, finalPath);
                stopwatch.Stop();
                PerfLog(
                    $"Thumbnail downloaded and cached in {stopwatch.ElapsedMilliseconds} ms. " +
                    $"productId='{productId}', hash='{hash}', bytes={bytes.Length}, file='{Path.GetFileName(finalPath)}'");
                return finalPath;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                PerfLog(
                    $"Thumbnail download exception after {stopwatch.ElapsedMilliseconds} ms. " +
                    $"productId='{productId}', hash='{hash}', error='{ex.Message}'");
                return string.Empty;
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        private static async Task SendRequestAsync(UnityWebRequest request)
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                await Task.Yield();
            }
        }

        private async Task<Texture2D> LoadTextureFromPathAsync(string path, string productId)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                PerfLog($"Cached thumbnail load skipped. productId='{productId}', reason='path missing'");
                return null;
            }

            lock (_syncRoot)
            {
                if (_memoryCache.TryGetValue(path, out var cached))
                {
                    TouchCache(path);
                    PerfLog($"Cached thumbnail load hit memory cache. productId='{productId}', file='{Path.GetFileName(path)}'");
                    return cached;
                }
            }

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var bytes = await Task.Run(() => File.ReadAllBytes(path));
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes, true))
                {
                    DestroyTexture(texture);
                    stopwatch.Stop();
                    PerfLog(
                        $"Cached thumbnail load failed (decode) in {stopwatch.ElapsedMilliseconds} ms. " +
                        $"productId='{productId}', file='{Path.GetFileName(path)}'");
                    return null;
                }

                lock (_syncRoot)
                {
                    if (_memoryCache.TryGetValue(path, out var existing))
                    {
                        DestroyTexture(texture);
                        TouchCache(path);
                        stopwatch.Stop();
                        PerfLog(
                            $"Cached thumbnail load raced into memory cache in {stopwatch.ElapsedMilliseconds} ms. " +
                            $"productId='{productId}', file='{Path.GetFileName(path)}'");
                        return existing;
                    }

                    AddTextureToCache(path, texture);
                }

                stopwatch.Stop();
                PerfLog(
                    $"Cached thumbnail load from disk completed in {stopwatch.ElapsedMilliseconds} ms. " +
                    $"productId='{productId}', bytes={bytes.Length}, file='{Path.GetFileName(path)}'");
                return texture;
            }
            catch
            {
                PerfLog($"Cached thumbnail load exception. productId='{productId}', file='{Path.GetFileName(path)}'");
                return null;
            }
        }

        private static string ComputeSha256Hex(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }

        private string FindCachedFileByHash(string hash)
        {
            var exactPath = Path.Combine(CacheRootPath, hash);
            if (File.Exists(exactPath))
            {
                return exactPath;
            }

            var matches = Directory.EnumerateFiles(CacheRootPath, $"{hash}.*").ToArray();
            return matches.Length > 0 ? matches[0] : string.Empty;
        }

        private void DeleteHashVariants(string hash)
        {
            var directPath = Path.Combine(CacheRootPath, hash);
            if (File.Exists(directPath))
            {
                TryDeleteFile(directPath);
            }

            foreach (var path in Directory.EnumerateFiles(CacheRootPath, $"{hash}.*"))
            {
                TryDeleteFile(path);
            }
        }

        private static string ResolveFileExtension(string contentType, string url)
        {
            var extension = ResolveFileExtensionFromContentType(contentType);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return extension;
            }

            var fromUrl = Path.GetExtension(url ?? string.Empty);
            if (string.IsNullOrWhiteSpace(fromUrl))
            {
                return string.Empty;
            }

            return fromUrl.TrimStart('.').Trim().ToLowerInvariant();
        }

        private static string ResolveFileExtensionFromContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return string.Empty;
            }

            var normalized = contentType.Trim().ToLowerInvariant();
            if (normalized.Contains("image/png"))
            {
                return "png";
            }

            if (normalized.Contains("image/jpeg") || normalized.Contains("image/jpg"))
            {
                return "jpg";
            }

            if (normalized.Contains("image/webp"))
            {
                return "webp";
            }

            if (normalized.Contains("image/gif"))
            {
                return "gif";
            }

            if (normalized.Contains("image/bmp"))
            {
                return "bmp";
            }

            return string.Empty;
        }

        private static bool IsExpired(string filePath)
        {
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                return DateTime.UtcNow - lastWriteUtc > TimeSpan.FromDays(BlmConstants.ThumbnailDiskCacheTtlDays);
            }
            catch
            {
                return true;
            }
        }

        private static bool TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private int GetClampedMaxEntries(int requested)
        {
            var min = BlmConstants.ThumbnailMemoryCacheMaxEntriesMin;
            var max = BlmConstants.ThumbnailMemoryCacheMaxEntriesMax;
            if (min > max)
            {
                min = max;
            }

            return Mathf.Clamp(requested, min, max);
        }

        private void AddTextureToCache(string key, Texture2D texture)
        {
            _memoryCache[key] = texture;
            var node = new LinkedListNode<string>(key);
            _memoryCacheNodes[key] = node;
            _memoryCacheLru.AddFirst(node);
            TrimMemoryCacheIfNeeded();
        }

        private void TouchCache(string key)
        {
            if (!_memoryCacheNodes.TryGetValue(key, out var node))
            {
                return;
            }

            _memoryCacheLru.Remove(node);
            _memoryCacheLru.AddFirst(node);
        }

        private void TrimMemoryCacheIfNeeded()
        {
            lock (_syncRoot)
            {
                while (_memoryCache.Count > MaxEntries && _memoryCacheLru.Last != null)
                {
                    var tail = _memoryCacheLru.Last;
                    _memoryCacheLru.RemoveLast();
                    if (tail == null)
                    {
                        continue;
                    }

                    var key = tail.Value;
                    _memoryCacheNodes.Remove(key);
                    if (_memoryCache.TryGetValue(key, out var texture))
                    {
                        DestroyTexture(texture);
                        _memoryCache.Remove(key);
                    }
                }
            }
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static void PerfLog(string message)
        {
            if (!BlmConstants.EnablePerformanceLogging)
            {
                return;
            }

            Debug.Log($"{BlmConstants.PerformanceLogPrefix}[Thumbnail] {message}");
        }
    }
}
