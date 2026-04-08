using System;
using System.Collections.Generic;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmImportIndexAssetResolveCache
    {
        private readonly Dictionary<string, string> _guidByAssetPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _assetPathByGuid =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public bool TryGetGuidByAssetPath(string assetPath, out string guid)
        {
            return _guidByAssetPath.TryGetValue(NormalizeAssetPath(assetPath), out guid);
        }

        public void SetGuidByAssetPath(string assetPath, string guid)
        {
            _guidByAssetPath[NormalizeAssetPath(assetPath)] = NormalizeGuid(guid);
        }

        public bool TryGetAssetPathByGuid(string guid, out string assetPath)
        {
            return _assetPathByGuid.TryGetValue(NormalizeGuid(guid), out assetPath);
        }

        public void SetAssetPathByGuid(string guid, string assetPath)
        {
            _assetPathByGuid[NormalizeGuid(guid)] = NormalizeAssetPath(assetPath);
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            return assetPath.Replace('\\', '/').Trim();
        }

        private static string NormalizeGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                return string.Empty;
            }

            return guid.Trim().ToLowerInvariant();
        }
    }
}
