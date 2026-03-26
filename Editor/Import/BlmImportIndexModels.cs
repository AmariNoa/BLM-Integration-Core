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
}
