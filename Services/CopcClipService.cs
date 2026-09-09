/*
 * Given an AOI geometry (map CRS), an optional buffer in feet, and the LiDAR phase(s) to search:
 *   1. Buffer the AOI (converting feet to the geometry's own linear unit).
 *   2. Project to WGS84 and search the selected STAC collections for intersecting LiDAR tiles.
 *   3. Download each matching tile locally (see TileDownloadService / PdalRunner for why).
 *   4. Run a PDAL pipeline: crop each local tile to the AOI polygon, merge, write one .las.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using KylidarAddin.Stac;

namespace KylidarAddin.Services
{
    public class ClipResult
    {
        public bool Success { get; set; }
        public string OutputLasPath { get; set; }
        public string Error { get; set; }
    }

    public static class CopcClipService
    {
        /// <summary>
        /// Geometry-engine step (buffer + project to WGS84 + WKT/GeoJSON export). Must be called
        /// from the MCT thread (inside QueuedTask.Run) -- unlike ClipToAoiAsync below, which does
        /// no ArcGIS.Core.Geometry work and must NOT be run on/blocked against the MCT thread,
        /// since it awaits network I/O and an external process for potentially a long time.
        /// </summary>
        public static (string geoJson, string wkt) PrepareAoi(Geometry aoi, double bufferFeet, IProgress<string> progress)
        {
            var working = BufferInFeet(aoi, bufferFeet, progress);
            if (!(working is Polygon) && bufferFeet <= 0)
                throw new InvalidOperationException("A buffer is required unless the AOI is already a polygon.");

            var wgs84 = (Geometry)GeometryEngine.Instance.Project(working, SpatialReferences.WGS84);
            return (GeoJsonConverter.ToGeoJsonGeometry(wgs84), GeoJsonConverter.ToWkt(wgs84));
        }

        public static async Task<ClipResult> ClipToAoiAsync(
            string geoJson, string wkt, IReadOnlyCollection<string> collections, string outputLasPath,
            IProgress<string> progress, CancellationToken ct = default)
        {
            string tempDir = null;
            try
            {
                progress?.Report("Searching STAC catalog for LiDAR coverage...");
                using var stac = new StacClient();
                var results = await stac.SearchIntersectsAsync(geoJson, collections, limit: 200, ct: ct).ConfigureAwait(false);

                var tiles = new List<(StacItem item, StacAsset asset)>();
                foreach (var item in results?.Features ?? Enumerable.Empty<StacItem>())
                    foreach (var asset in item.GetLidarAssets())
                        tiles.Add((item, asset));

                if (tiles.Count == 0)
                    return new ClipResult { Success = false, Error = "No LiDAR coverage found for this AOI in the selected phase(s)." };

                progress?.Report($"Found {tiles.Count} LiDAR tile(s) intersecting the AOI.");

                // 3. Download each tile locally.
                tempDir = Path.Combine(Path.GetTempPath(), $"kylidar_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                var localTiles = new List<(string path, bool isCopc)>();
                foreach (var (item, asset) in tiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(new Uri(asset.Href).LocalPath);
                    var localPath = Path.Combine(tempDir, fileName);
                    await TileDownloadService.DownloadAsync(asset.Href, localPath, progress, ct).ConfigureAwait(false);
                    localTiles.Add((localPath, asset.IsCopc));
                }

                // 4. Crop + merge + write via PDAL.
                progress?.Report("Clipping and merging point cloud tiles...");
                var pipelineJson = BuildPipelineJson(localTiles, wkt, outputLasPath);
                var pdalResult = await PdalRunner.RunPipelineAsync(pipelineJson, progress, ct).ConfigureAwait(false);

                if (!pdalResult.Success)
                    return new ClipResult { Success = false, Error = pdalResult.Error ?? "PDAL failed." };

                return new ClipResult { Success = true, OutputLasPath = outputLasPath };
            }
            finally
            {
                if (tempDir != null)
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>Buffer a geometry by a distance in feet, converting to the geometry's own linear unit.</summary>
        private static Geometry BufferInFeet(Geometry geometry, double bufferFeet, IProgress<string> progress)
        {
            if (bufferFeet <= 0) return geometry;

            var sr = geometry.SpatialReference;
            double distanceInSrUnits;
            if (sr != null && !sr.IsGeographic && sr.Unit != null)
            {
                // 1 US survey foot = 0.3048006096 m; convert feet -> meters -> SR's linear unit.
                const double metersPerFoot = 0.3048006096;
                var metersPerSrUnit = sr.Unit.ConversionFactor; // SR unit -> meters
                distanceInSrUnits = (bufferFeet * metersPerFoot) / metersPerSrUnit;
            }
            else
            {
                // Geographic (or unknown) SR: reproject to Web Mercator (meters) to buffer, then
                // reproject back isn't meaningful here since callers reproject to WGS84 next anyway --
                // just buffer directly in Web Mercator and hand back a WGS84-reprojectable geometry.
                progress?.Report("AOI has no projected spatial reference; buffering in Web Mercator.");
                var webMercator = SpatialReferenceBuilder.CreateSpatialReference(3857);
                var projected = GeometryEngine.Instance.Project(geometry, webMercator);
                const double metersPerFoot = 0.3048006096;
                var buffered = GeometryEngine.Instance.Buffer(projected, bufferFeet * metersPerFoot);
                return buffered;
            }

            return GeometryEngine.Instance.Buffer(geometry, distanceInSrUnits);
        }

        /// <summary>
        /// Build a PDAL pipeline that crops each local tile to the AOI polygon and merges the
        /// results into one .las. COPC tiles use readers.copc's built-in "polygon" bounds option
        /// (spatially indexed, so cheaper even though the file is already local); plain LAS/LAZ
        /// tiles (phase 1) use readers.las followed by an explicit filters.crop stage instead,
        /// since that reader has no such option. All stages are explicitly tagged and wired via
        /// "inputs" rather than relying on PDAL's implicit chaining, since branches here have a
        /// different shape (one stage vs. two) depending on tile format.
        /// </summary>
        private static string BuildPipelineJson(IReadOnlyList<(string path, bool isCopc)> localTiles, string cropWkt, string outputLasPath)
        {
            var polygonOption = cropWkt + "/EPSG:4326";
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WritePropertyName("pipeline");
                w.WriteStartArray();

                var branchTags = new List<string>();
                for (int i = 0; i < localTiles.Count; i++)
                {
                    var (path, isCopc) = localTiles[i];
                    if (isCopc)
                    {
                        var tag = $"tile{i}";
                        w.WriteStartObject();
                        w.WriteString("type", "readers.copc");
                        w.WriteString("filename", path);
                        w.WriteString("polygon", polygonOption);
                        w.WriteString("tag", tag);
                        w.WriteEndObject();
                        branchTags.Add(tag);
                    }
                    else
                    {
                        var readTag = $"tile{i}_read";
                        var cropTag = $"tile{i}_crop";
                        w.WriteStartObject();
                        w.WriteString("type", "readers.las");
                        w.WriteString("filename", path);
                        w.WriteString("tag", readTag);
                        w.WriteEndObject();

                        w.WriteStartObject();
                        w.WriteString("type", "filters.crop");
                        w.WriteString("polygon", polygonOption);
                        w.WriteString("tag", cropTag);
                        w.WritePropertyName("inputs");
                        w.WriteStartArray();
                        w.WriteStringValue(readTag);
                        w.WriteEndArray();
                        w.WriteEndObject();
                        branchTags.Add(cropTag);
                    }
                }

                string writerInputTag;
                if (branchTags.Count > 1)
                {
                    const string mergeTag = "merged";
                    w.WriteStartObject();
                    w.WriteString("type", "filters.merge");
                    w.WriteString("tag", mergeTag);
                    w.WritePropertyName("inputs");
                    w.WriteStartArray();
                    foreach (var tag in branchTags) w.WriteStringValue(tag);
                    w.WriteEndArray();
                    w.WriteEndObject();
                    writerInputTag = mergeTag;
                }
                else
                {
                    writerInputTag = branchTags[0];
                }

                w.WriteStartObject();
                w.WriteString("type", "writers.las");
                w.WriteString("filename", outputLasPath);
                w.WritePropertyName("inputs");
                w.WriteStartArray();
                w.WriteStringValue(writerInputTag);
                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
