/*
 * Wraps the "Create LAS Dataset" / "Add Files To LAS Dataset" geoprocessing tools so a clipped
 * .las output can be folded into a new or existing .lasd, then added to the active map.
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
        public static async Task<bool> CreateLasDatasetAsync(string lasPath, string lasdPath, IProgress<string> progress)
        {
            var args = Geoprocessing.MakeValueArray(lasPath, lasdPath);
            var result = await Geoprocessing.ExecuteToolAsync("CreateLasDataset_management", args,
                environments: null, flags: GPExecuteToolFlags.None).ConfigureAwait(false);
            if (result.IsFailed)
            {
                progress?.Report("Create LAS Dataset failed: " + string.Join("; ", result.ErrorMessages));
                return false;
            }
            return true;
        }

        /// <summary>Append a .las file to an existing .lasd.</summary>
        public static async Task<bool> AddFilesToLasDatasetAsync(string lasdPath, string lasPath, IProgress<string> progress)
        {
            var args = Geoprocessing.MakeValueArray(lasdPath, lasPath);
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
    }
}
