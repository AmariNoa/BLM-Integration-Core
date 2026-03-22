using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
#if AMARI_BLM_INTEGRATION_CORE_HAS_SQLITE_NET_VPM
using SQLite;
#endif

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmDatabaseLoadResult
    {
        public List<BlmItemRecord> Items = new List<BlmItemRecord>();
        public List<BlmListRecord> Lists = new List<BlmListRecord>();
        public Dictionary<long, HashSet<string>> ListProductIdsByListId = new Dictionary<long, HashSet<string>>();
        public string LibraryRootPath = string.Empty;
        public string ErrorMessage = string.Empty;

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    }

    internal sealed class BlmDatabaseService
    {
        private readonly BlmProductFolderResolver _folderResolver;

        public string DatabasePath { get; set; }

        public BlmDatabaseService(BlmProductFolderResolver folderResolver = null)
        {
            _folderResolver = folderResolver ?? new BlmProductFolderResolver();
            DatabasePath = BlmConstants.GetDefaultBlmDatabasePath();
        }

        public void ClearCaches()
        {
            _folderResolver.ClearCache();
        }

        public BlmDatabaseLoadResult Load()
        {
            var result = new BlmDatabaseLoadResult();
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            PerfLog($"Load start. databasePath='{DatabasePath}'");

            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                result.ErrorMessage = "BLM Integration Core supports Windows Editor only.";
                PerfLog($"Load aborted in {totalStopwatch.ElapsedMilliseconds} ms. reason='Platform not supported'");
                return result;
            }

#if !AMARI_BLM_INTEGRATION_CORE_HAS_SQLITE_NET_VPM
            result.ErrorMessage = "SQLite dependency is not available. Install com.amari-noa.sqlite-net-vpm.";
            PerfLog($"Load aborted in {totalStopwatch.ElapsedMilliseconds} ms. reason='SQLite dependency missing'");
            return result;
#else
            if (!File.Exists(DatabasePath))
            {
                result.ErrorMessage = $"BLM database was not found: {DatabasePath}";
                PerfLog($"Load aborted in {totalStopwatch.ElapsedMilliseconds} ms. reason='Database not found'");
                return result;
            }

            try
            {
                var openConnectionStopwatch = System.Diagnostics.Stopwatch.StartNew();
                using (var connection = new SQLiteConnection(
                           DatabasePath,
                           SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex))
                {
                    openConnectionStopwatch.Stop();
                    PerfLog($"SQLite connection opened in {openConnectionStopwatch.ElapsedMilliseconds} ms");

                    var libraryRootStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    result.LibraryRootPath = LoadLibraryRootPath(connection);
                    libraryRootStopwatch.Stop();
                    PerfLog($"Library root loaded in {libraryRootStopwatch.ElapsedMilliseconds} ms. hasValue={(!string.IsNullOrWhiteSpace(result.LibraryRootPath)).ToString().ToLowerInvariant()}");

                    var tagsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var tagsByProductId = LoadTagsByProductId(connection);
                    tagsStopwatch.Stop();
                    PerfLog($"Tags loaded in {tagsStopwatch.ElapsedMilliseconds} ms. productsWithTags={tagsByProductId.Count}");

                    var queryProductsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var productRows = connection.Query<BlmItemRow>(ProductSql).ToList();
                    queryProductsStopwatch.Stop();
                    PerfLog($"Product rows queried in {queryProductsStopwatch.ElapsedMilliseconds} ms. rowCount={productRows.Count}");

                    var mapProductsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    long folderResolveElapsedMs = 0;
                    long fileEnumerateElapsedMs = 0;
                    var totalEnumeratedFiles = 0;
                    foreach (var row in productRows)
                    {
                        var item = new BlmItemRecord
                        {
                            ProductId = row.id.ToString(CultureInfo.InvariantCulture),
                            ProductName = NormalizeText(row.name),
                            ShopName = NormalizeText(row.shop_name),
                            ShopSubdomain = NormalizeText(row.shop_subdomain),
                            ThumbnailUrl = NormalizeText(row.thumbnail_url),
                            RegisteredAt = ParseRegisteredAtUtc(row.registered_at),
                            PublishedAt = ParsePublishedAtUtc(row.published_at),
                            Category = NormalizeText(row.category_name),
                            SubCategory = NormalizeText(row.subcategory_name),
                            AgeRestriction = MapAgeRestriction(row.adult),
                            Tags = tagsByProductId.TryGetValue(row.id.ToString(CultureInfo.InvariantCulture), out var tags)
                                ? tags
                                : new List<string>()
                        };

                        var resolveFolderStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        item.RootFolderPath = _folderResolver.Resolve(
                            result.LibraryRootPath,
                            item.ProductId,
                            item.ShopSubdomain,
                            item.ProductName) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(item.RootFolderPath) || !Directory.Exists(item.RootFolderPath))
                        {
                            item.RootFolderPath = ResolveDirectFolderPath(
                                result.LibraryRootPath,
                                item.ProductId,
                                item.ShopSubdomain);
                        }

                        resolveFolderStopwatch.Stop();
                        folderResolveElapsedMs += resolveFolderStopwatch.ElapsedMilliseconds;

                        var enumerateFilesStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        item.Files = LoadFiles(item.RootFolderPath);
                        enumerateFilesStopwatch.Stop();
                        fileEnumerateElapsedMs += enumerateFilesStopwatch.ElapsedMilliseconds;
                        totalEnumeratedFiles += item.Files.Count;
                        result.Items.Add(item);
                    }
                    mapProductsStopwatch.Stop();
                    PerfLog(
                        $"Product mapping done in {mapProductsStopwatch.ElapsedMilliseconds} ms. " +
                        $"itemCount={result.Items.Count}, totalFiles={totalEnumeratedFiles}, folderResolve={folderResolveElapsedMs} ms, fileEnumerate={fileEnumerateElapsedMs} ms");

                    var listsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    result.Lists = connection.Query<BlmListRow>(ListsSql)
                        .Select(MapListRecord)
                        .ToList();
                    listsStopwatch.Stop();
                    PerfLog($"Lists loaded in {listsStopwatch.ElapsedMilliseconds} ms. listCount={result.Lists.Count}");

                    var listItemsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var totalListEntries = 0;
                    foreach (var list in result.Lists)
                    {
                        var productIds = new HashSet<string>(
                            connection.Query<BlmListItemRow>(ListItemSql, list.Id)
                            .Select(row => row.booth_item_id.ToString(CultureInfo.InvariantCulture))
                            .Where(id => !string.IsNullOrWhiteSpace(id)),
                            StringComparer.Ordinal);

                        totalListEntries += productIds.Count;
                        result.ListProductIdsByListId[list.Id] = productIds;
                    }

                    listItemsStopwatch.Stop();
                    PerfLog($"List item mappings loaded in {listItemsStopwatch.ElapsedMilliseconds} ms. mappedEntries={totalListEntries}");
                }
            }
            catch (Exception ex)
            {
                result.Items.Clear();
                result.Lists.Clear();
                result.ListProductIdsByListId.Clear();
                result.ErrorMessage = ex.Message;
                PerfLog($"Load failed after {totalStopwatch.ElapsedMilliseconds} ms. error='{ex.Message}'");
            }

            totalStopwatch.Stop();
            if (!result.HasError)
            {
                PerfLog(
                    $"Load completed in {totalStopwatch.ElapsedMilliseconds} ms. " +
                    $"itemCount={result.Items.Count}, listCount={result.Lists.Count}, hasLibraryRoot={(!string.IsNullOrWhiteSpace(result.LibraryRootPath)).ToString().ToLowerInvariant()}");
            }

            return result;
#endif
        }

#if AMARI_BLM_INTEGRATION_CORE_HAS_SQLITE_NET_VPM
        private static string LoadLibraryRootPath(SQLiteConnection connection)
        {
            const string sql = "SELECT item_directory_path FROM preferences LIMIT 1";

            try
            {
                var blobValue = connection.ExecuteScalar<byte[]>(sql);
                var decodedFromBlob = NormalizeLibraryRootPath(DecodeLibraryRootPathBytes(blobValue));
                if (!string.IsNullOrWhiteSpace(decodedFromBlob))
                {
                    return decodedFromBlob;
                }
            }
            catch
            {
                // Some BLM DB variants do not expose this column as BLOB.
            }

            try
            {
                var textValue = connection.ExecuteScalar<string>(sql);
                var normalizedText = NormalizeLibraryRootPath(textValue);
                if (!string.IsNullOrWhiteSpace(normalizedText))
                {
                    return normalizedText;
                }
            }
            catch
            {
                // Fallback to empty path.
            }

            return string.Empty;
        }

        private static string DecodeLibraryRootPathBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            catch
            {
                try
                {
                    return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static string NormalizeLibraryRootPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var normalized = raw.Replace("\0", string.Empty).Trim();
            if (normalized.Length >= 2)
            {
                var startsWithDoubleQuote = normalized[0] == '\"' && normalized[normalized.Length - 1] == '\"';
                var startsWithSingleQuote = normalized[0] == '\'' && normalized[normalized.Length - 1] == '\'';
                if (startsWithDoubleQuote || startsWithSingleQuote)
                {
                    normalized = normalized.Substring(1, normalized.Length - 2).Trim();
                }
            }

            return normalized;
        }

        private static Dictionary<string, List<string>> LoadTagsByProductId(SQLiteConnection connection)
        {
            var rows = connection.Query<BlmTagRow>(TagSql);
            var tagsByProductId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                var productId = row.booth_item_id.ToString(CultureInfo.InvariantCulture);
                if (!tagsByProductId.TryGetValue(productId, out var tagSet))
                {
                    tagSet = new HashSet<string>(StringComparer.Ordinal);
                    tagsByProductId[productId] = tagSet;
                }

                var tag = NormalizeText(row.tag);
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    tagSet.Add(tag);
                }
            }

            return tagsByProductId.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        }
#endif

        private static List<BlmFileRecord> LoadFiles(string rootFolderPath)
        {
            var files = new List<BlmFileRecord>();
            if (string.IsNullOrWhiteSpace(rootFolderPath) || !Directory.Exists(rootFolderPath))
            {
                return files;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(rootFolderPath, "*", SearchOption.AllDirectories))
                {
                    files.Add(new BlmFileRecord
                    {
                        FileName = Path.GetFileName(path) ?? string.Empty,
                        FullPath = path,
                        FileExtension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant()
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BLM Integration Core] Failed to enumerate files: {ex.Message}");
            }

            files.Sort((left, right) => string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase));
            return files;
        }

        private static string ResolveDirectFolderPath(string libraryRootPath, string productId, string shopSubdomain)
        {
            foreach (var candidate in EnumerateDirectFolderCandidates(libraryRootPath, productId, shopSubdomain))
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static IEnumerable<string> EnumerateDirectFolderCandidates(string libraryRootPath, string productId, string shopSubdomain)
        {
            if (string.IsNullOrWhiteSpace(libraryRootPath) || string.IsNullOrWhiteSpace(productId))
            {
                yield break;
            }

            foreach (var candidate in EnumerateDirectFolderCandidatesForSingleRoot(libraryRootPath, productId, shopSubdomain))
            {
                yield return candidate;
            }

            var parent = Directory.GetParent(libraryRootPath)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                yield break;
            }

            foreach (var candidate in EnumerateDirectFolderCandidatesForSingleRoot(parent, productId, shopSubdomain))
            {
                yield return candidate;
            }
        }

        private static IEnumerable<string> EnumerateDirectFolderCandidatesForSingleRoot(string rootPath, string productId, string shopSubdomain)
        {
            yield return Path.Combine(rootPath, $"b{productId}");
            yield return Path.Combine(rootPath, productId);

            if (string.IsNullOrWhiteSpace(shopSubdomain))
            {
                yield break;
            }

            yield return Path.Combine(rootPath, shopSubdomain, $"b{productId}");
            yield return Path.Combine(rootPath, shopSubdomain, productId);
        }

        private static BlmListRecord MapListRecord(BlmListRow row)
        {
            return new BlmListRecord
            {
                Id = row.id,
                Title = NormalizeText(row.title),
                Description = NormalizeText(row.description),
                CreatedAt = ParseDateTimeOffset(row.created_at),
                UpdatedAt = ParseDateTimeOffset(row.updated_at)
            };
        }

        private static string NormalizeText(string value)
        {
            return value?.Trim() ?? string.Empty;
        }

        private static DateTimeOffset? ParseDateTimeOffset(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static DateTime? ParseRegisteredAtUtc(string raw)
        {
            var parsed = ParseDateTimeOffset(raw);
            return parsed?.UtcDateTime;
        }

        private static DateTime? ParsePublishedAtUtc(string raw)
        {
            var parsed = ParseDateTimeOffset(raw);
            return parsed?.UtcDateTime;
        }

        private static BlmAgeRestriction MapAgeRestriction(long? rawAdult)
        {
            if (!rawAdult.HasValue)
            {
                return BlmAgeRestriction.Unknown;
            }

            if (rawAdult.Value == 1)
            {
                return BlmAgeRestriction.R18;
            }

            if (rawAdult.Value == 0)
            {
                return BlmAgeRestriction.AllAges;
            }

            return BlmAgeRestriction.Unknown;
        }

        private static void PerfLog(string message)
        {
            if (!BlmConstants.EnablePerformanceLogging)
            {
                return;
            }

            Debug.Log($"{BlmConstants.PerformanceLogPrefix}[DB] {message}");
        }

        private const string ProductSql = @"
SELECT
    i.id,
    COALESCE(obi.name, i.name) as name,
    s.name as shop_name,
    i.shop_subdomain,
    i.thumbnail_url,
    ri.created_at as registered_at,
    i.published_at as published_at,
    pc.name as category_name,
    sc.name as subcategory_name,
    COALESCE(obi.adult, i.adult) as adult
FROM booth_items i
LEFT JOIN overwritten_booth_items obi ON obi.booth_item_id = i.id
LEFT JOIN shops s ON i.shop_subdomain = s.subdomain
LEFT JOIN (
    SELECT booth_item_id, MAX(created_at) AS created_at
    FROM registered_items
    GROUP BY booth_item_id
) ri ON ri.booth_item_id = i.id
LEFT JOIN sub_categories sc ON i.sub_category = sc.id
LEFT JOIN parent_categories pc ON sc.parent_category_id = pc.id";

        private const string ListsSql = @"
SELECT id, title, description, created_at, updated_at
FROM lists
ORDER BY title";

        private const string ListItemSql = @"
SELECT DISTINCT ri.booth_item_id
FROM list_items li
INNER JOIN registered_items ri ON li.item_id = ri.id
WHERE li.list_id = ?";

        private const string TagSql = @"
WITH overwritten_items AS (
    SELECT DISTINCT booth_item_id
    FROM overwritten_booth_item_tags
)
SELECT booth_item_id, tag
FROM overwritten_booth_item_tags
UNION ALL
SELECT d.booth_item_id, d.tag
FROM booth_item_tag_relations d
LEFT JOIN overwritten_items o ON o.booth_item_id = d.booth_item_id
WHERE o.booth_item_id IS NULL";

        private sealed class BlmItemRow
        {
            public long id { get; set; }
            public string name { get; set; }
            public string shop_name { get; set; }
            public string shop_subdomain { get; set; }
            public string thumbnail_url { get; set; }
            public string registered_at { get; set; }
            public string published_at { get; set; }
            public string category_name { get; set; }
            public string subcategory_name { get; set; }
            public long? adult { get; set; }
        }

        private sealed class BlmTagRow
        {
            public long booth_item_id { get; set; }
            public string tag { get; set; }
        }

        private sealed class BlmListRow
        {
            public long id { get; set; }
            public string title { get; set; }
            public string description { get; set; }
            public string created_at { get; set; }
            public string updated_at { get; set; }
        }

        private sealed class BlmListItemRow
        {
            public long booth_item_id { get; set; }
        }
    }
}
