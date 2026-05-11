using System.Collections.Generic;
using System.Threading;
using com.amari_noa.unitypackage_pipeline_core.editor;
using UnityEditor;

namespace com.amari_noa.blm_integration_core.editor
{
    internal sealed class BlmUnityPackageContentReadProvider : IAmariUnityPackageContentReadProvider
    {
        public bool TryRead(
            string packagePath,
            out IReadOnlyList<AmariUnityPackageContentEntry> entries,
            out string errorMessage,
            CancellationToken cancellationToken = default)
        {
            return BlmUnityPackageGuidCache.Shared.TryGetContentEntries(
                packagePath,
                cancellationToken,
                out entries,
                out errorMessage);
        }
    }

    [InitializeOnLoad]
    internal static class BlmUnityPackageContentReadProviderRegistration
    {
        static BlmUnityPackageContentReadProviderRegistration()
        {
            AmariUnityPackageContentReaders.RegisterProvider(new BlmUnityPackageContentReadProvider());
        }
    }
}
