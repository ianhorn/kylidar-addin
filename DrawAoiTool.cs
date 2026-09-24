/*
 * Draw AOI map tools: let the user sketch a point, line, or polygon on the map. The resulting
 * geometry is stored in AoiState.Current in MAP coordinates (not reprojected) -- buffering by
 * feet needs a linear-unit CRS, and reprojection to WGS84 for the STAC search happens later,
 * inside CopcClipService. One tool class per sketch type (each with a fixed SketchType set in
 * the constructor) -- this is the reliable Pro SDK pattern: a single tool instance with a
 * dynamically-changed SketchType does not switch sketch behavior.
 *
 * Each sketch is also saved as a feature in the map's Point/Line/Polygon Map Notes layer (see
 * MapNotesService) so the user keeps a persistent, editable/reselectable record of what they drew,
 * not just the transient in-memory AoiState.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using KylidarAddin.Services;

namespace KylidarAddin
{
    /// <summary>
    /// Holds the most recently sketched/selected AOI geometry, in its original map spatial
    /// reference, plus a short human-readable description. Raises <see cref="Changed"/> so the
    /// Kylidar dock pane can update its status line without a MessageBox popup.
    /// </summary>
    internal static class AoiState
    {
        public static Geometry Current { get; private set; }
        public static string Description { get; private set; }
        public static event EventHandler Changed;

        public static void Set(Geometry geometry, string description)
        {
            Current = geometry;
            Description = description;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Clear()
        {
            Current = null;
            Description = null;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    internal abstract class DrawAoiToolBase : MapTool
    {
        private readonly MapNoteKind _noteKind;
        private readonly string _aoiLabel;

        protected DrawAoiToolBase(SketchGeometryType sketchType, MapNoteKind noteKind, string aoiLabel) : base()
        {
            IsSketchTool = true;
            SketchType = sketchType;
            SketchOutputMode = SketchOutputMode.Map; // geometry in map coordinates
            _noteKind = noteKind;
            _aoiLabel = aoiLabel;
        }

        protected override async Task<bool> OnSketchCompleteAsync(Geometry geometry)
        {
            if (geometry == null) return false;

            // Record it as a real, persistent map note feature so the user has an editable/
            // reselectable record of what they drew -- but don't let a hiccup there (e.g. the
            // layer template missing) block the core AOI workflow, which only needs AoiState.
            var map = MapView.Active?.Map;
            if (map != null)
            {
                try
                {
                    await MapNotesService.AddNoteAsync(map, _noteKind, geometry, _aoiLabel);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Drew the AOI, but couldn't add it to the {_aoiLabel} Map Notes layer: {ex.Message}",
                        "Kylidar", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            AoiState.Set(geometry, _aoiLabel);
            return true;
        }
    }

    internal class DrawPointAoiTool : DrawAoiToolBase
    {
        public const string ToolId = "KylidarAddin_DrawPointAoiTool";
        public DrawPointAoiTool() : base(SketchGeometryType.Point, MapNoteKind.Point, "Point AOI") { }
    }

    internal class DrawLineAoiTool : DrawAoiToolBase
    {
        public const string ToolId = "KylidarAddin_DrawLineAoiTool";
        public DrawLineAoiTool() : base(SketchGeometryType.Line, MapNoteKind.Line, "Line AOI") { }
    }

    internal class DrawPolygonAoiTool : DrawAoiToolBase
    {
        public const string ToolId = "KylidarAddin_DrawPolygonAoiTool";
        public DrawPolygonAoiTool() : base(SketchGeometryType.Polygon, MapNoteKind.Polygon, "Polygon AOI") { }
    }

    /// <summary>
    /// Lets the user click (or drag a box over) existing map features and uses their unioned
    /// geometry as the AOI, instead of sketching a new one. Uses MapView.SelectFeatures so the
    /// picked features get the normal Pro selection highlight, matching the built-in Select tool.
    /// </summary>
    internal class SelectFeatureAoiTool : MapTool
    {
        public const string ToolId = "KylidarAddin_SelectFeatureAoiTool";

        public SelectFeatureAoiTool()
        {
            IsSketchTool = true;
            SketchType = SketchGeometryType.Rectangle;
            // Screen (not Map) coordinates -- MapView.SelectFeatures throws in 3D scenes if handed
            // a map-coordinate sketch ("3D views only support selecting features interactively
            // using geometry in screen coordinates..."); screen coordinates work for both 2D maps
            // and 3D scenes, so this is the one mode that works everywhere.
            SketchOutputMode = SketchOutputMode.Screen;
        }

        protected override async Task<bool> OnSketchCompleteAsync(Geometry geometry)
        {
            if (geometry == null) return false;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // Step 1 -- select only, as its own short main-CIM-thread job. The geometry work in
                // step 2 (fetch, reproject, union) can take seconds for a big feature (a county
                // boundary, a long river line) and used to run in this same job right after
                // SelectFeatures, so Pro was still trying to paint the selection highlight while that
                // work held the thread -- the highlight then trickled in a piece at a time. Returning
                // here first lets Pro service its own queued work (the highlight redraw) before step 2
                // is queued.
                var selected = await QueuedTask.Run(() =>
                {
                    var mapView = MapView.Active;
                    if (mapView == null) return null;

                    var selection = mapView.SelectFeatures(geometry, SelectionCombinationMethod.New);
                    return selection.Count == 0 ? null : selection.ToDictionary();
                });

                if (selected == null)
                {
                    MessageBox.Show("No feature found there. Click directly on a feature, or drag a box over one.",
                        "Kylidar", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                // Step 2 -- read the selected features' shapes and union them into the AOI.
                var geometryTimer = Stopwatch.StartNew();
                var (unioned, count) = await QueuedTask.Run(() =>
                {
                    var mapSr = MapView.Active?.Map.SpatialReference;
                    var shapes = new List<Geometry>();
                    foreach (var kvp in selected)
                    {
                        if (kvp.Key is not FeatureLayer featureLayer) continue;

                        using var cursor = featureLayer.GetTable().Search(new QueryFilter { ObjectIDs = kvp.Value.ToList() }, false);
                        while (cursor.MoveNext())
                        {
                            using var feature = (Feature)cursor.Current;
                            var shape = feature.GetShape();
                            if (shape == null) continue;
                            if (mapSr != null && shape.SpatialReference != null && !shape.SpatialReference.IsEqual(mapSr))
                                shape = GeometryEngine.Instance.Project(shape, mapSr);
                            shapes.Add(shape);
                        }
                    }

                    if (shapes.Count == 0) return ((Geometry)null, 0);

                    // Union requires matching geometry dimension (point/multipoint vs polyline vs
                    // polygon/envelope) -- batch-union within each dimension group (fast, one native
                    // call instead of N-1 pairwise calls), then fold the handful of group results.
                    var result = shapes
                        .GroupBy(s => s.GeometryType)
                        .Select(g => g.Count() == 1 ? g.First() : GeometryEngine.Instance.Union(g))
                        .Aggregate((a, b) => GeometryEngine.Instance.Union(a, b));

                    return (result, shapes.Count);
                });

                if (unioned == null)
                {
                    MessageBox.Show("No feature found there. Click directly on a feature, or drag a box over one.",
                        "Kylidar", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                // When step 2 was slow, say so (and how big the geometry was) right in the AOI status
                // line, so a slow selection can be told apart from a slow highlight redraw.
                var description = $"AOI from {count} selected feature(s)";
                if (geometryTimer.Elapsed.TotalSeconds >= 1.5)
                {
                    var vertices = (unioned as Multipart)?.PointCount;
                    description += $" (reading the geometry took {geometryTimer.Elapsed.TotalSeconds:F1}s" +
                                   (vertices.HasValue ? $", {vertices.Value:N0} vertices)" : ")");
                }
                AoiState.Set(unioned, description);
                return true;
            }
            catch (Exception ex)
            {
                // A malformed feature geometry (e.g. a bad SR/Z mismatch written by some other tool)
                // could throw here during Project/Union -- show it instead of letting it escape
                // OnSketchCompleteAsync unhandled, which has previously crashed Pro entirely rather
                // than just failing this one selection.
                MessageBox.Show($"Couldn't use the selected feature(s) as an AOI: {ex.Message}",
                    "Kylidar", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}
