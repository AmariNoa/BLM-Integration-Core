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

    internal sealed partial class BlmDatabaseService
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

    }
}
