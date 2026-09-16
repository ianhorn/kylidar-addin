/*
 * Adds sketched AOI geometry into Esri's built-in Point/Line/Polygon Map Notes layers -- the same
 * "Point Map Notes" / "Line Map Notes" / "Polygon Map Notes" layer templates on Pro's Insert
 * ribbon tab -- instead of only holding it in memory (AoiState). This gives the user a real,
 * persistent, editable feature they can see, edit, and reselect later (SelectFeatureAoiTool
 * already lets any feature, map notes included, be picked back up as an AOI).
 *
 * The templates themselves (and the "Point"/"Line"/"Polygon Notes" layer names + NoteType/Name
 * schema they produce) were confirmed by inspecting the installed .lpkx packages under
 * "ArcGIS\Pro\Resources\LayerTemplates\Gallery\en-US" -- Esri's SDK docs don't spell out the
 * template item names or resulting layer names.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace KylidarAddin.Services
{
    internal enum MapNoteKind { Point, Line, Polygon }

    internal static class MapNotesService
    {
        // The layer template item's display name, as surfaced through Map.LayerTemplatePackages --
        // taken from each package's iteminfo.xml <title>. Kept as the first, exact-match attempt;
        // FindTemplateItem below also tries a looser match since some Pro installs/versions expose
        // Item.Name differently than that title (e.g. the package's own file-based name).
        private static string TemplateItemName(MapNoteKind kind) => kind switch
        {
            MapNoteKind.Point => "Point Map Notes",
            MapNoteKind.Line => "Line Map Notes",
            MapNoteKind.Polygon => "Polygon Map Notes",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        // A second keyword to require alongside the geometry keyword when the exact-title match
        // misses, e.g. "Point" note templates are also filed under "Point_MapNotes" or similar.
        private static string GeometryKeyword(MapNoteKind kind) => kind switch
        {
            MapNoteKind.Point => "Point",
            MapNoteKind.Line => "Line",
            MapNoteKind.Polygon => "Polygon",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        // The layer name the template actually produces once added to the map -- not the same
        // string as the template's own display name above.
        private static string LayerName(MapNoteKind kind) => kind switch
        {
            MapNoteKind.Point => "Point Notes",
            MapNoteKind.Line => "Line Notes",
            MapNoteKind.Polygon => "Polygon Notes",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        /// <summary>
        /// Find the Point/Line/Polygon Map Notes layer template item. Tries an exact title match
        /// first (what Esri's own packages report as of Pro 3.x); if that comes up empty (a
        /// different Pro version/locale surfacing Item.Name differently), falls back to a looser
        /// match requiring the geometry keyword ("Point"/"Line"/"Polygon") plus "Note", excluding
        /// "Text" (the Text Map Notes templates would otherwise also match "Point"-free searches).
        /// </summary>
        private static ArcGIS.Desktop.Core.Item FindTemplateItem(Map map, MapNoteKind kind)
        {
            var items = map.LayerTemplatePackages?.ToList() ?? new List<ArcGIS.Desktop.Core.Item>();
            if (items.Count == 0)
                throw new InvalidOperationException("Map.LayerTemplatePackages returned no layer templates.");

            var exactName = TemplateItemName(kind);
            var match = items.FirstOrDefault(i => i.Name.Equals(exactName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            var geometryKeyword = GeometryKeyword(kind);
            match = items.FirstOrDefault(i =>
                i.Name.IndexOf(geometryKeyword, StringComparison.OrdinalIgnoreCase) >= 0 &&
                i.Name.IndexOf("Note", StringComparison.OrdinalIgnoreCase) >= 0 &&
                i.Name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0);
            if (match != null) return match;

            var available = string.Join(", ", items.Select(i => $"'{i.Name}'"));
            throw new InvalidOperationException(
                $"Couldn't find the '{exactName}' layer template. Available layer templates: {available}");
        }

        /// <summary>
        /// Adds <paramref name="geometry"/> as a new feature in the map's Point/Line/Polygon Map
        /// Notes layer, creating that layer from its Pro-installed template the first time it's
        /// needed, and selects the new feature. Must be called on the MCT (wraps its own
        /// QueuedTask.Run). Throws if the template or the newly created layer can't be found.
        /// </summary>
        public static async Task AddNoteAsync(Map map, MapNoteKind kind, Geometry geometry, string name)
        {
            await QueuedTask.Run(() =>
            {
                var layerName = LayerName(kind);
                var layer = map.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

                if (layer == null)
                {
                    var item = FindTemplateItem(map, kind);
                    layer = LayerFactory.Instance.CreateLayer<FeatureLayer>(
                        new LayerCreationParams(item) { IsVisible = true }, map);
                }

                var attributes = new Dictionary<string, object>
                {
                    ["SHAPE"] = MatchFeatureClassSchema(layer, geometry),
                    ["NoteType"] = (short)0,
                    ["Name"] = name,
                };

                var editOp = new EditOperation { Name = $"Add {name}" };
                var token = editOp.Create(layer, attributes);
                if (!editOp.Execute())
                    throw new InvalidOperationException(editOp.ErrorMessage ?? "Failed to add the map note feature.");

                if (token.ObjectID.HasValue)
                    layer.Select(new QueryFilter { ObjectIDs = new List<long> { token.ObjectID.Value } }, SelectionCombinationMethod.New);
            });
        }

        /// <summary>
        /// Map notes feature classes have a fixed spatial reference (Web Mercator, baked into Esri's
        /// template) and are Z-aware -- neither necessarily matches the sketched geometry, which is
        /// in the map's own spatial reference and 2D. EditOperation.Create does NOT reproject on
        /// insert: coordinate values are written as-is and merely tagged with the target field's SR,
        /// so skipping this turns a Kentucky-state-plane polygon into a "Web Mercator" one with the
        /// same raw numbers -- coordinates nonsensical for that SR. That corruption doesn't surface
        /// at insert time; it surfaces (crashing Pro entirely, not just throwing) the first time the
        /// feature is read back and reprojected, e.g. by SelectFeatureAoiTool. Fix both mismatches
        /// before they're ever written.
        /// </summary>
        private static Geometry MatchFeatureClassSchema(FeatureLayer layer, Geometry geometry)
        {
            using var featureClass = layer.GetFeatureClass();
            using var definition = featureClass.GetDefinition();

            var targetSr = definition.GetSpatialReference();
            if (targetSr != null && geometry.SpatialReference != null && !geometry.SpatialReference.IsEqual(targetSr))
                geometry = GeometryEngine.Instance.Project(geometry, targetSr);

            if (definition.HasZ() && !geometry.HasZ)
            {
                geometry = geometry switch
                {
                    MapPoint pt => new MapPointBuilderEx(pt) { HasZ = true }.ToGeometry(),
                    Polyline pl => new PolylineBuilderEx(pl) { HasZ = true }.ToGeometry(),
                    Polygon pg => new PolygonBuilderEx(pg) { HasZ = true }.ToGeometry(),
                    _ => geometry
                };
            }

            return geometry;
        }
    }
}
