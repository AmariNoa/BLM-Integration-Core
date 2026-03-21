using System;
using System.Collections.Generic;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class AmariBlmItemRecord
    {
        public string ProductId = string.Empty;
        public string ProductName = string.Empty;
        public string ShopName = string.Empty;
        public string ShopSubdomain = string.Empty;
        public string RootFolderPath = string.Empty;
        public string ThumbnailPath = string.Empty;
        public string ThumbnailUrl = string.Empty;
        public DateTime? RegisteredAt;
        public DateTime? PublishedAt;
        public string Category = string.Empty;
        public string SubCategory = string.Empty;
        public AmariBlmAgeRestriction AgeRestriction = AmariBlmAgeRestriction.Unknown;
        public List<string> Tags = new List<string>();
        public List<AmariBlmFileRecord> Files = new List<AmariBlmFileRecord>();
    }
}
