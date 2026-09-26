/*
 * Wraps the "Create LAS Dataset" / "Add Files To LAS Dataset" geoprocessing tools so clipped .las
 * output (one merged file, or several individual tile files) can be folded into a new or existing
 * .lasd, then added to the active map.
 *
 * lasPaths is a single string because that's what these GP tools' multivalue "in_las"/"in_files"
 * parameters expect for more than one file: semicolon-separated, e.g. "a.las;b.las" -- callers
 * with several output files join them before calling in.
 */
using System;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace KylidarAddin.Services
{
    public static class LasDatasetService
    {
        /// <summary>Create a new .lasd containing the given .las file(s), optionally with a surface
        /// constraint (see BreaklineService.ToConstraintArgument). Statistics are NOT computed at
        /// creation (NO_COMPUTE_STATS) -- that's an extra pass over every point; they can still be
        /// computed afterward if wanted.
        ///
        /// Positional parameters of Create LAS Dataset: input, out_las_dataset, folder_recursion,
        /// in_surface_constraints, spatial_reference, compute_stats -- so getting to compute_stats
        /// means passing the optional constraint and spatial-reference slots too (empty = unset,
        /// which for the latter keeps the default of taking it from the .las files).</summary>
        public static async Task<bool> CreateLasDatasetAsync(string lasPaths, string lasdPath, IProgress<string> progress, string surfaceConstraint = null)
        {
            var args = Geoprocessing.MakeValueArray(lasPaths, lasdPath, "NO_RECURSION", surfaceConstraint ?? "", "", "NO_COMPUTE_STATS");
            var result = await Geoprocessing.ExecuteToolAsync("CreateLasDataset_management", args,
                environments: null, flags: GPExecuteToolFlags.None).ConfigureAwait(false);
            if (result.IsFailed)
            {
                progress?.Report("Create LAS Dataset failed: " + string.Join("; ", result.ErrorMessages));
                return false;
            }
            return true;
        }

        /// <summary>Append .las file(s) to an existing .lasd, optionally with a surface constraint.</summary>
        public static async Task<bool> AddFilesToLasDatasetAsync(string lasdPath, string lasPaths, IProgress<string> progress, string surfaceConstraint = null)
        {
            var args = surfaceConstraint == null
                ? Geoprocessing.MakeValueArray(lasdPath, lasPaths)
                : Geoprocessing.MakeValueArray(lasdPath, lasPaths, "NO_RECURSION", surfaceConstraint);
            var result = await Geoprocessing.ExecuteToolAsync("AddFilesToLasDataset_management", args,
                environments: null, flags: GPExecuteToolFlags.None).ConfigureAwait(false);
            if (result.IsFailed)
            {
                progress?.Report("Add Files To LAS Dataset failed: " + string.Join("; ", result.ErrorMessages));
                return false;
            }
            return true;
        }

        /// <summary>Add a .lasd to the active map as a layer.</summary>
        public static Task<bool> AddToMapAsync(string lasdPath)
        {
            return QueuedTask.Run(() =>
            {
                var mv = MapView.Active;
                if (mv == null) return false;
                try
                {
                    LayerFactory.Instance.CreateLayer(new Uri(lasdPath), mv.Map);
                    return true;
                }
                catch { return false; }
            });
        }

        /// <summary>Add a geodatabase feature class (here, the clipped breaklines) to the active map
        /// under the given layer name -- named explicitly because every run writes a feature class
        /// called "Breaklines", which would otherwise stack up as identically named layers.</summary>
        public static Task<bool> AddFeatureClassToMapAsync(string featureClassPath, string layerName)
        {
            return QueuedTask.Run(() =>
            {
                var mv = MapView.Active;
                if (mv == null) return false;
                try
                {
                    var parameters = new FeatureLayerCreationParams(new Uri(featureClassPath)) { Name = layerName };
                    LayerFactory.Instance.CreateLayer<FeatureLayer>(parameters, mv.Map);
                    return true;
                }
                catch { return false; }
            });
        }
    }
}
