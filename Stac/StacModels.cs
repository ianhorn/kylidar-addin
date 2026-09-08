/*
 * STAC API data models, trimmed to what kylidar-addin needs (AOI "intersects" search + COPC
 * asset discovery). Duplicated from the fuller set in the kyfromabove-ext add-in on purpose --
 * these are two independent add-ins (see README) -- rather than shared as a library.
 * Conforms to STAC API 1.0.0 / stac-fastapi responses.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KylidarAddin.Stac
{
    /// <summary>A STAC Link (pagination, self, parent, etc.).</summary>
    public class StacLink
    {
        [JsonPropertyName("rel")] public string Rel { get; set; }
        [JsonPropertyName("href")] public string Href { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
    }

    /// <summary>A STAC asset (the downloadable file: data, thumbnail, metadata).</summary>
    public class StacAsset
    {
        [JsonPropertyName("href")] public string Href { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("roles")] public List<string> Roles { get; set; }
        [JsonPropertyName("file:size")] public long? FileSize { get; set; }

        /// <summary>True if this asset is a Cloud Optimized Point Cloud (by filename convention).</summary>
        public bool IsCopc => !string.IsNullOrEmpty(Href) && Href.EndsWith(".copc.laz", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Item properties: known STAC core + common extensions, plus arbitrary extras.</summary>
    public class StacProperties
    {
        [JsonPropertyName("datetime")] public string Datetime { get; set; }
        [JsonPropertyName("proj:epsg")] public int? ProjEpsg { get; set; }

        /// <summary>Any additional properties not explicitly mapped.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }

    /// <summary>A single STAC Item (a Feature).</summary>
    public class StacItem
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("collection")] public string Collection { get; set; }
        [JsonPropertyName("bbox")] public double[] Bbox { get; set; }
        /// <summary>Raw GeoJSON geometry (kept as JsonElement to avoid modeling every geometry type).</summary>
        [JsonPropertyName("geometry")] public JsonElement Geometry { get; set; }
        [JsonPropertyName("assets")] public Dictionary<string, StacAsset> Assets { get; set; }
        [JsonPropertyName("properties")] public StacProperties Properties { get; set; }

        /// <summary>Every COPC point-cloud asset on this item (there's usually exactly one).</summary>
        public IEnumerable<StacAsset> GetCopcAssets() =>
            Assets?.Values.Where(a => a != null && a.IsCopc) ?? Enumerable.Empty<StacAsset>();
    }

    /// <summary>A GeoJSON FeatureCollection returned by STAC search / items endpoints.</summary>
    public class StacItemCollection
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("features")] public List<StacItem> Features { get; set; } = new List<StacItem>();
        [JsonPropertyName("links")] public List<StacLink> Links { get; set; } = new List<StacLink>();
        [JsonPropertyName("numberMatched")] public int? NumberMatched { get; set; }
        [JsonPropertyName("numberReturned")] public int? NumberReturned { get; set; }
    }
}
