/*
 * Converts an ArcGIS.Core.Geometry geometry (projected to WGS84 / CRS84) to a GeoJSON geometry
 * string suitable for the STAC 'intersects' parameter, and to WKT for PDAL's crop "polygon"
 * reader option. Handles MapPoint (Point), Polyline (LineString/MultiLineString), and Polygon
 * (Polygon/MultiPolygon).
 *
 * A "select feature" AOI (union of one or more selected features, see SelectFeatureAoiTool) can
 * come back multi-part -- e.g. two disjoint polygon pieces from unioning non-adjacent features.
 * Flattening every ring/path into a single ring (the old approach) produces a self-intersecting,
 * geometrically invalid shape once there's more than one part, which STAC/PDAL then reject. So
 * every part is split out via GeometryEngine.Instance.MultipartToSinglePart first, and the result
 * is emitted as MultiPolygon/MultiLineString when there's more than one. Note: for a polygon with
 * actual holes, MultipartToSinglePart treats each ring (exterior or hole) as its own part, so a
 * hole would come out as an extra polygon piece rather than a subtracted hole -- an acceptable
 * simplification for a LiDAR clip AOI, where holes are not a realistic case.
 */
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ArcGIS.Core.Geometry;

namespace KylidarAddin.Services
{
    public static class GeoJsonConverter
    {
        /// <summary>
        /// Convert a geometry to a GeoJSON geometry JSON string.
        /// The geometry should already be projected to WGS84 (lon/lat).
        /// </summary>
        public static string ToGeoJsonGeometry(Geometry geometry)
        {
            if (geometry == null) return null;
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                WriteGeometry(writer, geometry);
                writer.Flush();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void WriteGeometry(Utf8JsonWriter w, Geometry g)
        {
            switch (g)
            {
                case MapPoint p:
                    w.WriteStartObject();
                    w.WriteString("type", "Point");
                    w.WritePropertyName("coordinates");
                    w.WriteStartArray();
                    w.WriteNumberValue(p.X);
                    w.WriteNumberValue(p.Y);
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;

                case Polygon poly:
                {
                    var rings = SinglePartRings(poly);
                    w.WriteStartObject();
                    if (rings.Count == 1)
                    {
                        w.WriteString("type", "Polygon");
                        w.WritePropertyName("coordinates");
                        WritePolygonCoordinates(w, rings[0]);
                    }
                    else
                    {
                        w.WriteString("type", "MultiPolygon");
                        w.WritePropertyName("coordinates");
                        w.WriteStartArray();
                        foreach (var ring in rings) WritePolygonCoordinates(w, ring);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                    break;
                }

                case Polyline line:
                {
                    var paths = SinglePartPaths(line);
                    w.WriteStartObject();
                    if (paths.Count == 1)
                    {
                        w.WriteString("type", "LineString");
                        w.WritePropertyName("coordinates");
                        w.WriteStartArray();
                        WritePositions(w, paths[0], closeRing: false);
                        w.WriteEndArray();
                    }
                    else
                    {
                        w.WriteString("type", "MultiLineString");
                        w.WritePropertyName("coordinates");
                        w.WriteStartArray();
                        foreach (var path in paths)
                        {
                            w.WriteStartArray();
                            WritePositions(w, path, closeRing: false);
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                    break;
                }
            }
        }

        /// <summary>Write one Polygon's "coordinates" value: [ [ [x,y], ... ] ] (a single ring, no holes).</summary>
        private static void WritePolygonCoordinates(Utf8JsonWriter w, IReadOnlyList<Coordinate2D> ring)
        {
            w.WriteStartArray();
            WriteRing(w, ring, closeRing: true);
            w.WriteEndArray();
        }

        /// <summary>Write positions flat into the current array context: [x,y], [x,y], ...</summary>
        private static void WritePositions(Utf8JsonWriter w, IReadOnlyList<Coordinate2D> coords, bool closeRing)
        {
            foreach (var c in coords)
            {
                w.WriteStartArray();
                w.WriteNumberValue(c.X);
                w.WriteNumberValue(c.Y);
                w.WriteEndArray();
            }
            // GeoJSON polygon rings must be closed (first point repeated as last).
            if (closeRing && coords.Count > 0)
            {
                w.WriteStartArray();
                w.WriteNumberValue(coords[0].X);
                w.WriteNumberValue(coords[0].Y);
                w.WriteEndArray();
            }
        }

        /// <summary>Write a single ring = [ [x,y], [x,y], ... ] (used for Polygon).</summary>
        private static void WriteRing(Utf8JsonWriter w, IReadOnlyList<Coordinate2D> coords, bool closeRing)
        {
            w.WriteStartArray();
            WritePositions(w, coords, closeRing);
            w.WriteEndArray();
        }

        /// <summary>
        /// Convert a geometry to a WKT string (used for PDAL's crop "polygon" reader option).
        /// The geometry should already be projected to WGS84 (lon/lat).
        /// </summary>
        public static string ToWkt(Geometry geometry)
        {
            if (geometry == null) return null;
            switch (geometry)
            {
                case Polygon poly:
                {
                    var rings = SinglePartRings(poly);
                    if (rings.Count == 1)
                        return $"POLYGON(({RingToWkt(rings[0])}))";

                    var groups = rings.Select(r => $"(({RingToWkt(r)}))");
                    return $"MULTIPOLYGON({string.Join(",", groups)})";
                }
                case Polyline line:
                {
                    var paths = SinglePartPaths(line);
                    if (paths.Count == 1)
                        return $"LINESTRING({PositionsToWkt(paths[0])})";

                    var groups = paths.Select(p => $"({PositionsToWkt(p)})");
                    return $"MULTILINESTRING({string.Join(",", groups)})";
                }
                case MapPoint p:
                    return $"POINT({p.X} {p.Y})";
                default:
                    return null;
            }
        }

        private static string RingToWkt(IReadOnlyList<Coordinate2D> coords)
        {
            var sb = new StringBuilder();
            AppendPositions(sb, coords);
            if (coords.Count > 0)
                sb.Append(", ").Append(coords[0].X).Append(' ').Append(coords[0].Y);
            return sb.ToString();
        }

        private static string PositionsToWkt(IReadOnlyList<Coordinate2D> coords)
        {
            var sb = new StringBuilder();
            AppendPositions(sb, coords);
            return sb.ToString();
        }

        private static void AppendPositions(StringBuilder sb, IReadOnlyList<Coordinate2D> coords)
        {
            for (int i = 0; i < coords.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(coords[i].X).Append(' ').Append(coords[i].Y);
            }
        }

        /// <summary>Split a (possibly multi-part) polygon into each part's own ring of 2D coordinates.</summary>
        private static List<IReadOnlyList<Coordinate2D>> SinglePartRings(Polygon poly)
        {
            var parts = GeometryEngine.Instance.MultipartToSinglePart(poly);
            var result = new List<IReadOnlyList<Coordinate2D>>(parts.Count);
            foreach (var part in parts)
                if (part is Polygon p) result.Add(p.Copy2DCoordinatesToList());
            return result;
        }

        /// <summary>Split a (possibly multi-part) polyline into each part's own path of 2D coordinates.</summary>
        private static List<IReadOnlyList<Coordinate2D>> SinglePartPaths(Polyline line)
        {
            var parts = GeometryEngine.Instance.MultipartToSinglePart(line);
            var result = new List<IReadOnlyList<Coordinate2D>>(parts.Count);
            foreach (var part in parts)
                if (part is Polyline p) result.Add(p.Copy2DCoordinatesToList());
            return result;
        }
    }
}
