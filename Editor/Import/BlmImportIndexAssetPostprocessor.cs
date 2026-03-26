using UnityEditor;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmImportIndexAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            _ = importedAssets;
            _ = movedAssets;
            _ = movedFromAssetPaths;

            if (deletedAssets == null || deletedAssets.Length == 0)
            {
                return;
            }

            BlmImportIndexService.Shared.HandleDeletedAssets(deletedAssets);
        }
    }
}
