using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmProductFolderResolver
    {
        private const string UnresolvedPathSentinel = "<UNRESOLVED>";
        private readonly Dictionary<string, string> _resolvedPathCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<DirectoryCandidate>> _fuzzyCandidateCacheByRoot = new Dictionary<string, IReadOnlyList<DirectoryCandidate>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _hasUnityPackageCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public void ClearCache()
        {
            _resolvedPathCache.Clear();
            _fuzzyCandidateCacheByRoot.Clear();
            _hasUnityPackageCache.Clear();
        }

        public string Resolve(string libraryRoot, string productId, string shopSubdomain, string productName)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(productId))
            {
                return null;
            }

            var cacheKey = BuildResolutionCacheKey(libraryRoot, productId);
            if (_resolvedPathCache.TryGetValue(cacheKey, out var cachedPath))
            {
                if (string.Equals(cachedPath, UnresolvedPathSentinel, StringComparison.Ordinal))
                {
                    return null;
                }

                if (Directory.Exists(cachedPath))
                {
                    return cachedPath;
                }

                _resolvedPathCache.Remove(cacheKey);
            }

            var strictMatch = ResolveStrict(libraryRoot, productId, shopSubdomain);
            if (!string.IsNullOrWhiteSpace(strictMatch))
            {
                _resolvedPathCache[cacheKey] = strictMatch;
                return strictMatch;
            }

            var fuzzyMatch = ResolveFuzzy(libraryRoot, productId, shopSubdomain, productName);
            if (!string.IsNullOrWhiteSpace(fuzzyMatch))
            {
                _resolvedPathCache[cacheKey] = fuzzyMatch;
                return fuzzyMatch;
            }

            _resolvedPathCache[cacheKey] = UnresolvedPathSentinel;
            return null;
        }

        private static string BuildResolutionCacheKey(string libraryRoot, string productId)
        {
            var normalizedRoot = libraryRoot ?? string.Empty;
            try
            {
                normalizedRoot = Path.GetFullPath(normalizedRoot);
            }
            catch
            {
                // Fallback to raw value when path normalization fails.
            }

            normalizedRoot = normalizedRoot
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
            return $"{normalizedRoot}|{productId}";
        }

        public static string NormalizeForComparison(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var normalized = input.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var builder = new StringBuilder(normalized.Length);

            foreach (var c in normalized)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.SpaceSeparator ||
                    category == UnicodeCategory.ConnectorPunctuation ||
                    category == UnicodeCategory.DashPunctuation ||
                    category == UnicodeCategory.OpenPunctuation ||
                    category == UnicodeCategory.ClosePunctuation ||
                    category == UnicodeCategory.InitialQuotePunctuation ||
                    category == UnicodeCategory.FinalQuotePunctuation ||
                    category == UnicodeCategory.OtherPunctuation ||
                    category == UnicodeCategory.MathSymbol ||
                    category == UnicodeCategory.CurrencySymbol ||
                    category == UnicodeCategory.ModifierSymbol ||
                    category == UnicodeCategory.OtherSymbol)
                {
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        public static IReadOnlyList<string> TokenizeProductName(string productName)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                return Array.Empty<string>();
            }

            var split = productName
                .Normalize(NormalizationForm.FormKC)
                .Split(new[] { ' ', '　', '_', '-', '/', '\\', '.', ',', '・', '|' }, StringSplitOptions.RemoveEmptyEntries);

            var tokens = new List<string>();
            foreach (var token in split)
            {
                var normalized = NormalizeForComparison(token);
                if (normalized.Length >= 2)
                {
                    tokens.Add(normalized);
                }
            }

            return tokens;
        }

        private static string ResolveStrict(string libraryRoot, string productId, string shopSubdomain)
        {
            foreach (var candidate in BuildStrictCandidates(libraryRoot, productId, shopSubdomain))
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> BuildStrictCandidates(string libraryRoot, string productId, string shopSubdomain)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot))
            {
                yield break;
            }

            foreach (var path in BuildStrictCandidatesForSingleRoot(libraryRoot, productId, shopSubdomain))
            {
                yield return path;
            }

            var parent = Directory.GetParent(libraryRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                yield break;
            }

            foreach (var path in BuildStrictCandidatesForSingleRoot(parent, productId, shopSubdomain))
            {
                yield return path;
            }
        }

        private static IEnumerable<string> BuildStrictCandidatesForSingleRoot(string root, string productId, string shopSubdomain)
        {
            yield return Path.Combine(root, $"b{productId}");
            yield return Path.Combine(root, productId);

            if (!string.IsNullOrWhiteSpace(shopSubdomain))
            {
                yield return Path.Combine(root, shopSubdomain, $"b{productId}");
                yield return Path.Combine(root, shopSubdomain, productId);
            }
        }

        private string ResolveFuzzy(string libraryRoot, string productId, string shopSubdomain, string productName)
        {
            var searchRoots = new List<string>();
            if (Directory.Exists(libraryRoot))
            {
                searchRoots.Add(libraryRoot);
            }

            var parent = Directory.GetParent(libraryRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                searchRoots.Add(parent);
            }

            var normalizedProductId = NormalizeForComparison(productId);
            var normalizedShop = NormalizeForComparison(shopSubdomain);
            var normalizedProductName = NormalizeForComparison(productName);
            var productNameTokens = TokenizeProductName(productName);
            var normalizedStrictName = NormalizeForComparison($"b{productId}");

            var scored = new List<ScoredDirectory>();
            foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var candidate in GetFuzzyCandidates(root, 2))
                {
                    var score = ScoreCandidate(
                        candidate,
                        normalizedProductId,
                        normalizedStrictName,
                        normalizedShop,
                        normalizedProductName,
                        productNameTokens);
                    if (score.Score < 60)
                    {
                        continue;
                    }

                    scored.Add(score);
                }
            }

            if (scored.Count == 0)
            {
                return null;
            }

            return scored
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.HasUnityPackage)
                .ThenBy(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Path.Length)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
        }

        private IReadOnlyList<DirectoryCandidate> GetFuzzyCandidates(string rootPath, int maxDepth)
        {
            if (_fuzzyCandidateCacheByRoot.TryGetValue(rootPath, out var cached))
            {
                return cached;
            }

            var built = EnumerateDirectories(rootPath, maxDepth)
                .Select(tuple => BuildDirectoryCandidate(tuple.Path, tuple.Depth))
                .ToList();
            _fuzzyCandidateCacheByRoot[rootPath] = built;
            return built;
        }

        private DirectoryCandidate BuildDirectoryCandidate(string path, int depth)
        {
            var folderName = Path.GetFileName(path) ?? string.Empty;
            return new DirectoryCandidate
            {
                Path = path,
                Depth = depth,
                NormalizedPath = NormalizeForComparison(path),
                NormalizedFolderName = NormalizeForComparison(folderName),
                HasUnityPackage = GetHasUnityPackageFlag(path)
            };
        }

        private bool GetHasUnityPackageFlag(string directoryPath)
        {
            if (_hasUnityPackageCache.TryGetValue(directoryPath, out var cached))
            {
                return cached;
            }

            var hasUnityPackage = false;
            try
            {
                hasUnityPackage = Directory.EnumerateFiles(directoryPath, "*.unitypackage", SearchOption.TopDirectoryOnly).Any();
            }
            catch
            {
                hasUnityPackage = false;
            }

            _hasUnityPackageCache[directoryPath] = hasUnityPackage;
            return hasUnityPackage;
        }

        private static IEnumerable<(string Path, int Depth)> EnumerateDirectories(string rootPath, int maxDepth)
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((rootPath, 0));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!Directory.Exists(current.Path))
                {
                    continue;
                }

                yield return current;

                if (current.Depth >= maxDepth)
                {
                    continue;
                }

                string[] children;
                try
                {
                    children = Directory.GetDirectories(current.Path);
                }
                catch
                {
                    continue;
                }

                foreach (var child in children)
                {
                    queue.Enqueue((child, current.Depth + 1));
                }
            }
        }

        private static ScoredDirectory ScoreCandidate(
            DirectoryCandidate candidate,
            string normalizedProductId,
            string normalizedStrictName,
            string normalizedShop,
            string normalizedProductName,
            IReadOnlyList<string> productNameTokens)
        {
            var score = 0;
            if (string.Equals(candidate.NormalizedFolderName, normalizedStrictName, StringComparison.Ordinal))
            {
                score += 100;
            }

            if (!string.IsNullOrWhiteSpace(normalizedProductId) && candidate.NormalizedPath.Contains(normalizedProductId))
            {
                score += 80;
            }

            if (!string.IsNullOrWhiteSpace(normalizedShop) && candidate.NormalizedPath.Contains(normalizedShop))
            {
                score += 40;
            }

            if (!string.IsNullOrWhiteSpace(normalizedProductName) &&
                string.Equals(candidate.NormalizedFolderName, normalizedProductName, StringComparison.Ordinal))
            {
                score += 25;
            }

            var tokenScore = 0;
            foreach (var token in productNameTokens)
            {
                if (candidate.NormalizedPath.Contains(token))
                {
                    tokenScore += 5;
                    if (tokenScore >= 30)
                    {
                        tokenScore = 30;
                        break;
                    }
                }
            }

            score += tokenScore;

            if (candidate.HasUnityPackage)
            {
                score += 10;
            }

            score -= 2 * candidate.Depth;

            return new ScoredDirectory
            {
                Path = candidate.Path,
                Depth = candidate.Depth,
                HasUnityPackage = candidate.HasUnityPackage,
                Score = score
            };
        }

        private sealed class DirectoryCandidate
        {
            public string Path;
            public int Depth;
            public string NormalizedPath;
            public string NormalizedFolderName;
            public bool HasUnityPackage;
        }

        private sealed class ScoredDirectory
        {
            public string Path;
            public int Depth;
            public bool HasUnityPackage;
            public int Score;
        }
    }
}
