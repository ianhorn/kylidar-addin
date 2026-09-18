/*
 * Downloads the KyFromAbove hydro-enforced breaklines that fall inside the AOI, clips them to it,
 * reprojects them to EPSG:3089 (Kentucky Single Zone, ftUS -- the CRS of the point-cloud tiles),
 * and writes them to a Z-enabled feature class in a new file geodatabase so the LAS dataset can
 * reference them as a Hard_Line surface constraint (see LasDatasetService). Three steps, split so
 * each runs on the right thread:
 *   - PrepareRegion  (MCT):        AOI/search polygon -> clip polygon (3089) + query filter (3857)
 *   - FetchAsync     (any thread): page through each phase's feature service with that filter
 *   - WriteFeatureClass (MCT):     project, clip, and insert into the geodatabase
 * This file deliberately depends on ArcGIS.Core only (no QueuedTask/geoprocessing), so the whole
 * pipeline can also be exercised from a stand-alone CoreHost console app.
 *
 * The services are hosted in Web Mercator (EPSG:3857). Phase 1's has NO Z values (hasZ=false), so
 * it can't act as an elevation constraint and is skipped -- checked from the layer's own metadata
 * rather than hardcoded. Phases 2 and 3 carry Z in feet, matching the LAS tiles' vertical units.
 *
 * Reprojection uses GeometryEngine.Project's default datum transformation on purpose. It differs
 * from a plain "same numbers, relabel NAD83" conversion by ~3 ft (1.4 ft E, 2.6 ft S at Georgetown),
 * and that shift is right: against real Phase 2 tiles, the water-classified points were contained by
 * the Lake-Pond breaklines with 11-20 misclassified points using the default transformation versus
 * 400-600 using no shift (i.e. the data was published from state plane using Pro's default, and
 * projecting back with the same default recovers the original coordinates).
 *
 * The clip polygon and the lines must share a spatial reference or GeometryEngine.Intersection
 * throws, hence the clip region is held in 3089 alongside the 3857 copy used to query the service.
 * Z at each new clip vertex is interpolated along the original segment by Intersection (verified).
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.DDL;
using ArcGIS.Core.Geometry;

namespace KylidarAddin.Services
{
    /// <summary>The area breaklines are fetched for and clipped to: the same polygon the tile search
    /// used (AOI, buffered when clipping with a buffer), in both spatial references it's needed in.</summary>
    public sealed class BreaklineRegion
    {
        /// <summary>Simplified clip polygon in EPSG:3089, same SR as the projected lines.</summary>
        public Polygon Polygon3089 { get; init; }
        /// <summary>Esri JSON of the polygon in EPSG:3857, used as the service's spatial filter.</summary>
        public string EsriJson3857 { get; init; }
    }

    /// <summary>One downloaded breakline, still in the service's Web Mercator coordinates.</summary>
    public sealed class RawBreakline
    {
        public int Phase { get; init; }
        public string LineType { get; init; }
        public List<List<Coordinate3D>> Paths { get; init; }
    }

    public sealed class BreaklineFetchResult
    {
        public List<RawBreakline> Lines { get; } = new List<RawBreakline>();
        public List<string> Notes { get; } = new List<string>();
    }

    public sealed class BreaklineWriteResult
    {
        public string FeatureClassPath { get; init; }
        public int FeatureCount { get; init; }
    }

    public static class BreaklineService
    {
        private const int WebMercatorWkid = 3857;
        private const int OutputWkid = 3089;
        private const string FeatureClassName = "Breaklines";
        private const string TypeField = "B_LINE_TY";

        private static readonly (int phase, string url)[] Services =
        {
            (1, "https://services3.arcgis.com/ghsX9CKghMvyYjBU/ArcGIS/rest/services/Ky_KYAPED_HydroEnforcedBreaklines_Phase1_WM_gdb/FeatureServer/0"),
            (2, "https://services3.arcgis.com/ghsX9CKghMvyYjBU/ArcGIS/rest/services/KYAPED_HydroEnforced_Breaklines_Phase2/FeatureServer/0"),
            (3, "https://services3.arcgis.com/ghsX9CKghMvyYjBU/ArcGIS/rest/services/KYAPED_HydroEnforced_Breaklines_Phase3_/FeatureServer/0"),
        };

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        /// <summary>
        /// Build the clip/query region from the tile-search polygon. Returns null for a non-polygon
        /// (a point or line AOI with no buffer has no area to clip breaklines to). MCT thread.
        /// </summary>
        public static BreaklineRegion PrepareRegion(Geometry searchGeometry)
        {
            if (searchGeometry is not Polygon polygon || polygon.IsEmpty) return null;

            var sr3089 = SpatialReferenceBuilder.CreateSpatialReference(OutputWkid);
            var sr3857 = SpatialReferenceBuilder.CreateSpatialReference(WebMercatorWkid);

            var p3089 = (Polygon)GeometryEngine.Instance.Project(polygon, sr3089);
            p3089 = (Polygon)GeometryEngine.Instance.SimplifyAsFeature(p3089, true);
            // Densify any curves (e.g. from Buffer) into straight segments so the ring writer below,
            // which only reads segment vertices, doesn't cut corners.
            var p3857 = (Polygon)GeometryEngine.Instance.Project(p3089, sr3857);
            p3857 = (Polygon)GeometryEngine.Instance.DensifyByDeviation(p3857, 0.25);

            return new BreaklineRegion { Polygon3089 = p3089, EsriJson3857 = PolygonToEsriJson(p3857) };
        }

        /// <summary>Every ring (exterior and hole) as Esri JSON -- ArcGIS keeps exterior rings clockwise
        /// and holes counter-clockwise, which is the Esri JSON convention too.</summary>
        private static string PolygonToEsriJson(Polygon polygon)
        {
            var sb = new StringBuilder("{\"rings\":[");
            bool firstRing = true;
            foreach (var part in polygon.Parts)
            {
                if (!firstRing) sb.Append(',');
                firstRing = false;
                sb.Append('[');
                MapPoint last = null;
                bool firstPt = true;
                foreach (var segment in part)
                {
                    if (!firstPt) sb.Append(',');
                    firstPt = false;
                    AppendXy(sb, segment.StartPoint);
                    last = segment.EndPoint;
                }
                if (last != null) { sb.Append(','); AppendXy(sb, last); }
                sb.Append(']');
            }
            sb.Append("],\"spatialReference\":{\"wkid\":").Append(WebMercatorWkid).Append("}}");
            return sb.ToString();
        }

        private static void AppendXy(StringBuilder sb, MapPoint p) =>
            sb.Append('[').Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(']');

        /// <summary>
        /// Download the breaklines intersecting the region from each requested phase's service. A
        /// phase whose layer has no Z, or that returns nothing, is reported in Notes and skipped
        /// rather than failing the whole fetch. Safe on any thread.
        /// </summary>
        public static async Task<BreaklineFetchResult> FetchAsync(
            BreaklineRegion region, IReadOnlyCollection<int> phases, IProgress<string> progress, CancellationToken ct)
        {
            var result = new BreaklineFetchResult();
            foreach (var (phase, url) in Services.Where(s => phases.Contains(s.phase)))
            {
                ct.ThrowIfCancellationRequested();
                var layer = await GetJsonAsync(Http.GetAsync(url + "?f=json", ct), ct).ConfigureAwait(false);
                var root = layer.RootElement;
                ThrowIfServiceError(root);

                if (!(root.TryGetProperty("hasZ", out var hasZ) && hasZ.ValueKind == JsonValueKind.True))
                {
                    result.Notes.Add($"Phase {phase} breaklines have no elevation (Z) values, so they can't be used as a constraint -- skipped.");
                    continue;
                }

                var oidField = root.GetProperty("fields").EnumerateArray()
                    .Where(f => f.GetProperty("type").GetString() == "esriFieldTypeOID")
                    .Select(f => f.GetProperty("name").GetString()).FirstOrDefault() ?? "OBJECTID";
                int pageSize = root.TryGetProperty("maxRecordCount", out var mrc) && mrc.GetInt32() > 0 ? Math.Min(mrc.GetInt32(), 2000) : 1000;

                int phaseCount = 0, noZ = 0, offset = 0;
                bool more = true;
                while (more)
                {
                    ct.ThrowIfCancellationRequested();
                    var form = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["f"] = "json",
                        ["where"] = "1=1",
                        ["geometry"] = region.EsriJson3857,
                        ["geometryType"] = "esriGeometryPolygon",
                        ["inSR"] = WebMercatorWkid.ToString(CultureInfo.InvariantCulture),
                        ["spatialRel"] = "esriSpatialRelIntersects",
                        ["outFields"] = TypeField,
                        ["returnGeometry"] = "true",
                        ["returnZ"] = "true",
                        ["orderByFields"] = oidField, // paging needs a stable order
                        ["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture),
                        ["resultRecordCount"] = pageSize.ToString(CultureInfo.InvariantCulture),
                    });
                    var page = await GetJsonAsync(Http.PostAsync(url + "/query", form, ct), ct).ConfigureAwait(false);
                    var pageRoot = page.RootElement;
                    ThrowIfServiceError(pageRoot);

                    int returned = 0;
                    foreach (var feature in pageRoot.GetProperty("features").EnumerateArray())
                    {
                        returned++;
                        var line = ParseFeature(phase, feature);
                        if (line == null) { noZ++; continue; }
                        result.Lines.Add(line);
                        phaseCount++;
                    }
                    offset += returned;
                    more = returned > 0 && pageRoot.TryGetProperty("exceededTransferLimit", out var ex) && ex.ValueKind == JsonValueKind.True;
                    page.Dispose();
                    if (more) progress?.Report($"Phase {phase} breaklines: {phaseCount} downloaded so far...");
                }
                layer.Dispose();

                progress?.Report($"Phase {phase} breaklines: {phaseCount} feature(s) intersect the AOI.");
                if (noZ > 0) result.Notes.Add($"Phase {phase}: {noZ} breakline(s) had missing elevation values and were skipped.");
            }
            return result;
        }

        private static async Task<JsonDocument> GetJsonAsync(Task<HttpResponseMessage> request, CancellationToken ct)
        {
            using var resp = await request.ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(text);
        }

        private static void ThrowIfServiceError(JsonElement root)
        {
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException("Breakline service error: " +
                    (err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString()));
        }

        /// <summary>Returns null when the feature has no geometry, or any vertex lacks a Z value.</summary>
        private static RawBreakline ParseFeature(int phase, JsonElement feature)
        {
            if (!feature.TryGetProperty("geometry", out var geom) || geom.ValueKind != JsonValueKind.Object ||
                !geom.TryGetProperty("paths", out var paths))
                return null;

            var parsed = new List<List<Coordinate3D>>();
            foreach (var path in paths.EnumerateArray())
            {
                var coords = new List<Coordinate3D>();
                foreach (var v in path.EnumerateArray())
                {
                    if (v.GetArrayLength() < 3 || v[2].ValueKind != JsonValueKind.Number) return null;
                    coords.Add(new Coordinate3D(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble()));
                }
                if (coords.Count >= 2) parsed.Add(coords);
            }
            if (parsed.Count == 0) return null;

            string type = null;
            if (feature.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object &&
                attrs.TryGetProperty(TypeField, out var t) && t.ValueKind == JsonValueKind.String)
                type = t.GetString();
            return new RawBreakline { Phase = phase, LineType = type, Paths = parsed };
        }

        /// <summary>
        /// Reproject each line to EPSG:3089, clip it to the region, and write what's left to a new
        /// Z-enabled feature class in a fresh file geodatabase under <paramref name="outputFolder"/>.
        /// Lines fully inside the region skip the (slower) intersection. MCT thread.
        /// </summary>
        public static BreaklineWriteResult WriteFeatureClass(BreaklineRegion region, IReadOnlyList<RawBreakline> lines, string outputFolder)
        {
            var sr3089 = SpatialReferenceBuilder.CreateSpatialReference(OutputWkid);
            var sr3857 = SpatialReferenceBuilder.CreateSpatialReference(WebMercatorWkid);

            Directory.CreateDirectory(outputFolder);
            // Timestamped so a re-run never collides with a geodatabase a LAS dataset already references.
            var gdbUri = new Uri(Path.Combine(outputFolder, $"breaklines_{DateTime.Now:yyyyMMdd_HHmmss}.gdb"));
            var connection = new FileGeodatabaseConnectionPath(gdbUri);
            SchemaBuilder.CreateGeodatabase(connection);

            using var gdb = new Geodatabase(connection);
            var schema = new SchemaBuilder(gdb);
            schema.Create(new FeatureClassDescription(FeatureClassName,
                new[]
                {
                    new FieldDescription("BL_TYPE", FieldType.String) { Length = 50 },
                    new FieldDescription("PHASE", FieldType.Integer),
                },
                new ShapeDescription(GeometryType.Polyline, sr3089) { HasZ = true }));
            if (!schema.Build())
                throw new InvalidOperationException("Could not create the breakline feature class in " + gdbUri.LocalPath);

            GeometryEngine.Instance.AccelerateForRelationalOperations(region.Polygon3089);

            int written = 0;
            using (var fc = gdb.OpenDataset<FeatureClass>(FeatureClassName))
            using (var definition = fc.GetDefinition())
            using (var cursor = fc.CreateInsertCursor())
            using (var buffer = fc.CreateRowBuffer())
            {
                var shapeField = definition.GetShapeField();
                foreach (var raw in lines)
                {
                    var builder = new PolylineBuilderEx(sr3857) { HasZ = true };
                    foreach (var path in raw.Paths) builder.AddPart(path);
                    var projected = (Polyline)GeometryEngine.Instance.Project(builder.ToGeometry(), sr3089);
                    if (projected.IsEmpty) continue;

                    var clipped = GeometryEngine.Instance.Contains(region.Polygon3089, projected)
                        ? projected
                        : (Polyline)GeometryEngine.Instance.Intersection(projected, region.Polygon3089);
                    if (clipped == null || clipped.IsEmpty || clipped.Length <= 0) continue;

                    buffer["BL_TYPE"] = raw.LineType;
                    buffer["PHASE"] = raw.Phase;
                    buffer[shapeField] = clipped;
                    cursor.Insert(buffer);
                    written++;
                }
                cursor.Flush();
            }

            return new BreaklineWriteResult { FeatureClassPath = Path.Combine(gdbUri.LocalPath, FeatureClassName), FeatureCount = written };
        }

        /// <summary>The value-table row LasDatasetService hands to the LAS dataset tools' surface
        /// constraint parameter: feature class (quoted, since paths can contain spaces), height
        /// source (the features' own Z), and constraint type.</summary>
        public static string ToConstraintArgument(string featureClassPath) => $"'{featureClassPath}' Shape.Z Hard_Line";
    }
}
