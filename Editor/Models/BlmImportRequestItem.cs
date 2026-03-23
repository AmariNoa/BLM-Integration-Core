using System.Collections.Generic;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class BlmImportRequestItem
    {
        public string ProductId = string.Empty;
        public string ProductName = string.Empty;
        public string ShopName = string.Empty;
        public string SourcePath = string.Empty;
        public string RootFolderPath = string.Empty;
        public string NormalizedRelativePath = string.Empty;
        public List<string> DestinationAssetPaths = new List<string>();
        public List<string> Tags = new List<string>();
    }
}
