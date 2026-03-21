using System.Collections.Generic;

namespace com.amari_noa.blm_integration_core.editor
{
    public interface IAmariBlmDestinationAssetPathUpdater
    {
        void UpdateDestinationAssetPaths(
            string batchId,
            string productId,
            string sourcePath,
            IEnumerable<string> destinationAssetPaths);
    }
}
