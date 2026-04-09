using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed partial class CatalogWindow
    {
        private static void ApplyPackageFont(VisualElement root)
        {
            if (root == null)
            {
                return;
            }

            var fontAsset = ResolveCatalogWindowFontAsset();
            if (fontAsset == null)
            {
                var fontAssetPath = AssetDatabase.GUIDToAssetPath(BlmConstants.CatalogWindowFontAssetGuid);
                Debug.LogWarning($"[BLM Integration Core] Catalog font asset not found. guid={BlmConstants.CatalogWindowFontAssetGuid}, path={fontAssetPath}");
                return;
            }

            root.style.unityFontDefinition = FontDefinition.FromSDFFont(fontAsset);
        }

        private static FontAsset ResolveCatalogWindowFontAsset()
        {
            if (_catalogWindowFontAsset != null)
            {
                return _catalogWindowFontAsset;
            }

            var baseFontFile = LoadAssetByGuid<Font>(BlmConstants.CatalogWindowFontFileGuid);
            if (baseFontFile != null)
            {
                var baseFontAsset = FontAsset.CreateFontAsset(baseFontFile);
                if (baseFontAsset != null)
                {
                    var fallbackAssets = new List<FontAsset>();
                    var emojiFontFile = LoadAssetByGuid<Font>(BlmConstants.CatalogWindowEmojiFontFileGuid);
                    if (emojiFontFile != null)
                    {
                        var emojiFontAsset = FontAsset.CreateFontAsset(emojiFontFile);
                        if (emojiFontAsset != null)
                        {
                            fallbackAssets.Add(emojiFontAsset);
                        }
                    }

                    baseFontAsset.fallbackFontAssetTable = fallbackAssets;
                    _catalogWindowFontAsset = baseFontAsset;
                    return _catalogWindowFontAsset;
                }
            }

            _catalogWindowFontAsset = LoadAssetByGuid<FontAsset>(BlmConstants.CatalogWindowFontAssetGuid);
            return _catalogWindowFontAsset;
        }

        private static T LoadAssetByGuid<T>(string guid) where T : UnityEngine.Object
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                return null;
            }

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<T>(assetPath);
        }

        private static HashSet<string> BuildImportTargetProductIdSet(IReadOnlyList<BlmImportRequestItem> batchItems)
        {
            var productIds = new HashSet<string>(StringComparer.Ordinal);
            if (batchItems == null || batchItems.Count == 0)
            {
                return productIds;
            }

            for (var i = 0; i < batchItems.Count; i++)
            {
                var item = batchItems[i];
                if (item == null || string.IsNullOrWhiteSpace(item.ProductId))
                {
                    continue;
                }

                productIds.Add(item.ProductId);
            }

            return productIds;
        }

        private static List<string> BuildImportQueueLabels(IEnumerable<BlmImportRequestItem> items)
        {
            if (items == null)
            {
                return new List<string>();
            }

            var labels = new List<string>();
            foreach (var item in items)
            {
                labels.Add(BuildImportQueueLabel(item));
            }

            return labels;
        }

        private static string BuildImportQueueLabel(BlmImportRequestItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            var fileName = Path.GetFileName(item.SourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return WrapImportQueueText(item.SourcePath ?? string.Empty);
            }

            return WrapImportQueueText(fileName);
        }

        private static string WrapImportQueueText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            const int chunkLength = 32;
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var sourceLines = normalized.Split('\n');
            var builder = new StringBuilder(normalized.Length + (normalized.Length / chunkLength));
            for (var i = 0; i < sourceLines.Length; i++)
            {
                var line = sourceLines[i] ?? string.Empty;
                var offset = 0;
                while (offset < line.Length)
                {
                    var length = Math.Min(chunkLength, line.Length - offset);
                    builder.Append(line, offset, length);
                    offset += length;
                    if (offset < line.Length)
                    {
                        builder.Append('\n');
                    }
                }

                if (i < sourceLines.Length - 1)
                {
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }

        private static void ApplyImportedStateRowClass(
            VisualElement row,
            ImportedStateRowHighlightKind importedState,
            bool allowPartialHighlight)
        {
            if (row == null)
            {
                return;
            }

            row.RemoveFromClassList(ImportedFileRowClassName);
            row.RemoveFromClassList(PartiallyImportedFileRowClassName);
            if (importedState == ImportedStateRowHighlightKind.Imported)
            {
                row.AddToClassList(ImportedFileRowClassName);
                return;
            }

            if (allowPartialHighlight && importedState == ImportedStateRowHighlightKind.PartiallyImported)
            {
                row.AddToClassList(PartiallyImportedFileRowClassName);
            }
        }

        private static void ReplaceChoicesPreservingIndex(DropdownField field, List<string> choices)
        {
            if (field == null)
            {
                return;
            }

            var oldIndex = field.index;
            field.choices = choices ?? new List<string>();
            if (field.choices.Count == 0)
            {
                field.index = -1;
                return;
            }

            if (oldIndex < 0 || oldIndex >= field.choices.Count)
            {
                field.index = 0;
                return;
            }

            field.index = oldIndex;
        }

        private static int LoadDropdownIndexFromEditorPrefs(string editorPrefsKey, int defaultIndex, int choiceCount)
        {
            if (choiceCount <= 0)
            {
                return -1;
            }

            var normalizedDefaultIndex = NormalizeDropdownIndex(defaultIndex, choiceCount, 0);
            var hasStoredSetting = EditorPrefs.HasKey(editorPrefsKey);
            var storedValue = EditorPrefs.GetInt(editorPrefsKey, normalizedDefaultIndex);
            var normalized = NormalizeDropdownIndex(storedValue, choiceCount, normalizedDefaultIndex);
            if (!hasStoredSetting || storedValue != normalized)
            {
                EditorPrefs.SetInt(editorPrefsKey, normalized);
            }

            return normalized;
        }

        private static void PersistDropdownIndexToEditorPrefs(string editorPrefsKey, int index, int choiceCount, int defaultIndex)
        {
            if (choiceCount <= 0)
            {
                return;
            }

            var normalizedDefaultIndex = NormalizeDropdownIndex(defaultIndex, choiceCount, 0);
            var normalized = NormalizeDropdownIndex(index, choiceCount, normalizedDefaultIndex);
            EditorPrefs.SetInt(editorPrefsKey, normalized);
        }

        private static int NormalizeDropdownIndex(int index, int choiceCount, int defaultIndex)
        {
            if (choiceCount <= 0)
            {
                return -1;
            }

            var normalizedDefaultIndex = Mathf.Clamp(defaultIndex, 0, choiceCount - 1);
            if (index < 0 || index >= choiceCount)
            {
                return normalizedDefaultIndex;
            }

            return index;
        }

        private static int LoadPageSizeFromEditorPrefs()
        {
            var defaultPageSize = GetDefaultPageSize();
            var hasStoredSetting = EditorPrefs.HasKey(BlmConstants.PageSizeEditorPrefsKey);
            var storedValue = EditorPrefs.GetInt(BlmConstants.PageSizeEditorPrefsKey, defaultPageSize);
            var normalized = NormalizePageSize(storedValue);
            if (!hasStoredSetting || storedValue != normalized)
            {
                EditorPrefs.SetInt(BlmConstants.PageSizeEditorPrefsKey, normalized);
            }

            return normalized;
        }

        private static void PersistPageSizeToEditorPrefs(int pageSize)
        {
            EditorPrefs.SetInt(BlmConstants.PageSizeEditorPrefsKey, NormalizePageSize(pageSize));
        }

        private static int NormalizePageSize(int pageSize)
        {
            if (BlmConstants.PageSizes != null && BlmConstants.PageSizes.Contains(pageSize))
            {
                return pageSize;
            }

            return GetDefaultPageSize();
        }

        private static int GetDefaultPageSize()
        {
            if (BlmConstants.PageSizes == null || BlmConstants.PageSizes.Length == 0)
            {
                return Math.Max(1, BlmConstants.DefaultPageSize);
            }

            if (BlmConstants.PageSizes.Contains(BlmConstants.DefaultPageSize))
            {
                return BlmConstants.DefaultPageSize;
            }

            return BlmConstants.PageSizes[0];
        }

        private static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Mathf.Min(Mathf.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }

        private static string SanitizeForLog(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (sanitized.Length <= 80)
            {
                return sanitized;
            }

            return sanitized.Substring(0, 80);
        }

        private static void PerfLog(string message)
        {
            if (!BlmConstants.EnablePerformanceLogging)
            {
                return;
            }

            Debug.Log($"{BlmConstants.PerformanceLogPrefix}[CatalogWindow] {message}");
        }
    }
}
