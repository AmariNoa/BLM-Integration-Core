using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed partial class BlmThumbnailCacheService
    {
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
            if (string.IsNullOrWhiteSpace(hash))
            {
                return string.Empty;
            }

            EnsureDiskCachePathIndexLoaded();
            string indexedPath;
            lock (_syncRoot)
            {
                if (!_diskCachePathByHash.TryGetValue(hash, out indexedPath))
                {
                    return string.Empty;
                }
            }

            if (!string.IsNullOrWhiteSpace(indexedPath) && File.Exists(indexedPath))
            {
                return indexedPath;
            }

            RemoveDiskCachePathByHash(hash);
            return string.Empty;
        }

        private void DeleteHashVariants(string hash)
        {
            RemoveDiskCachePathByHash(hash);
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

        private void EnsureDiskCachePathIndexLoaded()
        {
            lock (_syncRoot)
            {
                if (_diskCachePathIndexLoaded)
                {
                    return;
                }

                _diskCachePathByHash.Clear();
                if (Directory.Exists(CacheRootPath))
                {
                    foreach (var filePath in Directory.EnumerateFiles(CacheRootPath))
                    {
                        if (!TryExtractDiskCacheHash(filePath, out var hash))
                        {
                            continue;
                        }

                        _diskCachePathByHash[hash] = filePath;
                    }
                }

                _diskCachePathIndexLoaded = true;
            }
        }

        private void InvalidateDiskCachePathIndex()
        {
            lock (_syncRoot)
            {
                _diskCachePathByHash.Clear();
                _diskCachePathIndexLoaded = false;
            }
        }

        private void RegisterDiskCachePathByHash(string hash, string filePath)
        {
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            lock (_syncRoot)
            {
                _diskCachePathByHash[hash] = filePath;
                _diskCachePathIndexLoaded = true;
            }
        }

        private void RemoveDiskCachePathByHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return;
            }

            lock (_syncRoot)
            {
                _diskCachePathByHash.Remove(hash);
            }
        }

        private void RemoveDiskCachePathByPath(string filePath)
        {
            if (!TryExtractDiskCacheHash(filePath, out var hash))
            {
                return;
            }

            RemoveDiskCachePathByHash(hash);
        }

        private static bool TryExtractDiskCacheHash(string filePath, out string hash)
        {
            hash = string.Empty;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var dotIndex = fileName.IndexOf('.');
            var candidateHash = dotIndex > 0
                ? fileName.Substring(0, dotIndex)
                : fileName;
            if (candidateHash.Length != 64)
            {
                return false;
            }

            for (var i = 0; i < candidateHash.Length; i++)
            {
                var c = candidateHash[i];
                var isDigit = c >= '0' && c <= '9';
                var isLowerHex = c >= 'a' && c <= 'f';
                var isUpperHex = c >= 'A' && c <= 'F';
                if (!isDigit && !isLowerHex && !isUpperHex)
                {
                    return false;
                }
            }

            hash = candidateHash.ToLowerInvariant();
            return true;
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

        private static async Task<T> WaitForTaskWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
        {
            if (task == null)
            {
                return default;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                return await task;
            }

            var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancellationSignal.TrySetResult(true)))
            {
                var completedTask = await Task.WhenAny(task, cancellationSignal.Task);
                if (ReferenceEquals(completedTask, task))
                {
                    return await task;
                }
            }

            throw new OperationCanceledException(cancellationToken);
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
