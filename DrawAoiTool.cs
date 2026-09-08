/*
 * Draw AOI map tools: let the user sketch a point, line, or polygon on the map. The resulting
 * geometry is stored in AoiState.Current in MAP coordinates (not reprojected) -- buffering by
 * feet needs a linear-unit CRS, and reprojection to WGS84 for the STAC search happens later,
 * inside CopcClipService. One tool class per sketch type (each with a fixed SketchType set in
 * the constructor) -- this is the reliable Pro SDK pattern: a single tool instance with a
 * dynamically-changed SketchType does not switch sketch behavior.
 */
using System.Threading.Tasks;
using System.Windows;
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
}
