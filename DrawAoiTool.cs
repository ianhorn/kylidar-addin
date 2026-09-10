/*
 * Draw AOI map tools: let the user sketch a point, line, or polygon on the map. The resulting
 * geometry is stored in AoiState.Current in MAP coordinates (not reprojected) -- buffering by
 * feet needs a linear-unit CRS, and reprojection to WGS84 for the STAC search happens later,
 * inside CopcClipService. One tool class per sketch type (each with a fixed SketchType set in
 * the constructor) -- this is the reliable Pro SDK pattern: a single tool instance with a
 * dynamically-changed SketchType does not switch sketch behavior.
 */
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace KylidarAddin
{
    /// <summary>Holds the most recently sketched AOI geometry, in its original map spatial reference.</summary>
    internal static class AoiState
    {
        public static Geometry Current { get; set; }
    }

    internal abstract class DrawAoiToolBase : MapTool
    {
        protected DrawAoiToolBase(SketchGeometryType sketchType) : base()
        {
            IsSketchTool = true;
            SketchType = sketchType;
            SketchOutputMode = SketchOutputMode.Map; // geometry in map coordinates
        }

        protected override Task<bool> OnSketchCompleteAsync(Geometry geometry)
        {
            if (geometry == null) return Task.FromResult(false);

            AoiState.Current = geometry;
            MessageBox.Show("AOI captured. Use \"Clip LiDAR to AOI\" to search and clip.",
                "Kylidar", MessageBoxButton.OK, MessageBoxImage.Information);

            return Task.FromResult(true);
        }
    }

    internal class DrawPointAoiTool : DrawAoiToolBase
    {
        public const string ToolId = "KylidarAddin_DrawPointAoiTool";
        public DrawPointAoiTool() : base(SketchGeometryType.Point) { }
    }

    internal class DrawLineAoiTool : DrawAoiToolBase
    {
        public const string ToolId = "KylidarAddin_DrawLineAoiTool";
        public DrawLineAoiTool() : base(SketchGeometryType.Line) { }
    }

    internal class DrawPolygonAoiTool : DrawAoiToolBase
    {
        public const string ToolId = "KylidarAddin_DrawPolygonAoiTool";
        public DrawPolygonAoiTool() : base(SketchGeometryType.Polygon) { }
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
            SketchOutputMode = SketchOutputMode.Map;
        }

        protected override async Task<bool> OnSketchCompleteAsync(Geometry geometry)
        {
            if (geometry == null) return false;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var (ok, unioned) = await QueuedTask.Run(() =>
                {
                    var mapView = MapView.Active;
                    if (mapView == null) return (false, (Geometry)null);

                    var selection = mapView.SelectFeatures(geometry, SelectionCombinationMethod.New);
                    if (selection.Count == 0) return (false, (Geometry)null);

                    var mapSr = mapView.Map.SpatialReference;
                    var shapes = new List<Geometry>();
                    foreach (var kvp in selection.ToDictionary())
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

                    if (shapes.Count == 0) return (false, (Geometry)null);

                    // Union requires matching geometry dimension (point/multipoint vs polyline vs
                    // polygon/envelope) -- batch-union within each dimension group (fast, one native
                    // call instead of N-1 pairwise calls), then fold the handful of group results.
                    var result = shapes
                        .GroupBy(s => s.GeometryType)
                        .Select(g => g.Count() == 1 ? g.First() : GeometryEngine.Instance.Union(g))
                        .Aggregate((a, b) => GeometryEngine.Instance.Union(a, b));

                    return (true, result);
                });

                if (!ok)
                {
                    MessageBox.Show("No feature found there. Click directly on a feature, or drag a box over one.",
                        "Kylidar", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                AoiState.Current = unioned;
                MessageBox.Show("AOI captured from selected feature(s). Use \"Clip LiDAR to AOI\" to search and clip.",
                    "Kylidar", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}
