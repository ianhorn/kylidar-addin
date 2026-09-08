/*
 * Given an AOI geometry (map CRS) and an optional buffer in feet:
 *   1. Buffer the AOI (converting feet to the geometry's own linear unit).
 *   2. Project to WGS84 and search the STAC API for intersecting LiDAR (COPC) tiles.
 *   3. Download each matching COPC tile locally (see TileDownloadService / PdalRunner for why).
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
            string geoJson, string wkt, string outputLasPath,
            IProgress<string> progress, CancellationToken ct = default)
        {
            string tempDir = null;
            try
            {
                progress?.Report("Searching STAC catalog for LiDAR coverage...");
                using var stac = new StacClient();
                var results = await stac.SearchIntersectsAsync(geoJson, limit: 200, ct).ConfigureAwait(false);

                var tiles = new List<(StacItem item, StacAsset asset)>();
                foreach (var item in results?.Features ?? Enumerable.Empty<StacItem>())
                    foreach (var asset in item.GetCopcAssets())
                        tiles.Add((item, asset));

                if (tiles.Count == 0)
                    return new ClipResult { Success = false, Error = "No LiDAR coverage found for this AOI." };

                progress?.Report($"Found {tiles.Count} LiDAR tile(s) intersecting the AOI.");

                // 3. Download each tile locally.
                tempDir = Path.Combine(Path.GetTempPath(), $"kylidar_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                var localPaths = new List<string>();
                foreach (var (item, asset) in tiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(new Uri(asset.Href).LocalPath);
                    var localPath = Path.Combine(tempDir, fileName);
                    await TileDownloadService.DownloadAsync(asset.Href, localPath, progress, ct).ConfigureAwait(false);
                    localPaths.Add(localPath);
                }

                // 4. Crop + merge + write via PDAL.
                progress?.Report("Clipping and merging point cloud tiles...");
                var pipelineJson = BuildPipelineJson(localPaths, wkt, outputLasPath);
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

        private static string BuildPipelineJson(IReadOnlyList<string> localCopcPaths, string cropWkt, string outputLasPath)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WritePropertyName("pipeline");
                w.WriteStartArray();

                foreach (var path in localCopcPaths)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "readers.copc");
                    w.WriteString("filename", path);
                    w.WriteString("polygon", cropWkt + "/EPSG:4326");
                    w.WriteEndObject();
                }

                if (localCopcPaths.Count > 1)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "filters.merge");
                    w.WriteEndObject();
                }

                w.WriteStartObject();
                w.WriteString("type", "writers.las");
                w.WriteString("filename", outputLasPath);
                w.WriteEndObject();

                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
