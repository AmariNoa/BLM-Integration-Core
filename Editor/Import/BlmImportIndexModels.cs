using System.Collections.Generic;
using Newtonsoft.Json;

namespace com.amari_noa.blm_integration_core.editor
{
    internal static class BlmImportIndexDeletePolicies
    {
        internal const string Deletable = "deletable";
        internal const string Protected = "protected";
    }

    internal enum BlmImportedItemKind
    {
        UnityPackage = 0,
        NonUnityPackage = 1
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmImportIndexDocument
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = BlmConstants.ImportIndexSchemaVersion;

        [JsonProperty("products")]
        public Dictionary<string, BlmImportIndexProductEntry> Products { get; set; } =
            new Dictionary<string, BlmImportIndexProductEntry>(System.StringComparer.Ordinal);

        [JsonProperty("guidOwners")]
        public Dictionary<string, BlmImportIndexGuidOwnerEntry> GuidOwners { get; set; } =
            new Dictionary<string, BlmImportIndexGuidOwnerEntry>(System.StringComparer.Ordinal);

        [JsonProperty("fileHashes")]
        public Dictionary<string, BlmImportIndexFileHashEntry> FileHashes { get; set; } =
            new Dictionary<string, BlmImportIndexFileHashEntry>(System.StringComparer.OrdinalIgnoreCase);
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmImportIndexProductEntry
    {
        [JsonProperty("guids")]
        public List<string> Guids { get; set; } = new List<string>();
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmImportIndexGuidOwnerEntry
    {
        [JsonProperty("ownerProductIds")]
        public List<string> OwnerProductIds { get; set; } = new List<string>();

        [JsonProperty("deletePolicy")]
        public string DeletePolicy { get; set; } = BlmImportIndexDeletePolicies.Protected;

        [JsonProperty("lastKnownAssetPath")]
        public string LastKnownAssetPath { get; set; } = string.Empty;
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class BlmImportIndexFileHashEntry
    {
        [JsonProperty("fileSize")]
        public long FileSize { get; set; }

        [JsonProperty("lastWriteTimeUtcTicks")]
        public long LastWriteTimeUtcTicks { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
