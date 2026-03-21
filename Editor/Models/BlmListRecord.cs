using System;

namespace com.amari_noa.blm_integration_core.editor
{
    public sealed class AmariBlmListRecord
    {
        public long Id;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public DateTimeOffset? CreatedAt;
        public DateTimeOffset? UpdatedAt;
    }
}
