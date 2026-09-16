/*
 * Wraps the "Create LAS Dataset" / "Add Files To LAS Dataset" / "Build LAS Dataset Pyramid"
 * geoprocessing tools so clipped .las output (one merged file, or several individual tile files)
 * can be folded into a new or existing .lasd, pyramided, then added to the active map.
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
        /// <summary>Create a new .lasd containing the given .las file(s).</summary>
        public static async Task<bool> CreateLasDatasetAsync(string lasPaths, string lasdPath, IProgress<string> progress)
        {
            var args = Geoprocessing.MakeValueArray(lasPaths, lasdPath);
            var result = await Geoprocessing.ExecuteToolAsync("CreateLasDataset_management", args,
                environments: null, flags: GPExecuteToolFlags.None).ConfigureAwait(false);
            if (result.IsFailed)
            {
                progress?.Report("Create LAS Dataset failed: " + string.Join("; ", result.ErrorMessages));
                return false;
            }
            return true;
        }

        /// <summary>Append .las file(s) to an existing .lasd.</summary>
        public static async Task<bool> AddFilesToLasDatasetAsync(string lasdPath, string lasPaths, IProgress<string> progress)
        {
            var args = Geoprocessing.MakeValueArray(lasdPath, lasPaths);
            var result = await Geoprocessing.ExecuteToolAsync("AddFilesToLasDataset_management", args,
                environments: null, flags: GPExecuteToolFlags.None).ConfigureAwait(false);
            if (result.IsFailed)
            {
                progress?.Report("Add Files To LAS Dataset failed: " + string.Join("; ", result.ErrorMessages));
                return false;
            }
            return true;
        }

        /// <summary>Build (or refresh) a LAS dataset's display pyramid.</summary>
        public static async Task<bool> BuildPyramidsAsync(string lasdPath, IProgress<string> progress)
        {
            var args = Geoprocessing.MakeValueArray(lasdPath);
            var result = await Geoprocessing.ExecuteToolAsync("BuildLasDatasetPyramid_management", args,
                environments: null, flags: GPExecuteToolFlags.None).ConfigureAwait(false);
            if (result.IsFailed)
            {
                progress?.Report("Build LAS Dataset Pyramid failed: " + string.Join("; ", result.ErrorMessages));
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
    }
}
